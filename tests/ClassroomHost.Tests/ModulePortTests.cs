// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ClassroomManager.Host;
using ClassroomManager.Modules;
using ClassroomManager.Reporting;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The first ports of the module broker: log, time and private storage, and what a manifest and a descriptor may ask for.</summary>
public sealed class ModulePortTests : IDisposable
{
    const string Id = "dausuva.demo.ports";
    readonly string _root = Path.Combine(Path.GetTempPath(), "cm-ports-" + Guid.NewGuid().ToString("N"));

    public ModulePortTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // A junction must be removed on its own first, or the delete would follow it.
        foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Where(path => new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)))
            try { Directory.Delete(directory); } catch (IOException) { }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static ModuleDescriptor Asking(params string[] ports) =>
        new(Id, "Demo", ModuleContract.CurrentVersion, []) { Ports = ports.Select(name => new ModulePortRequest(name, 1)).ToArray() };

    ModuleServices Grant(ModuleDescriptor descriptor, List<string>? lines = null, TimeProvider? time = null) =>
        ModuleBroker.Grant(descriptor, () => _root, line => lines?.Add(line), time ?? TimeProvider.System);

    [Fact]
    public void A_module_is_given_only_the_ports_it_asked_for()
    {
        var services = Grant(Asking(ModulePorts.Log));

        Assert.NotNull(services.Log);
        Assert.Throws<ModulePortException>(() => services.Storage);
        Assert.Throws<ModulePortException>(() => services.Time);

        var none = Grant(Asking());
        Assert.Throws<ModulePortException>(() => none.Log);
        Assert.Same(TimeProvider.System, Grant(Asking(ModulePorts.Time)).Time);
    }

    [Fact]
    public void A_log_line_carries_the_module_id_and_is_cleaned_and_cut()
    {
        var lines = new List<string>();
        var log = Grant(Asking(ModulePorts.Log), lines).Log;

        log.Write("hello\r\nworld\t!");
        log.Write("   ");
        log.Write("");
        log.Write(new string('x', 2000));

        Assert.Equal(2, lines.Count);
        Assert.Equal($"[{Id}] hello  world !", lines[0]);
        Assert.Equal($"[{Id}] ".Length + ModulePorts.MaxLogLineLength, lines[1].Length);
        Assert.DoesNotContain(lines, line => line.Contains('\n') || line.Contains('\r'));
    }

    [Fact]
    public void A_module_cannot_flood_the_log()
    {
        var lines = new List<string>();
        var clock = new ManualTime(new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero));
        var log = Grant(Asking(ModulePorts.Log), lines, clock).Log;

        for (var i = 0; i < ModulePorts.MaxLogLinesPerMinute + 40; i++) log.Write("line " + i);
        Assert.Equal(ModulePorts.MaxLogLinesPerMinute, lines.Count);

        clock.Now = clock.Now.AddSeconds(61);
        log.Write("after a minute");
        Assert.Equal("[" + Id + "] after a minute", lines[^1]);
    }

    [Fact]
    public void Private_storage_keeps_files_lists_them_and_forgets_them()
    {
        var storage = Grant(Asking(ModulePorts.StoragePrivate)).Storage;

        Assert.Null(storage.Read("state.json"));
        storage.Write("state.json", [1, 2, 3]);
        storage.Write("a.txt", [9]);
        storage.Write("state.json", [4, 5]);

        Assert.Equal([4, 5], storage.Read("state.json"));
        Assert.Equal(["a.txt", "state.json"], storage.List());
        Assert.Equal(3, storage.UsedBytes);
        Assert.True(storage.Delete("a.txt"));
        Assert.False(storage.Delete("a.txt"));
        Assert.Equal(["state.json"], storage.List());
        Assert.True(Directory.Exists(Path.Combine(_root, "modules", Id, "data")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("..\\x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:x")]
    [InlineData("x:stream")]
    [InlineData("name.")]
    [InlineData(".hidden")]
    [InlineData("a b")]
    [InlineData("x.tmp-123")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123x")]
    public void A_file_name_is_never_a_path(string name)
    {
        var storage = Grant(Asking(ModulePorts.StoragePrivate)).Storage;

        Assert.Throws<ModulePortException>(() => storage.Write(name, [1]));
        Assert.Throws<ModulePortException>(() => storage.Read(name));
        Assert.Throws<ModulePortException>(() => storage.Delete(name));
        Assert.Throws<ModulePortException>(() => storage.Write(null!, [1]));
    }

    [Fact]
    public void Private_storage_enforces_its_limits()
    {
        var storage = Grant(Asking(ModulePorts.StoragePrivate)).Storage;

        Assert.Throws<ModulePortException>(() => storage.Write("big", new byte[ModulePorts.StorageMaxFileBytes + 1]));
        for (var i = 0; i < ModulePorts.StorageQuotaBytes / ModulePorts.StorageMaxFileBytes; i++) storage.Write("part" + i, new byte[ModulePorts.StorageMaxFileBytes]);
        Assert.Throws<ModulePortException>(() => storage.Write("one-more", [1]));
        // Rewriting a file in place does not count its old size twice.
        storage.Write("part0", new byte[ModulePorts.StorageMaxFileBytes]);
        Assert.Equal(ModulePorts.StorageQuotaBytes, storage.UsedBytes);
    }

    [Fact]
    public void Private_storage_limits_the_number_of_files()
    {
        var storage = Grant(Asking(ModulePorts.StoragePrivate)).Storage;

        for (var i = 0; i < ModulePorts.StorageMaxFiles; i++) storage.Write("f" + i, [1]);

        Assert.Throws<ModulePortException>(() => storage.Write("extra", [1]));
        storage.Write("f0", [2]);
        Assert.Equal(ModulePorts.StorageMaxFiles, storage.List().Count);
    }

    [Fact]
    public void Two_modules_never_see_each_others_files()
    {
        var first = Grant(Asking(ModulePorts.StoragePrivate)).Storage;
        var second = ModuleBroker.Grant(new("dausuva.demo.other", "Other", 1, []) { Ports = [new(ModulePorts.StoragePrivate, 1)] }, () => _root, _ => { }, TimeProvider.System).Storage;

        first.Write("secret", [7]);

        Assert.Null(second.Read("secret"));
        Assert.Empty(second.List());
    }

    [Fact]
    public void A_module_folder_that_is_a_junction_is_refused()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var modules = Directory.CreateDirectory(Path.Combine(_root, "modules", Id)).FullName;
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(modules, "data")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var process = Process.Start(start)!) process.WaitForExit();
        Assert.True(new DirectoryInfo(Path.Combine(modules, "data")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        var storage = Grant(Asking(ModulePorts.StoragePrivate)).Storage;

        Assert.Throws<ModulePortException>(() => storage.Write("x", [1]));
        Assert.Empty(Directory.GetFiles(outside));
    }

    static object Manifest(object? ports, string fileSha = "", string entry = "ClassroomManager.Reporting.InventoryReportingModuleEntry") => new
    {
        schema = 1, id = InventoryReportingModule.Id, publisher = "dausuva", name = "Inventory CSV export", version = "1.0.0",
        contract = new { min = 1, max = 1 }, os = "windows", architecture = "x64",
        entry = new { assembly = "ClassroomManager.Reporting.dll", type = entry },
        client = new { minProtocol = "1.0", capabilities = new[] { "programs.list" } },
        files = new[] { new { path = "ClassroomManager.Reporting.dll", sha256 = fileSha.Length > 0 ? fileSha : new string('a', 64) } },
        ports,
    };

    static IReadOnlyList<string> Problems(object? ports) =>
        ModuleManifest.TryParse(JsonSerializer.Serialize(Manifest(ports)), out _)!.Validate();

    [Fact]
    public void A_manifest_may_ask_for_known_ports_once_at_a_version_that_exists()
    {
        Assert.Empty(Problems(null));
        Assert.Empty(Problems(Array.Empty<object>()));
        Assert.Empty(Problems(new[] { new { name = "log", version = 1 }, new { name = "storage.private", version = 1 }, new { name = "time", version = 1 } }));

        Assert.Contains(Problems(new[] { new { name = "network", version = 1 } }), problem => problem.Contains("network"));
        Assert.Contains(Problems(new[] { new { name = "log", version = 2 } }), problem => problem.Contains("version 2"));
        Assert.Contains(Problems(new[] { new { name = "log", version = 0 } }), problem => problem.Contains("version 0"));
        Assert.Contains(Problems(new[] { new { name = "log", version = 1 }, new { name = "log", version = 1 } }), problem => problem.Contains("twice"));
    }

    [Fact]
    public void A_module_whose_ports_differ_from_its_manifest_is_refused_at_load()
    {
        var folder = Path.Combine(_root, InventoryReportingModule.Id);
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, "ClassroomManager.Reporting.dll");
        File.Copy(typeof(InventoryReportingModuleEntry).Assembly.Location, dll);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)));
        File.WriteAllText(Path.Combine(folder, "module.json"), JsonSerializer.Serialize(Manifest(new[] { new { name = "log", version = 1 } }, hash)));
        var verified = ModulePackage.Verify(folder, new AcceptAll(), new HashSet<string> { "A" });

        var loaded = ModuleLoader.Load(verified);

        Assert.True(verified.Accepted, string.Join("; ", verified.Problems));
        Assert.False(loaded.Loaded);
        Assert.Contains(loaded.Problems, problem => problem.Contains("ports"));
    }

    sealed class AcceptAll : IModuleSignatureVerifier
    {
        public SignatureCheck Check(string path) => new(true, "A", "");
    }

    sealed class Recorder(ModuleDescriptor descriptor) : IClassroomModule
    {
        public ModuleServices? Received { get; private set; }
        public ModuleDescriptor Describe() => descriptor;
        public void Attach(ModuleServices services) => Received = services;
    }

    [Fact]
    public void The_registry_attaches_each_usable_module_with_only_its_own_ports()
    {
        var logger = new Recorder(Asking(ModulePorts.Log));
        var plain = new Recorder(new("dausuva.demo.plain", "Plain", 1, []));
        var registry = new ModuleRegistry();
        registry.AddBuiltIn(logger);
        registry.AddBuiltIn(plain);

        registry.AttachAll(descriptor => Grant(descriptor));

        Assert.NotNull(logger.Received!.Log);
        Assert.Throws<ModulePortException>(() => logger.Received.Storage);
        Assert.Throws<ModulePortException>(() => plain.Received!.Log);
    }

    [Fact]
    public void A_built_in_module_that_asks_for_an_unknown_port_cannot_be_registered()
    {
        var registry = new ModuleRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.AddBuiltIn(new Recorder(Asking("network"))));
        Assert.Empty(registry.All);
    }

    [Fact]
    public void Every_port_has_a_sentence_for_the_person_who_can_switch_the_module_off()
    {
        foreach (var port in ModulePorts.Known)
        {
            var sentence = Assert.Single(BuiltInModules.PortSentences(Asking(port)));
            Assert.StartsWith("This module ", sentence);
            Assert.DoesNotContain("does not know", sentence);
        }
        Assert.Empty(BuiltInModules.PortSentences(Asking()));
    }
}
