// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace ClassroomManager.Host;

/// <summary>
/// The teacher guide that opens with F1 and the Help button: a page with pictures, in English and
/// Lithuanian, carried inside the program so it is there on every teacher PC without a separate
/// install. It is written out to a folder of its own the first time it is asked for and opened in
/// the browser.
/// </summary>
public static class HelpGuide
{
    const string Prefix = "Help/";

    /// <summary>The languages the guide exists in; any other language gets the English one.</summary>
    public static readonly string[] Languages = ["en", "lt"];

    public static string FileFor(string? language)
    {
        var code = (language ?? "").Split('-', '_')[0].ToLowerInvariant();
        return $"guide-{(Languages.Contains(code) ? code : "en")}.html";
    }

    /// <summary>Every file of the guide inside the program: pages, style sheet and pictures, by their path below the guide's folder.</summary>
    public static IEnumerable<string> Files() => typeof(HelpGuide).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)).Select(name => name[Prefix.Length..]);

    /// <summary>Writes the guide into <paramref name="folder"/> (files already there and unchanged in size are left) and returns the page for the language.</summary>
    public static string Extract(string folder, string? language)
    {
        var assembly = typeof(HelpGuide).Assembly;
        foreach (var file in Files())
        {
            var target = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
            using var source = assembly.GetManifestResourceStream(Prefix + file)!;
            if (File.Exists(target) && new FileInfo(target).Length == source.Length) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = File.Create(target);
            source.CopyTo(output);
        }
        return Path.Combine(folder, FileFor(language));
    }

    /// <summary>Opens the guide in the browser, in the language the program is shown in. Returns what went wrong, or an empty text.</summary>
    public static string Open(string? language)
    {
        try
        {
            // One folder per build, so a newer program never shows an older guide's pictures.
            var folder = Path.Combine(Path.GetTempPath(), "ClassroomManager-help", HostBuildInfo.Version);
            var page = Extract(folder, language);
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
