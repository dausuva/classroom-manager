// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Appearance;
using ClassroomManager.Host;
using ClassroomManager.Modules;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The Appearance module's catalogue reader, merge, presets and operations; the real Client's answer is checked in the Client tests.</summary>
public sealed class AppearanceCatalogueTests
{
    const string Answer = """
        {"windows":"11","build":26100,"settings":[
          {"id":"colors.app-mode","category":"Colours","kind":"choice","label":"Default app mode","apply":"now","risk":"reversible","value":"dark","changed":true,
           "choices":[{"value":"light","label":"Light"},{"value":"dark","label":"Dark"}]},
          {"id":"colors.transparency","category":"Colours","kind":"toggle","label":"Transparency effects","apply":"now","risk":"reversible","value":"true","changed":false},
          {"id":"text.size","category":"Cursor and text","kind":"number","label":"Text size","apply":"now","risk":"reversible","value":"100","changed":false,"min":100,"max":225,"step":25},
          {"id":"bad id","category":"Colours","kind":"toggle","label":"Broken","value":"true"},
          {"id":"colors.nochoices","category":"Colours","kind":"choice","label":"No choices","value":"x"},
          {"id":"colors.unknown-kind","category":"Colours","kind":"wheel","label":"Odd","value":"x"}
        ]}
        """;

    static AppearanceComputerCatalogue Parse() => AppearanceWireReader.ReadCatalogue(Answer);

    [Fact]
    public void A_catalogue_answer_is_read_and_a_setting_that_is_malformed_is_left_out()
    {
        var catalogue = Parse();

        Assert.Equal(("11", 26100), (catalogue.Windows, catalogue.Build));
        Assert.Equal(["colors.app-mode", "colors.transparency", "text.size"], catalogue.Settings.Select(setting => setting.Id));
        var mode = catalogue.Settings[0];
        Assert.Equal(["light", "dark"], mode.Choices.Select(choice => choice.Value));
        Assert.True(mode.Changed);
        Assert.Equal("dark", mode.Value);
        Assert.Equal((100, 225, 25), (catalogue.Settings[2].Min, catalogue.Settings[2].Max, catalogue.Settings[2].Step));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"settings":"none"}""")]
    public void An_answer_that_is_not_a_catalogue_is_an_empty_one(string answer)
    {
        if (answer.Length == 0) Assert.ThrowsAny<System.Text.Json.JsonException>(() => AppearanceWireReader.ReadCatalogue(answer));
        else Assert.Empty(AppearanceWireReader.ReadCatalogue(answer).Settings);
    }

    [Fact]
    public void Several_computers_make_one_list_in_category_order_with_how_many_have_each_setting_and_whether_they_differ()
    {
        var first = Parse();
        var second = new AppearanceComputerCatalogue("10", 19045, [first.Settings[0] with { Value = "light", Changed = false }, first.Settings[1]]);

        var rows = AppearanceMerge.Rows([first, second, null]);

        Assert.Equal(["colors.app-mode", "colors.transparency", "text.size"], rows.Select(row => row.Setting.Id));
        var mode = rows[0];
        Assert.Equal((2, 3, 1), (mode.SupportedBy, mode.Total, mode.ChangedOn));
        Assert.Equal(["dark", "light"], mode.CurrentValues);
        Assert.True(mode.Mixed);
        Assert.False(rows[1].Mixed);
        Assert.Equal(1, rows[2].SupportedBy);
    }

    static IReadOnlyList<AppearanceRow> Rows() => AppearanceMerge.Rows([Parse()]);

    [Fact]
    public void A_preset_is_checked_against_what_the_selected_computers_offer_and_refused_whole_when_it_does_not_fit()
    {
        var good = new AppearancePreset("Dark lab", new Dictionary<string, string> { ["colors.app-mode"] = "dark", ["colors.transparency"] = "false", ["text.size"] = "125" });
        Assert.Empty(AppearancePresets.Plan(good, Rows()));

        Assert.Contains(AppearancePresets.Plan(good with { Values = new Dictionary<string, string> { ["colors.app-mode"] = "purple" } }, Rows()), problem => problem.Contains("purple"));
        Assert.Contains(AppearancePresets.Plan(good with { Values = new Dictionary<string, string> { ["taskbar.alignment"] = "left" } }, Rows()), problem => problem.Contains("taskbar.alignment"));
        Assert.NotEmpty(AppearancePresets.Plan(good with { Values = new Dictionary<string, string> { ["text.size"] = "130" } }, Rows()));   // not on the step
        Assert.NotEmpty(AppearancePresets.Plan(good with { Values = new Dictionary<string, string> { ["text.size"] = "250" } }, Rows()));   // above the maximum
        Assert.NotEmpty(AppearancePresets.Plan(good with { Values = new Dictionary<string, string> { ["colors.transparency"] = "yes" } }, Rows()));
    }

    [Fact]
    public void A_preset_needs_a_name_values_and_valid_setting_names()
    {
        Assert.NotEmpty(AppearancePresets.Problems(new("", new Dictionary<string, string> { ["colors.app-mode"] = "dark" })));
        Assert.NotEmpty(AppearancePresets.Problems(new(new string('x', 61), new Dictionary<string, string> { ["colors.app-mode"] = "dark" })));
        Assert.NotEmpty(AppearancePresets.Problems(new("Empty", new Dictionary<string, string>())));
        Assert.NotEmpty(AppearancePresets.Problems(new("Bad", new Dictionary<string, string> { ["Not A Name"] = "x" })));
        Assert.NotEmpty(AppearancePresets.Problems(new("Long", new Dictionary<string, string> { ["colors.app-mode"] = new string('x', 101) })));
        Assert.Empty(AppearancePresets.Problems(new("Fine", new Dictionary<string, string> { ["colors.app-mode"] = "dark" })));
    }

    [Fact]
    public void A_preset_goes_to_json_and_back_and_a_file_that_is_not_a_preset_is_not_one()
    {
        var preset = new AppearancePreset("Dark lab", new Dictionary<string, string> { ["colors.transparency"] = "false", ["colors.app-mode"] = "dark" });

        var back = AppearancePresets.FromJson(AppearancePresets.ToJson(preset));

        Assert.Equal("Dark lab", back!.Name);
        Assert.Equal(preset.Values.OrderBy(pair => pair.Key), back.Values.OrderBy(pair => pair.Key));
        Assert.Null(AppearancePresets.FromJson("not json"));
        Assert.Null(AppearancePresets.FromJson("""{"name":"x"}"""));
        Assert.Null(AppearancePresets.FromJson("""{"name":"x","values":{"a.b":5}}"""));
    }

    sealed class Computer : IModuleConnection
    {
        public List<(string Command, string Args)> Calls { get; } = [];
        public string Answer { get; set; } = "{}";

        public Task<string> CallUserAsync(string command, string argumentsJson, CancellationToken cancellation)
        {
            Calls.Add((command, argumentsJson));
            return Task.FromResult(Answer);
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken cancellation) => throw new NotSupportedException();
    }

    sealed class NoSinks : IModuleSinkFolders
    {
        public string? Resolve(string sink) => null;
    }

    static (ModuleClientSession Session, ModuleComputer Computer, Computer Connection) Open()
    {
        var connection = new Computer();
        var computer = new ModuleComputer(1, "PC-01");
        return (new ModuleClientSession(new AppearanceInventoryModuleEntry().Describe(), [new(computer, connection, "x")], new NoSinks(), _ => { }), computer, connection);
    }

    [Fact]
    public async Task The_module_sends_the_four_generic_commands_as_the_user_with_the_arguments_the_client_reads()
    {
        var (session, computer, connection) = Open();
        var appearance = new AppearanceOperations(session);
        connection.Answer = Answer;
        var catalogue = await appearance.CatalogAsync(computer, CancellationToken.None);
        connection.Answer = """{"values":{"colors.app-mode":"dark"},"unsupported":[]}""";
        var values = await appearance.GetAsync(computer, ["colors.app-mode"], CancellationToken.None);
        connection.Answer = """{"results":[{"id":"colors.app-mode","status":"applied"},{"id":"x.y","status":"failed","reason":"No."}]}""";
        var set = await appearance.SetAsync(computer, new Dictionary<string, string> { ["colors.app-mode"] = "light", ["text.size"] = "125" }, CancellationToken.None);
        var reset = await appearance.ResetAsync(computer, ["colors.app-mode"], CancellationToken.None);

        Assert.Equal(3, catalogue.Settings.Count);
        Assert.Equal("dark", values["colors.app-mode"]);
        Assert.Equal(["applied", "failed"], set.Select(result => result.Status));
        Assert.Equal("No.", set[1].Reason);
        Assert.Equal(2, reset.Count);
        Assert.Equal(["appearance.catalog", "appearance.get", "appearance.set", "appearance.reset"], connection.Calls.Select(call => call.Command));
        Assert.Equal("light", JsonNode.Parse(connection.Calls[2].Args)!["values"]!["colors.app-mode"]!.GetValue<string>());
        Assert.Equal("colors.app-mode", JsonNode.Parse(connection.Calls[3].Args)!["ids"]![0]!.GetValue<string>());
        Assert.Equal(0, session.Refusals);
    }

    [Theory]
    [InlineData("windows.preferences.set")]
    [InlineData("cmd.run")]
    [InlineData("power.action")]
    public async Task The_module_cannot_send_anything_else_and_never_with_administrator_rights(string command)
    {
        var (session, computer, connection) = Open();

        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(computer, command, "{}"));
        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsSystemAsync(computer, "appearance.set", """{"values":{}}"""));

        Assert.Empty(connection.Calls);
    }

    [Fact]
    public void The_module_declares_the_four_commands_beside_the_screen_saver_ones_and_administrator_rights_only_for_the_sign_in_picture()
    {
        var descriptor = new AppearanceInventoryModuleEntry().Describe();

        Assert.Subset(descriptor.ClientCapabilities.ToHashSet(), AppearanceOperations.Commands.ToHashSet());
        Assert.Equal(WallpaperOperations.SystemCommands.Order(), descriptor.SystemCommands.Order());
        Assert.Equal([ModuleUploads.Wallpaper], descriptor.Uploads);
        Assert.Empty(ModuleDescriptorRules.Problems(descriptor));
    }

    [Fact]
    public void The_window_says_what_is_now_how_many_computers_have_it_and_what_each_computer_did()
    {
        var rows = AppearanceMerge.Rows([Parse(), new("10", 19045, [Parse().Settings[0] with { Value = "light" }])]);

        Assert.Contains("different", AppearanceSettingsPresentation.Describe(rows[0]));
        Assert.Contains("on 1 of 2", AppearanceSettingsPresentation.Describe(rows[1]));
        var summary = AppearanceSettingsPresentation.Summary("PC-01", [new("a.b", "applied", ""), new("c.d", "unchanged", ""), new("e.f", "unsupported", ""), new("g.h", "failed", "Access is denied.")]);
        Assert.StartsWith("PC-01: ", summary);
        Assert.Contains("1 applied", summary);
        Assert.Contains("g.h: Access is denied.", summary);
        Assert.Equal("PC-01: nothing to report", AppearanceSettingsPresentation.Summary("PC-01", []));
    }

    [Fact]
    public void Saved_presets_survive_the_settings_file()
    {
        var settings = new HostSettings();
        settings.AppearancePresets.Add(new AppearancePresetRecord { Name = "Dark lab", Values = { ["colors.app-mode"] = "dark" } });

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<HostSettings>(json)!;

        Assert.Equal("dark", Assert.Single(back.AppearancePresets).Values["colors.app-mode"]);
        Assert.Empty(new HostSettings().AppearancePresets);
    }
}
