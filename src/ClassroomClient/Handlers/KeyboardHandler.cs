// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// The signed-in user's keyboard layouts, through Windows' own language list cmdlets, so the layouts
/// show in the taskbar's language switcher exactly as if the student had added them in Settings.
/// </summary>
sealed class KeyboardHandler
{
    public async Task<Response> ListAsync(CancellationToken ct)
    {
        const string script =
            "$list = Get-WinUserLanguageList; " +
            "$default = (Get-WinDefaultInputMethodOverride).InputMethodTip; " +
            "[pscustomobject]@{ default = \"$default\"; layouts = @($list | ForEach-Object { $tag = $_.LanguageTag; " +
            "$_.InputMethodTips | ForEach-Object { [pscustomobject]@{ tip = $_; language = $tag } } }) } | ConvertTo-Json -Depth 4 -Compress";
        var result = await RunAsync(script, 60, ct);
        if (!result.Ok) return result;
        try
        {
            var read = JsonNode.Parse(result.Data["output"]!.GetValue<string>())!.AsObject();
            var layouts = new JsonArray();
            foreach (var item in AsArray(read["layouts"]))
            {
                var tip = KeyboardLayouts.Normalize(item!["tip"]!.GetValue<string>());
                layouts.Add(new JsonObject
                {
                    ["tip"] = tip,
                    ["language"] = item["language"]?.GetValue<string>() ?? KeyboardLayouts.LanguageOf(tip),
                    ["name"] = LayoutName(tip),
                });
            }
            var chosen = read["default"]?.GetValue<string>() ?? "";
            return Response.Success(new JsonObject
            {
                [KeyboardLayouts.Layouts] = layouts,
                [KeyboardLayouts.Default] = KeyboardLayouts.IsTip(chosen) ? KeyboardLayouts.Normalize(chosen) : "",
            });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException)
        {
            return Response.Fail("Could not read the keyboard layouts Windows reported: " + ex.Message);
        }
    }

    public async Task<Response> SetAsync(JsonObject args, CancellationToken ct)
    {
        var add = args.Str(KeyboardLayouts.Add, "").Trim();
        var remove = args.Str(KeyboardLayouts.Remove, "").Trim();
        var makeDefault = args.Str(KeyboardLayouts.MakeDefault, "").Trim();
        foreach (var tip in new[] { add, remove, makeDefault }.Where(tip => tip.Length > 0))
            if (!KeyboardLayouts.IsTip(tip)) return Response.Fail($"'{tip}' is not a keyboard layout name such as 0427:00010427.");
        if (add.Length + remove.Length + makeDefault.Length == 0) return Response.Fail("Choose a layout to add, remove or make the default.");
        if (remove.Length > 0 && KeyboardLayouts.Normalize(remove) == KeyboardLayouts.Normalize(makeDefault))
            return Response.Fail("A layout cannot be removed and made the default at once.");

        // A layout is added to its language, which is added to the list first when it is not there.
        var toAdd = makeDefault.Length > 0 ? makeDefault : add;
        var lines = new List<string> { "$list = Get-WinUserLanguageList" };
        foreach (var tip in new[] { add, makeDefault }.Where(tip => tip.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var language = KeyboardLayouts.LanguageOf(tip);
            if (language.Length == 0) return Response.Fail($"Windows does not know the language of the layout {tip}.");
            lines.Add($"$language = $list | Where-Object {{ $_.LanguageTag -eq '{language}' }} | Select-Object -First 1");
            lines.Add($"if ($null -eq $language) {{ $list.Add('{language}'); $language = $list | Where-Object {{ $_.LanguageTag -eq '{language}' }} | Select-Object -First 1; $language.InputMethodTips.Clear() }}");
            lines.Add($"if (-not ($language.InputMethodTips -contains '{tip}')) {{ $language.InputMethodTips.Add('{tip}') }}");
        }
        if (remove.Length > 0)
        {
            lines.Add($"$owner = $list | Where-Object {{ $_.InputMethodTips -contains '{remove}' }} | Select-Object -First 1");
            lines.Add($"if ($null -eq $owner) {{ Write-Error 'The layout {remove} is not installed for this user.'; exit 3 }}");
            lines.Add("if (@($list | ForEach-Object { $_.InputMethodTips }).Count -le 1) { Write-Error 'The last keyboard layout cannot be removed.'; exit 4 }");
            lines.Add($"[void]$owner.InputMethodTips.Remove('{remove}')");
            // A language left without a layout is taken out too, as Settings does.
            lines.Add("if ($owner.InputMethodTips.Count -eq 0) { [void]$list.Remove($owner) }");
        }
        lines.Add("Set-WinUserLanguageList $list -Force -WarningAction SilentlyContinue");
        if (makeDefault.Length > 0) lines.Add($"Set-WinDefaultInputMethodOverride -InputTip '{makeDefault}'");
        var result = await RunAsync(string.Join("; ", lines), 120, ct);
        if (!result.Ok) return result;
        var done = new List<string>();
        if (add.Length > 0) done.Add("added " + add);
        if (remove.Length > 0) done.Add("removed " + remove);
        if (makeDefault.Length > 0) done.Add("default " + makeDefault);
        Log.Write("keyboard.set: " + string.Join(", ", done));
        return Response.Success(new JsonObject { ["summary"] = string.Join(", ", done), ["added"] = toAdd });
    }

    static IEnumerable<JsonNode?> AsArray(JsonNode? node) => node switch
    {
        JsonArray array => array,
        JsonObject single => [single],
        _ => [],
    };

    /// <summary>The name Windows gives a keyboard layout, from its registration; the tip itself when there is none.</summary>
    static string LayoutName(string tip)
    {
        var id = KeyboardLayouts.LayoutId(tip);
        if (id.Length == 0) return tip;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\" + id);
            return key?.GetValue("Layout Text") as string ?? tip;
        }
        catch (Exception) { return tip; }
    }

    static async Task<Response> RunAsync(string script, int timeoutSeconds, CancellationToken ct)
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
                return Response.Fail($"The keyboard layout change timed out after {timeoutSeconds} seconds.");
            }
            var output = (await stdout).Trim();
            var error = (await stderr).Trim();
            if (process.ExitCode != 0)
                return Response.Fail(error.Length > 0 ? error : $"Windows PowerShell exited with code {process.ExitCode}.");
            return Response.Success(new JsonObject { ["output"] = output });
        }
    }
}
