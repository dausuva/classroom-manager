// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

[Collection(nameof(LanguageSwitching))]
public sealed class AppearanceLocalizationTests
{
    static string DomainFolder => Path.Combine(StringExtractor.LocaleFolder, "modules", "dausuva.appearance.inventory");

    [Fact]
    public void Domain_template_matches_exact_source_keys_and_all_Lithuanian_entries_are_valid()
    {
        var keys = StringExtractor.AppearanceKeys;
        var template = PoFile.Parse(File.ReadAllText(Path.Combine(DomainFolder, "messages.pot"))).Where(entry => entry.Id.Length > 0).ToArray();
        var entries = PoFile.Parse(File.ReadAllText(Path.Combine(DomainFolder, "lt-LT.po"))).Where(entry => entry.Id.Length > 0).ToArray();
        Assert.Equal(16, keys.Count);
        Assert.Equal(keys, template.Select(entry => entry.Id));
        Assert.Equal(keys, entries.Select(entry => entry.Id));
        Assert.All(template, entry => Assert.Empty(entry.Text));
        Assert.All(entries, entry => Assert.NotEmpty(entry.Text));
        var core = PoFile.Parse(File.ReadAllText(Path.Combine(StringExtractor.LocaleFolder, "lt-LT.po")));
        Assert.DoesNotContain(core, entry => keys.Contains(entry.Id));
        var language = new AppearancePresentation(Loc.BuiltIn(Loc.Lithuanian)).Language;
        Assert.Equal(0, language.RejectedCatalogs);
        Assert.Equal("_Ekrano užsklanda", language.Text(AppearanceMessages.ChoiceLabel));
        Assert.Equal("Skaitomi ekrano užsklandos nustatymai iš 2 kompiuterio(-ių)...", language.FormatKey(AppearanceMessages.Reading, 2));
        Assert.Equal("Reading screen saver settings from 2 computer(s)...", Loc.BuiltIn(Loc.Lithuanian).Translate("Reading screen saver settings from 2 computer(s)..."));
    }

    [Theory]
    [InlineData("None — turn the screen saver off", "Nėra — išjungti ekrano užsklandą")]
    [InlineData("Install a .scr file from this computer", "Įdiegti .scr failą iš šio kompiuterio")]
    [InlineData("Restore each user's previous screen saver", "Atkurti kiekvieno naudotojo ankstesnę ekrano užsklandą")]
    [InlineData("Reading screen saver settings from {0} computer(s)...", "Skaitomi ekrano užsklandos nustatymai iš {0} kompiuterio(-ių)...")]
    [InlineData("No selected computer reported its screen saver settings. A user must be signed in for this action.", "Nė vienas pasirinktas kompiuteris nepranešė ekrano užsklandos nustatymų. Šiam veiksmui turi būti prisijungęs naudotojas.")]
    [InlineData("Current setting on {0} of {1} selected computer(s): {2}.", "Dabartinis nustatymas {0} pasirinktame(-uose) kompiuteryje(-iuose) iš {1}: {2}.")]
    [InlineData("A Group Policy controls the screen saver on {0} computer(s); the change will be refused there.", "Ekrano užsklandą {0} kompiuteryje(-iuose) valdo grupės strategija; ten pakeitimas bus atmestas.")]
    [InlineData("_Screen saver", "_Ekrano užsklanda")]
    [InlineData("Screen saver _file (only for “Install a .scr file”)", "Ekrano užsklandos _failas (tik parinkčiai „Įdiegti .scr failą“)")]
    [InlineData("Start after this many idle _minutes", "Paleisti po tiek neveiklumo _minučių")]
    [InlineData("On resume, show the sign-in screen", "Grįžtant rodyti prisijungimo ekraną")]
    [InlineData("While plugged in, turn the screen _off after the screen saver has run this many minutes (0 = never, empty = leave Windows' setting)", "Kai įjungtas į elektros tinklą, ekraną _išjungti praėjus tiek minučių nuo ekrano užsklandos paleidimo (0 = niekada, tuščia = palikti „Windows“ nustatymą)")]
    [InlineData("Unavailable:\n", "Nepasiekiami:\n")]
    public void Migrated_entries_keep_the_previous_Lithuanian_wording(string key, string expected)
    {
        Assert.Equal(expected, new AppearancePresentation(Loc.BuiltIn(Loc.Lithuanian)).Language.Text(key));
    }

    [Fact]
    public void Resource_composition_keeps_core_and_legacy_import_working()
    {
        var included = Environment.GetEnvironmentVariable("CLASSROOM_EXPECT_APPEARANCE_CATALOG") != "false";
        Assert.Equal(included, typeof(AppearancePresentation).Assembly.GetManifestResourceNames().Contains(AppearancePresentation.ResourcePrefix + "lt-LT.po"));
        var core = Loc.BuiltIn(Loc.Lithuanian);
        var domain = new AppearancePresentation(core).Language;
        Assert.Equal(included ? "Nėra — išjungti ekrano užsklandą" : AppearanceMessages.TurnOff, domain.Text(AppearanceMessages.TurnOff));
        Assert.Equal("Ekrano užsklanda", core.Translate("Screen saver"));
        Assert.Contains(PoFile.Parse(LanguageFiles.BuiltIn(template: true)), entry => entry.Id == AppearanceMessages.TurnOff);
    }

    [Fact]
    public void Typed_core_target_localizes_default_room_without_translating_user_room_names()
    {
        var language = Loc.BuiltIn(Loc.Lithuanian);
        var client = new ClientInfo { Name = "Close", Room = "" };
        var unassigned = OperationTargetSummary.Format([client], language);
        Assert.Contains(language.Exact("Unassigned"), unassigned.Detail);
        Assert.DoesNotContain("Unassigned", unassigned.Detail);
        client.Room = "Close";
        Assert.Contains("Close", OperationTargetSummary.Format([client], language).Detail);
        Assert.DoesNotContain("Uždaryti", OperationTargetSummary.Format([client], language).Detail);
    }

    [Fact]
    public void Core_warnings_shared_title_and_legacy_export_import_survive_the_split()
    {
        var core = Loc.BuiltIn(Loc.Lithuanian);
        Assert.Equal("Ekrano užsklanda", core.Translate("Screen saver"));
        Assert.NotEqual(LocalizationOwnership.ScreenSaverTrustWarning, core.Exact(LocalizationOwnership.ScreenSaverTrustWarning));
        var combined = PoFile.Parse(LanguageFiles.BuiltIn(template: false)).ToDictionary(entry => entry.Id, entry => entry.Text);
        Assert.Equal("Nėra — išjungti ekrano užsklandą", combined[AppearanceMessages.TurnOff]);
        Assert.Equal("Grįžtant rodyti prisijungimo ekraną", combined[AppearanceMessages.SecureLabel]);
        var template = PoFile.Parse(LanguageFiles.BuiltIn(template: true));
        Assert.Contains(template, entry => entry.Id == AppearanceMessages.Usage && entry.Text == "");
        var folder = Directory.CreateTempSubdirectory("cm-appearance-language-").FullName;
        try
        {
            var file = Path.Combine(folder, "custom.po");
            File.WriteAllText(file, PoFile.Write([new(AppearanceMessages.TurnOff, "Custom", [])], "lt-LT", "Custom"));
            var imported = LanguageFiles.Import(file, Path.Combine(folder, "languages"));
            Assert.Equal(1, imported.Translated);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Missing_or_invalid_domain_resource_falls_back_without_losing_core_or_read_behavior()
    {
        var core = Loc.BuiltIn(Loc.Lithuanian);
        var missing = new AppearancePresentation(core, _ => null).Language;
        var malformed = new AppearancePresentation(core, _ => "invalid").Language;
        Assert.Equal(AppearanceMessages.TurnOff, missing.Text(AppearanceMessages.TurnOff));
        Assert.Equal(AppearanceMessages.TurnOff, malformed.Text(AppearanceMessages.TurnOff));
        Assert.Equal(2, malformed.RejectedCatalogs); // lt-LT and lt resources each rejected.
        Assert.Equal("Ekrano užsklanda", core.Translate("Screen saver"));
    }

    [Fact]
    public void User_replacement_reload_removal_and_weak_snapshot_cache_keep_existing_semantics()
    {
        var previous = Loc.Current;
        var folder = Directory.CreateTempSubdirectory("cm-appearance-overrides-").FullName;
        var file = Path.Combine(folder, "lt-LT.po");
        try
        {
            Loc.Use(Loc.Lithuanian, folder, Loc.English);
            var builtIn = AppearancePresentation.Current;
            Assert.Same(builtIn, AppearancePresentation.Current);
            Assert.Equal("_Ekrano užsklanda", builtIn.Language.Text(AppearanceMessages.ChoiceLabel));
            File.WriteAllText(file, PoFile.Write([new(AppearanceMessages.TurnOff, "Custom", [])], Loc.Lithuanian, "Custom"));
            Loc.Use(Loc.Lithuanian, folder, Loc.English);
            var custom = AppearancePresentation.Current;
            Assert.NotSame(builtIn, custom);
            Assert.Equal("Custom", custom.Language.Text(AppearanceMessages.TurnOff));
            Assert.Equal(AppearanceMessages.ChoiceLabel, custom.Language.Text(AppearanceMessages.ChoiceLabel));
            File.WriteAllText(file, PoFile.Write([new(AppearanceMessages.TurnOff, "Reloaded", [])], Loc.Lithuanian, "Custom"));
            Loc.Use(Loc.Lithuanian, folder, Loc.English);
            Assert.Equal("Reloaded", AppearancePresentation.Current.Language.Text(AppearanceMessages.TurnOff));
            Assert.Equal("Custom", custom.Language.Text(AppearanceMessages.TurnOff));
            File.Delete(file);
            Loc.Use(Loc.Lithuanian, folder, Loc.English);
            Assert.Equal("Nėra — išjungti ekrano užsklandą", AppearancePresentation.Current.Language.Text(AppearanceMessages.TurnOff));
        }
        finally { Loc.Use(previous); Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("lt-LT")]
    public void Real_modal_form_renders_typed_inventory_and_keeps_command_values_and_core_controls(string culture)
    {
        RunSta(() =>
        {
            var previous = Loc.Current;
            FormDialog? dialog = null;
            ExceptionDispatchInfo? failure = null;
            try
            {
                Loc.Use(Loc.BuiltIn(culture));
                var presentation = AppearancePresentation.Current;
                var client = new ClientInfo { Name = "Close", Address = "192.0.2.1", Room = "Close" };
                var other = new ClientInfo { Name = "Close", Address = "192.0.2.2" };
                var available = new Dictionary<string, string>(StringComparer.Ordinal);
                dialog = presentation.CreateForm(null, [client, other], available, () => Task.FromResult<ScreenSaverRead[]>(
                    [new(client, new(true, "Close", 600, false, true, false, ["Close"]), ""), new(other, null, "Close") ]));
                dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                {
                    try
                    {
                        UiTranslator.TranslateTree(dialog);
                        var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
                        Assert.Equal(culture == "lt-LT" ? "Ekrano užsklanda" : "Screen saver", dialog.Title);
                        var notes = panel.Children.OfType<TextBlock>().Select(block => block.Text).ToArray();
                        Assert.Contains(notes, text => text.Contains("Close", StringComparison.Ordinal) && text.StartsWith(culture == "lt-LT" ? "Dabartinis" : "Current setting", StringComparison.Ordinal));
                        Assert.Contains(notes, text => text.EndsWith("• Close: Close", StringComparison.Ordinal));
                        Assert.Contains(notes, text => text.Contains(culture == "lt-LT" ? "valdo grupės strategija" : "A Group Policy", StringComparison.Ordinal));
                        Assert.DoesNotContain(notes, text => text.Contains("Uždaryti", StringComparison.Ordinal));
                        var combo = panel.Children.OfType<ComboBox>().Single();
                        Assert.Equal(AppearanceMessages.InstallFile, dialog.S("choice"));
                        combo.SelectedIndex = 1;
                        Assert.Equal("Close", available[dialog.S("choice")]);
                        combo.IsDropDownOpen = true;
                        combo.UpdateLayout();
                        UiTranslator.TranslateTree(dialog);
                        var item = Assert.IsAssignableFrom<DependencyObject>(combo.ItemContainerGenerator.ContainerFromIndex(1));
                        Assert.Contains(TextBlocks(item), block => block.Text.StartsWith("Close — ", StringComparison.Ordinal));
                        Assert.DoesNotContain(TextBlocks(item), block => block.Text.Contains("Uždaryti", StringComparison.Ordinal));
                        Assert.Equal(culture == "lt-LT" ? "Ekrano užsklanda" : "Screen saver", AutomationProperties.GetName(combo));
                        Assert.Equal("10", dialog.S("minutes"));
                        var card = panel.Children.OfType<Border>().Single();
                        Assert.Contains("Close", ((TextBlock)card.Child).Text);
                        Assert.DoesNotContain("Uždaryti", ((TextBlock)card.Child).Text);
                        Assert.DoesNotContain("2 rooms", culture == "lt-LT" ? ((TextBlock)card.Child).Text : "");
                        Assert.Contains(panel.Children.OfType<Label>(), label => label.Content is AccessText { TextWrapping: TextWrapping.Wrap });
                        if (Environment.GetEnvironmentVariable("CLASSROOM_CAPTURE_APPEARANCE") == "1") Capture(dialog, culture);
                        combo.IsDropDownOpen = false;
                    }
                    catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
                    finally { dialog.DialogResult = false; }
                });
                UiTranslator.Attach(dialog);
                Assert.False(dialog.Ask("Review"));
                failure?.Throw();
            }
            finally { dialog?.Close(); Loc.Use(previous); }
        });
    }

    static void Capture(Window dialog, string culture)
    {
        dialog.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dialog);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = Path.Combine(StringExtractor.RepositoryRoot(), "artifacts", "appearance-localization");
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, culture + ".png"));
        encoder.Save(stream);
    }

    static IEnumerable<TextBlock> TextBlocks(DependencyObject root)
    {
        if (root is TextBlock block) yield return block;
        if (root is not Visual) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in TextBlocks(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    static void RunSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception failure) { error = ExceptionDispatchInfo.Capture(failure); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        error?.Throw();
    }
}
