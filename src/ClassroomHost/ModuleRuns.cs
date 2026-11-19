// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Appearance;
#if BROWSER_INVENTORY_MODULE
using ClassroomManager.BrowserInventory;
#endif
using ClassroomManager.Maintenance;
#if REPORTING_MODULE
using ClassroomManager.Reporting;
#endif
using ClassroomManager.SoftwareInventory;
using ClassroomManager.Modules;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// Opens the connection a module is allowed for one run: the computers the Host chose, the commands the module declared and the folders
/// the teacher chose for its sinks. The module gets no <see cref="ClientSession"/>, address, key or path of its own.
/// </summary>
public static class ModuleRuns
{
    public static ModuleClientSession Open(string moduleId, IEnumerable<(ClientSession Session, ClientInfo Client)> computers,
        Func<string, string?> sinkFolder, Action<string>? audit = null, string? stagingFolder = null, IModulePickedFiles? pickedFiles = null)
    {
        var descriptor = BuiltInModules.Registry.Find(moduleId) is { Usable: true } registration
            ? registration.Descriptor! : throw new InvalidOperationException($"The module \"{moduleId}\" is not available.");
        if (ModuleBreaker.IsTripped(moduleId)) throw new InvalidOperationException($"The module \"{moduleId}\" was switched off after repeated refused calls. Tick it in Host settings to try again.");
        var targets = computers.Select((computer, index) => new ModuleTarget(
            new ModuleComputer(index + 1, computer.Client.Name), new SessionConnection(computer.Session, stagingFolder),
            RecentFilesPlan.ComputerFolder("", computer.Client))).ToArray();
        return new ModuleClientSession(descriptor, targets, new SinkFolders(sinkFolder),
            line => (audit ?? (text => BuiltInModules.LogSink?.Invoke(text)))($"[{moduleId}] {line}"), sources: pickedFiles,
            stopped: reason => ModuleBreaker.Trip(moduleId, reason));
    }

    /// <summary>The Windows Update operations of the Maintenance module for one computer, through the module's ports.</summary>
    public static (MaintenanceOperations Operations, ModuleComputer Computer) OpenMaintenance(ClientSession session, ClientInfo client)
    {
        var run = Open(BuiltInModules.Maintenance, [(session, client)], _ => null);
        return (new MaintenanceOperations(run), run.Computers[0]);
    }

    /// <summary>The installed-programs operations of the Software module for one computer.</summary>
    public static (SoftwareOperations Operations, ModuleComputer Computer) OpenSoftware(ClientSession session, ClientInfo client)
    {
        var run = Open(BuiltInModules.Software, [(session, client)], _ => null);
        return (new SoftwareOperations(run), run.Computers[0]);
    }

    /// <summary>The appearance commands of the Appearance module for one computer.</summary>
    public static (AppearanceOperations Operations, ModuleComputer Computer) OpenAppearance(ClientSession session, ClientInfo client)
    {
        var run = Open(BuiltInModules.Appearance, [(session, client)], _ => null);
        return (new AppearanceOperations(run), run.Computers[0]);
    }

#if BROWSER_INVENTORY_MODULE
    /// <summary>The browser reads of the Browser inventory module for one computer.</summary>
    public static (BrowserOperations Operations, ModuleComputer Computer) OpenBrowser(ClientSession session, ClientInfo client)
    {
        var run = Open(BuiltInModules.Browser, [(session, client)], _ => null);
        return (new BrowserOperations(run), run.Computers[0]);
    }
#endif

#if REPORTING_MODULE
    /// <summary>The inventory snapshot of the Reporting module for one computer.</summary>
    public static (ReportingOperations Operations, ModuleComputer Computer) OpenReporting(ClientSession session, ClientInfo client)
    {
        var run = Open(BuiltInModules.Reporting, [(session, client)], _ => null);
        return (new ReportingOperations(run), run.Computers[0]);
    }
#endif

    sealed class SinkFolders(Func<string, string?> resolve) : IModuleSinkFolders
    {
        public string? Resolve(string sink) => resolve(sink);
    }

    sealed class SessionConnection(ClientSession session, string? stagingFolder = null) : IModuleConnection
    {
        public string? StagingFolder => stagingFolder;

        public Task UploadAsync(string localPath, string remotePath, CancellationToken cancellation) => session.UploadAsync(localPath, remotePath, ct: cancellation);

        public async Task<string> CallSystemAsync(string command, string argumentsJson, CancellationToken cancellation) =>
            (await session.CallAtAsync(Levels.System, command, JsonNode.Parse(argumentsJson) as JsonObject, cancellation)).ToJsonString();

        public async Task<string> CallUserAsync(string command, string argumentsJson, CancellationToken cancellation) =>
            (await session.CallAtAsync(Levels.User, command, JsonNode.Parse(argumentsJson) as JsonObject, cancellation)).ToJsonString();

        public async Task DownloadAsync(string remotePath, string localPath, CancellationToken cancellation)
        {
            try { await session.DownloadAsync(remotePath, localPath, ct: cancellation); }
            // The one file could not be read there: the module is told that and goes on. A lost connection passes through and ends the run.
            catch (Exception ex) when (RecentFilesCopier.IsFileFailure(ex)) { throw new ModuleTransferException(ex.Message, ex); }
        }
    }
}
