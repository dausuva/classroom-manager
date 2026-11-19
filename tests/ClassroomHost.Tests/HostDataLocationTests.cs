// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>Where the Host keeps its files: --data-folder, CLASSROOM_DATA_FOLDER, or a ClassroomHost.portable file beside the program. None of these tests touches the real HostSettings.DataFolder.</summary>
public sealed class HostDataLocationTests : IDisposable
{
    readonly string _root = Path.Combine(TestDataFolder.Scratch, "data-location-" + Guid.NewGuid().ToString("N"));

    public HostDataLocationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static Func<string, string?> NoEnvironment => _ => null;

    [Fact]
    public void Nothing_is_changed_when_no_folder_is_asked_for()
    {
        var program = Path.Combine(_root, "program");
        Directory.CreateDirectory(program);

        var choice = HostDataLocation.Resolve(["--cli", "list", "--json"], program, NoEnvironment);

        Assert.Null(choice.Folder);
        Assert.Equal(["--cli", "list", "--json"], choice.Args);
    }

    [Fact]
    public void The_switch_wins_over_the_variable_and_the_portable_file_and_is_taken_out_of_the_arguments()
    {
        var program = Path.Combine(_root, "program");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, HostDataLocation.PortableMarker), "");
        string? Env(string name) => name == HostDataLocation.Variable ? Path.Combine(_root, "from-variable") : null;

        var first = HostDataLocation.Resolve(["--data-folder", Path.Combine(_root, "from-switch"), "--cli", "list"], program, Env);
        var last = HostDataLocation.Resolve(["--cli", "list", "--DATA-FOLDER=" + Path.Combine(_root, "from-switch")], program, Env);

        Assert.Equal(Path.Combine(_root, "from-switch"), first.Folder);
        Assert.Equal(HostDataLocation.Switch, first.Source);
        Assert.Equal(["--cli", "list"], first.Args);
        Assert.Equal(Path.Combine(_root, "from-switch"), last.Folder);
        Assert.Equal(["--cli", "list"], last.Args);
    }

    [Fact]
    public void The_variable_wins_over_the_portable_file_and_the_portable_file_means_a_folder_beside_the_program()
    {
        var program = Path.Combine(_root, "program");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, HostDataLocation.PortableMarker), "");

        var variable = HostDataLocation.Resolve([], program, name => name == HostDataLocation.Variable ? Path.Combine(_root, "from-variable") : null);
        var portable = HostDataLocation.Resolve([], program, NoEnvironment);

        Assert.Equal(Path.Combine(_root, "from-variable"), variable.Folder);
        Assert.Equal(HostDataLocation.Variable, variable.Source);
        Assert.Equal(Path.Combine(program, HostDataLocation.PortableFolder), portable.Folder);
        Assert.Equal(HostDataLocation.PortableMarker, portable.Source);
        // a variable that is only spaces is no choice
        Assert.Equal(HostDataLocation.PortableMarker, HostDataLocation.Resolve([], program, name => name == HostDataLocation.Variable ? "   " : null).Source);
    }

    [Fact]
    public void A_switch_without_a_folder_is_refused()
    {
        Assert.Throws<ArgumentException>(() => HostDataLocation.Resolve(["--data-folder"], _root, NoEnvironment));
        Assert.Throws<ArgumentException>(() => HostDataLocation.Resolve(["--data-folder", "--cli"], _root, NoEnvironment));
        Assert.Throws<ArgumentException>(() => HostDataLocation.Resolve(["--data-folder="], _root, NoEnvironment));
    }

    [Fact]
    public void Apply_creates_the_folder_checks_it_is_writable_sets_it_and_leaves_no_test_file_behind()
    {
        var folder = Path.Combine(_root, "new", "data");
        string? set = null;

        var rest = HostDataLocation.Apply(["--data-folder", folder, "--cli", "list"], _root, NoEnvironment, value => set = value);

        Assert.Equal(folder, set);
        Assert.Equal(["--cli", "list"], rest);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
        Assert.NotEqual(folder, HostSettings.DataFolder);   // the real setting of this test run was not moved
    }

    [Fact]
    public void Apply_with_no_choice_sets_nothing_and_a_folder_that_cannot_be_used_says_why()
    {
        string? set = null;
        Assert.Equal(["--cli"], HostDataLocation.Apply(["--cli"], _root, NoEnvironment, value => set = value));
        Assert.Null(set);

        var file = Path.Combine(_root, "a file");
        File.WriteAllText(file, "x");   // a folder cannot be made where a file is
        var problem = Assert.Throws<InvalidOperationException>(() => HostDataLocation.Apply(["--data-folder", Path.Combine(file, "inside")], _root, NoEnvironment, value => set = value));
        Assert.Contains("cannot be used", problem.Message);
        Assert.Contains("--data-folder", problem.Message);
        Assert.Null(set);
        Assert.Contains("needs a folder", Assert.Throws<InvalidOperationException>(() => HostDataLocation.Apply(["--data-folder"], _root, NoEnvironment, _ => { })).Message);
    }

    [Fact]
    public void The_host_stores_settings_and_keys_in_the_chosen_folder_and_not_in_the_default_one()
    {
        var folder = Path.Combine(_root, "profile");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "host.json");

        var settings = HostSettings.Load(path);
        settings.Psk = "a test secret of enough length";
        settings.Save(path);

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.ChangeExtension(path, ".keys")));
        Assert.DoesNotContain("a test secret of enough length", File.ReadAllText(path));
        Assert.Equal("a test secret of enough length", HostSettings.Load(path).Psk);
    }
}
