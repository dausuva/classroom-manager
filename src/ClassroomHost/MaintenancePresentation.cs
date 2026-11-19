// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Maintenance;
using ClassroomManager.Shared;
using System.Globalization;

namespace ClassroomManager.Host;

public static class MaintenancePresentation
{
    static readonly ScopedTranslation English = ScopedTranslation.Load(BuiltInModules.Maintenance, Loc.English, _ => null);
    public static string Unnamed => ModuleLanguages.Maintenance.Text("Unnamed update");
    public static string Details(UpdateProjection update, bool invariant = false)
    {
        var language = invariant ? English : ModuleLanguages.Maintenance;
        var details = new List<string>();
        if (update.Kb.Count > 0) details.Add(string.Join(", ", update.Kb));
        if (update.SizeMb is { } size)
        {
            FormattableString amount = $"{size:0.0} MB";
            details.Add(invariant ? amount.ToString(CultureInfo.InvariantCulture) : language.Format(amount));
        }
        if (update.Feature) details.Add(language.Text("new Windows version"));
        else if (update.Optional) details.Add(language.Text("optional/driver"));
        if (update.RequiresRestart) details.Add(language.Text("restart may be required"));
        return string.Join(" · ", details);
    }

    /// <summary>The existing snapshot schema stores English detail components, not a localized UI sentence.</summary>
    public static string SavedDetails(string stored) => string.Join(" · ", stored.Split(" · ", StringSplitOptions.None).Select(part =>
        part.EndsWith(" MB", StringComparison.Ordinal) && double.TryParse(part.AsSpan(0, part.Length - 3), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var size)
            ? ModuleLanguages.Maintenance.Format($"{size:0.0} MB") : ModuleLanguages.Maintenance.Text(part)));

    public static string Pause(UpdatePauseProjection projection) => projection.Paused && projection.Until is { } expiry
        ? ModuleLanguages.Maintenance.Format($"Paused until {expiry.ToLocalTime():g}")
        : ModuleLanguages.Maintenance.Text(projection.Paused ? "Paused" : "Running normally");

    public static string Header(string computer, string address, string status, string pause, string checkedUtc) =>
        $"{computer} ({address}) — {Loc.Current.Translate(status)} · {pause}" +
        (DateTime.TryParse(checkedUtc, out var at) ? ModuleLanguages.Maintenance.Format($" · checked {at.ToLocalTime():g}") : "");
}
