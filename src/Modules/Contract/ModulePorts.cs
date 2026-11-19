// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Modules;

/// <summary>One Host service a module asks for, by name and version.</summary>
public sealed record ModulePortRequest(string Name, int Version);

/// <summary>The names and limits of the Host services a module can be given. A module is given nothing it did not list.</summary>
public static class ModulePorts
{
    public const string Log = "log";
    public const string Time = "time";
    public const string StoragePrivate = "storage.private";
    public const string ClientCall = "client.call";
    public const string ClientTransfer = "client.transfer";
    public const string StorageSink = "storage.sink";
    public const string StorageSource = "storage.source";
    public const string ClientUpload = "client.upload";

    /// <summary>The ports this contract version defines, each at version 1.</summary>
    public static readonly IReadOnlyList<string> Known = [Log, Time, StoragePrivate, ClientCall, ClientTransfer, StorageSink, StorageSource, ClientUpload];

    public const int CurrentPortVersion = 1;

    public const int MaxLogLineLength = 500;
    public const int MaxLogLinesPerMinute = 60;
    public const long StorageQuotaBytes = 20L * 1024 * 1024;
    public const long StorageMaxFileBytes = 5L * 1024 * 1024;
    public const int StorageMaxFiles = 64;

    public const int MaxCallArgumentBytes = 64 * 1024;
    public const int MaxCallAnswerBytes = 8 * 1024 * 1024;
    public const int CallTimeoutSeconds = 60;
    public const int MaxCallTimeoutMinutes = 60;
    public const int MaxRefusalsPerRun = 3;
    public const int MaxTransfersPerRun = 100_000;
    public const int MaxUploadsPerRun = 20;
    public const int MaxRelativePathLength = 240;
}

/// <summary>Writes a line to the Host's activity log, marked with the module's id. Lines are cut to <see cref="ModulePorts.MaxLogLineLength"/> and rate limited.</summary>
public interface IModuleLog
{
    void Write(string message);
}

/// <summary>
/// A small private folder of the module's own. Names are plain file names (letters, digits, dot, hyphen, underscore; at most 64 characters),
/// never paths. Use is limited by <see cref="ModulePorts.StorageQuotaBytes"/> in all, <see cref="ModulePorts.StorageMaxFileBytes"/> per file and
/// <see cref="ModulePorts.StorageMaxFiles"/> files. Every call that breaks a rule throws <see cref="ModulePortException"/>.
/// </summary>
public interface IModulePrivateStorage
{
    byte[]? Read(string name);
    void Write(string name, byte[] content);
    bool Delete(string name);
    IReadOnlyList<string> List();
    long UsedBytes { get; }
}

/// <summary>A module asked a port for something it may not have, or used a port it was not given.</summary>
public sealed class ModulePortException(string message) : Exception(message);

/// <summary>The ports a module was granted. Asking for one it did not list throws, so a missing request is found the first time it is used.</summary>
public sealed class ModuleServices(IModuleLog? log = null, TimeProvider? time = null, IModulePrivateStorage? storage = null)
{
    public IModuleLog Log => log ?? throw NotGranted(ModulePorts.Log);
    public TimeProvider Time => time ?? throw NotGranted(ModulePorts.Time);
    public IModulePrivateStorage Storage => storage ?? throw NotGranted(ModulePorts.StoragePrivate);

    static ModulePortException NotGranted(string port) => new($"The module did not ask for the \"{port}\" port, so it was not given.");
}
