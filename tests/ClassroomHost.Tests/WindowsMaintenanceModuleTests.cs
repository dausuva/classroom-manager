// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Maintenance;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class WindowsMaintenanceModuleTests
{
    [Fact]
    public void Captured_shape_projects_existing_display_without_forwarding_update_id()
    {
        var report = WindowsMaintenanceInventory.ProjectScan(JsonNode.Parse("""
            {"updates":[{"id":"private-install-identity","title":"Feature","kb":["KB123","456"],
              "size":1572864,"feature":true,"optional":true,"requiresRestart":true,"private":"secret"},
              {"title":"Driver","optional":true},{"title":"Ordinary","size":-1}]}
            """)!.AsObject());
        Assert.Equal(3, report.Updates.Count);
        Assert.Equal(1, report.Features);
        Assert.Equal(1, report.Optional);
        Assert.Equal(1, report.RequiringRestart);
        Assert.Equal(["KB123", "KB456"], report.Updates[0].Kb);
        Assert.Equal(1.5, report.Updates[0].SizeMb);
        Assert.Null(report.Updates[2].SizeMb);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(report));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(report));
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("KB123, KB456 · 1.5 MB · new Windows version · restart may be required", WindowsMaintenanceInventory.Details(report.Updates[0]));
            Assert.Equal("optional/driver", WindowsMaintenanceInventory.Details(report.Updates[1]));
            Assert.Equal("", WindowsMaintenanceInventory.Details(report.Updates[2]));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void Empty_valid_scan_is_distinct_from_missing_or_malformed_scan()
    {
        Assert.Empty(WindowsMaintenanceInventory.ProjectScan(new JsonObject { ["updates"] = new JsonArray() }).Updates);
        Assert.Throws<JsonException>(() => WindowsMaintenanceInventory.ProjectScan(new JsonObject()));
        Assert.Throws<JsonException>(() => WindowsMaintenanceInventory.ProjectScan(JsonNode.Parse("""{"updates":[null]}""")!.AsObject()));
        Assert.Throws<JsonException>(() => WindowsMaintenanceInventory.ProjectScan(JsonNode.Parse("""{"updates":{}}""")!.AsObject()));
        Assert.Throws<InvalidOperationException>(() => WindowsMaintenanceInventory.ProjectScan(JsonNode.Parse("""{"updates":[{"size":"bad"}]}""")!.AsObject()));
    }

    [Fact]
    public void Projection_is_detached_and_handles_large_sizes_without_integer_overflow()
    {
        var kb = new List<string> { "123", "" };
        var inputs = new List<UpdateMetadata> { new("Update", kb, long.MaxValue, false, false, false) };
        var report = MaintenanceInventoryModule.ProjectScan(inputs);
        kb.Clear();
        inputs.Clear();
        var update = Assert.Single(report.Updates);
        Assert.Equal(["KB123"], update.Kb);
        Assert.Equal(long.MaxValue / 1048576d, update.SizeMb);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)update.Kb).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<UpdateProjection>)report.Updates).Clear());
    }

    [Theory]
    [InlineData(true, false, 1, true)]
    [InlineData(false, true, 1, false)]
    [InlineData(null, true, 1, true)]
    [InlineData(null, false, 1, false)]
    [InlineData(null, true, -1, false)]
    [InlineData(null, true, 0, false)]
    public void Pause_projection_preserves_explicit_status_and_legacy_requested_fallback(bool? paused, bool requested, int days, bool expected)
    {
        var now = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
        var until = now.AddDays(days);
        var report = MaintenanceInventoryModule.ProjectPause(new(paused, until), requested, now);
        Assert.Equal(expected, report.Paused);
        Assert.Equal(expected ? until : (DateTimeOffset?)null, report.Until);
    }

    [Fact]
    public void Legacy_or_invalid_expiry_does_not_invent_pause_but_explicit_paused_survives()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(WindowsMaintenanceInventory.ProjectPause(new JsonObject(), true, now).Paused);
        var report = WindowsMaintenanceInventory.ProjectPause(new JsonObject { ["paused"] = true, ["untilUtc"] = "invalid" }, false, now);
        Assert.True(report.Paused);
        Assert.Null(report.Until);
    }

    [Fact]
    public void Module_references_only_framework_assemblies() =>
        FrameworkReferenceAssertions.AssertOnly(typeof(MaintenanceInventoryModule).Assembly);
}
