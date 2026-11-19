// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;

namespace ClassroomManager.Host;

public enum FileTransferDirection { Upload, Download }

/// <summary>Acknowledged payload bytes, not Base64/TLS overhead or the network adapter's line rate.</summary>
public sealed record FileTransferProgress(FileTransferDirection Direction, long Bytes, long TotalBytes, TimeSpan Elapsed, bool Complete = false)
{
    public int Percent => TotalBytes <= 0 ? Complete ? 100 : 0 : (int)Math.Clamp(Bytes * 100d / TotalBytes, 0, 100);
    public double MegabitsPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : Math.Max(0, Bytes) * 8d / Elapsed.TotalSeconds / 1_000_000;
}

/// <summary>Monotonic per-file rate; bounded UI notifications, with initial and final progress always delivered.</summary>
public sealed class FileTransferMeter(FileTransferDirection direction, long totalBytes, Action<FileTransferProgress> report,
    TimeProvider? time = null, long? startedTimestamp = null)
{
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly long _started = startedTimestamp ?? (time ?? TimeProvider.System).GetTimestamp();
    long? _lastReport;

    public void Report(long bytes, bool complete = false)
    {
        var now = _time.GetTimestamp();
        if (!complete && _lastReport is { } previous && _time.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(250)) return;
        _lastReport = now;
        report(new(direction, bytes, totalBytes, _time.GetElapsedTime(_started, now), complete));
    }
}

public static class FileTransferProgressText
{
    // Source-English model text, like OperationActivity's other statuses. UI translation stays at presentation.
    public static string Format(FileTransferProgress progress, string computer) => progress.Direction == FileTransferDirection.Upload
        ? string.Create(CultureInfo.InvariantCulture, $"Uploading: {progress.Percent}% · {progress.MegabitsPerSecond:0.0} Mb/s · {computer}")
        : string.Create(CultureInfo.InvariantCulture, $"Downloading: {progress.Percent}% · {progress.MegabitsPerSecond:0.0} Mb/s · {computer}");
}
