// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Screen saver settings of the signed-in user (runs in the per-user helper). A screen saver is a
/// program Windows starts in the user's session, so only three sources are accepted: none, a .scr
/// named from the Windows system folder or the managed folder, or a file the Host uploaded to
/// staging whose SHA-256 matches the one the Host sent. An uploaded file is copied into a per-user
/// managed folder before use.
/// </summary>
sealed class ScreenSaverHandler
{
    const uint SPI_SETSCREENSAVETIMEOUT = 0x000F;
    const uint SPI_SETSCREENSAVEACTIVE = 0x0011;
    const uint SPI_SETSCREENSAVESECURE = 0x0077;
    const uint SPIF_UPDATEINIFILE = 0x01;
    const uint SPIF_SENDCHANGE = 0x02;
    const uint Persist = SPIF_UPDATEINIFILE | SPIF_SENDCHANGE;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, IntPtr vparam, uint winIni);

    const string DesktopKey = @"Control Panel\Desktop";
    const string PolicyKey = @"Software\Policies\Microsoft\Windows\Control Panel\Desktop";
    const string ProgramValue = "SCRNSAVE.EXE";
    const string ActiveValue = "ScreenSaveActive";
    const string TimeoutValue = "ScreenSaveTimeOut";
    const string SecureValue = "ScreenSaverIsSecure";

    static string ManagedDirectory => Path.Combine(ClientConfig.UserDir, "ScreenSavers");
    static string StatePath => Path.Combine(ClientConfig.UserDir, "screensaver-state.json");

    /// <summary>The values as they were before Classroom Manager first changed them; null means "value absent".</summary>
    sealed class SavedState
    {
        public string? Program { get; set; }
        public string? Active { get; set; }
        public string? Timeout { get; set; }
        public string? Secure { get; set; }
    }

    public Response Get()
    {
        try { return Response.Success(Describe()); }
        catch (Exception ex) { return Response.Fail("Could not read the screen saver settings: " + ex.Message); }
    }

    public async Task<Response> SetAsync(JsonObject a, CancellationToken ct)
    {
        try
        {
            if (IsPolicyManaged())
                return Response.Fail("A Group Policy controls this user's screen saver, so Classroom Manager cannot change it.");

            var mode = a.Str("mode").ToLowerInvariant();
            string program;
            switch (mode)
            {
                case "none":
                    program = "";
                    break;
                case "installed":
                    program = ResolveAvailable(a.Str("name"));
                    break;
                case "staged":
                    program = await InstallStagedAsync(a, ct);
                    break;
                default:
                    return Response.Fail($"Unknown screen saver mode '{mode}'.");
            }

            SaveOriginalState();
            using (var key = Registry.CurrentUser.CreateSubKey(DesktopKey))
            {
                if (program.Length == 0) key.DeleteValue(ProgramValue, throwOnMissingValue: false);
                else key.SetValue(ProgramValue, program, RegistryValueKind.String);
            }
            Apply(SPI_SETSCREENSAVEACTIVE, program.Length == 0 ? 0u : 1u, "turn the screen saver on or off");
            if (program.Length > 0)
            {
                if (a.Int("timeoutSec") is { } timeout)
                    Apply(SPI_SETSCREENSAVETIMEOUT, (uint)ScreenSaverRules.ClampTimeoutSeconds(timeout), "set the wait time");
                if (a.Bool("secure") is { } secure)
                    Apply(SPI_SETSCREENSAVESECURE, secure ? 1u : 0u, "set sign-in on resume");
            }
            return Response.Success(Describe());
        }
        catch (ScreenSaverException ex) { return Response.Fail(ex.Message); }
        catch (Exception ex) { return Response.Fail("Could not change the screen saver: " + ex.Message); }
    }

    public Response Reset()
    {
        try
        {
            if (!File.Exists(StatePath))
                return Response.Fail("Classroom Manager has not changed this user's screen saver, so there is nothing to restore.");
            if (IsPolicyManaged())
                return Response.Fail("A Group Policy controls this user's screen saver, so Classroom Manager cannot change it.");

            var state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath)) ?? new SavedState();
            using (var key = Registry.CurrentUser.CreateSubKey(DesktopKey))
            {
                Restore(key, ProgramValue, state.Program);
                Restore(key, TimeoutValue, state.Timeout);
                Restore(key, SecureValue, state.Secure);
                Restore(key, ActiveValue, state.Active);
            }
            // Tell Windows about the restored values; the registry above is already the source of truth.
            Apply(SPI_SETSCREENSAVEACTIVE, state.Active == "1" && !string.IsNullOrEmpty(state.Program) ? 1u : 0u,
                "turn the screen saver on or off");
            if (int.TryParse(state.Timeout, out var timeout) && timeout > 0)
                Apply(SPI_SETSCREENSAVETIMEOUT, (uint)timeout, "set the wait time");
            Apply(SPI_SETSCREENSAVESECURE, state.Secure == "1" ? 1u : 0u, "set sign-in on resume");
            File.Delete(StatePath);
            return Response.Success(Describe());
        }
        catch (Exception ex) { return Response.Fail("Could not restore the screen saver: " + ex.Message); }
    }

    // ---- sources ------------------------------------------------------------------------------

    /// <summary>
    /// Finds a .scr by plain file name in the Windows system folder, then in the managed folder.
    /// The Host never supplies a path, so nothing outside those two folders can be selected.
    /// </summary>
    static string ResolveAvailable(string requested)
    {
        var name = ScreenSaverRules.SafeFileName(requested);
        if (name.Length == 0)
            throw new ScreenSaverException("The screen saver name must be a plain file name ending in .scr.");
        foreach (var folder in new[] { Environment.SystemDirectory, ManagedDirectory })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) return path;
        }
        throw new ScreenSaverException($"Screen saver not found on this computer: {name}");
    }

    /// <summary>Verifies an uploaded file against the Host's hash and copies it into the managed folder.</summary>
    static async Task<string> InstallStagedAsync(JsonObject a, CancellationToken ct)
    {
        var name = ScreenSaverRules.SafeFileName(a.Str("name"));
        if (name.Length == 0)
            throw new ScreenSaverException("The screen saver file name must be a plain name ending in .scr.");
        var expected = a.Str("sha256").Trim();
        if (expected.Length != 64)
            throw new ScreenSaverException("The Host did not supply a SHA-256 for the uploaded screen saver.");

        var staging = Path.GetFullPath(ClientConfig.StagingDir).TrimEnd('\\');
        var source = Path.GetFullPath(a.Str("path"));
        if (!source.StartsWith(staging + "\\", StringComparison.OrdinalIgnoreCase))
            throw new ScreenSaverException("The screen saver must be uploaded into the staging folder first.");
        if (!File.Exists(source)) throw new ScreenSaverException("The uploaded screen saver was not found in staging.");
        if (new FileInfo(source).Length > ScreenSaverRules.MaxFileBytes)
            throw new ScreenSaverException("The uploaded file is too large to be a screen saver.");

        // Read once: the bytes that were hashed are exactly the bytes that get installed.
        var bytes = await File.ReadAllBytesAsync(source, ct);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new ScreenSaverException("Checksum mismatch - the uploaded screen saver is incomplete or was altered. Try again.");
        if (bytes.Length < 2 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            throw new ScreenSaverException("That file is not a Windows screen saver program (missing MZ header).");

        Directory.CreateDirectory(ManagedDirectory);
        var target = Path.Combine(ManagedDirectory, name);
        try { await File.WriteAllBytesAsync(target, bytes, ct); }
        catch (IOException ex)
        {
            throw new ScreenSaverException($"Could not replace {name}; it may be running right now. {ex.Message}");
        }
        return target;
    }

    // ---- state --------------------------------------------------------------------------------

    static JsonObject Describe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
        var program = key?.GetValue(ProgramValue) as string ?? "";
        var active = (key?.GetValue(ActiveValue) as string ?? "0") == "1" && program.Length > 0;
        _ = int.TryParse(key?.GetValue(TimeoutValue) as string, out var timeout);
        return new JsonObject
        {
            ["active"] = active,
            ["path"] = program,
            ["name"] = program.Length == 0 ? "" : Path.GetFileName(program),
            ["timeoutSec"] = timeout,
            ["secure"] = (key?.GetValue(SecureValue) as string ?? "0") == "1",
            ["policyManaged"] = IsPolicyManaged(),
            ["canRestore"] = File.Exists(StatePath),
            ["installed"] = List(Environment.SystemDirectory),
            ["managed"] = List(ManagedDirectory),
        };
    }

    static JsonArray List(string folder)
    {
        var items = new JsonArray();
        if (!Directory.Exists(folder)) return items;
        foreach (var file in Directory.EnumerateFiles(folder, "*" + ScreenSaverRules.Extension)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            items.Add(new JsonObject { ["name"] = Path.GetFileName(file), ["path"] = file });
        return items;
    }

    static bool IsPolicyManaged()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PolicyKey);
        return key is not null && new[] { ProgramValue, ActiveValue, TimeoutValue, SecureValue }
            .Any(value => key.GetValue(value) is not null);
    }

    static void SaveOriginalState()
    {
        if (File.Exists(StatePath)) return;
        using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
        var state = new SavedState
        {
            Program = key?.GetValue(ProgramValue) as string,
            Active = key?.GetValue(ActiveValue) as string,
            Timeout = key?.GetValue(TimeoutValue) as string,
            Secure = key?.GetValue(SecureValue) as string,
        };
        Directory.CreateDirectory(ClientConfig.UserDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
    }

    static void Restore(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    /// <summary>
    /// Changes one screen saver setting. Windows refuses these while the computer is idle in some way
    /// (seen in a lab at night as error 329, "operation in progress": display off, screen saver running),
    /// which is when a teacher is most likely to ask. So a refusal is answered in steps: wake the display
    /// and stop a running screen saver, ask again; ask without telling other programs; and at last store
    /// the setting where Windows keeps it, which then holds from the user's next sign-in.
    /// </summary>
    static void Apply(uint action, uint value, string what)
    {
        if (SystemParametersInfo(action, value, IntPtr.Zero, Persist)) return;
        int error = Marshal.GetLastWin32Error();

        DisplayWakeHandler.StopRunningScreenSaver();
        DisplayWakeHandler.WakeDisplay();
        Thread.Sleep(700);
        if (SystemParametersInfo(action, value, IntPtr.Zero, Persist))
        {
            Log.Write($"screensaver: Windows refused to {what} (error {error}) until the display was woken.");
            return;
        }
        if (SystemParametersInfo(action, value, IntPtr.Zero, SPIF_UPDATEINIFILE))
        {
            Log.Write($"screensaver: Windows refused to {what} (error {error}) with a notice to other programs; done without it.");
            return;
        }
        int second = Marshal.GetLastWin32Error();
        if (StoredName(action) is not { } name)
            throw new ScreenSaverException($"Windows refused to {what} (error {second}).");
        using (var key = Registry.CurrentUser.CreateSubKey(DesktopKey))
            key.SetValue(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
        Log.Write($"screensaver: Windows refused to {what} (errors {error}, {second}); the setting was stored and holds from the next sign-in.");
    }

    /// <summary>Where Windows keeps a setting between sign-ins.</summary>
    static string? StoredName(uint action) => action switch
    {
        SPI_SETSCREENSAVEACTIVE => ActiveValue,
        SPI_SETSCREENSAVETIMEOUT => TimeoutValue,
        SPI_SETSCREENSAVESECURE => SecureValue,
        _ => null,
    };

    sealed class ScreenSaverException(string message) : Exception(message);
}
