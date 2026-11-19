// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text;
using System.IO;
using System.Windows;

namespace ClassroomManager.Host;

public static class CrashLog
{
    static readonly object Gate = new();
    static bool _enabled;
    public static string Path => System.IO.Path.Combine(HostSettings.DataFolder, "host-crashes.log");

    public static void Initialize(bool enabled)
    {
        _enabled = enabled;
        Application.Current.DispatcherUnhandledException += (_, e) => Write("DispatcherUnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    public static void SetEnabled(bool enabled) => _enabled = enabled;

    public static string Read()
    {
        try { return File.Exists(Path) ? File.ReadAllText(Path) : "No Host crash reports have been recorded."; }
        catch (Exception ex) { return "Could not read the Host crash log: " + ex.Message; }
    }

    public static void Clear()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch (Exception ex) { throw new IOException("Could not clear the Host crash log.", ex); }
    }

    static void Write(string source, Exception exception)
    {
        if (!_enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 5_000_000) File.Delete(Path);
                var sb = new StringBuilder()
                    .AppendLine(new string('-', 80))
                    .AppendLine($"UTC: {DateTime.UtcNow:O}")
                    .AppendLine($"Host version: {HostBuildInfo.Version}")
                    .AppendLine($"Source: {source}")
                    .AppendLine(exception.ToString());
                File.AppendAllText(Path, sb.ToString());
            }
        }
        catch { }
    }
}
