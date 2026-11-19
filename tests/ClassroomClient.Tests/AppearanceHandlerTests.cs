// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>The Appearance catalogue handler with a fake registry: values in the Host's words, exact reset, and refusal of what it cannot do.</summary>
public sealed class AppearanceHandlerTests : IDisposable
{
    const int Windows10 = 19045;
    const int Windows11 = 26100;

    readonly string _folder = Directory.CreateTempSubdirectory("cm-appearance-").FullName;
    string Originals => Path.Combine(_folder, "appearance-originals.json");

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    sealed class FakeStore : IAppearanceStore
    {
        public Dictionary<(string Path, string Name), RawValue> Values { get; } = [];
        public List<string> Writes { get; } = [];
        public List<string> Persisted { get; } = [];
        public bool FailWrites { get; set; }

        public void Persist(string path, string name) => Persisted.Add(path);

        public RawValue? Read(string path, string name) => Values.TryGetValue((path, name), out var value) ? value : null;

        public void Write(string path, string name, RawValue value)
        {
            if (FailWrites) throw new UnauthorizedAccessException("Access is denied.");
            Values[(path, name)] = value;
            Writes.Add(name);
        }

        public void Delete(string path, string name)
        {
            if (FailWrites) throw new UnauthorizedAccessException("Access is denied.");
            Values.Remove((path, name));
            Writes.Add("-" + name);
        }
    }

    sealed class FakeShell : IAppearanceShell
    {
        public List<HashSet<AppearanceRefresh>> Calls { get; } = [];

        public void Refresh(IReadOnlySet<AppearanceRefresh> needed) => Calls.Add([.. needed]);
    }

    sealed class FakeConfirmer : IAppearanceConfirmer
    {
        public List<string> Questions { get; } = [];
        public Action? Keep { get; private set; }
        public Action? Revert { get; private set; }

        public void Ask(string what, int seconds, Action keep, Action revert)
        {
            Questions.Add(what + " / " + seconds);
            (Keep, Revert) = (keep, revert);
        }
    }

    (AppearanceHandler Handler, FakeStore Store, FakeShell Shell) Create(int build = Windows11, FakeStore? store = null, IAppearanceConfirmer? confirmer = null)
    {
        store ??= new FakeStore();
        var shell = new FakeShell();
        AppearanceSettings.DisplaySizeChoices = () => [("1920x1080", "1920 x 1080"), ("1600x900", "1600 x 900"), ("1280x720", "1280 x 720")];
        return (new AppearanceHandler(store, shell, Originals, build, confirmer), store, shell);
    }

    static JsonObject Set(AppearanceHandler handler, params (string Id, JsonNode Value)[] values)
    {
        var request = new JsonObject();
        var inner = new JsonObject();
        foreach (var (id, value) in values) inner[id] = value;
        request["values"] = inner;
        var response = handler.Set(request);
        Assert.True(response.Ok, response.Error);
        return response.Data!;
    }

    static string StatusOf(JsonObject data, string id) =>
        data["results"]!.AsArray().OfType<JsonObject>().Single(item => item["id"]!.GetValue<string>() == id)["status"]!.GetValue<string>();

    static string ValueOf(AppearanceHandler handler, string id) =>
        handler.Get(new JsonObject { ["ids"] = new JsonArray(id) }).Data!["values"]![id]!.GetValue<string>();

    [Fact]
    public void The_catalogue_lists_the_settings_with_the_windows_defaults_when_nothing_is_stored()
    {
        var (handler, _, _) = Create();

        var data = handler.Catalog().Data!;
        var settings = data["settings"]!.AsArray().OfType<JsonObject>().ToDictionary(item => item["id"]!.GetValue<string>());

        Assert.Equal("11", data["windows"]!.GetValue<string>());
        Assert.Equal("light", settings["colors.app-mode"]["value"]!.GetValue<string>());
        Assert.Equal("true", settings["colors.transparency"]["value"]!.GetValue<string>());
        Assert.Equal("false", settings["colors.accent-title-bars"]["value"]!.GetValue<string>());
        Assert.Equal("true", settings["desktop.show-icons"]["value"]!.GetValue<string>());
        Assert.Equal("true", settings["desktop.icon.recycle-bin"]["value"]!.GetValue<string>());
        Assert.Equal("false", settings["desktop.icon.this-pc"]["value"]!.GetValue<string>());
        Assert.Equal("fill", settings["desktop.wallpaper-fit"]["value"]!.GetValue<string>());
        Assert.All(settings.Values, setting => Assert.False(setting["changed"]!.GetValue<bool>()));
        Assert.Equal(["light", "dark"], settings["colors.windows-mode"]["choices"]!.AsArray().Select(choice => choice!["value"]!.GetValue<string>()));
    }

    [Fact]
    public void Windows_10_and_11_both_have_the_first_catalogue_and_a_build_below_windows_10_has_none()
    {
        Assert.Equal("10", Create(Windows10).Handler.Catalog().Data!["windows"]!.GetValue<string>());
        Assert.Equal(AppearanceSettings.All.Count(setting => setting.SupportedOn(Windows10)), Create(Windows10).Handler.Catalog().Data!["settings"]!.AsArray().Count);
        Assert.Equal(AppearanceSettings.All.Count(setting => setting.SupportedOn(Windows11)), Create(Windows11).Handler.Catalog().Data!["settings"]!.AsArray().Count);
        Assert.Empty(Create(9600).Handler.Catalog().Data!["settings"]!.AsArray());
    }

    [Fact]
    public void Stored_values_are_read_in_the_hosts_words()
    {
        var (handler, store, _) = Create();
        store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme")] = RawValue.Dword(0);
        store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideIcons")] = RawValue.Dword(1);
        store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", "{645FF040-5081-101B-9F08-00AA002F954E}")] = RawValue.Dword(1);
        store.Values[(@"Control Panel\Desktop", "WallpaperStyle")] = RawValue.Str("0");
        store.Values[(@"Control Panel\Desktop", "TileWallpaper")] = RawValue.Str("1");

        Assert.Equal("dark", ValueOf(handler, "colors.app-mode"));
        Assert.Equal("false", ValueOf(handler, "desktop.show-icons"));
        Assert.Equal("false", ValueOf(handler, "desktop.icon.recycle-bin"));
        Assert.Equal("tile", ValueOf(handler, "desktop.wallpaper-fit"));
    }

    [Fact]
    public void A_change_is_written_in_windows_words_saved_for_reset_and_the_shell_is_told()
    {
        var (handler, store, shell) = Create();

        var data = Set(handler, ("colors.app-mode", "dark"), ("desktop.show-icons", false), ("desktop.wallpaper-fit", "fit"), ("colors.transparency", "false"));

        Assert.All(new[] { "colors.app-mode", "desktop.show-icons", "desktop.wallpaper-fit", "colors.transparency" }, id => Assert.Equal("applied", StatusOf(data, id)));
        Assert.Equal(RawValue.Dword(0), store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme")]);
        Assert.Equal(RawValue.Dword(1), store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideIcons")]);
        Assert.Equal(RawValue.Str("6"), store.Values[(@"Control Panel\Desktop", "WallpaperStyle")]);
        Assert.Equal(RawValue.Str("0"), store.Values[(@"Control Panel\Desktop", "TileWallpaper")]);
        Assert.Equal(RawValue.Dword(0), store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency")]);
        var refreshed = Assert.Single(shell.Calls);
        Assert.Equal([AppearanceRefresh.Theme, AppearanceRefresh.Desktop, AppearanceRefresh.Wallpaper], refreshed.Order());
        Assert.True(File.Exists(Originals));
        Assert.All(handler.Catalog().Data!["settings"]!.AsArray().OfType<JsonObject>().Where(item => item["id"]!.GetValue<string>() is "colors.app-mode" or "desktop.wallpaper-fit"),
            item => Assert.True(item["changed"]!.GetValue<bool>()));
    }

    [Fact]
    public void Setting_what_is_already_there_changes_nothing_saves_nothing_and_tells_nobody()
    {
        var (handler, store, shell) = Create();

        var data = Set(handler, ("colors.app-mode", "light"), ("desktop.icon.recycle-bin", true));

        Assert.Equal("unchanged", StatusOf(data, "colors.app-mode"));
        Assert.Equal("unchanged", StatusOf(data, "desktop.icon.recycle-bin"));
        Assert.Empty(store.Writes);
        Assert.Empty(shell.Calls);
        Assert.False(File.Exists(Originals));
    }

    [Fact]
    public void Reset_puts_back_exactly_what_was_stored_including_no_value_at_all()
    {
        var (handler, store, shell) = Create();
        store.Values[(@"Control Panel\Desktop", "WallpaperStyle")] = RawValue.Str("2");
        store.Values[(@"Control Panel\Desktop", "TileWallpaper")] = RawValue.Str("0");
        // Nothing is stored for the app mode (Windows treats that as light); the title-bar setting has an explicit 0.
        store.Values[(@"Software\Microsoft\Windows\DWM", "ColorPrevalence")] = RawValue.Dword(0);
        Set(handler, ("desktop.wallpaper-fit", "tile"), ("colors.app-mode", "dark"), ("colors.accent-title-bars", true));
        Assert.Equal("tile", ValueOf(handler, "desktop.wallpaper-fit"));
        shell.Calls.Clear();

        var reset = handler.Reset(new JsonObject { ["ids"] = new JsonArray("desktop.wallpaper-fit", "colors.app-mode", "colors.accent-title-bars") }).Data!;

        Assert.All(new[] { "desktop.wallpaper-fit", "colors.app-mode", "colors.accent-title-bars" }, id => Assert.Equal("restored", StatusOf(reset, id)));
        Assert.Equal(RawValue.Str("2"), store.Values[(@"Control Panel\Desktop", "WallpaperStyle")]);
        Assert.Equal(RawValue.Str("0"), store.Values[(@"Control Panel\Desktop", "TileWallpaper")]);
        Assert.False(store.Values.ContainsKey((@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme")));
        Assert.Equal(RawValue.Dword(0), store.Values[(@"Software\Microsoft\Windows\DWM", "ColorPrevalence")]);
        Assert.Equal("stretch", ValueOf(handler, "desktop.wallpaper-fit"));
        Assert.Single(shell.Calls);
        Assert.Equal("unchanged", StatusOf(handler.Reset(new JsonObject { ["ids"] = new JsonArray("colors.app-mode") }).Data!, "colors.app-mode"));
    }

    [Fact]
    public void Reset_all_restores_every_setting_that_was_changed_and_nothing_else()
    {
        var (handler, store, _) = Create();
        Set(handler, ("colors.transparency", "false"), ("desktop.icon.this-pc", "true"));
        store.Values[(@"Software\Microsoft\Windows\DWM", "ColorPrevalence")] = RawValue.Dword(1);   // someone else's choice: never changed here

        var reset = handler.Reset(new JsonObject { ["all"] = true }).Data!;

        Assert.Equal(["colors.transparency", "desktop.icon.this-pc"], reset["results"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).Order());
        Assert.Equal("true", ValueOf(handler, "colors.transparency"));
        Assert.Equal("true", ValueOf(handler, "colors.accent-title-bars"));
        Assert.DoesNotContain(handler.Catalog().Data!["settings"]!.AsArray().OfType<JsonObject>(), item => item["changed"]!.GetValue<bool>());
    }

    [Fact]
    public void What_was_saved_survives_a_new_handler_so_reset_works_after_the_helper_restarts()
    {
        var store = new FakeStore();
        Set(Create(store: store).Handler, ("colors.windows-mode", "dark"));

        var again = Create(store: store).Handler;

        Assert.Equal("dark", ValueOf(again, "colors.windows-mode"));
        Assert.Equal("restored", StatusOf(again.Reset(new JsonObject { ["ids"] = new JsonArray("colors.windows-mode") }).Data!, "colors.windows-mode"));
        Assert.Equal("light", ValueOf(again, "colors.windows-mode"));
    }

    [Fact]
    public void A_value_the_setting_cannot_have_an_unknown_id_and_a_failing_registry_are_reported_per_setting_without_stopping_the_others()
    {
        var (handler, store, _) = Create();

        var data = Set(handler, ("colors.app-mode", "purple"), ("no.such.setting", "x"), ("colors.transparency", "false"), ("desktop.wallpaper-fit", 3));

        Assert.Equal("failed", StatusOf(data, "colors.app-mode"));
        Assert.Equal("unsupported", StatusOf(data, "no.such.setting"));
        Assert.Equal("applied", StatusOf(data, "colors.transparency"));
        Assert.Equal("failed", StatusOf(data, "desktop.wallpaper-fit"));

        store.FailWrites = true;
        var denied = Set(handler, ("colors.accent-title-bars", "true"));
        Assert.Equal("failed", StatusOf(denied, "colors.accent-title-bars"));
        Assert.Contains("denied", denied["results"]![0]!["reason"]!.GetValue<string>());
        store.FailWrites = false;
        Assert.Equal("false", ValueOf(handler, "colors.accent-title-bars"));
        // The failed write left what was stored as it was, and the saved copy of it still resets cleanly.
        Assert.Equal("restored", StatusOf(handler.Reset(new JsonObject { ["ids"] = new JsonArray("colors.accent-title-bars") }).Data!, "colors.accent-title-bars"));
    }

    [Fact]
    public void Requests_that_name_nothing_or_too_much_are_refused()
    {
        var (handler, _, _) = Create();

        Assert.False(handler.Set(new JsonObject()).Ok);
        Assert.False(handler.Set(new JsonObject { ["values"] = new JsonObject() }).Ok);
        Assert.False(handler.Get(new JsonObject()).Ok);
        Assert.False(handler.Reset(new JsonObject()).Ok);
        Assert.False(handler.Get(new JsonObject { ["ids"] = new JsonArray(1) }).Ok);
        var many = new JsonObject();
        for (var i = 0; i <= AppearanceWire.MaxSettingsPerRequest; i++) many["x" + i] = "y";
        Assert.False(handler.Set(new JsonObject { ["values"] = many }).Ok);
    }

    [Fact]
    public void Get_answers_for_supported_ids_and_names_the_rest()
    {
        var (handler, _, _) = Create();

        var data = handler.Get(new JsonObject { ["ids"] = new JsonArray("colors.transparency", "no.such-setting") }).Data!;

        Assert.Equal("true", data["values"]!["colors.transparency"]!.GetValue<string>());
        Assert.Null(data["values"]!["no.such-setting"]);
        Assert.Equal(["no.such-setting"], data["unsupported"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void Every_setting_is_well_formed_and_every_value_it_offers_reads_back_as_itself()
    {
        var ids = new HashSet<string>();
        foreach (var setting in AppearanceSettings.All)
        {
            Assert.Matches(@"^[a-z]+(\.[a-z][a-z0-9-]*)+$", setting.Id);
            Assert.True(ids.Add(setting.Id), setting.Id + " is listed twice");
            Assert.False(string.IsNullOrWhiteSpace(setting.Label));
            Assert.Contains(setting.Category, new[] { AppearanceSettings.Colours, AppearanceSettings.Desktop, AppearanceSettings.Taskbar, AppearanceSettings.Effects, AppearanceSettings.Display });
            Assert.NotEmpty(setting.Bindings);
            var values = setting.Kind switch
            {
                AppearanceWire.KindToggle => ["true", "false"],
                AppearanceWire.KindChoice => setting.OfferedChoices.Select(choice => choice.Value).ToArray(),
                _ => throw new InvalidOperationException(setting.Id + ": slice 1 has no other kinds"),
            };
            Assert.NotEmpty(values);
            Assert.Equal(values.Length, values.Distinct().Count());
            foreach (var value in values)
            {
                var raw = setting.ToRaw(value);
                Assert.NotNull(raw);
                Assert.Equal(setting.Bindings.Count, raw!.Count);
                Assert.Equal(value, setting.Current(raw));
            }
            Assert.Null(setting.ToRaw("not-a-value"));
        }
    }

    [Fact]
    public void Taskbar_settings_are_read_and_written_in_windows_words_and_only_where_the_windows_version_has_them()
    {
        var (eleven, store, _) = Create(Windows11);
        var (ten, _, _) = Create(Windows10);
        store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode")] = RawValue.Dword(3);
        store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl")] = RawValue.Dword(0);

        // Windows 11's fourth search mode (icon with a label) is shown as a box.
        Assert.Equal("box", ValueOf(eleven, "taskbar.search"));
        Assert.Equal("left", ValueOf(eleven, "taskbar.alignment"));
        Assert.Equal("false", ValueOf(eleven, "taskbar.clock-seconds"));
        Assert.Equal("true", ValueOf(eleven, "taskbar.task-view"));

        var data = Set(eleven, ("taskbar.search", "icon"), ("taskbar.alignment", "center"), ("taskbar.clock-seconds", true), ("taskbar.task-view", false));
        Assert.All(new[] { "taskbar.search", "taskbar.alignment", "taskbar.clock-seconds", "taskbar.task-view" }, id => Assert.Equal("applied", StatusOf(data, id)));
        Assert.Equal(RawValue.Dword(1), store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode")]);
        Assert.Equal(RawValue.Dword(1), store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl")]);
        Assert.Equal(RawValue.Dword(1), store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSecondsInSystemClock")]);
        Assert.Equal(RawValue.Dword(0), store.Values[(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton")]);

        // The alignment is a Windows 11 setting; Windows 10 has no such choice.
        Assert.Equal("unsupported", StatusOf(Set(ten, ("taskbar.alignment", "left")), "taskbar.alignment"));
        Assert.Equal("applied", StatusOf(Set(ten, ("taskbar.search", "hidden")), "taskbar.search"));
    }

    static (AppearanceHandler Handler, FakeStore Store, FakeConfirmer Confirmer) Display()
    {
        var store = new FakeStore();
        store.Values[(AppearanceSettings.DisplaySizePath, "")] = RawValue.Str("1920x1080");
        store.Values[(AppearanceSettings.DisplayOrientationPath, "")] = RawValue.Dword(0);
        var confirmer = new FakeConfirmer();
        var folder = Directory.CreateTempSubdirectory("cm-appearance-display-").FullName;
        AppearanceSettings.DisplaySizeChoices = () => [("1920x1080", "1920 x 1080"), ("1600x900", "1600 x 900"), ("1280x720", "1280 x 720")];
        return (new AppearanceHandler(store, new FakeShell(), Path.Combine(folder, "originals.json"), Windows11, confirmer), store, confirmer);
    }

    [Fact]
    public void A_display_change_is_applied_for_a_short_time_and_kept_only_when_the_person_there_says_so()
    {
        var (handler, store, confirmer) = Display();

        var data = Set(handler, ("display.size", "1280x720"));

        Assert.Equal("applied", StatusOf(data, "display.size"));
        Assert.Equal(AppearanceHandler.ConfirmSeconds, data["results"]![0]!["confirmSeconds"]!.GetValue<int>());
        Assert.Equal(RawValue.Str("1280x720"), store.Values[(AppearanceSettings.DisplaySizePath, "")]);
        Assert.Empty(store.Persisted);
        Assert.Single(confirmer.Questions);
        Assert.Contains("screen resolution", confirmer.Questions[0]);

        confirmer.Keep!();

        Assert.Equal([AppearanceSettings.DisplaySizePath], store.Persisted);
        Assert.Equal("1280x720", ValueOf(handler, "display.size"));
        // It can still be reset later, and the reset is permanent at once.
        Assert.Equal("restored", StatusOf(handler.Reset(new JsonObject { ["ids"] = new JsonArray("display.size") }).Data!, "display.size"));
        Assert.Equal(RawValue.Str("1920x1080"), store.Values[(AppearanceSettings.DisplaySizePath, "")]);
        Assert.Equal(2, store.Persisted.Count);
    }

    [Fact]
    public void A_display_change_nobody_confirms_goes_back_by_itself_and_leaves_nothing_saved()
    {
        var (handler, store, confirmer) = Display();
        Set(handler, ("display.size", "1600x900"), ("display.orientation", "portrait"));
        Assert.Single(confirmer.Questions);

        confirmer.Revert!();

        Assert.Equal(RawValue.Str("1920x1080"), store.Values[(AppearanceSettings.DisplaySizePath, "")]);
        Assert.Equal(RawValue.Dword(0), store.Values[(AppearanceSettings.DisplayOrientationPath, "")]);
        Assert.Empty(store.Persisted);
        Assert.DoesNotContain(handler.Catalog().Data!["settings"]!.AsArray().OfType<JsonObject>(), item => item["changed"]!.GetValue<bool>());
        // Another change can be made now.
        Assert.Equal("applied", StatusOf(Set(handler, ("display.size", "1280x720")), "display.size"));
    }

    [Fact]
    public void While_a_display_change_waits_for_an_answer_another_is_refused_and_so_are_sizes_the_screen_does_not_offer()
    {
        var (handler, store, confirmer) = Display();

        Assert.Equal("failed", StatusOf(Set(handler, ("display.size", "1024x600")), "display.size"));   // not offered
        Assert.Equal("failed", StatusOf(Set(handler, ("display.size", "wide")), "display.size"));
        Assert.Equal("failed", StatusOf(Set(handler, ("display.orientation", "sideways")), "display.orientation"));
        Assert.Empty(confirmer.Questions);

        Set(handler, ("display.size", "1280x720"));
        var second = Set(handler, ("display.orientation", "portrait"));

        Assert.Equal("failed", StatusOf(second, "display.orientation"));
        Assert.Contains("waiting", second["results"]![0]!["reason"]!.GetValue<string>());
        Assert.Equal(RawValue.Dword(0), store.Values[(AppearanceSettings.DisplayOrientationPath, "")]);
    }

    [Fact]
    public void A_computer_that_cannot_ask_the_person_does_not_change_the_display_at_all()
    {
        var (handler, store, _) = Create();
        store.Values[(AppearanceSettings.DisplaySizePath, "")] = RawValue.Str("1920x1080");

        var data = Set(handler, ("display.size", "1280x720"));

        Assert.Equal("failed", StatusOf(data, "display.size"));
        Assert.Equal(RawValue.Str("1920x1080"), store.Values[(AppearanceSettings.DisplaySizePath, "")]);
    }

    [Fact]
    public void Animation_effects_go_through_the_systems_own_switch_and_reset_restores_it()
    {
        var (handler, store, _) = Create();
        store.Values[(AppearanceSettings.AnimationsPath, "")] = RawValue.Dword(1);

        Assert.Equal("true", ValueOf(handler, "effects.animations"));
        Assert.Equal("applied", StatusOf(Set(handler, ("effects.animations", false)), "effects.animations"));
        Assert.Equal(RawValue.Dword(0), store.Values[(AppearanceSettings.AnimationsPath, "")]);
        Assert.Equal("restored", StatusOf(handler.Reset(new JsonObject { ["ids"] = new JsonArray("effects.animations") }).Data!, "effects.animations"));
        Assert.Equal(RawValue.Dword(1), store.Values[(AppearanceSettings.AnimationsPath, "")]);
    }

    [Fact]
    public void The_hosts_module_reads_exactly_what_the_real_handler_says_and_a_preset_of_every_value_fits_it()
    {
        var (handler, _, _) = Create();

        var catalogue = ClassroomManager.Appearance.AppearanceWireReader.ReadCatalogue(handler.Catalog().Data!.ToJsonString());
        var rows = ClassroomManager.Appearance.AppearanceMerge.Rows([catalogue]);

        var offered = AppearanceSettings.All.Where(setting => setting.SupportedOn(Windows11)).ToList();
        Assert.Equal(offered.Select(setting => setting.Id).Order(), catalogue.Settings.Select(setting => setting.Id).Order());
        Assert.Equal(offered.Count, rows.Count);
        foreach (var setting in catalogue.Settings)
        {
            var source = AppearanceSettings.Find(setting.Id)!;
            Assert.Equal((source.Category, source.Kind, source.Label), (setting.Category, setting.Kind, setting.Label));
            var values = setting.Kind == AppearanceWire.KindToggle ? ["true", "false"] : setting.Choices.Select(choice => choice.Value).ToArray();
            Assert.Equal(source.OfferedChoices.Select(choice => choice.Value), setting.Choices.Select(choice => choice.Value));
            foreach (var value in values)
            {
                var preset = new ClassroomManager.Appearance.AppearancePreset("p", new Dictionary<string, string> { [setting.Id] = value });
                Assert.Empty(ClassroomManager.Appearance.AppearancePresets.Plan(preset, rows));
                Assert.True(ClassroomManager.Appearance.AppearancePresets.ValueFits(setting, value));
            }
        }
        // What the module sends is what the handler accepts, and the answer is what the module reads.
        var preset2 = new Dictionary<string, string> { ["colors.app-mode"] = "dark", ["desktop.wallpaper-fit"] = "span" };
        var applied = ClassroomManager.Appearance.AppearanceWireReader.ReadResults(handler.Set(new JsonObject { ["values"] = new JsonObject(preset2.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))) }).Data!.ToJsonString());
        Assert.Equal(["applied", "applied"], applied.Select(result => result.Status));
    }

    [Fact]
    public void The_four_commands_are_known_to_the_protocol_with_the_right_roles_and_levels()
    {
        foreach (var command in new[] { Cmd.AppearanceCatalog, Cmd.AppearanceGet, Cmd.AppearanceSet, Cmd.AppearanceReset })
        {
            Assert.True(Cmd.IsUserOnly(command));
            Assert.Equal(AppearanceWire.RequiredVersion, Protocol.RequiredVersion(command));
            Assert.Contains(command, Protocol.Capabilities);
        }
        Assert.Equal(OperatorRole.Observer, CommandAccess.RequiredRole(Cmd.AppearanceCatalog));
        Assert.Equal(OperatorRole.Observer, CommandAccess.RequiredRole(Cmd.AppearanceGet));
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(Cmd.AppearanceSet));
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(Cmd.AppearanceReset));
        Assert.True(Protocol.IsAtLeast(Protocol.Version, AppearanceWire.RequiredVersion));
    }
}
