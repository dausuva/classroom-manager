// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Modules;
using Xunit;

namespace ClassroomHost.Tests;

// The command line sees the same modules as the Host window: what is on, what each asks for, and why a package was refused.
public sealed class CliModulesTests
{
    static ModuleRegistry Registry()
    {
        var registry = new ModuleRegistry();
        registry.AddBuiltIn(new ClassroomManager.Appearance.AppearanceInventoryModuleEntry());
        registry.AddBuiltIn(new ClassroomManager.SoftwareInventory.SoftwareInventoryModuleEntry());
        registry.AddPackages([new ModulePackageResult(@"C:\modules\broken", null, ["The files do not match their checksums."])]);
        return registry;
    }

    [Fact]
    public void Every_module_is_listed_with_its_state_ports_and_administrator_commands()
    {
        var settings = new HostSettings { DisabledModules = [] };

        var results = CliModules.List(Registry(), settings);

        var appearance = results.Single(result => result.Address == BuiltInModules.Appearance);
        Assert.True(appearance.Ok);
        Assert.Equal("built in", appearance.Room);
        Assert.StartsWith("on ", appearance.Detail);
        Assert.Contains("client.upload", appearance.Detail);
        Assert.Contains("signin.image.set", appearance.Detail);   // the one command it sends with administrator rights
    }

    [Fact]
    public void A_module_switched_off_in_Host_settings_is_listed_as_off()
    {
        var settings = new HostSettings { DisabledModules = [BuiltInModules.Software, BuiltInModules.Appearance] };

        var list = CliModules.List(Registry(), settings);
        var software = list.Single(result => result.Address == BuiltInModules.Software);

        Assert.StartsWith("off", software.Detail);
        Assert.True(software.Ok);
        // Appearance is a core module (in every build), and switched off like the others.
        Assert.StartsWith("off", list.Single(result => result.Address == BuiltInModules.Appearance).Detail);
    }

    [Fact]
    public void A_refused_package_says_why_and_fails_the_command()
    {
        var results = CliModules.List(Registry(), new HostSettings());

        var refused = results.Single(result => result.Room == "package");
        Assert.False(refused.Ok);
        Assert.Contains("do not match their checksums", refused.Detail);
        Assert.Equal(1, CliReport.ExitCode(results));
    }

    [Fact]
    public async Task The_command_prints_the_modules_of_this_Host_as_text_and_as_json_without_contacting_a_computer()
    {
        var folder = Directory.CreateTempSubdirectory("cm-cli-modules-").FullName;
        try
        {
            var path = Path.Combine(folder, "host.json");
            new HostSettings().Save(path);
            var text = new StringWriter();
            await HostCli.RunAsync(["modules", "--settings", path], text, new StringWriter());
            // Loading a fresh settings file fills in its generated ids once; after that the command leaves the file as it is.
            var before = File.ReadAllBytes(path);
            var json = new StringWriter();
            await HostCli.RunAsync(["modules", "--json", "--settings", path], json, new StringWriter());

            Assert.Contains(BuiltInModules.StudentWork.Split('.')[1], text.ToString());   // a built-in module is listed
            var parsed = JsonNode.Parse(json.ToString())!;
            Assert.Contains(parsed["results"]!.AsArray(), item => item!["address"]!.GetValue<string>() == BuiltInModules.Appearance);
            Assert.Equal(before, File.ReadAllBytes(path));   // it changes nothing
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Modules_takes_no_target()
    {
        Assert.Equal("modules", CliParser.Parse(["modules"]).Command);
        Assert.Contains("ClassroomCli modules", CliParser.Usage);
    }
}
