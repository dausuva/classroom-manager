// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;
using ClassroomManager.Appearance;

namespace ClassroomManager.Host;

/// <summary>One signed-in user's screen saver settings as reported by <c>screensaver.get</c>.</summary>
public sealed record ScreenSaverState(
    bool Active, string Name, int TimeoutSec, bool Secure, bool PolicyManaged, bool CanRestore,
    IReadOnlyList<string> Available)
{
    public static ScreenSaverState FromJson(JsonObject data) => new(
        data["active"]?.GetValue<bool>() ?? false,
        data["name"]?.GetValue<string>() ?? "",
        data["timeoutSec"]?.GetValue<int>() ?? 0,
        data["secure"]?.GetValue<bool>() ?? false,
        data["policyManaged"]?.GetValue<bool>() ?? false,
        data["canRestore"]?.GetValue<bool>() ?? false,
        Names(data["installed"]).Concat(Names(data["managed"]))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList());

    static IEnumerable<string> Names(JsonNode? list) =>
        (list as JsonArray ?? []).Select(item => item?["name"]?.GetValue<string>() ?? "").Where(name => name.Length > 0);
}

public static class ScreenSaverInventory
{
    public static ScreenSaverReport Project(IReadOnlyCollection<ScreenSaverState> states) =>
        AppearanceInventoryModule.Project(states.Select(state =>
            new ScreenSaverSnapshot(state.Active, state.Name, state.PolicyManaged, state.Available)).ToArray(), "off");

    /// <summary>Screen savers present on every computer that answered; only these can be chosen by name.</summary>
    public static List<string> CommonNames(IReadOnlyCollection<ScreenSaverState> states)
    {
        return Project(states).CommonNames.ToList();
    }

    /// <summary>For example "MatrixGeometry.scr on 3 · off on 1".</summary>
    public static string Summarize(IReadOnlyCollection<ScreenSaverState> states) =>
        Summarize(Project(states));

    public static string Summarize(ScreenSaverReport report) =>
        string.Join(" · ", report.Usage.Select(group => $"{group.Name} on {group.Count}"));

    /// <summary>Accepts the whole minutes Windows' own dialog offers (1 to 9999).</summary>
    public static bool TryParseMinutes(string text, out int seconds)
    {
        seconds = 0;
        if (!int.TryParse(text.Trim(), out var minutes)) return false;
        if (minutes < ScreenSaverRules.MinTimeoutSeconds / 60 || minutes > ScreenSaverRules.MaxTimeoutSeconds / 60) return false;
        seconds = minutes * 60;
        return true;
    }
}

/// <summary>What the Host can say about a local .scr file before sending it to student computers.</summary>
public sealed record ScreenSaverFile(string Path, string Name, long Length, string Sha256, string Signer)
{
    public bool IsSigned => Signer.Length > 0;

    /// <summary>Returns null with a reason when the file cannot be deployed as a screen saver.</summary>
    public static ScreenSaverFile? Inspect(string path, out string problem)
    {
        problem = "";
        var name = ScreenSaverRules.SafeFileName(System.IO.Path.GetFileName(path));
        if (name.Length == 0)
        {
            problem = "Choose a file whose name ends in .scr and uses only letters, digits, spaces, dots, hyphens, underscores, or brackets.";
            return null;
        }
        var info = new FileInfo(path);
        if (!info.Exists) { problem = $"File not found: {path}"; return null; }
        if (info.Length > ScreenSaverRules.MaxFileBytes) { problem = "That file is too large to be a screen saver."; return null; }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 2 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            problem = "That file is not a Windows screen saver program.";
            return null;
        }
        return new ScreenSaverFile(path, name, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), SignerOf(path));
    }

    /// <summary>The embedded Authenticode signer's subject, or empty when unsigned. The trust chain is not validated here.</summary>
    static string SignerOf(string path)
    {
        try
        {
            // X509CertificateLoader, the replacement for the obsolete loading APIs, has no method for
            // reading the signer out of an Authenticode-signed file, so this call is still the way.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch { return ""; }
    }
}
