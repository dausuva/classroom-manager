// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ExamModeScopeTests
{
    [Fact]
    public void A_computer_is_found_by_its_id_or_by_its_address()
    {
        var keys = new List<string>();
        ExamModeScope.Add(keys, "client-1", "10.0.0.5");

        Assert.True(ExamModeScope.Contains(keys, "client-1", "10.0.0.99"));   // its address changed
        Assert.True(ExamModeScope.Contains(keys, "", "10.0.0.5"));            // its id is not known yet
        Assert.True(ExamModeScope.Contains(keys, "CLIENT-1", ""));
        Assert.False(ExamModeScope.Contains(keys, "client-2", "10.0.0.6"));
    }

    [Fact]
    public void An_address_is_never_mistaken_for_an_id()
    {
        var keys = new List<string>();
        ExamModeScope.Add(keys, "", "PC-01");

        Assert.False(ExamModeScope.Contains(keys, "PC-01", "10.0.0.7"));
    }

    [Fact]
    public void Adding_twice_keeps_one_entry_and_removing_takes_both_keys_away()
    {
        var keys = new List<string>();
        ExamModeScope.Add(keys, "client-1", "10.0.0.5");
        ExamModeScope.Add(keys, "client-1", "10.0.0.5");
        ExamModeScope.Add(keys, "client-2", "10.0.0.6");
        Assert.Equal(4, keys.Count);

        ExamModeScope.Remove(keys, "client-1", "10.0.0.5");

        Assert.False(ExamModeScope.Contains(keys, "client-1", "10.0.0.5"));
        Assert.True(ExamModeScope.Contains(keys, "client-2", "10.0.0.6"));
    }

    [Fact]
    public void A_computer_without_id_and_address_is_never_in_scope()
    {
        var keys = new List<string>();
        ExamModeScope.Add(keys, " ", "");

        Assert.Empty(keys);
        Assert.False(ExamModeScope.Contains(keys, "", ""));
    }
}
