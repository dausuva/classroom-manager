// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text;
using System.Text.Json.Nodes;

namespace ClassroomManager.Modules;

/// <summary>The Host's real connection to one computer, seen by the broker. It sends as the signed-in user only.</summary>
public interface IModuleConnection
{
    Task<string> CallUserAsync(string command, string argumentsJson, CancellationToken cancellation);

    /// <summary>Sends with administrator rights. A connection that cannot says so; the broker only calls this for a declared system command.</summary>
    Task<string> CallSystemAsync(string command, string argumentsJson, CancellationToken cancellation) => throw new NotSupportedException();

    Task DownloadAsync(string remotePath, string localPath, CancellationToken cancellation);

    /// <summary>The folder on the computer where this Host's uploaded files go, or null when the connection cannot upload.</summary>
    string? StagingFolder => null;

    /// <summary>Copies one file of this PC to a path on the computer.</summary>
    Task UploadAsync(string localPath, string remotePath, CancellationToken cancellation) => throw new NotSupportedException();
}

/// <summary>A file the teacher picked, as the Host holds it: where it is, and what it was when picked.</summary>
public sealed record ModulePickedFileEntry(string LocalPath, ModulePickedFile Source);

/// <summary>The files the teacher picked for one run. A handle not issued here finds nothing.</summary>
public interface IModulePickedFiles
{
    ModulePickedFileEntry? Find(string sourceId);
}

/// <summary>One computer chosen for a run. <paramref name="FolderLabel"/> is the Host's safe name for the computer's folder inside a sink.</summary>
public sealed record ModuleTarget(ModuleComputer Computer, IModuleConnection Connection, string FolderLabel);

/// <summary>Maps a sink kind to the folder the teacher chose for it, or null when there is none.</summary>
public interface IModuleSinkFolders
{
    string? Resolve(string sink);
}

public sealed record ModuleClientLimits(TimeSpan CallTimeout, int MaxRefusals)
{
    public static ModuleClientLimits Default { get; } = new(TimeSpan.FromSeconds(ModulePorts.CallTimeoutSeconds), ModulePorts.MaxRefusalsPerRun);
}

/// <summary>
/// The ports <c>client.call</c>, <c>client.transfer</c> and <c>storage.sink</c> for one run of one module. Everything a module asks is checked
/// here, in the Host, whatever the module does: the port was requested, the command was declared, the computer was chosen, the path was
/// named by an earlier answer, the destination stays inside the sink. A refused call is logged and counted; the run stops after the limit.
/// </summary>
public sealed class ModuleClientSession : IModuleClient, IModuleUploads
{
    const int MaxRememberedStrings = 200_000;
    const int MaxJsonDepth = 64;
    const string PartialSuffix = ".cm-partial";

    readonly ModuleDescriptor _module;
    readonly IReadOnlyList<ModuleTarget> _targets;
    readonly IModuleSinkFolders _sinks;
    readonly Action<string> _audit;
    readonly ModuleClientLimits _limits;
    readonly IModulePickedFiles? _sources;
    readonly Action<string>? _stopped;
    readonly HashSet<string> _requested;
    readonly Dictionary<int, SemaphoreSlim> _gates = [];
    readonly Dictionary<int, HashSet<string>> _named = [];
    readonly object _lock = new();
    int _refusals, _transfers, _uploads;

    public ModuleClientSession(ModuleDescriptor module, IReadOnlyList<ModuleTarget> targets, IModuleSinkFolders sinks, Action<string> audit, ModuleClientLimits? limits = null,
        IModulePickedFiles? sources = null, Action<string>? stopped = null)
    {
        _sources = sources;
        _stopped = stopped;
        _module = module;
        _targets = targets;
        _sinks = sinks;
        _audit = audit;
        _limits = limits ?? ModuleClientLimits.Default;
        _requested = module.Ports.Select(port => port.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            _gates[target.Computer.Handle] = new SemaphoreSlim(1, 1);
            _named[target.Computer.Handle] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<ModuleComputer> Computers => _targets.Select(target => target.Computer).ToArray();

    /// <summary>How many calls were refused in this run.</summary>
    public int Refusals { get { lock (_lock) return _refusals; } }

    /// <summary>True once the refusal limit was reached; every further call fails.</summary>
    public bool Stopped { get { lock (_lock) return _refusals >= _limits.MaxRefusals; } }

    public Task<string> CallAsync(ModuleComputer computer, string command, string argumentsJson, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        SendAsync(computer, command, argumentsJson, system: false, cancellation, timeout);

    public Task<string> CallAsSystemAsync(ModuleComputer computer, string command, string argumentsJson, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        SendAsync(computer, command, argumentsJson, system: true, cancellation, timeout);

    async Task<string> SendAsync(ModuleComputer computer, string command, string argumentsJson, bool system, CancellationToken cancellation, TimeSpan? requested)
    {
        EnsureRunning();
        Require(ModulePorts.ClientCall);
        var target = Find(computer);
        if (command is null || !_module.ClientCapabilities.Contains(command, StringComparer.Ordinal))
            throw Refuse($"the command \"{command}\" is not one the module declared");
        if (system && !_module.SystemCommands.Contains(command, StringComparer.Ordinal))
            throw Refuse($"the command \"{command}\" is not one the module declared as a system command");
        var limit = requested ?? _limits.CallTimeout;
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(ModulePorts.MaxCallTimeoutMinutes)) throw Refuse("the timeout asked for is out of range");
        argumentsJson ??= "{}";
        if (Encoding.UTF8.GetByteCount(argumentsJson) > ModulePorts.MaxCallArgumentBytes) throw Refuse("the arguments are too large");
        if (!IsJsonObject(argumentsJson)) throw Refuse("the arguments are not a JSON object");

        var gate = _gates[target.Computer.Handle];
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            string answer;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(limit);
            try
            {
                answer = system ? await target.Connection.CallSystemAsync(command, argumentsJson, timeout.Token).ConfigureAwait(false)
                                : await target.Connection.CallUserAsync(command, argumentsJson, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw Refuse($"{command} on {target.Computer.Name} timed out"); }
            if (Encoding.UTF8.GetByteCount(answer ?? "") > ModulePorts.MaxCallAnswerBytes) throw Refuse("the answer is too large");
            Remember(target.Computer.Handle, answer ?? "");
            _audit($"{command} on {target.Computer.Name}{(system ? " (administrator)" : "")}");
            return answer ?? "";
        }
        finally { gate.Release(); }
    }

    public async Task<ModuleTransferResult> TransferAsync(ModuleComputer computer, ModuleTransfer transfer, CancellationToken cancellation = default)
    {
        EnsureRunning();
        Require(ModulePorts.ClientTransfer);
        Require(ModulePorts.StorageSink);
        var target = Find(computer);
        ArgumentNullException.ThrowIfNull(transfer);
        if (transfer.Sink is null || !_module.Sinks.Contains(transfer.Sink, StringComparer.Ordinal)) throw Refuse($"the sink \"{transfer.Sink}\" is not one the module declared");
        var sinkFolder = _sinks.Resolve(transfer.Sink);
        if (string.IsNullOrWhiteSpace(sinkFolder)) throw Refuse($"the teacher has not chosen a folder for the sink \"{transfer.Sink}\"");
        lock (_lock)
        {
            if (transfer.RemotePath is null || !_named[target.Computer.Handle].Contains(transfer.RemotePath))
                throw Refuse("the path was not named by an answer of this run");
        }
        var local = LocalPath(sinkFolder, target.FolderLabel, transfer.RelativePath);
        lock (_lock)
        {
            if (_transfers >= ModulePorts.MaxTransfersPerRun) throw Refuse("too many files in one run");
            _transfers++;
        }

        var gate = _gates[target.Computer.Handle];
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var existing = File.Exists(local) ? new FileInfo(local) : null;
            if (existing is not null && existing.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw Refuse("the copy to replace is a link");
            if (existing is not null && (!transfer.Overwrite ||
                existing.Length == transfer.Size && Math.Abs((existing.LastWriteTimeUtc - transfer.ModifiedUtc).TotalSeconds) < 1))
                return new(Copied: false, UpToDate: true);

            var partial = local + PartialSuffix;
            try
            {
                Prepare(sinkFolder, local);
                await target.Connection.DownloadAsync(transfer.RemotePath, partial, cancellation).ConfigureAwait(false);
                Finish(partial, local, transfer.ModifiedUtc);
                return new(Copied: true, UpToDate: false);
            }
            catch
            {
                try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<ModuleUploadResult> UploadAsync(ModuleComputer computer, ModulePickedFile source, string drop, CancellationToken cancellation = default)
    {
        EnsureRunning();
        Require(ModulePorts.ClientUpload);
        Require(ModulePorts.StorageSource);
        var target = Find(computer);
        if (drop is null || !_module.Uploads.Contains(drop, StringComparer.Ordinal)) throw Refuse($"the upload kind \"{drop}\" is not one the module declared");
        // Only the handle counts. What the module says the file is does not matter: the Host looks up what it picked.
        var picked = source is null ? null : _sources?.Find(source.Id);
        if (picked is null) throw Refuse("the file was not picked by the teacher in this run");
        var problem = ModuleUploads.Problem(drop, picked.Source.Name, picked.Source.Size);
        if (problem is not null) throw Refuse(problem.TrimEnd('.'));
        var staging = target.Connection.StagingFolder;
        if (string.IsNullOrWhiteSpace(staging)) throw Refuse("this computer cannot take an upload");
        lock (_lock)
        {
            if (_uploads >= ModulePorts.MaxUploadsPerRun) throw Refuse("too many uploads in one run");
            _uploads++;
        }

        var remote = staging.TrimEnd('\\') + "\\" + ModuleUploads.StagedName(drop, picked.Source.Name);
        var gate = _gates[target.Computer.Handle];
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // The file may have changed since the teacher picked it; what leaves this PC is what was picked.
            string now;
            try
            {
                await using var stream = File.OpenRead(picked.LocalPath);
                now = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false)).ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ModuleTransferException(ex.Message, ex); }
            if (!now.Equals(picked.Source.Sha256, StringComparison.OrdinalIgnoreCase)) throw Refuse("the file changed after it was picked");
            await target.Connection.UploadAsync(picked.LocalPath, remote, cancellation).ConfigureAwait(false);
            _audit($"uploaded {drop} to {target.Computer.Name}");
            return new(remote);
        }
        finally { gate.Release(); }
    }

    ModuleTarget Find(ModuleComputer computer) =>
        _targets.FirstOrDefault(target => target.Computer == computer) ?? throw Refuse("the computer is not one chosen for this run");

    void Require(string port)
    {
        if (!_requested.Contains(port)) throw Refuse($"the module did not ask for the port \"{port}\"");
    }

    void EnsureRunning()
    {
        if (Stopped) throw new ModulePortException($"The run was stopped after {_limits.MaxRefusals} refused calls.");
    }

    ModulePortException Refuse(string reason)
    {
        bool justStopped;
        lock (_lock) justStopped = ++_refusals == _limits.MaxRefusals;
        _audit($"refused: {reason}");
        // Told once, when the limit is reached, so the Host can switch the module off; the run itself stops by refusing every later call.
        if (justStopped) _stopped?.Invoke($"The run was stopped after {_limits.MaxRefusals} refused calls; the last was: {reason}.");
        return new ModulePortException($"Refused: {reason}.");
    }

    void Remember(int handle, string answer)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(answer); }
        catch (System.Text.Json.JsonException) { return; }
        lock (_lock) Collect(node, _named[handle], 0);
    }

    static void Collect(JsonNode? node, HashSet<string> into, int depth)
    {
        if (node is null || depth > MaxJsonDepth || into.Count >= MaxRememberedStrings) return;
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text): into.Add(text); break;
            case JsonArray array: foreach (var item in array) Collect(item, into, depth + 1); break;
            case JsonObject obj: foreach (var pair in obj) Collect(pair.Value, into, depth + 1); break;
        }
    }

    static bool IsJsonObject(string json)
    {
        try { return JsonNode.Parse(json) is JsonObject; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    string LocalPath(string sinkFolder, string folderLabel, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > ModulePorts.MaxRelativePathLength) throw Refuse("the destination path is empty or too long");
        var forward = relative.Replace('\\', '/');
        var problem = ModulePaths.Problem(forward);
        if (problem is not null) throw Refuse("the destination path is not safe: " + problem.TrimEnd('.'));
        var root = Path.GetFullPath(sinkFolder).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(root, folderLabel, forward.Replace('/', '\\')));
        var computerRoot = Path.GetFullPath(Path.Combine(root, folderLabel)).TrimEnd('\\') + "\\";
        if (!full.StartsWith(computerRoot, StringComparison.OrdinalIgnoreCase) || !computerRoot.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw Refuse("the destination would leave the sink");
        return full;
    }

    // Writing here can fail for this one file (a locked or read-only copy, a full disk); that is a per-file failure, the run goes on.
    void Prepare(string sinkFolder, string local)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(local)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ModuleTransferException(ex.Message, ex); }
        RefuseLinksOnTheWay(sinkFolder, local);
    }

    static void Finish(string partial, string local, DateTime modifiedUtc)
    {
        try
        {
            File.SetLastWriteTimeUtc(partial, modifiedUtc);
            File.Move(partial, local, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ModuleTransferException(ex.Message, ex); }
    }

    void RefuseLinksOnTheWay(string sinkFolder, string local)
    {
        var root = Path.GetFullPath(sinkFolder).TrimEnd('\\');
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(local)!);
             directory is not null && directory.FullName.Length > root.Length; directory = directory.Parent)
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw Refuse("a folder on the way to the destination is a link");
    }
}
