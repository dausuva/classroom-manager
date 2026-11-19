// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClassroomManager.Host;

/// <summary>
/// A process-lifetime lease for a schedule, held through queued and running work. The OS releases it on process death;
/// it never expires under a still-running job. All runners sharing the record path must use this lease (T-216).
/// </summary>
public sealed class ScheduleOwnership : IDisposable
{
    readonly FileStream _held;
    public string Owner { get; } = Guid.NewGuid().ToString("N");
    ScheduleOwnership(FileStream held) => _held = held;

    public static string LockPath(string scheduleId, string? recordPath = null) => Path.Combine(
        (recordPath ?? ScheduleRuns.ComputersFilePath) + ".owners",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scheduleId))) + ".lock");

    public static ScheduleOwnership? TryAcquire(string scheduleId, string? recordPath = null)
    {
        var path = LockPath(scheduleId, recordPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new ScheduleOwnership(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
    }

    public void Dispose() => _held.Dispose();
}
