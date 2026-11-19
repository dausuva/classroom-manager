// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class DeviceKeysTests
{
    const string Master = "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs";
    const string Id17 = "6532665a4a5e45f4bad18a2d66b44aaa";
    const string Id15 = "11112222333344445555666677778888";

    [Fact]
    public void The_same_master_and_computer_always_give_the_same_key()
    {
        Assert.Equal(DeviceKeys.Derive(Master, Id17), DeviceKeys.Derive(Master, Id17));
        Assert.Equal(DeviceKeys.Derive(Master, Id17), DeviceKeys.Derive(Master, "  " + Id17.ToUpperInvariant() + " "));   // the id is read in any common writing
    }

    [Fact]
    public void Two_computers_get_different_keys_and_so_do_two_masters_and_two_roles()
    {
        var key17 = DeviceKeys.Derive(Master, Id17);

        Assert.NotEqual(key17, DeviceKeys.Derive(Master, Id15));
        Assert.NotEqual(key17, DeviceKeys.Derive("another-master-key-0002", Id17));
        Assert.NotEqual(key17, DeviceKeys.Derive(Master, Id17, role: "teacher"));
    }

    [Fact]
    public void A_derived_key_is_a_valid_key_that_looks_derived_and_holds_neither_the_master_nor_the_id()
    {
        var key = DeviceKeys.Derive(Master, Id17);

        Assert.True(SharedKey.IsAcceptable(key, out var reason), reason);
        Assert.True(DeviceKeys.IsDerived(key));
        Assert.False(DeviceKeys.IsDerived(SharedKey.Generate()));
        Assert.Equal(DeviceKeys.Prefix.Length + 64, key.Length);
        Assert.DoesNotContain(Master, key);
        Assert.DoesNotContain(Id17, key);
    }

    [Fact]
    public void A_device_key_is_not_a_function_of_another_device_key_so_one_theft_gives_nothing_for_the_rest()
    {
        // The only input an attacker holding key17 has is key17 itself. Deriving "from" it, as if it were the master, must not give
        // the key of another computer.
        var key17 = DeviceKeys.Derive(Master, Id17);
        var key15 = DeviceKeys.Derive(Master, Id15);

        Assert.NotEqual(key15, DeviceKeys.Derive(key17, Id15));
        Assert.NotEqual(key15, key17);
        Assert.NotEqual(Master, DeviceKeys.Derive(key17, Id17));
    }

    [Fact]
    public void Many_computers_never_collide()
    {
        var keys = Enumerable.Range(0, 2000).Select(i => DeviceKeys.Derive(Master, Guid.NewGuid().ToString("N") + i)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Theory]
    [InlineData("", Id17)]
    [InlineData("  ", Id17)]
    [InlineData(Master, "")]
    [InlineData(Master, "   ")]
    public void A_missing_master_or_client_id_is_refused(string master, string clientId) =>
        Assert.Throws<ArgumentException>(() => DeviceKeys.Derive(master, clientId));

    [Fact]
    public void The_refusals_say_which_part_is_missing()
    {
        Assert.Contains("master key is empty", Assert.Throws<ArgumentException>(() => DeviceKeys.Derive(" ", "abc")).Message);
        Assert.Contains("needs the client id", Assert.Throws<ArgumentException>(() => DeviceKeys.Derive(Master, null!)).Message);
    }

    [Fact]
    public void Role_and_client_id_cannot_run_into_each_other()
    {
        // Found by mutation testing: without the separator, "adminx" + "abc" and "admin" + "xabc" were the same text and the same key.
        Assert.NotEqual(DeviceKeys.Derive(Master, "abc", role: "adminx"), DeviceKeys.Derive(Master, "xabc", role: "admin"));
    }
}
