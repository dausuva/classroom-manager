// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>What the Browsers dialog asks for, decided without a window (T-253 phase 2): the policy, the default browser, the review, and what each computer can take.</summary>
public sealed class BrowserChangePlanTests
{
    static readonly (string Id, string Name)[] Browsers = [("chrome", "Google Chrome"), ("edge", "Microsoft Edge")];

    static BrowserChoices Choices(string profiles = BrowserChangePlan.Leave, string exit = BrowserChangePlan.Leave, string chrome = BrowserChangePlan.Leave,
        string edge = BrowserChangePlan.Leave, string firefox = BrowserChangePlan.Leave, string scope = BrowserChangePlan.ScopeComputer, string defaultBrowser = BrowserChangePlan.Leave,
        bool clearNow = false, bool removeExtra = false, bool history = true, bool cache = true, bool cookies = false) =>
        new(profiles, exit, new Dictionary<string, string>
        {
            [BrowserRules.NoTranslateChrome] = chrome, [BrowserRules.NoTranslateEdge] = edge, [BrowserRules.NoTranslateFirefox] = firefox,
        }, scope, defaultBrowser, clearNow, removeExtra, history, cache, cookies);

    static BrowserChangePlan Plan(BrowserChoices choices) => BrowserChangePlan.Build(choices, Browsers);

    [Fact]
    public void Choosing_nothing_is_a_problem_and_so_is_clearing_nothing()
    {
        Assert.Equal("Nothing was chosen to change.", Plan(Choices()).Problem);
        Assert.Equal("Choose what to clear: history, cache or cookies.", Plan(Choices(clearNow: true, history: false, cache: false, cookies: false)).Problem);
        Assert.Null(Plan(Choices(clearNow: true, cookies: true, history: false, cache: false)).Problem);
        Assert.Null(Plan(Choices(removeExtra: true)).Problem);
        Assert.Null(Plan(Choices(profiles: BrowserChangePlan.ProfilesOn)).Problem);
        Assert.Null(Plan(Choices(defaultBrowser: BrowserChangePlan.DefaultStop)).Problem);
    }

    [Fact]
    public void Each_choice_becomes_the_policy_value_it_names()
    {
        var plan = Plan(Choices(profiles: BrowserChangePlan.ProfilesOn, exit: BrowserChangePlan.ExitOff, chrome: BrowserChangePlan.TranslateOff, edge: BrowserChangePlan.TranslateBack));
        Assert.True((bool?)plan.Policy[BrowserRules.PreventProfiles]);
        Assert.False((bool?)plan.Policy[BrowserRules.ClearOnExit]);
        Assert.True((bool?)plan.Policy[BrowserRules.NoTranslateChrome]);
        Assert.False((bool?)plan.Policy[BrowserRules.NoTranslateEdge]);
        Assert.Null(plan.Policy[BrowserRules.NoTranslateFirefox]);
        Assert.Equal(4, plan.Policy.Count);
        Assert.False(plan.ForUser);
        Assert.True(Plan(Choices(profiles: BrowserChangePlan.ProfilesOff, scope: BrowserChangePlan.ScopeUser)).ForUser);
        Assert.False((bool?)Plan(Choices(profiles: BrowserChangePlan.ProfilesOff)).Policy[BrowserRules.PreventProfiles]);
    }

    [Fact]
    public void The_default_browser_is_left_stopped_or_named_by_its_id()
    {
        Assert.Null(Plan(Choices(profiles: BrowserChangePlan.ProfilesOn)).DefaultBrowser);
        Assert.Equal("", Plan(Choices(defaultBrowser: BrowserChangePlan.DefaultStop)).DefaultBrowser);
        Assert.Equal("edge", Plan(Choices(defaultBrowser: "Microsoft Edge")).DefaultBrowser);
        Assert.Throws<InvalidOperationException>(() => Plan(Choices(defaultBrowser: "Netscape")));
    }

    [Fact]
    public void The_review_lists_what_will_be_done_in_the_order_the_teacher_reads_it()
    {
        var plan = Plan(Choices(profiles: BrowserChangePlan.ProfilesOn, exit: BrowserChangePlan.ExitOn, chrome: BrowserChangePlan.TranslateOff, firefox: BrowserChangePlan.TranslateBack,
            scope: BrowserChangePlan.ScopeUser, defaultBrowser: "Google Chrome", clearNow: true, history: true, cache: false, cookies: true, removeExtra: true));
        Assert.Equal(
        [
            ("Default browser", "Google Chrome, from each user's next sign-in"),
            ("Translation in Google Chrome", BrowserChangePlan.TranslateOff),
            ("Translation in Mozilla Firefox", BrowserChangePlan.TranslateBack),
            ("Policies for", BrowserChangePlan.ScopeUser),
            ("New profiles", BrowserChangePlan.ProfilesOn),
            ("On close", BrowserChangePlan.ExitOn),
            ("Clear now", "history, cookies"),
            ("Profiles", "Remove all but each browser's first profile"),
        ], plan.ReviewSteps().Select(step => (step.Label, step.Value)));
    }

    [Fact]
    public void Stopping_the_default_browser_and_restoring_a_policy_are_worded_as_restoring()
    {
        var steps = Plan(Choices(defaultBrowser: BrowserChangePlan.DefaultStop, exit: BrowserChangePlan.ExitOff)).ReviewSteps().Select(step => (step.Label, step.Value)).ToList();
        Assert.Equal(("Default browser", BrowserChangePlan.DefaultStop), steps[0]);
        Assert.Contains(("On close", BrowserChangePlan.ExitOff), steps);
        Assert.Contains(("Policies for", BrowserChangePlan.ScopeComputer), steps);
    }

    [Fact]
    public void Only_clearing_or_removing_earns_the_warning_that_the_browsers_are_closed()
    {
        Assert.Equal("", Plan(Choices(profiles: BrowserChangePlan.ProfilesOn)).Warning);
        Assert.Contains("cannot be restored", Plan(Choices(clearNow: true)).Warning);
        Assert.Contains("cannot be restored", Plan(Choices(removeExtra: true)).Warning);
        Assert.True(Plan(Choices(clearNow: true)).NeedsWarning);
        Assert.False(Plan(Choices(exit: BrowserChangePlan.ExitOn)).NeedsWarning);
    }

    [Fact]
    public void A_computer_that_knows_everything_gets_the_whole_policy_and_one_for_a_user_is_marked_for_the_user()
    {
        var plan = Plan(Choices(chrome: BrowserChangePlan.TranslateOff, exit: BrowserChangePlan.ExitOn, scope: BrowserChangePlan.ScopeUser));
        var (policy, translationLeftOut, block) = plan.PolicyFor(supportsTranslation: true, supportsUserPolicy: true, signedIn: true);
        Assert.False(translationLeftOut);
        Assert.Equal(PolicyBlock.None, block);
        Assert.True((bool?)policy[BrowserRules.ForUser]);
        Assert.Equal(3, policy.Count);
        Assert.Equal(2, plan.Policy.Count);   // the plan itself is not changed by what one computer gets

        var (machine, _, _) = Plan(Choices(exit: BrowserChangePlan.ExitOn)).PolicyFor(true, false, false);
        Assert.Null(machine[BrowserRules.ForUser]);
        Assert.Single(machine);
    }

    [Fact]
    public void An_old_Client_is_not_sent_translation_policies_it_would_pass_over_in_silence()
    {
        var plan = Plan(Choices(chrome: BrowserChangePlan.TranslateOff, profiles: BrowserChangePlan.ProfilesOn));
        var (policy, translationLeftOut, block) = plan.PolicyFor(supportsTranslation: false, supportsUserPolicy: true, signedIn: true);
        Assert.True(translationLeftOut);
        Assert.Equal(PolicyBlock.None, block);
        Assert.Null(policy[BrowserRules.NoTranslateChrome]);
        Assert.True((bool?)policy[BrowserRules.PreventProfiles]);

        var onlyTranslation = Plan(Choices(chrome: BrowserChangePlan.TranslateOff, scope: BrowserChangePlan.ScopeUser)).PolicyFor(false, true, true);
        Assert.True(onlyTranslation.TranslationLeftOut);
        Assert.Empty(onlyTranslation.Policy);
        Assert.Equal(PolicyBlock.None, onlyTranslation.Block);   // nothing left to send, so nothing is blocked for the user either
    }

    [Fact]
    public void Policies_for_one_user_need_a_new_enough_Client_and_somebody_signed_in_and_otherwise_none_is_sent()
    {
        var plan = Plan(Choices(exit: BrowserChangePlan.ExitOn, scope: BrowserChangePlan.ScopeUser));
        var old = plan.PolicyFor(true, supportsUserPolicy: false, signedIn: true);
        Assert.Equal(PolicyBlock.UserPoliciesNeedNewerClient, old.Block);
        Assert.Empty(old.Policy);
        var nobody = plan.PolicyFor(true, supportsUserPolicy: true, signedIn: false);
        Assert.Equal(PolicyBlock.NobodySignedIn, nobody.Block);
        Assert.Empty(nobody.Policy);
        var both = plan.PolicyFor(true, supportsUserPolicy: false, signedIn: false);
        Assert.Equal(PolicyBlock.UserPoliciesNeedNewerClient, both.Block);   // the version is named first: updating comes before signing in
    }

    [Fact]
    public void The_arguments_of_clearing_carry_the_three_choices_under_the_names_the_Client_reads()
    {
        var args = Plan(Choices(clearNow: true, history: true, cache: false, cookies: true)).ClearArguments();
        Assert.Equal(["cache", "cookies", "history"], args.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.True((bool?)args[BrowserRules.History]);
        Assert.False((bool?)args[BrowserRules.Cache]);
        Assert.True((bool?)args[BrowserRules.Cookies]);
    }
}
