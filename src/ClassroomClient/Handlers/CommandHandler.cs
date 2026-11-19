// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Runs a single command line (cmd or PowerShell) and returns its output.
/// Not an interactive terminal: stdin is closed, so commands must not wait for input.
/// </summary>
sealed class CommandHandler
{
    const int MaxOutputChars = 200_000;

    static CommandHandler()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // for OEM code pages used by cmd.exe
    }

    public async Task<Response> RunAsync(JsonObject a, CancellationToken ct)
    {
        var shell = a.Str("shell", "cmd").Equals("powershell", StringComparison.OrdinalIgnoreCase) ? "powershell" : "cmd";
        var command = a.Str("command");
        var operation = Guid.NewGuid().ToString("N");
        int timeoutSec = Math.Clamp(a.Int("timeoutSec") ?? 60, 1, 3600);

        ProcessStartInfo psi;
        Encoding encoding;
        if (shell == "powershell")
        {
            // Encoded command avoids every quoting problem; force UTF-8 output.
            var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + command;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}");
            encoding = new UTF8Encoding(false);
        }
        else
        {
            psi = new ProcessStartInfo("cmd.exe", "/c " + command);
            try { encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { encoding = Encoding.UTF8; }
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = encoding;
        psi.StandardErrorEncoding = encoding;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        psi.WorkingDirectory = Directory.Exists(profile) ? profile : Environment.SystemDirectory;

        Process? proc;
        Log.Write($"cmd.run [{operation}]: starting; shell={shell}; command omitted.");
        try { proc = Process.Start(psi); }
        catch (Exception ex)
        {
            Log.Write($"cmd.run [{operation}]: could not start ({ex.GetType().Name}).");
            return Response.Fail($"Could not start {shell} ({ex.GetType().Name}).");
        }
        if (proc is null) return Response.Fail($"Could not start {shell}.");

        using (proc)
        {
            proc.StandardInput.Close();
            var output = new StringBuilder();
            var stdout = Pump(proc.StandardOutput, output);
            var stderr = Pump(proc.StandardError, output);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            bool timedOut = false;
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { proc.Kill(entireProcessTree: true); } catch { }
            }

            // let the readers finish draining (bounded, in case a grandchild keeps the pipe open)
            await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(2000, CancellationToken.None));

            string text;
            lock (output) text = output.ToString();
            var data = new JsonObject
            {
                ["exitCode"] = timedOut ? -1 : proc.ExitCode,
                ["timedOut"] = timedOut,
                ["output"] = text,
            };
            Log.Write($"cmd.run [{operation}]: completed; shell={shell}; exitCode={(timedOut ? -1 : proc.ExitCode)}; timedOut={timedOut}; output omitted.");
            return Response.Success(data);
        }
    }

    static Task Pump(StreamReader reader, StringBuilder sink) => Task.Run(async () =>
    {
        var buf = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            lock (sink)
            {
                if (sink.Length < MaxOutputChars)
                    sink.Append(buf, 0, Math.Min(n, MaxOutputChars - sink.Length));
            }
        }
    });

}
