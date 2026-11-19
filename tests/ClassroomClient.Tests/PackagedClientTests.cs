// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Client;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>The Client installed as a Windows package (T-235): its configuration from the kit file, the refusals, the firewall rules. Scratch folders only; the folder protection and the firewall are replaced.</summary>
public sealed class PackagedClientTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "classroom-packaged-" + Guid.NewGuid().ToString("N"));
    const string LabKey = "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs";

    public PackagedClientTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    [Fact]
    public void A_kit_with_only_a_key_gives_the_default_ports_and_roots()
    {
        var result = PackagedKit.BuildConfig($$"""{ "key": "{{LabKey}}" }""");
        Assert.Null(result.Error);
        var config = ClientConfig.Parse(result.ConfigJson!)!;
        Assert.Equal(LabKey, config.Psk);
        Assert.Equal(5050, config.TcpPort);
        Assert.Equal(5051, config.UdpPort);
        Assert.Equal([@"C:\Users"], config.AllowedRoots);
        Assert.Empty(config.AccessKeys);
    }

    [Fact]
    public void A_kit_with_ports_roots_and_named_keys_is_read_the_way_the_setup_scripts_read_it()
    {
        var result = PackagedKit.BuildConfig($$"""
            { "key": "{{LabKey}}", "tcpPort": 6060, "udpPort": 6061, "allowedRoots": ["D:\\Work"],
              "accessKeys": [ { "name": "Room 12 teacher", "role": "teacher", "key": "bbbbb-ccccc-ddddd-eeeee" }, { "name": "IT", "role": "Admin", "key": "fffff-ggggg-hhhhh-iiiii" } ] }
            """);
        Assert.Null(result.Error);
        var config = ClientConfig.Parse(result.ConfigJson!)!;
        Assert.Equal((6060, 6061), (config.TcpPort, config.UdpPort));
        Assert.Equal([@"D:\Work"], config.AllowedRoots);
        Assert.Equal([("Room 12 teacher", "Teacher"), ("IT", "Admin")], config.AccessKeys.Select(key => (key.Name, key.Role)));
    }

    [Theory]
    [InlineData("""{ "tcpPort": 5050 }""", "no \"key\"")]
    [InlineData("""{ "key": "short" }""", "at least 12")]
    [InlineData("""{ "key": " k7mq2-xh9vd-3ptwa " }""", "spaces")]
    [InlineData("""{ "key": "k7mq2-xh9vd-3ptwa", "tcpPort": 70000 }""", "1 to 65535")]
    [InlineData("""not json""", "JSON object")]
    public void A_kit_that_is_not_usable_is_refused_with_a_reason(string kit, string reason)
    {
        var result = PackagedKit.BuildConfig(kit);
        Assert.Null(result.ConfigJson);
        Assert.Contains(reason, result.Error);
    }

    [Theory]
    [InlineData("""{ "name": "Lab key", "role": "Teacher", "key": "bbbbb-ccccc-ddddd" }""", "reserved")]
    [InlineData("""{ "name": "A", "role": "Observer", "key": "bbbbb-ccccc-ddddd" }""", "Teacher or Admin")]
    [InlineData("""{ "name": "A", "role": "Teacher", "key": "replace-with-a-real-key" }""", "placeholder")]
    [InlineData("""{ "name": "A", "role": "Teacher", "key": "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs" }""", "already uses")]
    public void A_named_key_that_would_weaken_the_lab_is_refused_and_no_key_is_repeated_in_the_reason(string entry, string reason)
    {
        var result = PackagedKit.BuildConfig($$"""{ "key": "{{LabKey}}", "accessKeys": [ {{entry}} ] }""");
        Assert.Null(result.ConfigJson);
        Assert.Contains(reason, result.Error);
        Assert.DoesNotContain(LabKey, result.Error);
        Assert.DoesNotContain("bbbbb-ccccc-ddddd", result.Error);
    }

    [Fact]
    public void Two_named_keys_with_the_same_name_or_the_same_key_are_refused()
    {
        var sameName = PackagedKit.BuildConfig($$"""{ "key": "{{LabKey}}", "accessKeys": [ { "name": "A", "role": "Teacher", "key": "bbbbb-ccccc-ddddd" }, { "name": "a", "role": "Teacher", "key": "eeeee-fffff-ggggg" } ] }""");
        Assert.Contains("Two access keys", sameName.Error);
        var sameKey = PackagedKit.BuildConfig($$"""{ "key": "{{LabKey}}", "accessKeys": [ { "name": "A", "role": "Teacher", "key": "bbbbb-ccccc-ddddd" }, { "name": "B", "role": "Teacher", "key": "bbbbb-ccccc-ddddd" } ] }""");
        Assert.Contains("already uses", sameKey.Error);
    }

    [Fact]
    public void A_packaged_Client_with_a_good_kit_writes_the_configuration_opens_the_firewall_and_removes_the_kit()
    {
        File.WriteAllText(Path.Combine(_folder, PackagedKit.KitFileName), $$"""{ "key": "{{LabKey}}", "tcpPort": 6060 }""");
        var log = new List<string>();
        IReadOnlyList<string[]>? firewall = null;
        string? protectedFolder = null;

        var written = PackagedKit.BootstrapFromKit(true, _folder, log.Add, commands => firewall = commands, folder => protectedFolder = folder);

        Assert.True(written);
        Assert.Equal(LabKey, ClientConfig.Parse(File.ReadAllText(Path.Combine(_folder, "client.json")))!.Psk);
        Assert.False(File.Exists(Path.Combine(_folder, PackagedKit.KitFileName)));
        Assert.Equal(_folder, protectedFolder);
        Assert.Contains(firewall!, command => command.Contains("localport=6060"));
        Assert.DoesNotContain(log, line => line.Contains(LabKey));
    }

    [Fact]
    public void Nothing_happens_when_the_Client_is_not_packaged_has_no_kit_has_a_bad_kit_or_is_already_configured()
    {
        var log = new List<string>();
        var kit = Path.Combine(_folder, PackagedKit.KitFileName);
        File.WriteAllText(kit, $$"""{ "key": "{{LabKey}}" }""");
        Assert.False(PackagedKit.BootstrapFromKit(false, _folder, log.Add, _ => { }, _ => { }));
        Assert.True(File.Exists(kit));
        Assert.False(File.Exists(Path.Combine(_folder, "client.json")));

        File.WriteAllText(kit, """{ "key": "short" }""");
        Assert.False(PackagedKit.BootstrapFromKit(true, _folder, log.Add, _ => { }, _ => { }));
        Assert.True(File.Exists(kit), "A kit that was not used stays where the administrator put it.");
        Assert.Contains(log, line => line.Contains("not used"));

        File.Delete(kit);
        log.Clear();
        Assert.False(PackagedKit.BootstrapFromKit(true, _folder, log.Add, _ => { }, _ => { }));
        Assert.Contains(log, line => line.Contains("waiting"));

        File.WriteAllText(Path.Combine(_folder, "client.json"), """{ "psk": "an-existing-configuration" }""");
        File.WriteAllText(kit, $$"""{ "key": "{{LabKey}}" }""");
        Assert.False(PackagedKit.BootstrapFromKit(true, _folder, log.Add, _ => { }, _ => { }));
        Assert.Contains("an-existing-configuration", File.ReadAllText(Path.Combine(_folder, "client.json")));
    }

    [Fact]
    public void A_packaged_Client_does_not_update_remove_or_restart_helpers_itself_and_an_installed_one_is_unchanged()
    {
        Assert.Contains("MSIX", PackagedClient.SelfUpdateRefusal(true));
        Assert.Contains("MSIX", PackagedClient.UninstallRefusal(true));
        Assert.False(PackagedClient.ServiceStartsHelpers(true));
        Assert.Null(PackagedClient.SelfUpdateRefusal(false));
        Assert.Null(PackagedClient.UninstallRefusal(false));
        Assert.True(PackagedClient.ServiceStartsHelpers(false));
    }

    [Fact]
    public void The_firewall_rules_are_the_three_the_setup_scripts_make_for_Domain_and_Private_networks_only()
    {
        var commands = PackagedKit.FirewallCommands(5050, 5051);
        var adds = commands.Where(command => command[2] == "add").ToList();
        Assert.Equal(3, adds.Count);
        Assert.All(adds, command => Assert.Contains("profile=domain,private", command));
        Assert.Contains(adds, command => command.Contains("protocol=TCP") && command.Contains("localport=5050"));
        Assert.Contains(adds, command => command.Contains("protocol=UDP") && command.Contains("localport=5051"));
        Assert.Contains(adds, command => command.Contains("protocol=icmpv4:8,any"));
        Assert.Equal(3, commands.Count(command => command[2] == "delete"));
    }
}
