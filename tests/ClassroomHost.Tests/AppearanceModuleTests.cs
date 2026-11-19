// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Appearance;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class AppearanceModuleTests
{
    [Fact]
    public void Empty_inventory_does_not_invent_an_available_screen_saver()
    {
        var report = AppearanceInventoryModule.Project([], "off");
        Assert.Equal(0, report.Read);
        Assert.Empty(report.CommonNames);
        Assert.Empty(report.Usage);
    }

    [Fact]
    public void Common_names_are_case_insensitive_and_usage_is_counted_only_from_supplied_successful_reads()
    {
        var report = AppearanceInventoryModule.Project([
            new(true, "Example.scr", true, ["B.scr", "A.scr"]),
            new(true, "example.scr", false, ["a.scr", "B.scr", "C.scr"]),
            new(false, "ignored.scr", false, ["B.scr", "a.scr"])], "off");
        Assert.Equal(3, report.Read);
        Assert.Equal(1, report.PolicyManaged);
        Assert.Equal(["A.scr", "B.scr"], report.CommonNames);
        Assert.Equal([new AppearanceCount("Example.scr", 2), new AppearanceCount("off", 1)], report.Usage);
    }

    [Fact]
    public void Results_are_detached_read_only_snapshots()
    {
        var available = new List<string> { "A.scr" };
        var states = new List<ScreenSaverSnapshot> { new(false, "", false, available) };
        var report = AppearanceInventoryModule.Project(states, "off");
        available.Clear();
        states.Clear();
        Assert.Equal(["A.scr"], report.CommonNames);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)report.CommonNames).Clear());
    }

    [Fact]
    public void Core_adapter_does_not_forward_paths_or_unrelated_wire_properties()
    {
        var state = ScreenSaverState.FromJson(JsonNode.Parse("""
            {"active":true,"name":"A.scr","path":"private-path","private":"private-secret",
             "installed":[{"name":"A.scr","path":"private-path"}]}
            """)!.AsObject());
        var report = ScreenSaverInventory.Project([state]);
        Assert.Equal(1, report.Read);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(report));
        Assert.Equal("A.scr on 1", ScreenSaverInventory.Summarize(report));
    }

    [Fact]
    public void Module_references_only_framework_assemblies() =>
        FrameworkReferenceAssertions.AssertOnly(typeof(AppearanceInventoryModule).Assembly);
}
