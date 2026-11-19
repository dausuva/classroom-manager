// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using ClassroomManager.Host;
using ClassroomManager.Modules;
using ClassroomManager.Reporting;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// The module contract, loader and registry. The Reporting module is packaged for real here: its compiled assembly is copied into a
/// package folder, checked, loaded into its own load context and registered, so the same path a downloaded module takes is exercised.
/// </summary>
public sealed class ModuleRegistryTests : IDisposable
{
    const string Trusted = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    static readonly IReadOnlySet<string> TrustedSet = new HashSet<string> { Trusted };
    static readonly string ReportingAssembly = typeof(InventoryReportingModuleEntry).Assembly.Location;
    const string ReportingId = InventoryReportingModule.Id;
    const string EntryType = "ClassroomManager.Reporting.InventoryReportingModuleEntry";

    readonly string _root = Path.Combine(Path.GetTempPath(), "cm-registry-" + Guid.NewGuid().ToString("N"));

    public ModuleRegistryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    sealed class AcceptingSignatures : IModuleSignatureVerifier
    {
        public SignatureCheck Check(string path) => new(true, Trusted, "");
    }

    string MakeReportingPackage(string id = ReportingId, string entryType = EntryType, int min = 1, int max = 1, string[]? capabilities = null) =>
        MakePackage(ReportingAssembly, id, entryType, min, max, capabilities ?? [.. new InventoryReportingModuleEntry().Describe().ClientCapabilities], new InventoryReportingModuleEntry().Describe());

    string MakePackage(string assemblyPath, string id, string entryType, int min = 1, int max = 1, string[]? capabilities = null, ModuleDescriptor? descriptor = null)
    {
        var folder = Path.Combine(_root, id);
        Directory.CreateDirectory(folder);
        var assemblyFile = Path.GetFileName(assemblyPath);
        var dll = Path.Combine(folder, assemblyFile);
        File.Copy(assemblyPath, dll, overwrite: true);
        var manifest = new
        {
            schema = 1, id, publisher = "dausuva", name = "Inventory CSV export", version = "1.0.0",
            contract = new { min, max }, os = "windows", architecture = "x64",
            entry = new { assembly = assemblyFile, type = entryType },
            client = new { minProtocol = "1.0", capabilities = capabilities ?? [] },
            files = new[] { new { path = assemblyFile, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))) } },
            ports = descriptor?.Ports.Select(port => new { name = port.Name, version = port.Version }).ToArray(),
            sinks = descriptor?.Sinks.ToArray(),
            uploads = descriptor?.Uploads.ToArray(),
            systemCommands = descriptor?.SystemCommands.ToArray(),
        };
        File.WriteAllText(Path.Combine(folder, "module.json"), JsonSerializer.Serialize(manifest));
        return folder;
    }

    ModulePackageResult Verified(string folder)
    {
        var result = ModulePackage.Verify(folder, new AcceptingSignatures(), TrustedSet);
        Assert.True(result.Accepted, string.Join("; ", result.Problems));
        return result;
    }

    [Fact]
    public void The_reporting_module_runs_as_a_package_in_its_own_load_context()
    {
        var loaded = ModuleLoader.Load(Verified(MakeReportingPackage()));

        Assert.True(loaded.Loaded, string.Join("; ", loaded.Problems));
        Assert.Equal(ReportingId, loaded.Descriptor!.Id);
        Assert.Equal(ModuleContract.CurrentVersion, loaded.Descriptor.ContractVersion);
        var context = AssemblyLoadContext.GetLoadContext(loaded.Module!.GetType().Assembly);
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.Equal("module:" + ReportingId, context!.Name);
        Assert.NotSame(typeof(InventoryReportingModuleEntry), loaded.Module.GetType());
    }

    [Fact]
    public void A_package_that_was_not_accepted_is_not_loaded()
    {
        var folder = MakeReportingPackage();
        File.WriteAllText(Path.Combine(folder, "extra.txt"), "x");
        var result = ModulePackage.Verify(folder, new AcceptingSignatures(), TrustedSet);

        var loaded = ModuleLoader.Load(result);

        Assert.False(loaded.Loaded);
        Assert.Contains("not accepted", Assert.Single(loaded.Problems));
    }

    [Fact]
    public void A_file_replaced_after_the_check_is_never_loaded()
    {
        var folder = MakeReportingPackage();
        var verified = Verified(folder);
        File.AppendAllText(Path.Combine(folder, "ClassroomManager.Reporting.dll"), "tamper");

        var loaded = ModuleLoader.Load(verified);

        Assert.False(loaded.Loaded);
        Assert.Contains("changed after it was checked", Assert.Single(loaded.Problems));
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(1, 1)]
    public void The_contract_range_decides_whether_the_host_loads_the_module(int min, int max)
    {
        var verified = Verified(MakeReportingPackage(min: min, max: max));

        Assert.Equal(min <= 1 && max >= 1, ModuleLoader.Load(verified).Loaded);
        Assert.False(ModuleLoader.Load(verified, hostContract: 4).Loaded);
    }

    [Fact]
    public void A_module_that_claims_another_id_than_its_manifest_is_refused()
    {
        var verified = Verified(MakeReportingPackage(id: "dausuva.somebody.else"));

        var loaded = ModuleLoader.Load(verified);

        Assert.False(loaded.Loaded);
        Assert.Contains(loaded.Problems, problem => problem.Contains("dausuva.somebody.else") && problem.Contains(ReportingId));
    }

    [Fact]
    public void A_module_whose_capabilities_differ_from_its_manifest_is_refused()
    {
        var loaded = ModuleLoader.Load(Verified(MakeReportingPackage(capabilities: ["inventory.get", "screen.capture"])));

        Assert.False(loaded.Loaded);
        Assert.Contains(loaded.Problems, problem => problem.Contains("capabilities"));
    }

    [Theory]
    [InlineData("ClassroomManager.Reporting.Nothing")]
    [InlineData("ClassroomManager.Reporting.InventoryReportingModule")]
    [InlineData("ClassroomManager.Reporting.InventoryReportSession")]
    public void An_entry_type_that_is_missing_or_not_a_module_is_refused(string type)
    {
        var loaded = ModuleLoader.Load(Verified(MakeReportingPackage(entryType: type)));

        Assert.False(loaded.Loaded);
        Assert.NotEmpty(loaded.Problems);
    }

    [Fact]
    public void A_registry_lists_packages_and_keeps_the_reason_a_package_was_refused()
    {
        MakeReportingPackage();
        var broken = Directory.CreateDirectory(Path.Combine(_root, "dausuva.broken.thing")).FullName;
        File.WriteAllText(Path.Combine(broken, "module.json"), "{ not json");
        var registry = new ModuleRegistry();

        registry.AddPackages(ModuleDiscovery.Scan(_root, new AcceptingSignatures(), TrustedSet));

        Assert.Equal(2, registry.All.Count);
        var good = registry.Find(ReportingId)!;
        Assert.True(good.Usable);
        Assert.Equal(ModuleSource.Package, good.Source);
        var bad = registry.Find("dausuva.broken.thing")!;
        Assert.False(bad.Usable);
        Assert.NotEmpty(bad.Problems);
        Assert.Equal([ReportingId], registry.Usable.Select(item => item.Id));
    }

    [Fact]
    public void A_built_in_module_wins_over_a_package_with_the_same_id()
    {
        var registry = new ModuleRegistry();
        registry.AddBuiltIn(new InventoryReportingModuleEntry());
        MakeReportingPackage();

        registry.AddPackages(ModuleDiscovery.Scan(_root, new AcceptingSignatures(), TrustedSet));

        Assert.Equal(ModuleSource.BuiltIn, registry.Usable.Single().Source);
        Assert.Contains("already provided", registry.All.Single(item => item.Source == ModuleSource.Package).Problems.Single());
    }

    [Fact]
    public void A_module_cannot_be_registered_twice_as_built_in()
    {
        var registry = new ModuleRegistry();
        registry.AddBuiltIn(new InventoryReportingModuleEntry());

        Assert.Throws<InvalidOperationException>(() => registry.AddBuiltIn(new InventoryReportingModuleEntry()));
    }

    [Fact]
    public void A_module_is_on_unless_the_person_switched_it_off_and_unknown_or_refused_modules_are_off()
    {
        var registry = new ModuleRegistry();
        registry.AddBuiltIn(new InventoryReportingModuleEntry());
        var broken = Directory.CreateDirectory(Path.Combine(_root, "dausuva.broken.thing")).FullName;
        registry.AddPackages(ModuleDiscovery.Scan(_root, new AcceptingSignatures(), TrustedSet));

        Assert.True(registry.IsEnabled(ReportingId, null));
        Assert.True(registry.IsEnabled(ReportingId, ["something.else.entirely"]));
        Assert.False(registry.IsEnabled(ReportingId, [ReportingId]));
        Assert.False(registry.IsEnabled("dausuva.unknown.module", null));
        Assert.False(registry.IsEnabled("dausuva.broken.thing", null));
        Assert.True(Directory.Exists(broken));
    }

    [Fact]
    public void A_client_must_announce_every_capability_a_module_needs()
    {
        var module = new ModuleDescriptor("dausuva.demo.thing", "Demo", 1, ["screen.capture", "files.read"]);

        Assert.Empty(ModuleCompatibility.MissingClientCapabilities(module, ["files.read", "screen.capture", "more.stuff"]));
        Assert.Equal(["files.read"], ModuleCompatibility.MissingClientCapabilities(module, ["screen.capture"]));
        Assert.Equal(2, ModuleCompatibility.MissingClientCapabilities(module, []).Count);
        Assert.Empty(ModuleCompatibility.MissingClientCapabilities(new("dausuva.demo.none", "Demo", 1, []), []));
    }

    static void AssertSameDescriptor(ModuleDescriptor expected, ModuleDescriptor actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.ContractVersion, actual.ContractVersion);
        Assert.Equal(expected.ClientCapabilities, actual.ClientCapabilities);
    }

    public static TheoryData<string, Type> EveryModule() => new()
    {
#if REPORTING_MODULE
        { "dausuva.reporting.inventory", typeof(InventoryReportingModuleEntry) },
#endif
#if BROWSER_INVENTORY_MODULE
        { "dausuva.browser.inventory", typeof(ClassroomManager.BrowserInventory.BrowserInventoryModuleEntry) },
#endif
        { "dausuva.automation.scheduling", typeof(ClassroomManager.Automation.SchedulingModuleEntry) },
        { "dausuva.student-work.backup", typeof(ClassroomManager.StudentWork.StudentBackupModuleEntry) },
        { "dausuva.appearance.inventory", typeof(ClassroomManager.Appearance.AppearanceInventoryModuleEntry) },
        { "dausuva.software.inventory", typeof(ClassroomManager.SoftwareInventory.SoftwareInventoryModuleEntry) },
        { "dausuva.maintenance.inventory", typeof(ClassroomManager.Maintenance.MaintenanceInventoryModuleEntry) },
    };

    // The conformance suite: what every module, whoever writes it, must satisfy to be installed as a package.
    [Theory]
    [MemberData(nameof(EveryModule))]
    public void Every_module_conforms_to_the_contract_and_runs_as_a_package(string id, Type entry)
    {
        var instance = (IClassroomModule)Activator.CreateInstance(entry)!;
        var descriptor = instance.Describe();
        Assert.Equal(id, descriptor.Id);
        Assert.Equal(ModuleContract.CurrentVersion, descriptor.ContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Name));
        Assert.Equal(descriptor.ClientCapabilities.Distinct().Count(), descriptor.ClientCapabilities.Count);
        AssertSameDescriptor(descriptor, instance.Describe()); // asking twice gives the same answer; no work happens in Describe
        FrameworkReferenceAssertions.AssertOnly(entry.Assembly);

        var folder = MakePackage(entry.Assembly.Location, id, entry.FullName!, capabilities: [.. descriptor.ClientCapabilities], descriptor: descriptor);
        var loaded = ModuleLoader.Load(Verified(folder));

        Assert.True(loaded.Loaded, string.Join("; ", loaded.Problems));
        AssertSameDescriptor(descriptor, loaded.Descriptor!);
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(loaded.Module!.GetType().Assembly));
        Assert.Contains(BuiltInModules.Registry.Usable, module => module.Id == id && module.Name == descriptor.Name);
    }

    [Fact]
    public void Every_capability_a_module_names_is_a_command_the_client_can_serve()
    {
        var commands = typeof(Cmd).GetFields().Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!).ToHashSet();

        foreach (var module in BuiltInModules.Registry.Usable)
            foreach (var capability in module.Descriptor!.ClientCapabilities)
            {
                Assert.Contains(capability, commands);
                // Either the Client announces it in Ping, or it is a baseline command every Client of this protocol has.
                Assert.True(Protocol.Capabilities.Contains(capability) || Protocol.RequiredVersion(capability) == "1.0",
                    $"{module.Id} needs {capability}, which a Client neither announces nor has from the first protocol version.");
            }
    }

    [Fact]
    public void A_client_that_announces_its_capabilities_is_judged_by_them()
    {
        var module = new ModuleDescriptor("dausuva.demo.thing", "Demo", 1, [Cmd.WindowsUpdateScan, Cmd.ProgramsList]);
        var client = new ClientInfo { Version = Protocol.Version, CapabilitiesAdvertised = true };
        client.Capabilities.Add(Cmd.ProgramsList);

        Assert.Equal([Cmd.WindowsUpdateScan], client.MissingForModule(module));

        client.Capabilities.Add(Cmd.WindowsUpdateScan.ToUpperInvariant());
        Assert.Empty(client.MissingForModule(module));
    }

    [Fact]
    public void An_older_client_that_announces_nothing_is_judged_by_its_protocol_version()
    {
        var module = new ModuleDescriptor("dausuva.demo.thing", "Demo", 1, [Cmd.WindowsUpdateScan, Cmd.ProgramsList]);

        Assert.Empty(new ClientInfo { Version = "1.4" }.MissingForModule(module));
        Assert.Equal([Cmd.WindowsUpdateScan], new ClientInfo { Version = "1.3" }.MissingForModule(module));
        Assert.Equal([Cmd.WindowsUpdateScan, Cmd.ProgramsList], new ClientInfo { Version = "" }.MissingForModule(module));
    }

    [Fact]
    public void Every_registered_module_works_with_a_client_of_the_current_protocol_that_announces_the_standard_capabilities()
    {
        var client = new ClientInfo { Version = Protocol.Version, CapabilitiesAdvertised = true };
        foreach (var capability in Protocol.Capabilities) client.Capabilities.Add(capability);

        Assert.All(BuiltInModules.Registry.Usable, module => Assert.Empty(client.MissingForModule(module.Descriptor!)));
    }

    [Fact]
    public void Every_module_has_an_ascii_icon_within_the_limits()
    {
        Assert.All(BuiltInModules.Registry.Usable, module =>
        {
            var icon = module.Descriptor!.Icon;
            Assert.NotEmpty(icon);
            Assert.Empty(ModuleIcon.Problems(icon));
            Assert.True(icon.Count <= ModuleIcon.MaxLines && icon.All(line => line.Length <= ModuleIcon.MaxWidth));
        });
    }

    [Theory]
    [InlineData("tab\there")]
    [InlineData("accent \u00e9")]
    [InlineData("0123456789abcdefX")]
    public void An_icon_that_is_too_wide_or_not_plain_ascii_is_refused(string line)
    {
        Assert.NotEmpty(ModuleIcon.Problems([line]));
        Assert.NotEmpty(ModuleIcon.Problems(Enumerable.Repeat("x", ModuleIcon.MaxLines + 1).ToArray()));
        Assert.Empty(ModuleIcon.Problems([]));
        Assert.Empty(ModuleIcon.Problems(null));
    }

    sealed class FakeModule(ModuleDescriptor descriptor) : IClassroomModule
    {
        public ModuleDescriptor Describe() => descriptor;
    }

    [Fact]
    public void A_built_in_module_with_a_bad_icon_or_action_cannot_be_registered()
    {
        var registry = new ModuleRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.AddBuiltIn(new FakeModule(
            new("dausuva.demo.icon", "Demo", 1, []) { Icon = ["\u00e9"] })));
        Assert.Throws<InvalidOperationException>(() => registry.AddBuiltIn(new FakeModule(
            new("dausuva.demo.code", "Demo", 1, []) { ScheduledActions = [new("Bad Code", "Label")] })));
        Assert.Throws<InvalidOperationException>(() => registry.AddBuiltIn(new FakeModule(
            new("dausuva.demo.twice", "Demo", 1, []) { ScheduledActions = [new("same", "A"), new("same", "B")] })));
        Assert.Empty(registry.All);
    }

    [Fact]
    public void A_scheduled_action_belongs_to_one_module_and_follows_whether_it_is_on()
    {
        var registry = new ModuleRegistry();
        registry.AddBuiltIn(new FakeModule(new("dausuva.demo.first", "First", 1, []) { ScheduledActions = [new("first-job", "Do the first job")] }));

        Assert.Equal("dausuva.demo.first", registry.ActionOwner("first-job"));
        Assert.Null(registry.ActionOwner("lock"));
        Assert.Throws<InvalidOperationException>(() => registry.AddBuiltIn(new FakeModule(
            new("dausuva.demo.second", "Second", 1, []) { ScheduledActions = [new("first-job", "Again")] })));
        Assert.Equal(("first-job", "Do the first job"), (registry.ScheduledActions.Single().Action.Code, registry.ScheduledActions.Single().Action.Label));
    }

    [Fact]
    public void The_backup_and_update_scan_actions_come_from_their_modules_and_switch_with_them()
    {
        Assert.Equal(BuiltInModules.StudentWork, BuiltInModules.ActionOwner("backup"));
        Assert.Equal(BuiltInModules.Maintenance, BuiltInModules.ActionOwner("updatescan"));
        Assert.Null(BuiltInModules.ActionOwner("lock"));
        Assert.Contains(BuiltInModules.ModuleActions, action => action.Code == "backup" && action.Label.StartsWith("Back up student work"));
        Assert.Contains(BuiltInModules.ModuleActions, action => action.Code == "updatescan" && action.Label.StartsWith("Check for Windows updates"));
    }

    [Fact]
    public void The_conformance_suite_covers_every_module_the_host_registers()
    {
        var covered = EveryModule().Select(row => (string)row[0]).Order(StringComparer.Ordinal);

        Assert.Equal(BuiltInModules.Registry.All.Select(module => module.Id).Order(StringComparer.Ordinal), covered);
    }

    [Fact]
    public void Every_built_in_module_of_the_host_describes_itself_the_same_way()
    {
        var modules = BuiltInModules.Registry.Usable;

        Assert.NotEmpty(modules);
        Assert.All(modules, module =>
        {
            Assert.Equal(ModuleSource.BuiltIn, module.Source);
            Assert.Matches(@"^dausuva\.[a-z0-9-]+\.[a-z0-9-]+$", module.Id);
            Assert.False(string.IsNullOrWhiteSpace(module.Name));
            Assert.Equal(ModuleContract.CurrentVersion, module.Descriptor!.ContractVersion);
        });
        Assert.Equal(modules.Select(module => module.Id).Distinct().Count(), modules.Count);
        Assert.Equal(modules.Select(module => (module.Id, module.Name)), BuiltInModules.Available);
    }
}
