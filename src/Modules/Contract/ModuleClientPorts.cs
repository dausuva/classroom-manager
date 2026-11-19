// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Modules;

/// <summary>
/// A computer the Host chose for one run. The handle is meaningful only inside that run; a module never sees an address, a key or a connection.
/// <see cref="Name"/> is for messages.
/// </summary>
public sealed record ModuleComputer(int Handle, string Name);

/// <summary>One file to move from a computer to a folder of this PC.</summary>
/// <param name="RemotePath">A path a <c>client.call</c> answer of this run named. Any other path is refused.</param>
/// <param name="Sink">A kind of destination the module declared (<see cref="ModuleDescriptor.Sinks"/>); the Host decides which folder it is.</param>
/// <param name="RelativePath">Where the file goes below the computer's folder in the sink; never a rooted path and never leaves the sink.</param>
/// <param name="Size">The size the module was told, to tell whether an earlier copy is the same file.</param>
/// <param name="ModifiedUtc">The change time the module was told; the copy is given the same time.</param>
/// <param name="Overwrite">Replace an earlier copy that differs in size or change time; when false an existing copy is left alone.</param>
public sealed record ModuleTransfer(string RemotePath, string Sink, string RelativePath, long Size, DateTime ModifiedUtc, bool Overwrite);

/// <summary>What a transfer did: a copy was made, or the earlier copy was already the same file.</summary>
public sealed record ModuleTransferResult(bool Copied, bool UpToDate);

/// <summary>
/// The Host's connection to the computers chosen for one run (ports <c>client.call</c> and <c>client.transfer</c>). Every call is checked:
/// the command must be one the module declared in its client capabilities, the computer one of <see cref="Computers"/>, and a transfer may
/// only name a path a call of this run returned. A refused call throws <see cref="ModulePortException"/>; after three the run is stopped.
/// </summary>
public interface IModuleClient
{
    IReadOnlyList<ModuleComputer> Computers { get; }

    /// <summary>
    /// Sends one command as the signed-in user and returns the answer as JSON text. <paramref name="argumentsJson"/> is a JSON object.
    /// <paramref name="timeout"/> asks for longer than the usual 60 seconds, up to <see cref="ModulePorts.MaxCallTimeoutMinutes"/> minutes.
    /// Any command the module declared may be sent this way, including one it may also send with administrator rights.
    /// </summary>
    Task<string> CallAsync(ModuleComputer computer, string command, string argumentsJson, CancellationToken cancellation = default, TimeSpan? timeout = null);

    /// <summary>
    /// Sends one command with administrator rights on the computer. Only a command listed in <see cref="ModuleDescriptor.SystemCommands"/> is
    /// sent; the Client's own key-role and step-up rules apply exactly as they do for a Host feature.
    /// </summary>
    Task<string> CallAsSystemAsync(ModuleComputer computer, string command, string argumentsJson, CancellationToken cancellation = default, TimeSpan? timeout = null);

    /// <summary>Copies one file to a sink without its bytes passing through the module.</summary>
    Task<ModuleTransferResult> TransferAsync(ModuleComputer computer, ModuleTransfer transfer, CancellationToken cancellation = default);
}
