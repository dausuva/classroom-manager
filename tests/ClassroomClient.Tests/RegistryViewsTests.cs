// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Client;
using Microsoft.Win32;
using Xunit;

namespace ClassroomClient.Tests;

public sealed class RegistryViewsTests
{
    [Fact]
    public void Sixty_four_bit_windows_is_read_through_both_views_and_thirty_two_bit_windows_through_one()
    {
        Assert.Equal([RegistryView.Registry64, RegistryView.Registry32], RegistryViews.ForWindows(true));
        Assert.Equal([RegistryView.Registry64], RegistryViews.ForWindows(false));      // two views of one registry would list every program twice
    }

    [Fact]
    public void The_views_of_this_windows_follow_its_bitness()
    {
        Assert.Equal(Environment.Is64BitOperatingSystem ? 2 : 1, RegistryViews.All.Length);
    }
}
