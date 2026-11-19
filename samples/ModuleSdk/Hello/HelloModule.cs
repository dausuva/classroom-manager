// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Modules;

namespace ClassroomManager.Samples.Hello;

/// <summary>
/// The smallest useful module: it says who it is, asks for two Host services (the activity log and the clock) and writes one line
/// to the log when the Host has accepted it. It has no way to reach a file, a computer or the network: it only has what it was given.
/// </summary>
public sealed class HelloModule : IClassroomModule
{
    public const string Id = "example.hello.sample";

    public ModuleDescriptor Describe() => new(Id, "Hello sample", ModuleContract.CurrentVersion, [])
    {
        Ports = [new(ModulePorts.Log, 1), new(ModulePorts.Time, 1)],
        Icon =
        [
            @"+-------+",
            @"| Hello |",
            @"+-------+",
        ],
    };

    public void Attach(ModuleServices services) =>
        services.Log.Write($"Hello from the sample module, at {services.Time.GetUtcNow():yyyy-MM-dd HH:mm} UTC.");
}
