// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ClassroomManager.Modules;
using Xunit;

namespace ClassroomHost.Tests;

// Installing, updating, removing and rolling back module packages: checked first, staged, applied at start, never half done.
public sealed class ModulePackageStoreTests : IDisposable
{
    const string Id = "dausuva.sample.demo";
    const string Trusted = "AAAA0000AAAA0000AAAA0000AAAA0000AAAA0000AAAA0000AAAA0000AAAA0000";

    readonly string _scratch = Path.Combine(Path.GetTempPath(), "module-store-test-" + Guid.NewGuid().ToString("N"));
    string Root => Path.Combine(_scratch, "modules");
    string Source(string name) => Path.Combine(_scratch, "source", name);

    public void Dispose()
    {
        try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    sealed class Signatures(bool valid = true) : IModuleSignatureVerifier
    {
        public SignatureCheck Check(string path) => new(valid, Trusted, valid ? "" : "The signature is not valid.");
    }

    static readonly IReadOnlySet<string> TrustedSet = new HashSet<string> { Trusted };

    /// <summary>A well-formed package folder named after its id, with one program file whose content tells the versions apart.</summary>
    string Package(string version, string id = Id)
    {
        var folder = Path.Combine(Source(version), id);
        Directory.CreateDirectory(folder);
        var content = System.Text.Encoding.UTF8.GetBytes("program " + version);
        File.WriteAllBytes(Path.Combine(folder, "Demo.dll"), content);
        File.WriteAllText(Path.Combine(folder, "module.json"), JsonSerializer.Serialize(new
        {
            schema = 1, id, publisher = "dausuva", name = "Demo module", version,
            contract = new { min = 1, max = 1 }, os = "windows", architecture = "x64",
            entry = new { assembly = "Demo.dll", type = "Demo.Entry" },
            client = new { minProtocol = "1.14", capabilities = Array.Empty<string>() },
            files = new[] { new { path = "Demo.dll", sha256 = Convert.ToHexString(SHA256.HashData(content)) } },
        }));
        return folder;
    }

    string Installed() => File.ReadAllText(Path.Combine(Root, Id, "Demo.dll"));

    void Install(string version)
    {
        var change = ModulePackageStore.Stage(Package(version), Root, new Signatures(), TrustedSet);
        Assert.True(change.Done, string.Join(" ", change.Messages));
        Assert.DoesNotContain(ModulePackageStore.ApplyPending(Root), line => line.StartsWith("Could not"));
    }

    [Fact]
    public void A_checked_package_is_staged_and_nothing_live_changes_until_it_is_applied()
    {
        var change = ModulePackageStore.Stage(Package("1.0.0"), Root, new Signatures(), TrustedSet);

        Assert.True(change.Done);
        Assert.False(Directory.Exists(Path.Combine(Root, Id)));
        Assert.True(File.Exists(Path.Combine(Root, ".pending", Id, "module.json")));

        Assert.Contains("Installed " + Id + ".", ModulePackageStore.ApplyPending(Root));
        Assert.Equal("program 1.0.0", Installed());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(Root, ".pending")));
    }

    [Fact]
    public void A_package_that_fails_a_check_is_refused_and_leaves_nothing_behind()
    {
        var folder = Package("1.0.0");
        File.WriteAllText(Path.Combine(folder, "Demo.dll"), "tampered");

        var change = ModulePackageStore.Stage(folder, Root, new Signatures(), TrustedSet);

        Assert.False(change.Done);
        Assert.Contains(change.Messages, message => message.Contains("SHA-256"));
        Assert.False(Directory.Exists(Path.Combine(Root, ".pending", Id)));
        Assert.False(ModulePackageStore.Stage(Package("1.0.1"), Root, new Signatures(valid: false), TrustedSet).Done);
        Assert.False(ModulePackageStore.Stage(Package("1.0.2"), Root, new Signatures(), new HashSet<string>()).Done);   // a signer nobody trusts
    }

    [Fact]
    public void An_update_keeps_the_old_version_for_one_rollback_and_a_rollback_can_be_undone()
    {
        Install("1.0.0");
        Install("1.1.0");

        Assert.Equal("program 1.1.0", Installed());
        Assert.True(ModulePackageStore.StageRollback(Id, Root).Done);
        ModulePackageStore.ApplyPending(Root);
        Assert.Equal("program 1.0.0", Installed());

        Assert.True(ModulePackageStore.StageRollback(Id, Root).Done);   // the version rolled back from is the new step back
        ModulePackageStore.ApplyPending(Root);
        Assert.Equal("program 1.1.0", Installed());
    }

    [Fact]
    public void Only_one_step_back_is_kept()
    {
        Install("1.0.0");
        Install("1.1.0");
        Install("1.2.0");

        ModulePackageStore.StageRollback(Id, Root);
        ModulePackageStore.ApplyPending(Root);

        Assert.Equal("program 1.1.0", Installed());
    }

    [Fact]
    public void An_older_or_equal_version_is_refused_unless_it_was_asked_for()
    {
        Install("1.1.0");

        var same = ModulePackageStore.Stage(Package("1.1.0"), Root, new Signatures(), TrustedSet);
        var older = ModulePackageStore.Stage(Package("1.0.0"), Root, new Signatures(), TrustedSet);
        var asked = ModulePackageStore.Stage(Package("1.0.0"), Root, new Signatures(), TrustedSet, allowOlder: true);

        Assert.False(same.Done);
        Assert.False(older.Done);
        Assert.Contains("older", older.Messages[0]);
        Assert.True(asked.Done);
    }

    [Fact]
    public void A_removal_moves_the_module_aside_and_a_rollback_brings_it_back()
    {
        Install("1.0.0");

        Assert.True(ModulePackageStore.StageRemoval(Id, Root).Done);
        Assert.True(Directory.Exists(Path.Combine(Root, Id)));   // still there until the next start
        ModulePackageStore.ApplyPending(Root);
        Assert.False(Directory.Exists(Path.Combine(Root, Id)));

        Assert.True(ModulePackageStore.StageRollback(Id, Root).Done);
        ModulePackageStore.ApplyPending(Root);
        Assert.Equal("program 1.0.0", Installed());
    }

    [Fact]
    public void Removing_or_rolling_back_what_is_not_there_is_refused()
    {
        Directory.CreateDirectory(Root);

        Assert.False(ModulePackageStore.StageRemoval(Id, Root).Done);
        Assert.False(ModulePackageStore.StageRollback(Id, Root).Done);
    }

    [Fact]
    public void A_staged_install_replaces_an_earlier_staged_copy_and_cancels_a_staged_removal()
    {
        Install("1.0.0");
        ModulePackageStore.StageRemoval(Id, Root);
        ModulePackageStore.Stage(Package("1.1.0"), Root, new Signatures(), TrustedSet);
        ModulePackageStore.Stage(Package("1.2.0"), Root, new Signatures(), TrustedSet);

        ModulePackageStore.ApplyPending(Root);

        Assert.Equal("program 1.2.0", Installed());
    }

    [Fact]
    public void A_swap_that_cannot_happen_leaves_the_module_as_it_was_and_stays_staged()
    {
        Install("1.0.0");
        ModulePackageStore.Stage(Package("1.1.0"), Root, new Signatures(), TrustedSet);
        var holding = File.Open(Path.Combine(Root, Id, "Demo.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);   // a running Host holds its files like this

        IReadOnlyList<string> report;
        try { report = ModulePackageStore.ApplyPending(Root); }
        finally { holding.Dispose(); }

        Assert.Contains(report, line => line.StartsWith("Could not apply"));
        Assert.True(Directory.Exists(Path.Combine(Root, ".pending", Id)));
        Assert.Equal("program 1.0.0", Installed());
        ModulePackageStore.ApplyPending(Root);
        Assert.Equal("program 1.1.0", Installed());
    }

    [Fact]
    public void Discovery_ignores_the_stores_own_folders()
    {
        Install("1.0.0");
        Install("1.1.0");
        ModulePackageStore.Stage(Package("1.2.0"), Root, new Signatures(), TrustedSet);

        var found = ModuleDiscovery.Scan(Root, new Signatures(), TrustedSet);

        var only = Assert.Single(found);
        Assert.True(only.Accepted);
        Assert.Equal("1.1.0", only.Manifest!.Version);
    }
}
