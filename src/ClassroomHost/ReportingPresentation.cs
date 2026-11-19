// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;

namespace ClassroomManager.Host;

/// <summary>Wording only. CSV schema/data and output/authorization stay outside localization.</summary>
public static class ReportingPresentation
{
    public static FormDialog Form(Window? owner, IReadOnlyCollection<ClientInfo> targets)
    {
        var language = ModuleLanguages.Reporting;
        var dialog = new FormDialog(language.Text("Export inventory"), owner);
        UiTranslator.SetPreserveText(dialog, true);
        return dialog.Target(targets).Folder("folder", language.Text("Save CSV files to"))
            .Note(language.Text("Writes one CSV of hardware info and one CSV of installed programs, covering every selected computer."));
    }

    public static string ChooseFolder => ModuleLanguages.Reporting.Text("Choose a folder to save into.");
    public static string Collect => ModuleLanguages.Reporting.Text("Collect inventory");
    public const string CollectEnglish = "Collect inventory";
    public static string Written(int computers, string hardware, int programs, string software, bool english = false)
    {
        FormattableString message = $"Inventory: wrote {computers} computer(s) to {hardware} and {programs} program row(s) to {software}.";
        return english ? message.ToString(System.Globalization.CultureInfo.InvariantCulture) : ModuleLanguages.Reporting.Format(message);
    }
    public static string SaveFailed(string detail) => ModuleLanguages.Reporting.Format($"Collected the data but could not save the CSV files: {detail}");
}
