// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

[Collection(nameof(LanguageSwitching))]
public sealed class ModulePresentationTests
{
    [Fact]
    public void Preserved_grid_headers_keep_distinct_saved_column_IDs_when_labels_collide() => Sta(() =>
    {
        var before = Loc.Current;
        var folder = Directory.CreateTempSubdirectory("module-headers-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "pl-PL.po"), PoFile.Write([new("Program", "Same", []), new("Publisher", "Same", [])], "pl-PL", "Custom"));
            Loc.Use("pl-PL", folder, Loc.English);
            var program = new DataGridTextColumn();
            var publisher = new DataGridTextColumn();
            UiTranslator.SetColumnHeader(program, "Program");
            UiTranslator.SetColumnHeader(publisher, "Publisher");
            Assert.Equal(program.Header, publisher.Header);
            Assert.Equal("Program", UiTranslator.ColumnName(program));
            Assert.Equal("Publisher", UiTranslator.ColumnName(publisher));
        }
        finally { Loc.Use(before); Directory.Delete(folder, true); }
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("lt-LT")]
    public void Scoped_forms_and_schedule_rows_render_without_translating_data(string culture) => Sta(() =>
    {
        var before = Loc.Current;
        var countOpening = UiPreferences.Instance.CountOpening;
        try
        {
            UiPreferences.Instance.CountOpening = null;
            Loc.Use(Loc.BuiltIn(culture));
            var lt = culture == Loc.Lithuanian;
            var client = new ClientInfo { Name = "Close", Address = "192.0.2.1", Room = "Close" };
            var row = new ScheduleRow { Item = new() { Name = "Close", Room = "Close", Days = ["Mon"], Monthly = true } };
            Assert.Equal("Close", row.Name);
            Assert.Equal(lt ? "Klasė: Close" : "Room: Close", row.Target);
            Assert.Equal(lt ? "1-oji Pr" : "1st Mon", row.Days);

#if REPORTING_MODULE
            var report = ReportingPresentation.Form(null, [client]);
            Modal(report, "Export", () =>
            {
                Assert.Equal(lt ? "Eksportuoti inventorių" : "Export inventory", report.Title);
                Assert.Contains(Visuals<TextBlock>(report), block => block.Text.Contains("Close", StringComparison.Ordinal));
                Capture(report, culture, "reporting");
            });
#endif

            var schedules = new SchedulesWindow(new HostSettings { Schedules = [row.Item] }, null!, () => [], () => ["Close"]);
            Modal(schedules, null, () =>
            {
                Assert.Equal(lt ? "Suplanuoti veiksmai" : "Scheduled actions", schedules.Title);
                Assert.Contains(Visuals<TextBlock>(schedules), block => block.Text == "Close");
                Capture(schedules, culture, "automation");
            });

            var settings = new HostSettings { DisabledModules = ["unknown.module"] };
            var options = new FormDialog(Loc.T("Settings"), null);
            BuiltInModules.AddSettings(options, settings);
            Modal(options, "Save", () =>
            {
                Assert.All(BuiltInModules.Available, module => Assert.True(options.B(module.Id)));
                var panel = (StackPanel)((ScrollViewer)options.Content).Content;
                panel.Children.OfType<CheckBox>().First().IsChecked = false;
                BuiltInModules.ReadSettings(options, settings);
                Assert.Contains(BuiltInModules.Available[0].Id, settings.DisabledModules);
                Assert.Contains("unknown.module", settings.DisabledModules);
                Capture(options, culture, "module-settings");
            });

            var disabled = AppearancePresentation.Current.CreateForm(null, [client], new(),
                () => throw new Exception("Disabled inventory must not read"), includeInventory: false);
            Modal(disabled, "Review", () =>
            {
                Assert.Equal(AppearanceMessages.InstallFile, disabled.S("choice"));
                var combo = Visuals<ComboBox>(disabled).Single();
                Assert.Equal(3, combo.Items.Count); // Install, off, restore remain usable without inventory.
                combo.SelectedIndex = 2;
                Assert.Equal(AppearanceMessages.RestorePrevious, disabled.S("choice"));
            });
        }
        finally { Loc.Use(before); UiPreferences.Instance.CountOpening = countOpening; }
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("lt-LT")]
    public void Typed_summaries_preserve_inventory_values_and_invariant_support_log(string culture)
    {
        var before = Loc.Current;
        try
        {
            Loc.Use(Loc.BuiltIn(culture));
            Assert.Contains("Close", ReportingPresentation.Written(1, "Close", 2, "Close"));
            Assert.StartsWith("Inventory: wrote 1 computer(s)", ReportingPresentation.Written(1, "Close", 2, "Close", english: true));
            Assert.Equal(culture == Loc.Lithuanian ? "Nukopijuota failų: 3, jau buvo: 2" : "3 file(s) copied, 2 already there", StudentBackupPresentation.Summary(3, 2));
            var client = new ClientInfo { Name = "Close" };
            var compared = ProgramInventoryComparison.Build([client], new Dictionary<ClientInfo, IReadOnlyList<ProgramEntry>>
                { [client] = [new() { Name = "Close", Version = "Close", Publisher = "Close" }] });
            var product = Assert.Single(compared);
            Assert.Equal("Close", product.Name);
            Assert.Equal("Close", product.Versions);
            Assert.Equal("Close", product.Publishers);
            Assert.Equal("Close: Close", product.Computers);
            var filter = SoftwareInventoryPresentation.Filters[1];
            Assert.Equal("Differences only", filter.Value);
            Assert.Equal(culture == Loc.Lithuanian ? "Tik skirtumai" : "Differences only", filter.Shown);
#if BROWSER_INVENTORY_MODULE
            var browsers = BrowserInventorySummary.Describe([("Close", new ClassroomManager.BrowserInventory.BrowserSnapshot(
                [new("custom", "Close")], "custom", ""))]);
            Assert.Contains("Close", browsers);
            Assert.DoesNotContain("Uždaryti", browsers);
#endif
            var details = MaintenancePresentation.Details(new("Close", ["Close"], 1.25, false, true, true));
            Assert.StartsWith("Close · ", details);
            Assert.Contains(culture == Loc.Lithuanian ? "pasirinktinis / tvarkyklė" : "optional/driver", details);
            var stored = MaintenancePresentation.Details(new("Close", ["Close"], 1.25, false, true, true), invariant: true);
            Assert.Equal("Close · 1.3 MB · optional/driver · restart may be required", stored);
            Assert.Equal(details, MaintenancePresentation.SavedDetails(stored));
            var expiry = new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.Zero);
            Assert.EndsWith(expiry.ToLocalTime().ToString("g"), MaintenancePresentation.Pause(new(true, expiry)));
            Assert.EndsWith(" · Close", MaintenancePresentation.Header("Close", "Close", "Waiting", "Close", ""));
            var readFailure = Loc.T("Pause status unavailable: ") + "Close";
            Assert.Contains(culture == Loc.Lithuanian ? "Pristabdymo būsena nepasiekiama: Close" : "Pause status unavailable: Close",
                MaintenancePresentation.Header("Close", "Close", "Waiting", readFailure, ""));
        }
        finally { Loc.Use(before); }
    }

    static void Modal(Window window, string? accept, Action check)
    {
        ExceptionDispatchInfo? error = null;
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            try { UiTranslator.TranslateTree(window); check(); }
            catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); }
            finally { window.DialogResult = false; }
        });
        if (window is FormDialog form) Assert.False(form.Ask(accept!)); else Assert.False(window.ShowDialog());
        error?.Throw();
    }

    static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        if (root is not Visual) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    static void Capture(Window window, string culture, string name)
    {
        if (Environment.GetEnvironmentVariable("CLASSROOM_CAPTURE_MODULES") != "1") return;
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = Path.Combine(StringExtractor.RepositoryRoot(), "artifacts", "module-localization");
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, name + "-" + culture + ".png"));
        encoder.Save(stream);
    }

    static void Sta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        error?.Throw();
    }
}
