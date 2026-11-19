// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Windows Update operations through the supported Microsoft.Update session API.</summary>
sealed class WindowsUpdateHandler
{
    const string PauseKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    // Updates Windows offers for optional installation are asked for by name as well: the documented
    // default of a search is updates deployed for installation only.
    const string Pending = "IsInstalled=0 and IsHidden=0";
    const string PendingWithOptional = "(" + Pending + ") or (" + Pending + " and DeploymentAction='OptionalInstallation')";
    const int OptionalInstallation = 4;

    public Task<Response> ScanAsync(JsonObject args, CancellationToken ct) => Task.Run(() => Scan(args), ct);

    public Task<Response> InstallAsync(JsonObject args, CancellationToken ct) => Task.Run(() => Install(args), ct);

    static Response Scan(JsonObject args)
    {
        try
        {
            dynamic session = Create("Microsoft.Update.Session");
            dynamic searcher = session.CreateUpdateSearcher();
            // Keep optional updates in the normal pending set. The flag is explicit so
            // newer clients can offer a stricter view without changing this command.
            var includeOptional = args.Bool("includeOptional") ?? true;
            dynamic result = searcher.Search(includeOptional ? PendingWithOptional : Pending + " and Type='Software'");
            var updates = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < (int)result.Updates.Count; i++)
            {
                dynamic update = result.Updates.Item(i);
                if (seen.Add((string)update.Identity.UpdateID)) updates.Add(UpdateJson(update));
            }
            return Response.Success(new JsonObject { ["updates"] = updates });
        }
        catch (Exception ex) { return Response.Fail("Windows Update scan failed: " + ex.Message); }
    }

    static Response Install(JsonObject args)
    {
        try
        {
            var requested = args["ids"]?.AsArray().Select(node => node?.GetValue<string>() ?? "")
                .Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var all = args.Bool("all") ?? false;
            if (!all && requested.Count == 0) return Response.Fail("Select at least one update or choose Update all.");

            dynamic session = Create("Microsoft.Update.Session");
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search(PendingWithOptional);
            dynamic collection = Create("Microsoft.Update.UpdateColl");
            var selected = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < (int)result.Updates.Count; i++)
            {
                dynamic update = result.Updates.Item(i);
                var id = (string)update.Identity.UpdateID;
                if (!seen.Add(id)) continue;
                if (!all && !requested.Contains(id)) continue;
                try { update.AcceptEula(); } catch { }
                collection.Add(update);
                selected++;
            }
            if (selected == 0) return Response.Fail("The selected updates are no longer available.");

            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = collection;
            dynamic download = downloader.Download();
            if ((int)download.ResultCode is 4 or 5) return Response.Fail("Windows Update could not download the selected updates.");

            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = collection;
            dynamic install = installer.Install();
            var reboot = (bool)install.RebootRequired;
            Log.Write($"windows.update.install: selected={selected}, result={install.ResultCode}, reboot={reboot}.");
            return Response.Success(new JsonObject
            {
                ["selected"] = selected,
                ["rebootRequired"] = reboot,
                ["resultCode"] = install.ResultCode.ToString(),
            });
        }
        catch (Exception ex) { return Response.Fail("Windows Update install failed: " + ex.Message); }
    }

    public Response Pause(JsonObject args)
    {
        var days = Math.Clamp(args.Int("days") ?? 7, 1, 35);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var until = now.AddDays(days);
            using var key = Registry.LocalMachine.CreateSubKey(PauseKey);
            key.SetValue("PauseUpdatesStartTime", now.ToString("O"), RegistryValueKind.String);
            key.SetValue("PauseUpdatesExpiryTime", until.ToString("O"), RegistryValueKind.String);
            Log.Write($"windows.update.pause: {days} day(s).");
            return Response.Success(StatusData(until));
        }
        catch (Exception ex) { return Response.Fail("Windows Update could not be paused (administrator rights may be required): " + ex.Message); }
    }

    public Response Resume()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PauseKey, writable: true);
            key?.DeleteValue("PauseUpdatesStartTime", false);
            key?.DeleteValue("PauseUpdatesExpiryTime", false);
            Log.Write("windows.update.resume.");
            return Response.Success(StatusData(null));
        }
        catch (Exception ex) { return Response.Fail("Windows Update could not be resumed: " + ex.Message); }
    }

    public Response Status()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PauseKey);
            var raw = key?.GetValue("PauseUpdatesExpiryTime")?.ToString();
            var until = DateTimeOffset.TryParse(raw, out var parsed) && parsed > DateTimeOffset.UtcNow
                ? parsed.ToUniversalTime()
                : (DateTimeOffset?)null;
            return Response.Success(StatusData(until));
        }
        catch (Exception ex) { return Response.Fail("Windows Update pause status could not be read: " + ex.Message); }
    }

    static JsonObject StatusData(DateTimeOffset? until)
    {
        var data = new JsonObject
        {
            ["paused"] = until is not null,
            ["untilUtc"] = until?.ToUniversalTime().ToString("O") ?? "",
        };
        // The Windows version is extra information: a failure to read it must not hide the pause state.
        try
        {
            using var windows = Registry.LocalMachine.OpenSubKey(VersionKey);
            var build = int.TryParse(windows?.GetValue("CurrentBuild")?.ToString(), out var number) ? number : 0;
            data[WindowsRelease.Product] = WindowsRelease.ProductOfBuild(build);
            data[WindowsRelease.Version] = windows?.GetValue("DisplayVersion")?.ToString() ?? "";
            data[WindowsRelease.Build] = build == 0 ? "" : $"{build}.{windows?.GetValue("UBR") ?? 0}";
            data[WindowsRelease.Edition] = windows?.GetValue("EditionID")?.ToString() ?? "";
            using var policy = Registry.LocalMachine.OpenSubKey(PolicyKey);
            data[WindowsRelease.TargetVersion] = policy?.GetValue("TargetReleaseVersion") is int on && on == 1
                ? policy.GetValue("TargetReleaseVersionInfo")?.ToString() ?? "" : "";
            dynamic system = Create("Microsoft.Update.SystemInfo");
            data[WindowsRelease.RestartPending] = (bool)system.RebootRequired;
        }
        catch (Exception ex) { Log.Write($"windows.update.status: Windows version not read: {ex.Message}"); }
        return data;
    }

    /// <summary>
    /// Sets the Windows version this computer moves to and then stays on, or removes that target.
    /// Windows then offers the new version as an ordinary update instead of leaving it behind a
    /// "Download and install" link in Settings.
    /// </summary>
    public Response SetTarget(JsonObject args)
    {
        var asked = args.Str("version", "");
        var version = WindowsRelease.Normalize(asked);
        if (asked.Trim().Length > 0 && version.Length == 0) return Response.Fail($"'{asked}' is not a Windows version name such as 25H2.");
        try
        {
            using var windows = Registry.LocalMachine.OpenSubKey(VersionKey);
            var current = windows?.GetValue("DisplayVersion")?.ToString() ?? "";
            if (version.Length == 0)
            {
                using var existing = Registry.LocalMachine.OpenSubKey(PolicyKey, writable: true);
                foreach (var name in new[] { "TargetReleaseVersion", "TargetReleaseVersionInfo", "ProductVersion" }) existing?.DeleteValue(name, false);
                Log.Write("windows.update.target.set: target version removed.");
            }
            else
            {
                var edition = windows?.GetValue("EditionID")?.ToString() ?? "";
                if (!WindowsRelease.EditionTakesTarget(edition))
                    return Response.Fail("This computer runs a Home edition of Windows, which ignores a target version.");
                if (WindowsRelease.Compare(version, current) < 0)
                    return Response.Fail($"This computer already runs {current}; Windows cannot go back to {version}.");
                var build = int.TryParse(windows?.GetValue("CurrentBuild")?.ToString(), out var number) ? number : 0;
                using var policy = Registry.LocalMachine.CreateSubKey(PolicyKey);
                policy.SetValue("TargetReleaseVersion", 1, RegistryValueKind.DWord);
                policy.SetValue("ProductVersion", WindowsRelease.ProductOfBuild(build), RegistryValueKind.String);
                policy.SetValue("TargetReleaseVersionInfo", version, RegistryValueKind.String);
                Log.Write($"windows.update.target.set: target version {version} (now {current}).");
            }
            StartWindowsScan();
            return Status();
        }
        catch (Exception ex) { return Response.Fail("The Windows target version could not be changed: " + ex.Message); }
    }

    /// <summary>Asks Windows to look for updates now, so a changed target is noticed without waiting for its own schedule.</summary>
    static void StartWindowsScan()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "UsoClient.exe"), "StartScan")
                { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (Exception ex) { Log.Write($"windows.update.target.set: scan not started: {ex.Message}"); }
    }

    public Response Restart()
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                "/r /t 60 /d p:2:17 /c \"Classroom Manager scheduled a restart to finish Windows Update.\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            Log.Write("windows.update.restart: restart scheduled in 60 seconds.");
            return Response.Success(new JsonObject { ["scheduledSeconds"] = 60 });
        }
        catch (Exception ex) { return Response.Fail("Windows could not schedule the update restart: " + ex.Message); }
    }

    static JsonObject UpdateJson(dynamic update)
    {
        var kb = new JsonArray();
        var rebootBehavior = 0;
        var categories = new List<string>();
        try
        {
            for (int i = 0; i < (int)update.KBArticleIDs.Count; i++) kb.Add((string)update.KBArticleIDs.Item(i));
            rebootBehavior = Convert.ToInt32(update.InstallationBehavior.RebootBehavior);
            for (int i = 0; i < (int)update.Categories.Count; i++) categories.Add((string)update.Categories.Item(i).CategoryID);
        }
        catch { }
        return new JsonObject
        {
            ["id"] = (string)update.Identity.UpdateID,
            ["title"] = (string)update.Title,
            ["kb"] = kb,
            ["size"] = (long)update.MaxDownloadSize,
            ["downloaded"] = (bool)update.IsDownloaded,
            ["requiresRestart"] = rebootBehavior is 1 or 2,
            ["optional"] = Optional(update),
            ["feature"] = WindowsRelease.IsFeatureUpdate((string)update.Title, categories),
        };
    }

    static bool Optional(dynamic update)
    {
        try
        {
            // Windows' own mark for "offered, installs only when someone asks"; older builds leave it to the title.
            if (Convert.ToInt32(update.DeploymentAction) == OptionalInstallation) return true;
            var title = ((string)update.Title).ToLowerInvariant();
            return title.Contains("preview") || title.Contains("optional") || title.Contains("driver");
        }
        catch { return false; }
    }

    static dynamic Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException($"Windows component {progId} is unavailable."))!;
}
