// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// One executable, two roles:
///   ClassroomClient.exe --service   the SYSTEM Windows service (network server, elevated work)
///   ClassroomClient.exe             the per-user helper started at logon (wallpaper, mouse, lock, standard-user work)
/// </summary>
static class Program
{
    /// <summary>
    /// Start-up check used by self-update (see UpdateHandler): a build that gets this far has found
    /// everything it needs to run on this computer, including its .NET runtime.
    /// </summary>
    public const string ProbeSwitch = "--probe";

    /// <summary>
    /// Run elevated by setup just before the Client is removed: puts back what would otherwise stay
    /// changed with nothing left to undo it (exam device controls, the secure DNS policy).
    /// </summary>
    public const string CleanupSwitch = "--uninstall-cleanup";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals(UpdateServiceHealth.Switch, StringComparison.OrdinalIgnoreCase))
            return UpdateServiceHealth.RunAsync(args[1..]).GetAwaiter().GetResult();
        if (args.Contains(ProbeSwitch, StringComparer.OrdinalIgnoreCase)) return 0;
        if (args.Contains(CleanupSwitch, StringComparer.OrdinalIgnoreCase)) return UninstallCleanup();
        if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
            return RunService(args);
        return RunUserAgent();
    }

    static int UninstallCleanup()
    {
        Log.FilePath = Path.Combine(ClientConfig.DataDir, "client.log");
        AgentContext.IsUserAgent = false;
        try
        {
            new ExamDeviceHandler().Restore();
            BlockHandler.SetSecureDnsOff(false);
            // Each on its own: one that cannot be put back must not keep the others in place.
            var failed = new List<string>();
            try { BrowserHandler.RestoreAll(); }
            catch (Exception ex) { failed.Add("browser policies: " + ex.Message); }
            var signIn = new SignInImageHandler().UseDefault();
            if (!signIn.Ok) failed.Add("sign-in picture: " + signIn.Error);
            Log.Write("Uninstall clean-up: exam device controls, the secure DNS policy, browser policies, the default browser and the sign-in picture were put back." +
                      (failed.Count > 0 ? " Not put back: " + string.Join("; ", failed) : ""));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Uninstall clean-up failed: {ex.Message}");
            return 1;
        }
    }

    static int RunService(string[] args)
    {
        Log.FilePath = Path.Combine(ClientConfig.DataDir, "client.log");
        AgentContext.IsUserAgent = false;
        Log.Write("Service starting.");
        // The few texts the service itself shows a student (the shutdown notice) follow the language of Windows.
        Loc.Use(Loc.Windows);
        MemoryReclaimer.ReleaseAfterStartup(Loc.Current);

        try
        {
            Host.CreateDefaultBuilder(args)
                .UseWindowsService(o => o.ServiceName = "ClassroomManagerClient")
                .ConfigureServices(s => s.AddHostedService<ClientService>())
                .Build()
                .Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Service failed: {ex}");
            return 1;
        }
    }

    static int RunUserAgent()
    {
        using var mutex = new Mutex(true, @"Local\ClassroomManagerUserAgent", out bool isFirst);
        if (!isFirst) return 0;

        AgentContext.IsUserAgent = true;
        Log.FilePath = Path.Combine(ClientConfig.UserDir, "user-agent.log");
        Log.Write($"User helper starting as {Environment.UserName}.");

        ApplicationConfiguration.Initialize();
        Ui.Init();
        // What the student sees (lock screen, message window, this icon) is in the language of their Windows.
        Loc.Use(Loc.Windows);
        MemoryReclaimer.ReleaseAfterStartup(Loc.Current);

        using var cts = new CancellationTokenSource();
        var agent = new UserAgent();
        _ = Task.Run(() => agent.RunAsync(cts.Token));

        // A visible tray icon so students can see the machine is managed.
        using var tray = new NotifyIcon
        {
            Icon = TrayIcon(),
            Text = ClientText.TrayNotice(System.Globalization.CultureInfo.CurrentUICulture.Name),
            Visible = true,
        };
        // Opening the icon shows what is true about this computer; the window has nothing a student could change.
        StatusWindow? status = null;
        void ShowStatus()
        {
            if (status is { IsDisposed: false }) { status.Activate(); return; }
            status = new StatusWindow();
            status.Show();
        }
        tray.DoubleClick += (_, _) => ShowStatus();
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatus(); };
        var statusMenu = new ContextMenuStrip();
        statusMenu.Items.Add(Loc.T("Classroom Manager status"), null, (_, _) => ShowStatus());
        tray.ContextMenuStrip = statusMenu;

        Application.ApplicationExit += (_, _) => cts.Cancel();
        Application.Run();
        return 0;
    }

    /// <summary>The program's own icon at the size the notification area uses; the Windows shield if it cannot be read.</summary>
    static Icon TrayIcon()
    {
        try
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("client.ico");
            if (stream is not null) return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception ex) { Log.Write($"Tray icon: {ex.Message}"); }
        return SystemIcons.Shield;
    }
}

/// <summary>Hosts the network server, discovery responder and user bridge inside the Windows service.</summary>
sealed class ClientService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A Client installed as a Windows package has no setup script: its first start builds client.json from the kit file the administrator left in the data folder.
        PackagedKit.BootstrapFromKit(ClassroomManager.Shared.PackageIdentity.IsPackaged, ClientConfig.DataDir, Log.Write);

        // The lab key and the certificate's private key are readable by SYSTEM and Administrators only; repaired here if not.
        SecurityEvents.Current = new SecurityEvents(Path.Combine(ClientConfig.DataDir, "security-events.log"));
        SecurityEvents.Current.Write("service-start", ("protocol", Protocol.Version));
        foreach (var file in new[] { ClientConfig.ConfigPath, Path.Combine(ClientConfig.DataDir, "tls-cert.pfx") })
            if (SecretFiles.Ensure(file) is { Length: > 0 } finding)
            {
                Log.Write("Secret file: " + finding);
                SecurityEvents.Current.Write("secret-file", ("file", Path.GetFileName(file)));
            }

        var config = ClientConfig.Load();
        if (config is null || string.IsNullOrWhiteSpace(config.Psk))
            throw new InvalidOperationException($"Missing or empty shared key in {ClientConfig.ConfigPath}");

        if (config.TlsWasOff) SecurityEvents.Current?.Write("tls-setting-ignored", ("file", "client.json"));
        if (config.TlsWasOff)
            Log.Write("client.json says useTls is false. That is ignored: TLS is always on. Remove the line, and update the Host if it still has TLS off.");

        Directory.CreateDirectory(ClientConfig.StagingDir);

        var bridge = new UserBridge();
        var blockHandler = new BlockHandler();
        var keys = new KeyStore(config.Psk, config.AccessKeys, primaryChanged: config.KeyChangedUtc is { } changed
            ? new DateTimeOffset(DateTime.SpecifyKind(changed, DateTimeKind.Utc)) : null, epoch: config.KeyEpoch, room: config.Room);
        Log.KnownSecrets = () => keys.Current.Select(key => key.Psk);
        var router = new CommandRouter(config, bridge, blockHandler, keys);
        var enrolment = new EnrolmentState(Path.Combine(ClientConfig.DataDir, "enrolment.json"));
        var server = new CommandServer(config, router, keys, enrolment);
        router.CertificateNotAfter = () => server.CertificateNotAfterUtc;
        var discovery = new DiscoveryResponder(config, keys, enrolment: enrolment, certificate: () => server.CertificateSha256);
        if (enrolment.IsOpen(config.EnrolmentWindowDays))
            Log.Write($"Enrolment mode: a Host with the lab key may add this computer by itself until a Host connects (at most until {enrolment.FirstStart.AddDays(config.EnrolmentWindowDays):yyyy-MM-dd}).");
        var supervisor = new HelperSupervisor(bridge);

        await Task.WhenAll(
            server.RunAsync(ct),
            discovery.RunAsync(ct),
            bridge.RunAsync(ct),
            supervisor.RunAsync(ct),
            blockHandler.RunWatcherAsync(ct),
            // Finishes a remote sign-in requested before this start: removes the stored password.
            new SignInHandler().RunPendingCleanupAsync(ct));
    }
}
