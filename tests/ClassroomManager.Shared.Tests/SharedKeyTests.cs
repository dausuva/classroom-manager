// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public class SharedKeyTests
{
    [Fact]
    public void Generate_makes_five_dash_separated_groups_of_five_from_the_unambiguous_alphabet()
    {
        var key = SharedKey.Generate();

        var groups = key.Split('-');
        Assert.Equal(5, groups.Length);
        Assert.All(groups, g => Assert.Equal(5, g.Length));
        Assert.All(key.Replace("-", ""), ch => Assert.Contains(ch, SharedKey.Alphabet));
        Assert.True(SharedKey.IsAcceptable(key, out _));
    }

    [Fact]
    public void Generate_returns_a_different_key_each_time()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => SharedKey.Generate()).ToHashSet();
        Assert.Equal(50, keys.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    [InlineData(" leading-space-key")]
    [InlineData("trailing-space-key ")]
    [InlineData("tab\tinside-key")]
    public void IsAcceptable_rejects_bad_keys_with_a_reason(string key)
    {
        Assert.False(SharedKey.IsAcceptable(key, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void IsAcceptable_enforces_the_length_limits()
    {
        Assert.True(SharedKey.IsAcceptable(new string('a', SharedKey.MinLength), out _));
        Assert.True(SharedKey.IsAcceptable(new string('a', SharedKey.MaxLength), out _));
        Assert.False(SharedKey.IsAcceptable(new string('a', SharedKey.MaxLength + 1), out _));
    }

    [Fact]
    public void Fingerprint_is_stable_short_and_distinguishes_keys()
    {
        var a = SharedKey.Fingerprint("classroom-key-one");
        Assert.Equal(a, SharedKey.Fingerprint("classroom-key-one"));
        Assert.Equal(8, a.Length);
        Assert.NotEqual(a, SharedKey.Fingerprint("classroom-key-two"));
        Assert.DoesNotContain("classroom", a);
    }

    [Fact]
    public async Task A_client_that_switched_keys_accepts_only_the_new_key_while_an_open_channel_keeps_working()
    {
        // Mirrors key rotation: the host's session opened with the old key survives the switch,
        // a fresh connection with the new key succeeds, and one with the old key is refused.
        var oldKey = Encoding.UTF8.GetBytes("old-classroom-key");
        var newKey = Encoding.UTF8.GetBytes("new-classroom-key");

        // Session opened before the switch, with the old key.
        using var first = await LoopbackStreamPair.CreateAsync();
        var accept = SecureChannel.AcceptAsync(first.Server, oldKey, CancellationToken.None);
        using var hostChannel = await SecureChannel.ConnectAsync(first.Client, oldKey, CancellationToken.None);
        using var clientChannel = await accept;

        // The client now accepts newKey for new connections: the host's verification connection works.
        using var second = await LoopbackStreamPair.CreateAsync();
        var accept2 = SecureChannel.AcceptAsync(second.Server, newKey, CancellationToken.None);
        using var verify = await SecureChannel.ConnectAsync(second.Client, newKey, CancellationToken.None);
        using var clientChannel2 = await accept2;

        // The original channel still carries messages.
        await hostChannel.SendAsync(Encoding.UTF8.GetBytes("still here"));
        Assert.Equal("still here", Encoding.UTF8.GetString(await clientChannel.ReceiveAsync()));

        // A new connection with the old key is refused on both sides.
        using var third = await LoopbackStreamPair.CreateAsync();
        var reject = SecureChannel.AcceptAsync(third.Server, newKey, CancellationToken.None);
        var connectWithOld = SecureChannel.ConnectAsync(third.Client, oldKey, CancellationToken.None);
        await Assert.ThrowsAsync<AuthException>(() => reject);
        third.CloseServerSocket();
        await Assert.ThrowsAsync<AuthException>(() => connectWithOld);
    }
}
