// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClassroomManager.Host;

/// <summary>A published version that is newer than the one running.</summary>
public sealed record AvailableRelease(string Version, string Title, string Page);

/// <summary>
/// Tells the person at the Host that a newer version has been published. It only looks and tells:
/// once a day at most, one request to the project's release page, and nothing is downloaded or
/// installed. A lab without internet access simply never hears of one; the check can also be
/// switched off in Host settings.
/// </summary>
public static class UpdateNotice
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/dausuva/classroom-manager/releases/latest";

    static readonly Regex Named = new(@"(\d+\.\d+\.\d+)\s*\(build\s+(\d+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the answer of the release page. A release is titled "Classroom Manager 1.2.0 (build 310)",
    /// which is version 1.2.0.310. Returns null for anything that is not such a release.
    /// </summary>
    public static AvailableRelease? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var early) && early.ValueKind == JsonValueKind.True) return null;
            var title = root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
            var page = root.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() ?? "" : "";
            var match = Named.Match(title);
            // Only a page on the project's own site is offered to open.
            if (!match.Success || !page.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return null;
            return new AvailableRelease($"{match.Groups[1].Value}.{match.Groups[2].Value}", title, page);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>True when <paramref name="offered"/> is a later version than <paramref name="running"/>; false when either cannot be read.</summary>
    public static bool IsNewer(string running, string offered)
    {
        if (!Version.TryParse(running, out var mine) || !Version.TryParse(offered, out var theirs)) return false;
        // A customer release is shown as three numbers (1.2.0). It is not behind the internal build 1.2.0.310 of the same version.
        if (mine.Revision < 0) theirs = new Version(theirs.Major, theirs.Minor, Math.Max(theirs.Build, 0));
        return theirs > mine;
    }

    /// <summary>True when the last look was more than a day ago, or never, or its record cannot be read.</summary>
    public static bool Due(string lastCheckUtc, DateTime nowUtc) =>
        !DateTime.TryParse(lastCheckUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last) ||
        nowUtc - last.ToUniversalTime() >= TimeSpan.FromHours(24) || last.ToUniversalTime() > nowUtc.AddMinutes(5);

    /// <summary>Asks the release page once. Returns the newer release, or null when there is none or the page could not be reached.</summary>
    public static async Task<AvailableRelease?> CheckAsync(HttpClient http, string running, CancellationToken ct, string url = LatestReleaseUrl)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ClassroomManager-Host");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var release = Parse(await response.Content.ReadAsStringAsync(ct));
            return release is not null && IsNewer(running, release.Version) ? release : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { return null; }
    }
}
