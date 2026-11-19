// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// Keeps what the Log view shows: every line is also appended to a text file, one file per day, so
/// that what was done from this Host can be looked up later. The view itself only holds the most
/// recent lines of the current run. Files older than <see cref="KeepDays"/> are removed at start.
/// Writing must never get in the way of the Host, so a file that cannot be written is skipped.
/// </summary>
public sealed class ActivityLogFile
{
    public const int KeepDays = 365;

    /// <summary>The retention periods offered in Host settings, in days.</summary>
    public static readonly int[] RetentionChoices = [7, 14, 30, 180, 365];

    /// <summary>A stored retention period, or the longest one when the stored number is not a choice.</summary>
    public static int NormalizeKeepDays(int days) => RetentionChoices.Contains(days) ? days : KeepDays;

    public static string RetentionLabel(int days) => $"{days} days";

    public static int ParseRetention(string label) =>
        NormalizeKeepDays(int.TryParse(label.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var days) ? days : KeepDays);

    public static string DefaultDirectory =>
        Path.Combine(HostSettings.DataFolder, "logs");

    readonly object _gate = new();

    readonly Func<IEnumerable<string>> _secrets;
    public ActivityLogFile(string? directory = null, Func<IEnumerable<string>>? knownSecrets = null)
    {
        Directory = directory ?? DefaultDirectory;
        _secrets = knownSecrets ?? (() => []);
    }

    public string Directory { get; }

    public static string FileName(DateTime day) => $"host-{day:yyyy-MM-dd}.log";

    /// <summary>One line as it is stored: the full date and time, then the message on a single line.</summary>
    public static string Line(DateTime at, string message) =>
        $"{at:yyyy-MM-dd HH:mm:ss}  {message.ReplaceLineEndings(" ")}";

    public void Write(DateTime at, string message)
    {
        try { message = DiagnosticText.OmitSecrets(message, _secrets()); }
        catch { return; } // No raw fallback if the credential provider is unavailable.
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path.Combine(Directory, FileName(at)), Line(at, message) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the log is a record, not a dependency */ }
    }

    /// <summary>Deletes the daily files that are older than the retention period; returns how many.</summary>
    public int RemoveOld(DateTime today, int keepDays = KeepDays)
    {
        int removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "host-*.log"))
            {
                var stamp = Path.GetFileNameWithoutExtension(file)["host-".Length..];
                if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var day)) continue;
                if ((today.Date - day).TotalDays <= keepDays) continue;
                File.Delete(file);
                removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}
