// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.CompilerServices;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Trusted built-in presentation domains. No registration into the global/core translator.</summary>
public static class ModuleLanguages
{
    static readonly ConditionalWeakTable<Translation, Dictionary<string, ScopedTranslation>> Snapshots = new();
    public static ScopedTranslation Reporting => Current(BuiltInModules.Reporting);
    public static ScopedTranslation Browser => Current(BuiltInModules.Browser);
    public static ScopedTranslation Automation => Current(BuiltInModules.Automation);
    public static ScopedTranslation StudentWork => Current(BuiltInModules.StudentWork);
    public static ScopedTranslation Software => Current(BuiltInModules.Software);
    public static ScopedTranslation Maintenance => Current(BuiltInModules.Maintenance);

    public static string? Resource(string domain, string name)
    {
        using var stream = typeof(ModuleLanguages).Assembly.GetManifestResourceStream("locale.modules." + domain + "." + name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static ScopedTranslation Create(string domain, Translation language, Func<string, string?>? resource = null)
    {
        var scoped = language.IsOverride
            ? ScopedTranslation.FromLegacy(domain, language, PoFile.Parse(Resource(domain, "messages.pot") ?? "")
                .Where(entry => entry.Id.Length > 0).Select(entry => entry.Id))
            : ScopedTranslation.Load(domain, language.Language, resource ?? (culture => Resource(domain, culture + ".po")));
        if (scoped.RejectedCatalogs > 0) System.Diagnostics.Trace.WriteLine(string.Join(" ", "localization.domain", domain, scoped.RejectedCatalogs));
        return scoped;
    }

    static ScopedTranslation Current(string domain)
    {
        var language = Loc.Current;
        var domains = Snapshots.GetValue(language, _ => new(StringComparer.Ordinal));
        lock (domains)
        {
            if (!domains.TryGetValue(domain, out var scoped)) domains[domain] = scoped = Create(domain, language);
            return scoped;
        }
    }
}
