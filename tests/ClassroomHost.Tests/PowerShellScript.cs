// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.IO;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>Which PowerShell runs a script under test.</summary>
public enum PowerShellEdition
{
    /// <summary>Windows PowerShell 5.1 (powershell.exe): the only PowerShell on a school PC, and the default window on Windows.</summary>
    Windows,
    /// <summary>PowerShell 7, pinned in dotnet-tools.json and run with `dotnet tool run pwsh`: the same version on every machine.</summary>
    Core,
}

/// <summary>
/// Runs a script for a test, with the edition chosen by where the script itself runs:
/// - setup kit and deploy\ scripts run on school PCs, so they are tested under <see cref="PowerShellEdition.Windows"/> only;
/// - release, coverage and register scripts run on the teacher PC (often from Windows PowerShell) and in CI (PowerShell 7),
///   so they are tested under both (<see cref="Both"/>).
/// </summary>
static class PowerShellScript
{
    public static TheoryData<PowerShellEdition> Both => new() { PowerShellEdition.Windows, PowerShellEdition.Core };

    public static async Task<(int ExitCode, string Output, string Errors)> RunAsync(PowerShellEdition edition, string script, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var root = StringExtractor.RepositoryRoot();
        var start = new ProcessStartInfo(edition == PowerShellEdition.Windows ? "powershell.exe" : "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            // dotnet finds the tool manifest (dotnet-tools.json) from the folder it is started in.
            WorkingDirectory = root,
        };
        if (edition == PowerShellEdition.Core) foreach (var argument in new[] { "tool", "run", "pwsh", "--" }) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, script) })
            start.ArgumentList.Add(argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // A PowerShell started from the other edition inherits its module path and loads modules it cannot use: Windows PowerShell under
        // PowerShell 7 lost Get-FileHash on the CI runner (2026-10-24). Without the variable each edition uses its own module folders.
        start.Environment.Remove("PSModulePath");
        start.Environment.Remove("GITHUB_STEP_SUMMARY");   // a script under test must not write into the CI job's summary

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var text = await errors;
        if (edition == PowerShellEdition.Core && process.ExitCode != 0 && text.Contains("dotnet tool restore", StringComparison.OrdinalIgnoreCase))
            throw new Xunit.Sdk.XunitException("PowerShell 7 is not restored here. Run `dotnet tool restore` in the repository once.\n" + text);
        return (process.ExitCode, await output, text);
    }
}

public sealed class PowerShellScriptTests
{
    [Fact]
    public void Tests_run_scripts_through_PowerShellScript_not_their_own_process()
    {
        var tests = Path.Combine(StringExtractor.RepositoryRoot(), "tests");
        var own = Directory.GetFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.EndsWith("PowerShellScript.cs"))
            .Where(path => File.ReadAllText(path).Contains("ProcessStartInfo(\"powershell.exe\")") || File.ReadAllText(path).Contains("ProcessStartInfo(\"pwsh"))
            .Select(Path.GetFileName).ToList();

        Assert.True(own.Count == 0, "Use PowerShellScript.RunAsync, which picks the edition and clears the inherited module path, in: " + string.Join(", ", own));
    }

    [Theory]
    [InlineData(PowerShellEdition.Windows, "Desktop", "5.1")]
    [InlineData(PowerShellEdition.Core, "Core", "7.6.8")]
    public async Task Each_edition_is_the_one_asked_for_and_finds_its_own_commands(PowerShellEdition edition, string psEdition, string version)
    {
        var script = Path.Combine(Path.GetTempPath(), "classroom-edition-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, "$PSVersionTable.PSEdition\n$PSVersionTable.PSVersion.ToString()\n(Get-FileHash -Path $PSCommandPath).Algorithm\n");
        try
        {
            var (exit, output, errors) = await PowerShellScript.RunAsync(edition, script, [], TimeSpan.FromSeconds(60));

            Assert.True(exit == 0, output + errors);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(psEdition, lines[0]);
            Assert.StartsWith(version, lines[1]);
            Assert.Equal("SHA256", lines[2]);
        }
        finally { File.Delete(script); }
    }
}
