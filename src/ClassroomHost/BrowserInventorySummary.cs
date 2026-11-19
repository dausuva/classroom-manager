// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.BrowserInventory;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Localized presentation only; aggregation belongs to the typed module.</summary>
public static class BrowserInventorySummary
{
    static ScopedTranslation Language => ModuleLanguages.Browser;

    public static string Policies(BrowserInventoryReport report, int selected) => string.Join("\n",
        new[] { Language.Format($"Policies read from {report.PoliciesRead} of {selected} computer(s):") }
            .Concat(report.Policies.Select(policy => Language.Format($"• {Loc.T(BrowserRules.PolicyName(policy.Id))}: on at {policy.On} of {report.PoliciesRead} computers"))));
    public static BrowserInventoryReport Project(IReadOnlyList<BrowserInventoryResult> answers) =>
        BrowserInventoryModule.Project(answers,
            BrowserCatalog.Main.Select(browser => new BrowserIdentity(browser.Id, browser.Name)).ToArray(),
            BrowserCatalog.All.Select(browser => new BrowserIdentity(browser.Id, browser.Name)).ToArray(), BrowserRules.Policies);

    static BrowserInventoryResult BrowserAnswer(int id, string computer, BrowserSnapshot? answer) =>
        new(new(id, computer, true, true, true), new(InventoryReadStatus.Failed),
            new(answer is null ? InventoryReadStatus.Failed : InventoryReadStatus.Success, answer), new(InventoryReadStatus.Failed));

    public static string Describe(IReadOnlyList<(string Computer, BrowserSnapshot? Answer)> answers) =>
        Describe(Project(answers.Select((answer, id) => BrowserAnswer(id, answer.Computer, answer.Answer)).ToArray()));

    public static string Describe(BrowserInventoryReport report)
    {
        if (report.BrowsersRead == 0) return Language.Text("Browser inventory could not be read. See the per-computer read status.");
        var lines = new List<string> { Language.Format($"Browsers read from {report.BrowsersRead} of {report.Total} computer(s):") };
        foreach (var main in report.MainBrowsers)
        {
            var missing = main.Missing;
            var names = string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? ", ..." : "");
            lines.Add(missing.Count == 0
                ? Language.Format($"• {main.Browser.Name}: on all {report.BrowsersRead} computers")
                : Language.Format($"• {main.Browser.Name}: on {main.Present} of {report.BrowsersRead} computers; missing on {names}"));
        }
        if (report.Others.Count > 0)
            lines.Add(Language.Format($"• Also: {string.Join(", ", report.Others.Select(group => $"{group.Name} ({group.Count})"))}"));
        if (report.Defaults.Count > 0)
            lines.Add(Language.Format($"Opens web links for the signed-in user: {string.Join(", ", report.Defaults.Select(group => $"{group.Name} ({group.Count})"))}"));
        if (report.Managed.Count > 0)
            lines.Add(Language.Format($"Default set at sign-in: {string.Join(", ", report.Managed.Select(group => $"{group.Name} ({group.Count})"))}"));
        return string.Join("\n", lines);
    }

    public static List<(string Id, string Name)> DefaultChoices(IEnumerable<BrowserSnapshot?> answers) =>
        Project(answers.Select((answer, id) => BrowserAnswer(id, "", answer)).ToArray()).DefaultChoices.Select(browser => (browser.Id, browser.Name)).ToList();

    public static string Profiles(IReadOnlyList<BrowserInventoryResult> answers) => Profiles(Project(answers));

    public static string Profiles(BrowserInventoryReport report)
    {
        if (report.ProfilesRead == 0) return Language.Text("Browser profiles were not read; their state is unknown.");
        var extra = report.ExtraProfiles;
        return Language.Format($"Profiles read from {report.ProfilesRead} of {report.Total} computer(s):") + "\n" + (extra.Count == 0
            ? Language.Text("No extra profiles were found on the computers whose profiles were read.")
            : Language.Text("Extra browser profiles:") + "\n" + string.Join("\n", extra.Take(15).Select(answer =>
                $"• {answer.Computer}: {string.Join(", ", answer.Profiles)}")) + (extra.Count > 15 ? "\n..." : ""));
    }

    public static string ReadStatus(IReadOnlyList<BrowserInventoryResult> answers) => ReadStatus(Project(answers));

    public static string ReadStatus(BrowserInventoryReport report)
    {
        static string Name(InventoryReadStatus status) => status switch
        {
            InventoryReadStatus.Success => Language.Text("read"),
            InventoryReadStatus.Unsupported => Language.Text("not supported"),
            InventoryReadStatus.NoUser => Language.Text("no signed-in user"),
            InventoryReadStatus.Unavailable => Language.Text("unavailable"),
            _ => Language.Text("failed"),
        };
        var incomplete = report.Incomplete;
        return incomplete.Count == 0 ? "" : Language.Text("Incomplete inventory (unread data is unknown):") + "\n" +
            string.Join("\n", incomplete.Take(15).Select(answer => Language.Format($"{answer.Target.Computer}: policies {Name(answer.Policies.Status)}, browsers {Name(answer.Browsers.Status)}, profiles {Name(answer.Profiles.Status)}"))) +
            (incomplete.Count > 15 ? "\n..." : "");
    }
}
