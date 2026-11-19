// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ToolbarArrangementTests
{
    [Fact]
    public void Moving_commands_preserves_hidden_choices_in_their_new_tab()
    {
        var saved = new Dictionary<string, ToolbarLayout>
        {
            ["Classroom"] = new() { Top = ["Classroom|Message...", "Classroom|Lock screen..."], More = ["Classroom|Hide messages"], Hidden = ["Classroom|Power..."] },
            ["Settings"] = new() { Top = ["Settings|Languages..."], Hidden = ["Settings|About..."] },
        };
        ToolbarArrangement.Relocate(saved,
        [
            new("Classroom", ["Classroom|Message...", "Classroom|Hide messages", "Classroom|Lock screen..."], []),
            new("Computers", ["Computers|Power..."], []),
            new("Settings", [], ["Settings|Languages..."]),
            new("Help", ["Help|About..."], []),
        ]);
        Assert.Equal(["Classroom|Message...", "Classroom|Hide messages", "Classroom|Lock screen..."], saved["Classroom"].Top);
        Assert.Equal(["Computers|Power..."], saved["Computers"].Hidden);
        Assert.Empty(saved["Computers"].Top);
        Assert.Equal(["Settings|Languages..."], saved["Settings"].More);
        Assert.Equal(["Help|About..."], saved["Help"].Hidden);
    }

    [Fact]
    public void More_order_uses_translated_labels_and_ignores_shortcut_marks()
    {
        Assert.Equal(["T|Beta", "T|Alpha"], ToolbarArrangement.OrderMore(["T|Alpha", "T|Beta"], label => label == "Alpha" ? "Zulu" : "_Alpha"));
    }

    static readonly string[] Top = ["T|Message...", "T|Lock screen...", "T|Power..."];
    static readonly string[] More = ["T|Hide messages", "T|Mouse..."];

    [Fact]
    public void Without_a_saved_arrangement_the_toolbar_is_the_programs_own()
    {
        var layout = ToolbarArrangement.Resolve(Top, More, null);
        Assert.Equal(Top, layout.Top);
        Assert.Equal(More, layout.More);
        Assert.Empty(layout.Hidden);
    }

    [Fact]
    public void A_saved_arrangement_moves_reorders_and_hides_actions()
    {
        var saved = new ToolbarLayout { Top = ["T|Power...", "T|Hide messages", "T|Message..."], More = ["T|Lock screen..."], Hidden = ["T|Mouse..."] };
        var layout = ToolbarArrangement.Resolve(Top, More, saved);
        Assert.Equal(["T|Power...", "T|Hide messages", "T|Message..."], layout.Top);
        Assert.Equal(["T|Lock screen..."], layout.More);
        Assert.Equal(["T|Mouse..."], layout.Hidden);
    }

    [Fact]
    public void A_new_action_takes_its_default_row_and_a_removed_one_is_forgotten()
    {
        var saved = new ToolbarLayout { Top = ["T|Message...", "T|Old action..."], More = ["T|Hide messages", "T|Power..."] };
        var layout = ToolbarArrangement.Resolve(Top, More, saved);
        Assert.Equal(["T|Message...", "T|Lock screen..."], layout.Top);
        Assert.Equal(["T|Hide messages", "T|Power...", "T|Mouse..."], layout.More);
        Assert.DoesNotContain("T|Old action...", layout.Top.Concat(layout.More).Concat(layout.Hidden));
    }

    [Fact]
    public void An_action_saved_in_two_rows_is_shown_once()
    {
        var saved = new ToolbarLayout { Top = ["T|Message..."], More = ["T|Message..."], Hidden = ["T|Message..."] };
        var layout = ToolbarArrangement.Resolve(Top, More, saved);
        Assert.Single(layout.Top.Concat(layout.More).Concat(layout.Hidden), id => id == "T|Message...");
    }

    [Fact]
    public void Ids_are_the_tab_and_the_english_label_and_arrangements_are_saved_with_the_settings()
    {
        Assert.Equal("Classroom|Lock screen...", ToolbarArrangement.Id("Classroom", "_Lock screen..."));
        Assert.Equal("Lock screen...", ToolbarArrangement.Label("Classroom|Lock screen..."));

        var settings = new HostSettings();
        settings.ToolbarLayouts["Classroom"] = new ToolbarLayout { Top = ["Classroom|Power..."], Hidden = ["Classroom|Mouse..."] };
        var back = JsonSerializer.Deserialize<HostSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(["Classroom|Mouse..."], back.ToolbarLayouts["Classroom"].Hidden);
    }

    [Fact]
    public void Arrange_toolbar_and_Interface_settings_cannot_stay_hidden()
    {
        var arrange = ToolbarArrangement.Id("Settings", "Arrange _toolbar...");
        var other = ToolbarArrangement.Id("Settings", "Help");
        var saved = new ToolbarLayout { Hidden = [arrange, other] };
        var layout = ToolbarArrangement.Resolve([arrange, other], [], saved);
        Assert.Equal([arrange], layout.Top);
        Assert.Equal([other], layout.Hidden);
    }

    [Theory]
    [InlineData("Software", 12, 0, true)]
    [InlineData("Software", 12, 1, false)]
    [InlineData("Overview", 0, 0, false)]
    [InlineData("Settings", 15, 0, false)]
    public void A_tab_with_actions_but_none_shown_is_hidden_except_Settings(string tab, int actions, int shown, bool hidden) =>
        Assert.Equal(hidden, ToolbarArrangement.HidesTab(tab, actions, shown));

    [Fact]
    public void Teacher_mode_always_shows_the_way_back_to_the_toolbar_settings()
    {
        Assert.Contains("settings.arrange-toolbar", TeacherCommandCatalog.AlwaysVisible);
        Assert.Contains("settings.interface", TeacherCommandCatalog.AlwaysVisible);
        Assert.All(TeacherCommandCatalog.AlwaysVisible, id => Assert.Contains(TeacherCommandCatalog.All, command => command.Id == id));
    }

    [Fact]
    public async Task The_command_line_resets_the_toolbar_in_the_given_settings()
    {
        var folder = Directory.CreateTempSubdirectory("cm-toolbar-").FullName;
        try
        {
            var path = Path.Combine(folder, "host.json");
            var settings = new HostSettings();
            settings.ToolbarLayouts["Classroom"] = new ToolbarLayout { Hidden = ["Classroom|Mouse..."] };
            settings.Save(path);

            var output = new StringWriter();
            var exit = await HostCli.RunAsync(["toolbar-reset", "--settings", path], output, new StringWriter());

            Assert.Equal(HostCli.ExitOk, exit);
            Assert.Empty(HostSettings.Load(path).ToolbarLayouts);
            Assert.Contains("toolbar is back", output.ToString());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
