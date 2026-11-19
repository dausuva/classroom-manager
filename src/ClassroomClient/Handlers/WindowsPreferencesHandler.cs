// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Changes supported per-user Windows appearance and display-language preferences.</summary>
sealed class WindowsPreferencesHandler
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string DefaultLightTheme = "builtin:windows-light";
    const string DefaultDarkTheme = "builtin:windows-dark";
    const uint WmSettingChange = 0x001A;
    const uint SmtoAbortIfHung = 0x0002;
    static readonly Regex LanguageTag = new(
        @"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    public Response Get() => Response.Success(new JsonObject
    {
        ["interfaceLanguage"] = CultureInfo.CurrentUICulture.Name,
        ["installedInterfaceLanguage"] = CultureInfo.InstalledUICulture.Name,
        [AccentColours.Accent] = CurrentAccent(),
    });

    /// <summary>The accent colour of the signed-in user as six digits, "" when Windows keeps none.</summary>
    static string CurrentAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AccentColours.ExplorerAccentKey);
            return key?.GetValue("AccentColorMenu") is int abgr ? AccentColours.RgbFromAbgr(abgr) : "";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Writes the colour values; the user's own registry hive, so no administrator rights are needed.</summary>
    static void WriteAccent(IEnumerable<AccentColours.RegistryValue> values)
    {
        foreach (var value in values)
        {
            using var key = Registry.CurrentUser.CreateSubKey(value.Key);
            key.SetValue(value.Name, value.Value, value.Value is byte[] ? RegistryValueKind.Binary : RegistryValueKind.DWord);
        }
        Broadcast("ImmersiveColorSet");
        Broadcast("WindowsThemeElement");
    }

    public async Task<Response> SetAsync(JsonObject args, CancellationToken ct)
    {
        var changed = new List<string>();
        var theme = args["theme"]?.GetValue<string>()?.ToLowerInvariant() ?? "unchanged";
        if (theme != "unchanged")
        {
            var light = theme switch
            {
                "light" => 1,
                "dark" => 0,
                "default" => 1,
                _ => -1,
            };
            if (light < 0) return Response.Fail("Theme must be light, dark, default, or unchanged.");

            using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
            Broadcast("ImmersiveColorSet");
            Broadcast("WindowsThemeElement");
            changed.Add(theme == "default" ? "Windows default light mode" : $"{theme} mode");
        }

        var accent = args[AccentColours.Accent]?.GetValue<string>()?.Trim() ?? AccentColours.Unchanged;
        var titleBars = args[AccentColours.TitleBars]?.GetValue<bool>();
        if (accent.Equals(AccentColours.Default, StringComparison.OrdinalIgnoreCase))
        {
            WriteAccent(AccentColours.ValuesForDefault(titleBars));
            changed.Add("Windows default accent colour");
        }
        else if (!accent.Equals(AccentColours.Unchanged, StringComparison.OrdinalIgnoreCase))
        {
            var rgb = AccentColours.InPalette(accent);
            if (rgb is null) return Response.Fail("The accent colour must be one of the colours Windows offers, written as #RRGGBB.");
            WriteAccent(AccentColours.ValuesFor(rgb, titleBars));
            changed.Add($"accent colour #{rgb}");
        }
        else if (titleBars is bool bars)
        {
            using var dwm = Registry.CurrentUser.CreateSubKey(AccentColours.DwmKey);
            dwm.SetValue("ColorPrevalence", bars ? 1 : 0, RegistryValueKind.DWord);
            Broadcast("ImmersiveColorSet");
            changed.Add(bars ? "accent colour on title bars" : "no accent colour on title bars");
        }

        var resetLanguage = args["resetLanguage"]?.GetValue<bool>() ?? false;
        var language = args["language"]?.GetValue<string>()?.Trim() ?? "";
        if (resetLanguage && language.Length > 0)
            return Response.Fail("Choose either a language or reset the language override, not both.");

        if (resetLanguage)
        {
            var result = await RunPowerShellAsync("Set-WinUILanguageOverride", 120, ct);
            if (!result.Ok) return result;
            changed.Add("Windows default interface language");
        }
        else if (language.Length > 0)
        {
            if (!IsLanguageTag(language)) return Response.Fail("Enter a valid BCP-47 language tag such as en-US, lt-LT, or uk-UA.");
            var script =
                $"$tag='{language}'; " +
                "$list=Get-WinUserLanguageList; " +
                "if (-not ($list.LanguageTag -contains $tag)) { $list.Add($tag); Set-WinUserLanguageList $list -Force }; " +
                "Set-WinUILanguageOverride -Language $tag";
            var result = await RunPowerShellAsync(script, 180, ct);
            if (!result.Ok) return result;
            changed.Add($"interface language {language} (applies after sign-out)");
        }

        return changed.Count == 0
            ? Response.Fail("No Windows preference was selected.")
            : Response.Success(new JsonObject { ["summary"] = string.Join(", ", changed) });
    }

    public async Task<Response> InstallLanguageAsync(JsonObject args, CancellationToken ct)
    {
        var language = args.Str("language").Trim();
        if (!IsLanguageTag(language)) return Response.Fail("Enter a valid BCP-47 language tag such as en-US, lt-LT, or uk-UA.");

        // LanguagePackManagement is built into current Windows 11 client editions and downloads the
        // complete display-language resources. Windows 10 can apply an already-installed language,
        // but adding a display pack requires the matching language-pack/FOD deployment source.
        var script =
            $"$command=Get-Command Install-Language -ErrorAction SilentlyContinue; " +
            "if ($null -eq $command) { Write-Error 'Automatic display-language installation is unavailable on this Windows version. Install the Windows 10 language pack and matching Features on Demand through Settings or your deployment source, then apply it again.'; exit 50 }; " +
            $"Install-Language -Language '{language}' -ErrorAction Stop";
        var response = await RunPowerShellAsync(script, 1800, ct);
        if (!response.Ok) return response;
        response.Data["language"] = language;
        return response;
    }

    public Response ListThemes()
    {
        try
        {
            var current = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes")
                ?.GetValue("CurrentTheme") as string ?? "";
            var available = AvailableThemes();
            var defaultPath = DefaultThemePath();
            var isDefault = current.Equals(defaultPath, StringComparison.OrdinalIgnoreCase);
            using var personalize = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var lightMode = Convert.ToInt32(personalize?.GetValue("AppsUseLightTheme", 1), CultureInfo.InvariantCulture) != 0;
            var themes = new List<JsonObject>();
            if (File.Exists(defaultPath))
            {
                themes.Add(BuiltInTheme(DefaultLightTheme, "Windows default — light", isDefault && lightMode));
                themes.Add(BuiltInTheme(DefaultDarkTheme, "Windows default — dark", isDefault && !lightMode));
            }
            themes.AddRange(available.Where(path => !path.Equals(defaultPath, StringComparison.OrdinalIgnoreCase))
                .Select(path => new JsonObject
                {
                    ["id"] = path,
                    ["name"] = Path.GetFileName(path).Equals("aero.theme", StringComparison.OrdinalIgnoreCase)
                        ? "Windows default"
                        : Path.GetFileNameWithoutExtension(path),
                    ["location"] = path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        StringComparison.OrdinalIgnoreCase) ? "Windows" : "Current user",
                    ["current"] = path.Equals(current, StringComparison.OrdinalIgnoreCase),
                }));
            return Response.Success(new JsonObject
            {
                ["themes"] = new JsonArray(themes.Cast<JsonNode>().ToArray()),
            });
        }
        catch (Exception ex) { return Response.Fail("Could not list Windows themes: " + ex.Message); }
    }

    public async Task<Response> ApplyThemeAsync(JsonObject args, CancellationToken ct)
    {
        var id = args.Str("id");
        if (id is DefaultLightTheme or DefaultDarkTheme)
            return await ApplyBuiltInThemeAsync(id == DefaultLightTheme, ct);
        var theme = AvailableThemes().FirstOrDefault(path => path.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (theme is null) return Response.Fail("The selected Windows theme is no longer available.");
        try
        {
            Process.Start(new ProcessStartInfo(theme) { UseShellExecute = true });
            return Response.Success(new JsonObject { ["theme"] = Path.GetFileNameWithoutExtension(theme) });
        }
        catch (Exception ex) { return Response.Fail("Windows could not apply the selected theme: " + ex.Message); }
    }

    static JsonObject BuiltInTheme(string id, string name, bool current) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["location"] = "Windows preset",
        ["current"] = current,
    };

    async Task<Response> ApplyBuiltInThemeAsync(bool light, CancellationToken ct)
    {
        var theme = DefaultThemePath();
        if (!File.Exists(theme)) return Response.Fail("The Windows default theme file is not available on this computer.");
        try
        {
            Process.Start(new ProcessStartInfo(theme) { UseShellExecute = true });
            // Applying a .theme file is asynchronous. Set the requested color mode after Windows has
            // loaded the base theme so the theme file cannot overwrite the light/dark choice.
            await Task.Delay(1200, ct);
            using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            key.SetValue("AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
            Broadcast("ImmersiveColorSet");
            Broadcast("WindowsThemeElement");
            return Response.Success(new JsonObject { ["theme"] = light ? "Windows default — light" : "Windows default — dark" });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Response.Fail("Windows could not apply the default theme: " + ex.Message); }
    }

    static string DefaultThemePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Resources\Themes\aero.theme");

    static IReadOnlyList<string> AvailableThemes()
    {
        var directories = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Resources\Themes"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Themes"),
        };
        return directories.Where(Directory.Exists)
            .SelectMany(SafeThemeFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    static IEnumerable<string> SafeThemeFiles(string directory)
    {
        try { return Directory.EnumerateFiles(directory, "*.theme", SearchOption.AllDirectories).ToArray(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    static bool IsLanguageTag(string value)
    {
        if (!LanguageTag.IsMatch(value)) return false;
        try { _ = CultureInfo.GetCultureInfo(value); return true; }
        catch (CultureNotFoundException) { return false; }
    }

    static void Broadcast(string setting)
    {
        _ = SendMessageTimeout(new IntPtr(0xffff), WmSettingChange, UIntPtr.Zero, setting,
            SmtoAbortIfHung, 1000, out _);
    }

    static async Task<Response> RunPowerShellAsync(string script, int timeoutSeconds, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process? process;
        try { process = Process.Start(psi); }
        catch (Exception ex) { return Response.Fail("Could not start Windows PowerShell: " + ex.Message); }
        if (process is null) return Response.Fail("Could not start Windows PowerShell.");

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return Response.Fail($"Windows language operation timed out after {timeoutSeconds} seconds.");
            }

            var output = (await stdout).Trim();
            var error = (await stderr).Trim();
            if (process.ExitCode != 0)
                return Response.Fail(error.Length > 0 ? error : $"Windows PowerShell exited with code {process.ExitCode}.");
            return Response.Success(new JsonObject { ["output"] = output });
        }
    }
}
