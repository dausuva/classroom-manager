// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class TeacherExportPlanTests
{
    [Fact]
    public void A_good_name_with_matching_passwords_has_no_problem_and_a_bad_name_or_mismatch_says_why()
    {
        Assert.Null(TeacherExportPlan.Problem("Room 12 teacher", "secret", "secret"));
        Assert.Null(TeacherExportPlan.Problem("Teacher", "", ""));
        Assert.Equal("The two passwords are not the same.", TeacherExportPlan.Problem("Teacher", "secret", "Secret"));
        var badName = TeacherExportPlan.Problem("", "a", "a");
        Assert.NotNull(badName);
        AccessKeyNames.IsAcceptable("", out var reason);
        Assert.Equal(reason, badName);
        // The name is judged before the passwords.
        Assert.Equal(reason, TeacherExportPlan.Problem("", "a", "b"));
    }

    [Fact]
    public void Only_an_empty_password_needs_the_extra_question()
    {
        Assert.True(TeacherExportPlan.NeedsNoPasswordConfirmation(""));
        Assert.False(TeacherExportPlan.NeedsNoPasswordConfirmation(" "));
        Assert.False(TeacherExportPlan.NeedsNoPasswordConfirmation("x"));
    }

    [Fact]
    public void The_key_is_a_Teacher_key_of_the_given_name()
    {
        Assert.Equal("Add Teacher access key 'Anna'", TeacherExportPlan.Title("Anna"));
        var arguments = TeacherExportPlan.Arguments("Anna", "k-12345678901");
        Assert.Equal("Anna", arguments["name"]!.GetValue<string>());
        Assert.Equal("Teacher", arguments["role"]!.GetValue<string>());
        Assert.Equal("k-12345678901", arguments["key"]!.GetValue<string>());
    }

    [Fact]
    public void The_file_lists_each_computer_with_its_address_name_room_and_a_normalised_card()
    {
        var computers = TeacherExportPlan.Computers([new ClientInfo
        {
            Address = "10.42.0.5", ComputerName = "PC-05", Alias = "Front", ClientId = "id-5", Mac = "aa-bb-cc-dd-ee-ff", Port = 5050, Room = "Lab 1",
        }]);
        var one = Assert.Single(computers);
        Assert.Equal(("10.42.0.5", "PC-05", "Front", "id-5", 5050, "Lab 1"), (one.Address, one.Name, one.Alias, one.ClientId, one.Port, one.Room));
        Assert.Equal(ComputerIdentity.NormalizeMac("aa-bb-cc-dd-ee-ff"), one.Mac);
    }

    [Fact]
    public void The_closing_texts_say_how_many_took_the_key_and_whether_the_file_is_protected()
    {
        var all = TeacherExportPlan.Summary(@"C:\t.json", "Anna", 3, 3, passwordSet: true);
        Assert.StartsWith(@"Saved C:\t.json", all);
        Assert.Contains("The Teacher key 'Anna' is on 3 of 3 computer(s).", all);
        Assert.DoesNotContain("did not take it", all);
        Assert.EndsWith("Give the teacher the file, and the password by another way.", all);

        var some = TeacherExportPlan.Summary("f", "Anna", 2, 5, passwordSet: false);
        Assert.Contains("is on 2 of 5 computer(s). 3 did not take it", some);
        Assert.EndsWith("hand it over privately and delete copies you do not need.", some);

        var failed = TeacherExportPlan.WriteFailed("Anna", "disk full");
        Assert.Contains("The key 'Anna' is on the computers, but the file could not be written: disk full", failed);
    }
}

public sealed class InterfaceSettingsPlanTests
{
    static readonly List<(string Language, string Name, string? File)> Available =
    [
        ("en-US", "English", null), ("lt-LT", "Lietuvių", null), ("de-DE", "", @"C:\l\de.po"),
    ];

    [Fact]
    public void The_languages_start_with_the_Windows_choice_and_name_each_one()
    {
        var choices = InterfaceSettingsPlan.Languages("System", Available);
        Assert.Equal(["System", "en-US", "lt-LT", "de-DE"], choices.Codes);
        Assert.Equal(["Same as Windows", "English (en-US)", "Lietuvių (lt-LT)", "de-DE"], choices.Labels);
    }

    [Fact]
    public void A_stored_language_that_is_no_longer_offered_stays_selectable_and_is_not_added_twice()
    {
        var gone = InterfaceSettingsPlan.Languages("fr-FR", Available);
        Assert.Equal("fr-FR", gone.Codes[^1]);
        Assert.Equal("fr-FR", gone.Labels[^1]);
        Assert.Equal(4, gone.IndexOf("fr-FR"));

        var same = InterfaceSettingsPlan.Languages("LT-lt", Available);
        Assert.Equal(4, same.Codes.Count);   // matched ignoring case: nothing added
    }

    [Fact]
    public void A_label_maps_back_to_its_code_and_an_unknown_one_means_Windows()
    {
        var choices = InterfaceSettingsPlan.Languages("lt-LT", Available);
        Assert.Equal("lt-LT", choices.CodeFor("Lietuvių (lt-LT)"));
        Assert.Equal("en-US", choices.CodeFor("English (en-US)"));
        Assert.Equal("System", choices.CodeFor("nonsense"));
        Assert.Equal(2, choices.IndexOf("lt-LT"));
        Assert.Equal(0, choices.IndexOf("xx"));
    }

    [Theory]
    [InlineData("System", 0)]
    [InlineData("Light", 1)]
    [InlineData("Dark", 2)]
    [InlineData("Sepia", 1)]
    [InlineData("", 1)]
    public void The_theme_is_found_in_the_choices_and_light_is_the_fallback(string theme, int expected) =>
        Assert.Equal(expected, InterfaceSettingsPlan.ThemeIndex(["System", "Light", "Dark"], theme));

    [Fact]
    public void The_ticked_quick_actions_become_their_ids_in_catalogue_order()
    {
        var all = QuickActionCatalog.All;
        var text = string.Join("\n", all[2].Label.ToUpperInvariant(), all[0].Label, "not an action", "");
        Assert.Equal([all[0].Id, all[2].Id], InterfaceSettingsPlan.QuickActionIds(text));
        Assert.Empty(InterfaceSettingsPlan.QuickActionIds(""));
    }

    [Fact]
    public void Chosen_splits_on_lines_ignores_empty_ones_and_case()
    {
        var set = InterfaceSettingsPlan.Chosen("A\n\nb\n");
        Assert.Equal(2, set.Count);
        Assert.Contains("B", set);
    }

    [Fact]
    public void Teacher_commands_hidden_by_a_module_are_kept_and_the_ticked_ones_of_each_group_are_added()
    {
        var groups = TeacherCommandCatalog.All.Select(c => c.Group).Distinct().Take(2).ToList();
        var first = TeacherCommandCatalog.All.First(c => c.Group == groups[0]);
        var second = TeacherCommandCatalog.All.First(c => c.Group == groups[1]);
        var hiddenByModule = TeacherCommandCatalog.All.Last(c => c.Id != first.Id && c.Id != second.Id).Id;

        var result = InterfaceSettingsPlan.TeacherVisible(
            stored: [hiddenByModule, first.Id],                  // first.Id was on, but is offered and now unticked: dropped
            offered: id => id != hiddenByModule,
            groups: groups,
            chosenForGroup: i => i == 0 ? "" : second.Label);

        Assert.Contains(hiddenByModule, result, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(second.Id, result, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(first.Id, result, StringComparer.OrdinalIgnoreCase);

        var ticked = InterfaceSettingsPlan.TeacherVisible([], _ => true, groups, i => i == 0 ? first.Label : "");
        Assert.Contains(first.Id, ticked, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(second.Id, ticked, StringComparer.OrdinalIgnoreCase);
    }
}
