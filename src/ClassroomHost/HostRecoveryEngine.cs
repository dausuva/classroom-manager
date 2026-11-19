// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;

namespace ClassroomManager.Host;

/// <summary>The four fixed files of the settings pair and the one recovery record, as the engine sees them. No paths cross this interface.</summary>
internal interface IHostRecoveryFiles
{
    /// <summary>The bytes of a role, or null when the file is absent.</summary>
    byte[]? Read(RecoveryRole role);
    /// <summary>Replaces a role as one flushed step: a reader sees the old bytes or the new ones, never a mixture.</summary>
    void Write(RecoveryRole role, byte[] bytes);
    void Delete(RecoveryRole role);
    byte[]? ReadRecord();
    void WriteRecord(byte[] bytes);
    void DeleteRecord();
}

internal enum RecoveryResolution
{
    /// <summary>No record: nothing to recover.</summary>
    Clean,
    /// <summary>A Prepared record: the four files were put back exactly as they were before the operation.</summary>
    RolledBack,
    /// <summary>A Committed record: the four files are the operation's result.</summary>
    RolledForward,
    /// <summary>The record cannot be trusted, or a file holds bytes neither side of the operation explains. Nothing was changed.</summary>
    RecoveryRequired,
}

internal enum RecoveryCommit
{
    /// <summary>Committed durably and the record is gone.</summary>
    Committed,
    /// <summary>Committed durably; removing the record failed. The next resolve finishes it.</summary>
    CommittedCleanupPending,
    /// <summary>Failed before the commit point; the files were put back.</summary>
    NotCommitted,
    /// <summary>Failed and the files could not be put back, or the pair or record is not trustworthy. Preserve everything.</summary>
    RecoveryRequired,
}

/// <summary>
/// DR-2 core: runs a change of the settings and key pair as a Prepared then Committed record and finishes or undoes it after any interruption
/// (the order of the plan in HOST-SETTINGS-DURABLE-RECOVERY-PLAN.md). It works on injected files, so every termination point is testable. It is
/// not wired to Load or Save yet; the caller must hold the settings lease (<see cref="HostSettingsCommit.Enter"/>) around it.
/// </summary>
internal sealed class HostRecoveryEngine(IHostRecoveryFiles files, HostRecoveryCodec codec)
{
    /// <summary>Finishes or undoes an interrupted operation. Safe to run again at any point, including after it was itself interrupted.</summary>
    public RecoveryResolution Resolve()
    {
        byte[]? raw;
        try { raw = files.ReadRecord(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return RecoveryResolution.RecoveryRequired; }
        if (raw is null) return RecoveryResolution.Clean;

        HostRecoveryRecord record;
        try { record = codec.Decode(raw); }
        catch (HostRecoveryFormatException) { return RecoveryResolution.RecoveryRequired; }

        var forward = record.Phase == RecoveryPhase.Committed;
        try
        {
            // Every file must be explained by the record before anything is touched: before, after, or (for a roll forward) missing.
            foreach (var change in record.Changes)
            {
                var current = files.Read(change.Role);
                if (!Matches(current, change.Before) && !Matches(current, change.After) && !(forward && current is null))
                    return RecoveryResolution.RecoveryRequired;
            }
            foreach (var change in record.Changes) Put(change.Role, forward ? change.After : change.Before);
            foreach (var change in record.Changes)
                if (!Matches(files.Read(change.Role), forward ? change.After : change.Before)) return RecoveryResolution.RecoveryRequired;
            files.DeleteRecord();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return RecoveryResolution.RecoveryRequired; }
        return forward ? RecoveryResolution.RolledForward : RecoveryResolution.RolledBack;
    }

    /// <summary>
    /// Replaces the four files with <paramref name="after"/> (null = absent) as one durable operation. An earlier interrupted operation is resolved first;
    /// if that cannot be done, nothing is written.
    /// </summary>
    public RecoveryCommit Commit(RecoveryOperation operation, IReadOnlyDictionary<RecoveryRole, byte[]?> after)
    {
        if (Resolve() == RecoveryResolution.RecoveryRequired) return RecoveryCommit.RecoveryRequired;
        var transaction = Guid.NewGuid();
        List<RecoveryChange> changes;
        try
        {
            changes = Enum.GetValues<RecoveryRole>()
                .Select(role => new RecoveryChange(role, new(files.Read(role)), new(after.GetValueOrDefault(role)))).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return RecoveryCommit.NotCommitted; }

        try
        {
            files.WriteRecord(codec.Encode(new HostRecoveryRecord(transaction, RecoveryPhase.Prepared, operation, changes)));
            foreach (var change in changes) Put(change.Role, change.After);
            foreach (var change in changes)
                if (!Matches(files.Read(change.Role), change.After)) throw new IOException("A settings file did not hold what was written.");
            files.WriteRecord(codec.Encode(new HostRecoveryRecord(transaction, RecoveryPhase.Committed, operation, changes)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HostRecoveryFormatException)
        {
            // Before the commit point: undo. A record that could not be written leaves the old files untouched or partly replaced, and resolve decides.
            return Resolve() switch
            {
                RecoveryResolution.RolledBack or RecoveryResolution.Clean => RecoveryCommit.NotCommitted,
                // The Committed record had in fact been published when the error was reported: the operation stands.
                RecoveryResolution.RolledForward => RecoveryCommit.Committed,
                _ => RecoveryCommit.RecoveryRequired,
            };
        }

        try { files.DeleteRecord(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return RecoveryCommit.CommittedCleanupPending; }
        return RecoveryCommit.Committed;
    }

    void Put(RecoveryRole role, RecoveryImage image)
    {
        if (Matches(files.Read(role), image)) return;
        if (image.Present) files.Write(role, image.Bytes.ToArray());
        else files.Delete(role);
    }

    static bool Matches(byte[]? current, RecoveryImage image) =>
        current is null ? !image.Present : image.Present && CryptographicOperations.FixedTimeEquals(current, image.Bytes);
}

/// <summary>The recovery files on disk, next to the settings: the pair and its two backups, and <c>host.recovery</c>. Not used by Load or Save yet.</summary>
internal sealed class HostRecoveryDiskFiles : IHostRecoveryFiles
{
    readonly string _settings, _keys;

    public HostRecoveryDiskFiles(string settingsPath)
    {
        _settings = Path.GetFullPath(settingsPath);
        _keys = HostSecretStore.PathFor(_settings);
    }

    public string RecordPath => Path.ChangeExtension(_settings, ".recovery");

    public string PathFor(RecoveryRole role) => role switch
    {
        RecoveryRole.Settings => _settings,
        RecoveryRole.SettingsBackup => AtomicFile.BackupPath(_settings),
        RecoveryRole.Keys => _keys,
        RecoveryRole.KeysBackup => AtomicFile.BackupPath(_keys),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public byte[]? Read(RecoveryRole role) => ReadIfPresent(PathFor(role));
    public void Write(RecoveryRole role, byte[] bytes) => AtomicFile.ReplaceAllBytes(PathFor(role), bytes);
    public void Delete(RecoveryRole role) => File.Delete(PathFor(role));

    public byte[]? ReadRecord() => ReadIfPresent(RecordPath, HostRecoveryCodec.MaxRecordBytes);
    public void WriteRecord(byte[] bytes) => AtomicFile.ReplaceAllBytes(RecordPath, bytes);
    public void DeleteRecord() => File.Delete(RecordPath);

    static byte[]? ReadIfPresent(string path, long limit = HostRecoveryCodec.MaxFileBytes)
    {
        if (!File.Exists(path)) return null;
        // A file bigger than the codec allows is not one the host wrote; reading it whole would be the wrong response.
        if (new FileInfo(path).Length > limit) throw new IOException("A settings file is larger than the host writes.");
        return File.ReadAllBytes(path);
    }
}
