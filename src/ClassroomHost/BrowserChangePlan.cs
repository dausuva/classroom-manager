// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>The choices of the Browsers dialog, as the texts the teacher saw (T-253 phase 2: the decisions of <c>Browsers_Click</c>, taken out of the window).</summary>
public sealed record BrowserChoices(
    string Profiles, string Exit, IReadOnlyDictionary<string, string> Translation, string Scope, string DefaultBrowser,
    bool ClearNow, bool RemoveExtra, bool History, bool Cache, bool Cookies);

/// <summary>Why a computer does not get the policies the teacher chose, so the window can say so in its own words.</summary>
public enum PolicyBlock { None, UserPoliciesNeedNewerClient, NobodySignedIn }

/// <summary>
/// What the Browsers dialog asks for, decided without a window: the policy to write, the default browser, the clearing, the review shown before it runs,
/// and what each computer can take of it. Texts are the dialog's labels; the log lines stay in the window so they keep their translations.
/// </summary>
public sealed class BrowserChangePlan
{
    public const string Leave = "Leave as it is";
    public const string ProfilesOn = "Prevent new profiles and guest windows", ProfilesOff = "Allow them again (restore the earlier setting)";
    public const string ExitOn = "Clear browsing data whenever the browser closes", ExitOff = "Stop clearing on close (restore the earlier setting)";
    public const string TranslateOff = "Turn page translation off", TranslateBack = "Allow page translation again (restore the earlier setting)";
    public const string DefaultStop = "Stop setting a default browser (restore the earlier setting)";
    public const string ScopeComputer = "All users of each computer", ScopeUser = "Only the signed-in user";

    public JsonObject Policy { get; }
    public bool ForUser { get; }

    /// <summary>null: leave the default alone; "": stop setting one; otherwise the browser's id.</summary>
    public string? DefaultBrowser { get; }
    public string DefaultChoice { get; }
    public bool ClearNow { get; }
    public bool RemoveExtra { get; }
    public bool History { get; }
    public bool Cache { get; }
    public bool Cookies { get; }

    /// <summary>What is wrong with the choices (shown to the teacher), or null.</summary>
    public string? Problem { get; }

    public bool NeedsWarning => ClearNow || RemoveExtra;

    BrowserChangePlan(BrowserChoices choices, IReadOnlyList<(string Id, string Name)> defaultChoices)
    {
        Policy = new JsonObject();
        if (choices.Profiles != Leave) Policy[BrowserRules.PreventProfiles] = choices.Profiles == ProfilesOn;
        if (choices.Exit != Leave) Policy[BrowserRules.ClearOnExit] = choices.Exit == ExitOn;
        foreach (var translation in BrowserRules.TranslationPolicies)
            if (choices.Translation.TryGetValue(translation, out var choice) && choice != Leave) Policy[translation] = choice == TranslateOff;
        ForUser = choices.Scope == ScopeUser;
        DefaultChoice = choices.DefaultBrowser;
        DefaultBrowser = choices.DefaultBrowser == Leave ? null : choices.DefaultBrowser == DefaultStop ? ""
            : defaultChoices.First(choice => choice.Name == choices.DefaultBrowser).Id;
        ClearNow = choices.ClearNow; RemoveExtra = choices.RemoveExtra;
        History = choices.History; Cache = choices.Cache; Cookies = choices.Cookies;
        if (ClearNow && !History && !Cache && !Cookies) Problem = "Choose what to clear: history, cache or cookies.";
        else if (Policy.Count == 0 && DefaultBrowser is null && !ClearNow && !RemoveExtra) Problem = "Nothing was chosen to change.";
    }

    public static BrowserChangePlan Build(BrowserChoices choices, IReadOnlyList<(string Id, string Name)> defaultChoices) => new(choices, defaultChoices);

    /// <summary>The lines of the review the teacher confirms before anything runs.</summary>
    public IReadOnlyList<BulkOperationReviewItem> ReviewSteps()
    {
        var steps = new List<BulkOperationReviewItem>();
        if (DefaultBrowser is not null) steps.Add(new("Default browser", DefaultBrowser.Length == 0 ? DefaultStop : $"{DefaultChoice}, from each user's next sign-in"));
        foreach (var translation in BrowserRules.TranslationPolicies.Where(translation => Policy[translation] is not null))
            steps.Add(new($"Translation in {BrowserRules.TranslationBrowser(translation)}", Policy[translation]!.GetValue<bool>() ? TranslateOff : TranslateBack));
        if (Policy.Count > 0) steps.Add(new("Policies for", ForUser ? ScopeUser : ScopeComputer));
        if (Policy[BrowserRules.PreventProfiles] is JsonNode prevent) steps.Add(new("New profiles", prevent.GetValue<bool>() ? ProfilesOn : ProfilesOff));
        if (Policy[BrowserRules.ClearOnExit] is JsonNode onExit) steps.Add(new("On close", onExit.GetValue<bool>() ? ExitOn : ExitOff));
        if (ClearNow) steps.Add(new("Clear now", string.Join(", ", new[] { History ? "history" : "", Cache ? "cache" : "", Cookies ? "cookies" : "" }.Where(part => part.Length > 0))));
        if (RemoveExtra) steps.Add(new("Profiles", "Remove all but each browser's first profile"));
        return steps;
    }

    public string Warning => NeedsWarning ? "The students' browsers are closed without asking them, and what is cleared or removed cannot be restored." : "";

    /// <summary>
    /// The policy this computer can take: a Client before protocol 1.12 does not know the translation policies and would pass over them without a word (they are
    /// left out and <c>TranslationLeftOut</c> says so); policies for one user need protocol 1.13 and someone signed in (otherwise none is sent and <c>Block</c> says why).
    /// </summary>
    public (JsonObject Policy, bool TranslationLeftOut, PolicyBlock Block) PolicyFor(bool supportsTranslation, bool supportsUserPolicy, bool signedIn)
    {
        var policy = (JsonObject)Policy.DeepClone();
        var translationLeftOut = !supportsTranslation && BrowserRules.TranslationPolicies.Any(translation => policy.Remove(translation));
        var block = PolicyBlock.None;
        if (policy.Count > 0 && ForUser)
        {
            if (!supportsUserPolicy) { block = PolicyBlock.UserPoliciesNeedNewerClient; policy.Clear(); }
            else if (!signedIn) { block = PolicyBlock.NobodySignedIn; policy.Clear(); }
            else policy[BrowserRules.ForUser] = true;
        }
        return (policy, translationLeftOut, block);
    }

    /// <summary>The arguments of browser.data.clear.</summary>
    public JsonObject ClearArguments() => new() { [BrowserRules.History] = History, [BrowserRules.Cache] = Cache, [BrowserRules.Cookies] = Cookies };
}
