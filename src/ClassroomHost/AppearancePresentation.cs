// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using ClassroomManager.Appearance;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed record ScreenSaverRead(ClientInfo Client, ScreenSaverState? State, string Error);

/// <summary>Built-in Host presentation adapter. Its resources never enter the legacy global translation table.</summary>
public sealed class AppearancePresentation
{
    static readonly ConditionalWeakTable<Translation, AppearancePresentation> Snapshots = new();
    public ScopedTranslation Language { get; }
    public const string ResourcePrefix = "locale.modules.dausuva.appearance.inventory.";

    public static AppearancePresentation Current => Snapshots.GetValue(Loc.Current, translation => new(translation));

    public AppearancePresentation(Translation applicationLanguage, Func<string, string?>? resource = null)
    {
        resource ??= culture =>
        {
            using var stream = typeof(AppearancePresentation).Assembly.GetManifestResourceStream(ResourcePrefix + culture + ".po");
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        };
        var keys = typeof(AppearanceMessages).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!).ToArray();
        Language = applicationLanguage.IsOverride
            ? ScopedTranslation.FromLegacy(AppearanceInventoryModule.Id, applicationLanguage, keys)
            : ScopedTranslation.Load(AppearanceInventoryModule.Id, applicationLanguage.Language, resource);
        // Counts/domain only; never catalog text, user values, paths or exception messages.
        if (Language.RejectedCatalogs > 0) System.Diagnostics.Trace.WriteLine(string.Join(" ", "localization.domain", Language.Domain, Language.RejectedCatalogs));
    }

    public string Summary(ScreenSaverReport report) => string.Join(" · ", report.Usage.Select(group =>
        Language.FormatKey(AppearanceMessages.Usage, group.Name == "off" ? Language.Text(AppearanceMessages.Off) : group.Name, group.Count)));

    public FormDialog CreateForm(Window? owner, IReadOnlyCollection<ClientInfo> targets, Dictionary<string, string> available,
        Func<Task<ScreenSaverRead[]>> read, bool includeInventory = true)
    {
        var dialog = new FormDialog(Loc.T("Screen saver"), owner);
        UiTranslator.SetPreserveText(dialog, true);
        return dialog.Target(targets).LoadAsync(Language.FormatKey(AppearanceMessages.Reading, targets.Count), async form =>
        {
            var inventories = includeInventory ? await read() : [];
            Populate(form, targets.Count, inventories, available, includeInventory);
        });
    }

    public void Populate(FormDialog form, int selected, IReadOnlyList<ScreenSaverRead> inventories, Dictionary<string, string> available, bool includeInventory = true)
    {
        var states = inventories.Where(item => item.State is not null).Select(item => item.State!).ToList();
        var overview = includeInventory ? ScreenSaverInventory.Project(states) : new ScreenSaverReport(0, 0, [], []);
        foreach (var name in overview.CommonNames)
            available[string.Format(System.Globalization.CultureInfo.InvariantCulture, AppearanceMessages.AvailableChoice, name)] = name;
        if (includeInventory) form.Note(states.Count == 0 ? Language.Text(AppearanceMessages.NoSettings)
            : Language.FormatKey(AppearanceMessages.CurrentSetting, states.Count, selected, Summary(overview)));
        if (overview.PolicyManaged > 0) form.Note(Language.FormatKey(AppearanceMessages.PolicyManaged, overview.PolicyManaged));
        var failures = inventories.Where(item => item.Error.Length > 0).ToList();
        if (failures.Count > 0)
            form.Note(Language.Text(AppearanceMessages.Unavailable) + string.Join("\n", failures.Select(item => $"• {item.Client.Name}: {item.Error}")));
        form.DisplayCombo("choice", Language.Text(AppearanceMessages.ChoiceLabel),
                [(AppearanceMessages.InstallFile, Language.Text(AppearanceMessages.InstallFile)),
                 .. available.Select(pair => (pair.Key, Language.FormatKey(AppearanceMessages.AvailableChoice, pair.Value))),
                 (AppearanceMessages.TurnOff, Language.Text(AppearanceMessages.TurnOff)),
                 (AppearanceMessages.RestorePrevious, Language.Text(AppearanceMessages.RestorePrevious))])
            .File("file", Language.Text(AppearanceMessages.FileLabel), "Screen savers (*.scr)|*.scr")
            .Text("minutes", Language.Text(AppearanceMessages.MinutesLabel), "10")
            .Check("secure", Language.Text(AppearanceMessages.SecureLabel))
            .Text("displayMinutes", Language.Text(AppearanceMessages.DisplayLabel), "")
            .Note(Loc.T("The setting applies to the user signed in on each computer. A screen saver is a program that " +
                "runs in that user's session, so install only files you built or trust."));
    }
}
