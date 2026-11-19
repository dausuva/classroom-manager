// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Interactive shell over redirected pipes. The host polls term.read for new output and sends input with term.write.
/// Works for cmd and PowerShell; programs that need a real console (full-screen editors, some REPLs) will not.
/// Sessions live in whichever process handled term.start (service = elevated, helper = standard user).
/// </summary>
sealed class TerminalHandler
{
    sealed class Session
    {
        public required Process Proc { get; init; }
        public readonly StringBuilder Buffer = new();
        public DateTime LastUsed = DateTime.UtcNow;
    }

    static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);
    const int MaxBufferChars = 400_000;
    readonly ConcurrentDictionary<string, Session> _sessions = new();

    static TerminalHandler() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    readonly System.Threading.Timer _reaper;

    public TerminalHandler()
    {
        // close shells whose host window went away without saying goodbye
        _reaper = new System.Threading.Timer(_ => Reap(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public Response Start(JsonObject a)
    {
        Reap();
        var shell = a.Str("shell", "cmd").ToLowerInvariant();

        Encoding enc;
        try { enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { enc = Encoding.UTF8; }

        var psi = shell == "powershell"
            ? new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -ExecutionPolicy Bypass")
            : new ProcessStartInfo("cmd.exe");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = enc;
        psi.StandardErrorEncoding = enc;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        psi.WorkingDirectory = Directory.Exists(profile) ? profile : Environment.SystemDirectory;

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return Response.Fail($"Could not start {shell}: {ex.Message}"); }
        if (proc is null) return Response.Fail($"Could not start {shell}.");

        var id = Guid.NewGuid().ToString("N");
        var session = new Session { Proc = proc };
        _sessions[id] = session;
        Pump(proc.StandardOutput, session);
        Pump(proc.StandardError, session);
        Log.Write($"Terminal {id[..6]} started ({shell})");
        return Response.Success(new JsonObject { ["id"] = id });
    }

    public async Task<Response> WriteAsync(JsonObject a)
    {
        if (!_sessions.TryGetValue(a.Str("id"), out var s)) return Response.Fail("Terminal session not found.");
        s.LastUsed = DateTime.UtcNow;
        try
        {
            await s.Proc.StandardInput.WriteAsync(a.Str("text"));
            await s.Proc.StandardInput.FlushAsync();
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail(ex.Message); }
    }

    public Response Read(JsonObject a)
    {
        if (!_sessions.TryGetValue(a.Str("id"), out var s)) return Response.Fail("Terminal session not found.");
        s.LastUsed = DateTime.UtcNow;

        string text;
        lock (s.Buffer)
        {
            text = s.Buffer.ToString();
            s.Buffer.Clear();
        }
        bool exited = s.Proc.HasExited;
        return Response.Success(new JsonObject
        {
            ["output"] = text,
            ["exited"] = exited,
            ["exitCode"] = exited ? s.Proc.ExitCode : 0,
        });
    }

    public Response Close(JsonObject a)
    {
        if (_sessions.TryRemove(a.Str("id"), out var s)) Kill(s);
        return Response.Success();
    }

    void Reap()
    {
        foreach (var kv in _sessions)
        {
            if (DateTime.UtcNow - kv.Value.LastUsed > IdleLimit && _sessions.TryRemove(kv.Key, out var s)) Kill(s);
        }
    }

    static void Kill(Session s)
    {
        try { if (!s.Proc.HasExited) s.Proc.Kill(entireProcessTree: true); } catch { }
        try { s.Proc.Dispose(); } catch { }
    }

    static void Pump(StreamReader reader, Session s) => _ = Task.Run(async () =>
    {
        var buf = new char[2048];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                lock (s.Buffer)
                {
                    s.Buffer.Append(buf, 0, n);
                    if (s.Buffer.Length > MaxBufferChars) s.Buffer.Remove(0, s.Buffer.Length - MaxBufferChars);
                }
            }
        }
        catch { /* process ended */ }
    });
}
