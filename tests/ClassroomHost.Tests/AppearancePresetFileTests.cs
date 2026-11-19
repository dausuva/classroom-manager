// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Appearance;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>Appearance presets in files and on the command line: export, import, the scheduled action and the argument rules of <c>ClassroomCli appearance</c>.</summary>
public sealed class AppearancePresetFileTests : IDisposable
{
    readonly string _folder = Path.Combine(TestDataFolder.Scratch, "preset-files-" + Guid.NewGuid().ToString("N"));

    public AppearancePresetFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    string File_(string name) => Path.Combine(_folder, name);

    static AppearancePreset Sample() => new("Dark lesson", new Dictionary<string, string> { ["colors.app-mode"] = "dark", ["text.size"] = "125" });

    [Fact]
    public void A_preset_written_to_a_file_is_read_back_the_same()
    {
        AppearancePresetFiles.Write(Sample(), File_("a.json"));

        var (preset, problems) = AppearancePresetFiles.Read(File_("a.json"));

        Assert.Empty(problems);
        Assert.Equal("Dark lesson", preset!.Name);
        Assert.Equal(Sample().Values.OrderBy(pair => pair.Key), preset.Values.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void A_preset_that_would_not_be_accepted_is_not_written()
    {
        Assert.Throws<InvalidDataException>(() => AppearancePresetFiles.Write(new AppearancePreset("", new Dictionary<string, string>()), File_("b.json")));
        Assert.False(File.Exists(File_("b.json")));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"name":"x","values":{"colors.app-mode":5}}""")]
    [InlineData("""{"name":"x","values":{}}""")]
    [InlineData("""{"name":"x","values":{"Bad Id":"1"}}""")]
    public void A_file_that_is_not_a_good_preset_gives_nothing_and_says_why(string text)
    {
        File.WriteAllText(File_("c.json"), text);

        var (preset, problems) = AppearancePresetFiles.Read(File_("c.json"));

        Assert.Null(preset);
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void A_missing_or_oversized_file_is_refused_without_being_read()
    {
        Assert.Null(AppearancePresetFiles.Read(File_("none.json")).Preset);
        File.WriteAllText(File_("big.json"), new string(' ', (int)AppearancePresetFiles.MaxBytes + 1));
        Assert.Contains("too big", AppearancePresetFiles.Read(File_("big.json")).Problems.Single());
    }

    [Fact]
    public void The_command_line_exports_a_saved_preset_and_imports_it_into_other_settings_replacing_one_of_the_same_name()
    {
        var first = new HostSettings();
        first.AppearancePresets.Add(new AppearancePresetRecord { Name = "Dark lesson", Values = new Dictionary<string, string>(Sample().Values) });

        CliAppearance.Export(first, "dark LESSON", File_("d.json"));

        var second = new HostSettings();
        second.AppearancePresets.Add(new AppearancePresetRecord { Name = "dark lesson", Values = new Dictionary<string, string> { ["text.size"] = "100" } });
        CliAppearance.Import(second, File_("d.json"));

        var only = Assert.Single(second.AppearancePresets);
        Assert.Equal("Dark lesson", only.Name);
        Assert.Equal("125", only.Values["text.size"]);
    }

    [Fact]
    public void An_unknown_or_ambiguous_preset_name_and_a_bad_file_are_usage_errors()
    {
        var settings = new HostSettings();
        Assert.Throws<CliUsageException>(() => CliAppearance.Find(settings, "nothing"));
        settings.AppearancePresets.Add(new AppearancePresetRecord { Name = "A", Values = new() { ["text.size"] = "100" } });
        settings.AppearancePresets.Add(new AppearancePresetRecord { Name = "a", Values = new() { ["text.size"] = "125" } });
        Assert.Throws<CliUsageException>(() => CliAppearance.Find(settings, "A"));
        File.WriteAllText(File_("e.json"), "nope");
        Assert.Throws<CliUsageException>(() => CliAppearance.Import(settings, File_("e.json")));
        Assert.Equal(2, settings.AppearancePresets.Count);
    }

    [Fact]
    public void The_listing_names_every_preset_in_order()
    {
        var settings = new HostSettings();
        settings.AppearancePresets.Add(new AppearancePresetRecord { Name = "b", Values = new() { ["text.size"] = "100" } });
        settings.AppearancePresets.Add(new AppearancePresetRecord { Name = "A", Values = new() { ["text.size"] = "100", ["colors.app-mode"] = "dark" } });

        var list = CliAppearance.List(settings);

        Assert.Equal(["A", "b"], list.Select(item => item.Computer));
        Assert.Equal("2 setting(s)", list[0].Detail);
    }

    [Fact]
    public void An_apply_summary_counts_what_happened_and_a_failure_is_an_error()
    {
        var ok = CliAppearance.Summary([new AppearanceResult("a.b", ClassroomManager.Shared.AppearanceWire.Applied, "", 0), new AppearanceResult("c.d", ClassroomManager.Shared.AppearanceWire.Unchanged, "", 0)]);
        Assert.Equal("1 applied; 1 already that way", ok);
        var ex = Assert.Throws<InvalidOperationException>(() => CliAppearance.Summary([new AppearanceResult("a.b", ClassroomManager.Shared.AppearanceWire.Failed, "no access", 0)]));
        Assert.Contains("a.b: no access", ex.Message);
    }

    [Fact]
    public void The_arguments_of_appearance_are_checked()
    {
        Assert.Equal("presets", CliParser.Parse(["appearance", "presets"]).Action);
        Assert.Equal("apply", CliParser.Parse(["appearance", "apply", "--all", "--name", "Dark lesson"]).Action);
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["appearance"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["appearance", "apply", "--all"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["appearance", "apply", "--name", "x"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["appearance", "export", "--name", "x"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["appearance", "import", "--file", "x.json", "--all"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["appearance", "presets", "--room", "410"]));
    }

    [Fact]
    public void A_schedule_of_the_appearance_action_applies_the_preset_named_in_its_text()
    {
        var command = CliSchedules.ToCommand(new ScheduledAction { Name = "Monday", Action = "appearance", Text = "  Dark lesson " }, new CliOptions());

        Assert.Equal("appearance", command.Command);
        Assert.Equal("apply", command.Action);
        Assert.Equal("Dark lesson", command.Name);
    }
}
