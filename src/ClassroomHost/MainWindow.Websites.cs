// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ClassroomManager.Shared;
#if BROWSER_INVENTORY_MODULE
using ClassroomManager.BrowserInventory;
#endif
#if REPORTING_MODULE
using ClassroomManager.Reporting;
#endif

namespace ClassroomManager.Host;

public partial class MainWindow
{
    void WebsiteBlockSync_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.ExamModeEnabled)
        {
            MessageBox.Show(this,
                "Turn off this Host's active Exam mode before enforcing the default website blocklist. The sync can still clear stale Exam-mode website blocks left by another Host.",
                "Website blocklist sync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var targets = Selected();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "Select one or more computers to synchronize.", "Website blocklist sync");
            return;
        }
        new WebsiteBlockSyncWindow(targets, _settings, this).Show();
    }

    async void BlockWebsites_Click(object sender, RoutedEventArgs e) => await BlockWebsitesAsync();

    /// <summary>The Block websites dialog; <paramref name="suggested"/> are domains to put in it, from the draft list.</summary>
    async Task BlockWebsitesAsync(IReadOnlyList<string>? suggested = null)
    {
        var targets = Selected();
        if (targets.Count == 0) return;

        // With exactly one computer targeted, show what's actually blocked on it right now instead of
        // asking you to type a list blind - the safest way to edit a hosts entry is to see it first.
        string prefill = "";
        string note = "";
        if (targets.Count == 1)
        {
            try
            {
                using var s = await ClientSession.OpenAsync(targets[0], _settings);
                var data = await s.CallAsync(Cmd.BlockHostsList);
                var current = data["domains"]!.AsArray().Select(d => d!.GetValue<string>()).ToList();
                prefill = string.Join("\n", current);
                var other = data["otherEntries"]!.GetValue<int>();
                note = current.Count == 0 ? "Nothing currently blocked on this computer. "
                    : $"Currently blocking {current.Count} domain(s) on this computer (shown below). ";
                if (other > 0) note += $"Its hosts file also has {other} unmanaged line(s) of its own, outside anything this tool controls.";
            }
            catch (Exception ex) { note = $"Could not read the current block: {ex.Message}"; }
        }

        if (suggested is { Count: > 0 })
        {
            prefill = WebsiteBlockPlan.Prefill(prefill, suggested);
            note = "The domains from your draft list are added below. " + note;
        }
        var dlg = new FormDialog("Block websites", this)
            .Target(targets)
            .Combo("mode", "Action", WebsiteBlockPlan.Modes, WebsiteBlockPlan.DefaultModeIndex(suggested is { Count: > 0 }, targets.Count))
            .Text("domains", "Domains, one per line or comma-separated (e.g. example.com, games.example.net)", prefill)
            .Note((note.Length > 0 ? note + "\n\n" : "") +
                  "Works by rewriting the hosts file, so it blocks the domain everywhere on the computer, not just in " +
                  "one browser - a determined student can still get around it (a VPN, a different DNS server, or the " +
                  "site's raw IP address), so treat this as a deterrent, not a filter. \"Add to the existing block\" " +
                  "reads each computer's own current list before adding to it, so running this on a room with mixed " +
                  "existing blocks never wipes one out by accident - unlike \"Replace\", which does exactly that.");
        if (!dlg.Ask("Apply")) return;

        var plan = WebsiteBlockPlan.Build(dlg.S("mode"), dlg.S("domains"));
        if (plan.Problem is { } problem) { MessageBox.Show(this, problem); return; }
        if (plan.IsDraft)
        {
            DraftWebsites(plan.Domains, WebsiteDrafts.SourceBlockDialog);
            return;
        }

        var targetText = OperationTargetSummary.Format(targets);
        var websiteReview = new BulkOperationReview(
            "Review website restriction",
            targetText.Heading + "\n" + targetText.Detail,
            plan.ReviewItems,
            "Run progress and failed computers remain available in Operations. Safely repeatable failures can be retried without changing successful computers again.",
            plan.Warning,
            "Apply restriction");
        if (new BulkOperationReviewWindow(this, websiteReview).Ask() != BulkOperationReviewDecision.Run) return;

        await RunOnAsync(targets, plan.Title, async (c, s) =>
        {
            if (plan.Mode == WebsiteBlockPlan.Clear)
            {
                var cleared = await s.CallAsync(Cmd.BlockHostsClear);
                UpdateBlockedWebsiteCount(c, cleared, "effectiveCount");
                return;
            }

            if (plan.Mode == WebsiteBlockPlan.Replace)
            {
                var replaced = await s.CallAsync(Cmd.BlockHostsSet, new JsonObject { ["domains"] = new JsonArray(plan.Domains.Select(d => (JsonNode)d).ToArray()) });
                UpdateBlockedWebsiteCount(c, replaced, "effectiveCount");
                return;
            }

            if (plan.UsesAddRequest(c.SupportsCommand(Cmd.BlockHostsAdd)))
            {
                UpdateBlockedWebsiteCount(c, await AddToWebsiteBlockAsync(s, plan.Domains), "effectiveCount");
                return;
            }

            // Merge / remove: read this computer's own current list first, so several computers with
            // different existing blocks each end up with the right result instead of all getting the
            // same list blindly overwritten.
            var current = await s.CallAsync(Cmd.BlockHostsList);
            var existing = current["domains"]!.AsArray().Select(d => d!.GetValue<string>()).ToList();
            var merged = plan.ListAfter(existing);
            var updated = await s.CallAsync(Cmd.BlockHostsSet, new JsonObject { ["domains"] = new JsonArray(merged.Select(d => (JsonNode)d).ToArray()) });
            UpdateBlockedWebsiteCount(c, updated, "effectiveCount");
        }, allowRetry: true, warnings: websiteReview.Warning);
    }

    /// <summary>Notes websites in the draft list and tells how many were new. Nothing is sent to a computer.</summary>
    void DraftWebsites(IEnumerable<string> domains, string source)
    {
        var all = domains.ToList();
        var added = WebsiteDrafts.Add(_settings.DraftWebsites, all, source, DateTime.Now);
        _settings.Save();
        MessageBox.Show(this,
            Loc.F($"{added} website(s) added to the draft list; {all.Count - added} were already there. Nothing is blocked. Open Website drafts... to block them later."),
            "Website drafts", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>The websites noted to block later: review them, remove some, or send the ticked ones to Block websites.</summary>
    async void WebsiteDrafts_Click(object sender, RoutedEventArgs e)
    {
        const string blockTicked = "Block the ticked websites...", removeTicked = "Remove the ticked websites from the list";
        if (_settings.DraftWebsites.Count == 0)
        {
            MessageBox.Show(this, "The draft list is empty. Note websites in it from Websites in use or Block websites.", "Website drafts",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new FormDialog("Website drafts", this)
            .Note($"{_settings.DraftWebsites.Count} website(s) are noted. Nothing here is blocked on any computer.")
            .MultiSelect("drafts", "Tick the websites", _settings.DraftWebsites.Select(WebsiteDrafts.Row).ToList(), filter: true)
            .Combo("what", "What to do", [blockTicked, removeTicked]);
        if (!dlg.Ask("Continue")) return;

        var ticked = dlg.S("drafts").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(WebsiteDrafts.DomainOfRow).Where(d => d.Length > 0).ToList();
        if (ticked.Count == 0) { MessageBox.Show(this, "Tick at least one website."); return; }
        if (dlg.S("what") == removeTicked)
        {
            WebsiteDrafts.Remove(_settings.DraftWebsites, ticked);
            _settings.Save();
            return;
        }
        if (Selected().Count == 0)
        {
            MessageBox.Show(this, "Select the computers to block them on, then choose Block the ticked websites again.", "Website drafts");
            return;
        }
        await BlockWebsitesAsync(ticked);
    }

    /// <summary>
    /// Adds domains to a computer's website block in one request, without reading the list first.
    /// The computer keeps what it already blocks; a name it does not accept as a website is an error.
    /// </summary>
    static async Task<JsonObject> AddToWebsiteBlockAsync(ClientSession session, IReadOnlyCollection<string> domains)
    {
        var answer = await session.CallAsync(Cmd.BlockHostsAdd, new JsonObject { ["domains"] = new JsonArray(domains.Select(d => (JsonNode)d).ToArray()) });
        if (answer["rejected"] is JsonNode rejected && long.TryParse(rejected.ToJsonString(), out var count) && count > 0)
            throw new InvalidOperationException($"{count} of {domains.Count} name(s) were not accepted as website domains; the others are blocked.");
        return answer;
    }

    /// <summary>
    /// Shows which websites are open in the browsers of the selected computers right now and adds the
    /// ticked ones to the website block. Each computer keeps what it already blocks.
    /// </summary>
    async void SitesInUse_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0 || !EnsureFeatureSupported(targets, Cmd.BrowserSites, "Websites in use")) return;

        var allOpen = new List<string>();
        var openOn = new Dictionary<ClientInfo, HashSet<string>>();
        bool anySite = false;
        var dlg = new FormDialog("Websites in use", this)
            .Target(targets)
            .LoadAsync($"Reading the open browser windows of {targets.Count} computer(s)...", async form =>
            {
                var answers = await Task.WhenAll(targets.Select(async client =>
                {
                    try
                    {
                        using var session = await ClientSession.OpenAsync(client, _settings);
                        return (Client: client, Data: (JsonObject?)await session.CallAtAsync(Levels.User, Cmd.BrowserSites), Error: "");
                    }
                    catch (Exception ex) { return (Client: client, Data: (JsonObject?)null, Error: ex.Message); }
                }));
                var seen = new List<(string Computer, string Domain, string Browser)>();
                foreach (var answer in answers.Where(answer => answer.Data is not null))
                {
                    var domains = BrowserSites.FromAnswer(answer.Data!).ToList();
                    openOn[answer.Client] = domains.Select(site => site.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    seen.AddRange(domains.Select(site => (answer.Client.Name, site.Domain, site.Browser)));
                }
                var sites = BrowserSites.Group(seen);
                anySite = sites.Count > 0;
                allOpen.AddRange(sites.Select(site => BrowserSites.DomainOfRow(site.Row)));
                var failed = answers.Where(answer => answer.Data is null).ToList();

                form.Note($"{answers.Length - failed.Count} of {targets.Count} computer(s) answered. " +
                          (anySite ? $"{sites.Count} website(s) are open in their browsers right now." : "No website is open in a browser right now.") +
                          " Only the tab in front of each browser window is seen; tabs behind it are not.");
                if (failed.Count > 0)
                    form.Note("Not read:\n" + string.Join("\n", failed.Take(12).Select(answer => $"• {answer.Client.Name}: {answer.Error}")));
                if (!anySite) return;
                form.MultiSelect("sites", "Tick the websites to block", sites.Select(site => site.Row).ToList(), filter: true);
                form.Combo("what", "What to do", [SitesInUsePlan.BlockNow, SitesInUsePlan.DraftTicked, SitesInUsePlan.DraftAll]);
                form.Combo("where", "Block on", [SitesInUsePlan.Everywhere, SitesInUsePlan.WhereOpen])
                    .Note("The ticked domains are added to each computer's website block; what a computer already blocks is kept. " +
                          "A page that is already open stays on screen until the student reloads it or follows a link. " +
                          "Remove a domain again with Block websites...");
            });
        if (!dlg.Ask("Block ticked") || !anySite) return;

        var plan = SitesInUsePlan.Build(dlg.S("what"), dlg.S("where"), dlg.S("sites"), openOn);
        if (plan.Action == SitesInUseAction.DraftAll)
        {
            DraftWebsites(allOpen, WebsiteDrafts.SourceInUse);
            return;
        }
        if (plan.Problem is { } problem) { MessageBox.Show(this, problem); return; }
        if (plan.Action == SitesInUseAction.DraftTicked)
        {
            DraftWebsites(plan.Blocked, WebsiteDrafts.SourceInUse);
            return;
        }
        var blockTargets = plan.Targets(targets);

        var targetText = OperationTargetSummary.Format(blockTargets);
        var review = new BulkOperationReview(
            "Review website restriction",
            targetText.Heading + "\n" + targetText.Detail,
            plan.ReviewItems,
            "Run progress and failed computers remain available in Operations. Safely repeatable failures can be retried without changing successful computers again.",
            "",
            "Apply restriction");
        if (new BulkOperationReviewWindow(this, review).Ask() != BulkOperationReviewDecision.Run) return;

        await RunOnAsync(blockTargets, plan.Title, async (c, s) =>
        {
            var add = plan.AddFor(c);
            if (c.SupportsCommand(Cmd.BlockHostsAdd))
            {
                UpdateBlockedWebsiteCount(c, await AddToWebsiteBlockAsync(s, add), "effectiveCount");
                return;
            }
            var current = await s.CallAsync(Cmd.BlockHostsList);
            var merged = current["domains"]!.AsArray().Select(d => d!.GetValue<string>()).Union(add, StringComparer.OrdinalIgnoreCase).ToList();
            var updated = await s.CallAsync(Cmd.BlockHostsSet, new JsonObject { ["domains"] = new JsonArray(merged.Select(d => (JsonNode)d).ToArray()) });
            UpdateBlockedWebsiteCount(c, updated, "effectiveCount");
        }, allowRetry: true);
    }

    /// <summary>
    /// Browser management on the selected computers: which browsers they have, the default browser, machine-wide
    /// policies for Chrome, Edge and Firefox (kept until restored) and two one-off clean-ups of the signed-in
    /// user's browser data.
    /// </summary>
    async void Browsers_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0 || !EnsureFeatureSupported(targets, Cmd.BrowserPolicySet, "Browser management")) return;

        var defaultChoices = new List<(string Id, string Name)>();
#if BROWSER_INVENTORY_MODULE
        using var inventoryCancellation = new CancellationTokenSource();
        var inventoryToken = inventoryCancellation.Token;
        var inventoryTargets = targets.Select((client, index) => new BrowserInventoryTarget(index, client.Name, client.User.Length > 0,
            client.SupportsCommand(Cmd.BrowserList), client.SupportsCommand(Cmd.BrowserProfilesList))).ToList();
        var inventory = new BrowserInventoryLoader(async (target, ct) =>
            new ClientBrowserInventoryConnection(await ClientSession.OpenAsync(targets[target.Id], _settings, ct), targets[target.Id]));
#endif
        var dlg = new FormDialog("Browsers", this)
            .Target(targets)
            .LoadAsync($"Reading browsers, policies and profiles from {targets.Count} computer(s)...", async form =>
            {
#if BROWSER_INVENTORY_MODULE
                if (BuiltInModules.Enabled(_settings, BuiltInModules.Browser))
                {
                var answers = await inventory.ReadAsync(inventoryTargets, inventoryToken, OperationsController.DefaultParallel);
                var report = BrowserInventorySummary.Project(answers);
                form.Note(BrowserInventorySummary.Describe(report), preserveText: true);
                form.Note(BrowserInventorySummary.Policies(report, targets.Count), preserveText: true);
                form.Note(BrowserInventorySummary.Profiles(report), preserveText: true);
                var incomplete = BrowserInventorySummary.ReadStatus(report);
                if (incomplete.Length > 0) form.Note(incomplete, preserveText: true);

                defaultChoices.AddRange(report.DefaultChoices.Select(browser => (browser.Id, browser.Name)));
                }
                else
                {
                    defaultChoices.AddRange(BrowserCatalog.Main.Select(browser => (browser.Id, browser.Name)));
                }
#else
                await Task.CompletedTask;
                defaultChoices.AddRange(BrowserCatalog.Main.Select(browser => (browser.Id, browser.Name)));
#endif
                form.Combo("defaultBrowser", "Default browser (every user, from their next sign-in)",
                    [BrowserChangePlan.Leave, .. defaultChoices.Select(choice => choice.Name), BrowserChangePlan.DefaultStop]);
                form.Combo("policyScope", "Policies below apply to", [BrowserChangePlan.ScopeComputer, BrowserChangePlan.ScopeUser])
                    .Note("A policy stays in place until it is changed back here.");
                foreach (var policy in BrowserRules.TranslationPolicies)
                    form.Combo(policy, $"Page translation in {BrowserRules.TranslationBrowser(policy)}", [BrowserChangePlan.Leave, BrowserChangePlan.TranslateOff, BrowserChangePlan.TranslateBack]);
                form.Combo("profilesPolicy", "New profiles and guest windows", [BrowserChangePlan.Leave, BrowserChangePlan.ProfilesOn, BrowserChangePlan.ProfilesOff])
                    .Combo("exitPolicy", "Browsing data when the browser closes", [BrowserChangePlan.Leave, BrowserChangePlan.ExitOn, BrowserChangePlan.ExitOff])
                    .Check("clearNow", "Clear the signed-in user's browsing data now (closes their browsers)")
                    .Check("history", "   History, downloads list and open tabs", true)
                    .Check("cache", "   Cached pages and images", true)
                    .Check("cookies", "   Cookies (signs the student out of every website)")
                    .Check("removeExtra", "Remove every browser profile except each browser's first one (closes the browsers)")
                    .Note("Windows lets each user choose their own default browser, so Classroom Manager gives Windows a default for every user, " +
                          "which it applies when a user signs in: sign the students out and in again to see it. A computer that does not have " +
                          "the browser for all its users says so and is left as it is.\n\n" +
                          "The policies are written where Chrome, Edge and Firefox read their administrative policies, so the browser " +
                          "enforces them and shows \"managed by your organization\". Chrome only clears on close with sync switched off, so that " +
                          "is set with it. Firefox has no policy against new profiles; its profile page is closed instead. Firefox's translation " +
                          "is turned off through its Preferences policy, which replaces other preferences an administrator set there until it is restored.\n\n" +
                          "Clearing and removing cannot be undone. A removed profile loses its bookmarks, history and saved passwords. " +
                          "Firefox keeps bookmarks and history in one file: its bookmarks come back from Firefox's own daily backup, which can be a day old.");
            });
#if BROWSER_INVENTORY_MODULE
        dlg.Closed += (_, _) => inventoryCancellation.Cancel();
#endif
        if (!dlg.Ask("Apply")) return;

        var plan = BrowserChangePlan.Build(new BrowserChoices(
            dlg.S("profilesPolicy"), dlg.S("exitPolicy"), BrowserRules.TranslationPolicies.ToDictionary(translation => translation, translation => dlg.S(translation)),
            dlg.S("policyScope"), dlg.S("defaultBrowser"), dlg.B("clearNow"), dlg.B("removeExtra"), dlg.B("history"), dlg.B("cache"), dlg.B("cookies")), defaultChoices);
        if (plan.Problem is { } problem) { MessageBox.Show(this, problem); return; }

        var targetText = OperationTargetSummary.Format(targets);
        var review = new BulkOperationReview(
            "Review browser changes",
            targetText.Heading + "\n" + targetText.Detail,
            plan.ReviewSteps(),
            "Run progress and failed computers remain available in Operations.",
            plan.Warning,
            "Apply");
        if (new BulkOperationReviewWindow(this, review).Ask() != BulkOperationReviewDecision.Run) return;

        await RunOnAsync(targets, "Browsers", async (c, s) =>
        {
            var (policyHere, translationLeftOut, block) = plan.PolicyFor(c.SupportsCommand(Cmd.BrowserList), c.SupportsCommand(Cmd.LockScreenSet), c.User.Length > 0);
            // A client before 1.12 does not know the translation policies and would pass over them without a word.
            if (translationLeftOut)
                Log($"  {c.Name}: page translation was not changed; update the client to protocol 1.12 first");
            if (block == PolicyBlock.UserPoliciesNeedNewerClient) Log($"  {c.Name}: policies not changed; policies for one user need client protocol 1.13");
            else if (block == PolicyBlock.NobodySignedIn) Log($"  {c.Name}: policies not changed; nobody is signed in");
            if (policyHere.Count > 0)
                Log($"  {c.Name}: {BrowserRules.DescribePolicies(await s.CallAsync(Cmd.BrowserPolicySet, policyHere))}");
            if (plan.DefaultBrowser is not null)
            {
                if (!c.SupportsCommand(Cmd.BrowserDefaultSet))
                    throw new InvalidOperationException("Setting the default browser needs client protocol 1.12. Update the client first.");
                Log($"  {c.Name}: {BrowserCatalog.Describe(await s.CallAsync(Cmd.BrowserDefaultSet, new JsonObject { [BrowserCatalog.Browser] = plan.DefaultBrowser }))}");
            }
            if (!plan.ClearNow && !plan.RemoveExtra) return;
            if (c.User.Length == 0) { Log($"  {c.Name}: nobody is signed in, so there is no browser data to clear"); return; }
            if (plan.RemoveExtra)
                Log($"  {c.Name}: {BrowserRules.DescribeRemoved(await s.CallAtAsync(Levels.User, Cmd.BrowserProfilesRemove))}");
            if (plan.ClearNow)
                Log($"  {c.Name}: {BrowserRules.DescribeCleared(await s.CallAtAsync(Levels.User, Cmd.BrowserDataClear, plan.ClearArguments()))}");
        }, warnings: review.Warning);
    }

    /// <summary>
    /// The browser profiles of one computer, and removal of the ones the teacher ticks. A browser's first profile
    /// is kept; removing a profile deletes its bookmarks, history and saved passwords and closes the browsers.
    /// </summary>
    async void BrowserProfiles_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (targets.Count != 1)
        {
            MessageBox.Show(this, "Select one computer: the profiles differ from computer to computer, so each is read and chosen on its own.", "Browser profiles");
            return;
        }
        var computer = targets[0];
        if (!computer.SupportsCommand(Cmd.BrowserProfilesList) || !BrowserRules.CanRemoveSelected(computer.Version))
        {
            MessageBox.Show(this, Loc.F($"{computer.Name} has a Client too old to remove chosen profiles. Update the Client first."), "Browser profiles");
            return;
        }
        if (computer.User.Length == 0)
        {
            MessageBox.Show(this, Loc.F($"Nobody is signed in on {computer.Name}, so there are no browser profiles to read."), "Browser profiles");
            return;
        }

        var rows = new Dictionary<string, BrowserRules.Profile>();
        var anyExtra = false;
        var dlg = new FormDialog("Browser profiles", this)
            .Target(targets)
            .LoadAsync($"Reading the browser profiles of {computer.Name}...", async form =>
            {
                using var session = await ClientSession.OpenAsync(computer, _settings);
                var profiles = BrowserRules.ProfilesFrom(await session.CallAtAsync(Levels.User, Cmd.BrowserProfilesList));
                foreach (var profile in profiles.Where(profile => !profile.IsFirst)) rows[$"{profile}  [{profile.Id}]"] = profile;
                anyExtra = rows.Count > 0;
                var kept = profiles.Where(profile => profile.IsFirst).Select(profile => profile.ToString()).ToList();
                form.Note(Loc.F($"{profiles.Count} profile(s) found. Each browser's first profile is always kept: {(kept.Count == 0 ? "-" : string.Join(", ", kept))}."));
                if (!anyExtra) { form.Note("There are no other profiles to remove."); return; }
                form.MultiSelect("profiles", "Tick the profiles to remove", rows.Keys.ToList());
                form.Note("Removing a profile deletes its bookmarks, history and saved passwords and closes the browsers on the computer without asking the student. It cannot be undone.");
            });
        if (!dlg.Ask("Remove ticked") || !anyExtra) return;

        var chosen = dlg.S("profiles").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(rows.ContainsKey).Select(row => rows[row]).ToList();
        if (chosen.Count == 0) { MessageBox.Show(this, "Tick at least one profile."); return; }
        if (MessageBox.Show(this,
                Loc.F($"Remove {chosen.Count} browser profile(s) from {computer.Name}?") + "\n\n" + string.Join("\n", chosen.Select(profile => profile.ToString())) +
                "\n\n" + Loc.T("Their bookmarks, history and saved passwords are deleted and the browsers are closed. This cannot be undone."),
                "Confirm profile removal", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        var args = new JsonObject { [BrowserRules.ProfileKeys] = new JsonArray(chosen.Select(profile => (JsonNode)profile.Key).ToArray()) };
        await RunOnAsync(targets, "Remove browser profiles", async (c, s) =>
            Log($"  {c.Name}: {BrowserRules.DescribeRemoved(await s.CallAtAsync(Levels.User, Cmd.BrowserProfilesRemove, args))}"));
    }

    static void UpdateBlockedWebsiteCount(ClientInfo client, JsonObject response, string property)
    {
        if (response[property] is JsonNode count) client.BlockedWebsiteCount = count.GetValue<int>();
    }
}
