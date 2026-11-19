// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassroomManager.Client;

/// <summary>
/// Whether this Client is still "newly installed": no Host has connected to it yet. While it is, and for at most
/// <see cref="ClientConfig.EnrolmentWindowDays"/> days after it first started, a signed discovery probe is answered with the Client's
/// certificate fingerprint (T-185, enrolment mode) so that a Host holding the lab key can approve and add it by itself. The first
/// authenticated session of any kind closes the window for good; deleting <c>enrolment.json</c> and restarting the service reopens it.
/// Nothing in the file is secret.
/// </summary>
sealed class EnrolmentState
{
    readonly string _path;
    readonly TimeProvider _time;
    readonly object _lock = new();
    DateTimeOffset _firstStart;
    DateTimeOffset? _enrolled;

    public EnrolmentState(string path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        _firstStart = _time.GetUtcNow();
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject saved)
            {
                if (DateTimeOffset.TryParse(saved["firstStartUtc"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var first)) _firstStart = first;
                if (DateTimeOffset.TryParse(saved["enrolledUtc"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var enrolled)) _enrolled = enrolled;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        Save();   // the first start of a fresh install, or a file that could not be read: the window starts now
    }

    public DateTimeOffset FirstStart { get { lock (_lock) return _firstStart; } }
    public DateTimeOffset? Enrolled { get { lock (_lock) return _enrolled; } }

    /// <summary>True while no Host has connected and fewer than <paramref name="days"/> days have passed since the first start. 0 days = never.</summary>
    public bool IsOpen(int days)
    {
        lock (_lock) return days > 0 && _enrolled is null && _time.GetUtcNow() - _firstStart < TimeSpan.FromDays(days);
    }

    /// <summary>A Host proved the key: the Client is enrolled and stops announcing itself. Safe to call on every session.</summary>
    public void MarkEnrolled()
    {
        lock (_lock)
        {
            if (_enrolled is not null) return;
            _enrolled = _time.GetUtcNow();
            Save();
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var node = new JsonObject { ["firstStartUtc"] = _firstStart.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") };
            if (_enrolled is { } enrolled) node["enrolledUtc"] = enrolled.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            File.WriteAllText(_path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the state then lives for this run only */ }
    }
}
