// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>MW-G: Exam mode and key rotation as trusted services. The scope lists and the keys are the only state, saved before anything is sent.</summary>
[Collection(nameof(HostBackupTests))]
public sealed class TrustedWorkflowTests
{
    static ClientInfo Client(string name, string id = "", string address = "", string version = Protocol.Version) =>
        new() { Name = name, ClientId = id.Length > 0 ? id : "id-" + name, Address = address.Length > 0 ? address : "10.0.0." + (100 + name[^1]), Version = version };

    sealed class Peer : IExamPeer
    {
        public readonly List<(string Cmd, JsonObject? Args)> Calls = [];
        public Func<string, Exception?> Fails = _ => null;

        public Task<JsonObject> CallAsync(string cmd, JsonObject? args = null)
        {
            Calls.Add((cmd, args));
            return Fails(cmd) is { } error ? Task.FromException<JsonObject>(error) : Task.FromResult(new JsonObject { ["effectiveWebsiteCount"] = 2 });
        }
    }

    static HostSettings Settings()
    {
        var settings = new HostSettings { ExamModeDomains = ["a.example"], ExamModeApps = ["game.exe"] };
        return settings;
    }

    [Fact]
    public void Turning_on_starts_the_list_afresh_with_the_members_and_clears_their_pending_off()
    {
        var settings = Settings();
        var one = Client("PC-1");
        var two = Client("PC-2");
        var stale = Client("PC-9");
        ExamModeScope.Add(settings.ExamModeComputers, stale.ClientId, stale.Address);
        ExamModeScope.Add(settings.ExamModePendingClear, one.ClientId, one.Address);
        var exam = new ExamModeWorkflow(settings);

        exam.Begin(true, false, [one, two]);

        Assert.True(settings.ExamModeEnabled);
        Assert.True(exam.InExamMode(one));
        Assert.True(exam.InExamMode(two));
        Assert.False(exam.InExamMode(stale));
        Assert.False(ExamModeScope.Contains(settings.ExamModePendingClear, one.ClientId, one.Address));
    }

    [Fact]
    public void A_reapply_keeps_the_computers_it_was_already_on_for()
    {
        var settings = Settings();
        var one = Client("PC-1");
        var two = Client("PC-2");
        var exam = new ExamModeWorkflow(settings);
        exam.Begin(true, false, [one]);

        exam.Begin(true, true, [two]);

        Assert.True(exam.InExamMode(one));
        Assert.True(exam.InExamMode(two));
    }

    [Fact]
    public void Turning_off_lists_every_member_to_be_told_and_empties_the_list_it_is_on_for()
    {
        var settings = Settings();
        var one = Client("PC-1");
        one.ExamModeApplied = true;
        var exam = new ExamModeWorkflow(settings);
        exam.Begin(true, false, [one]);

        exam.Begin(false, false, [one]);

        Assert.False(settings.ExamModeEnabled);
        Assert.Empty(settings.ExamModeComputers);
        Assert.True(ExamModeScope.Contains(settings.ExamModePendingClear, one.ClientId, one.Address));
        Assert.False(one.ExamModeApplied);
        Assert.Equal(1, exam.Waiting(false, [one]));
    }

    [Fact]
    public async Task Applying_sends_the_overlay_then_the_device_controls_and_marks_the_computer_done()
    {
        var settings = Settings();
        settings.ExamModeMuteAudio = true;
        var client = Client("PC-1");
        var exam = new ExamModeWorkflow(settings);
        exam.Begin(true, false, [client]);
        var peer = new Peer();
        JsonObject? seen = null;

        await exam.ApplyAsync(client, peer, true, exam.OverlayArguments(), (_, answer) => seen = answer);

        Assert.Equal([Cmd.ExamModeSet, Cmd.ExamDevicesSet], peer.Calls.Select(call => call.Cmd));
        Assert.Equal("a.example", peer.Calls[0].Args!["domains"]![0]!.GetValue<string>());
        Assert.True(peer.Calls[1].Args![ExamDeviceControls.MuteAudio]!.GetValue<bool>());
        Assert.Equal(2, seen!["effectiveWebsiteCount"]!.GetValue<int>());
        Assert.True(client.ExamModeApplied);
        Assert.Equal(0, exam.Waiting(true, [client]));
    }

    [Fact]
    public async Task Turning_a_computer_off_clears_its_pending_entry_only_after_the_call_worked()
    {
        var settings = Settings();
        var client = Client("PC-1");
        var exam = new ExamModeWorkflow(settings);
        exam.Begin(true, false, [client]);
        exam.Begin(false, false, [client]);
        var peer = new Peer { Fails = cmd => cmd == Cmd.ExamModeClear ? new InvalidOperationException("offline") : null };

        await Assert.ThrowsAsync<InvalidOperationException>(() => exam.ApplyAsync(client, peer, false, new JsonObject(), (_, _) => { }));
        Assert.True(ExamModeScope.Contains(settings.ExamModePendingClear, client.ClientId, client.Address));

        peer.Fails = _ => null;
        await exam.ApplyAsync(client, peer, false, new JsonObject(), (_, _) => { });
        Assert.False(ExamModeScope.Contains(settings.ExamModePendingClear, client.ClientId, client.Address));
        Assert.DoesNotContain(peer.Calls, call => call.Cmd == Cmd.ExamDevicesSet);
    }

    [Fact]
    public async Task A_computer_that_answers_late_is_turned_off_or_on_as_the_lists_say_and_a_device_failure_is_reported_not_thrown()
    {
        var settings = Settings();
        settings.ExamModeBlockUsbStorage = true;
        var away = Client("PC-1");
        var late = Client("PC-2");
        var exam = new ExamModeWorkflow(settings);
        exam.Begin(true, false, [away, late]);
        exam.Begin(false, false, [away]);
        exam.Begin(true, true, [late]);
        // PC-1 is only pending-clear now; PC-2 is on and was never reached.
        var peer = new Peer();

        var first = await exam.ReconcileAsync(away, peer);
        Assert.True(first.ClearedPending);
        Assert.Equal([Cmd.ExamModeClear], peer.Calls.Select(call => call.Cmd));
        Assert.False(ExamModeScope.Contains(settings.ExamModePendingClear, away.ClientId, away.Address));

        peer.Calls.Clear();
        peer.Fails = cmd => cmd == Cmd.ExamDevicesSet ? new InvalidOperationException("device") : null;
        var second = await exam.ReconcileAsync(late, peer);
        Assert.Contains(Cmd.ExamModeSet, peer.Calls.Select(call => call.Cmd));
        Assert.True(late.ExamModeApplied);
        Assert.False(second.ClearedPending);
        Assert.True(second.DevicesNotApplied);

        peer.Calls.Clear();
        var third = await exam.ReconcileAsync(late, peer);
        Assert.Empty(peer.Calls);
        Assert.False(third.ClearedPending);
    }

    [Fact]
    public void A_computer_found_at_a_new_address_is_followed_in_both_lists()
    {
        var settings = Settings();
        var client = Client("PC-1", "id-1", "10.0.0.5");
        ExamModeScope.Add(settings.ExamModeComputers, "id-1", "10.0.0.5");
        ExamModeScope.Add(settings.ExamModePendingClear, "id-1", "10.0.0.5");
        var exam = new ExamModeWorkflow(settings);

        exam.FollowAddress("id-1", "10.0.0.5", "10.0.0.77");

        Assert.True(ExamModeScope.Contains(settings.ExamModeComputers, "id-1", "10.0.0.77"));
        Assert.False(ExamModeScope.Contains(settings.ExamModeComputers, "", "10.0.0.5"));
        Assert.True(ExamModeScope.Contains(settings.ExamModePendingClear, "id-1", "10.0.0.77"));
        Assert.NotNull(client);
    }

    // ---- key rotation ----

    const string OldKey = "old-key-for-the-lab-1234";
    const string NewKey = "new-key-for-the-lab-5678";

    static HostSettings KeySettings() => new() { Psk = OldKey };

    static ClientInfo OnKey(string name, string key) => new() { Name = name, KeyId = SharedKey.Fingerprint(key) };

    [Fact]
    public void Only_computers_on_the_default_lab_key_or_an_earlier_one_of_it_can_be_rotated()
    {
        var settings = KeySettings();
        settings.PreviousPsks.Add("earlier-key-of-the-lab-9999");
        var rotation = new KeyRotationWorkflow(settings);
        var onCurrent = OnKey("current", OldKey);
        var onEarlier = OnKey("earlier", "earlier-key-of-the-lab-9999");
        var other = OnKey("other", "a-different-lab-key-0000");
        var unknown = new ClientInfo { Name = "unknown" };

        var refused = rotation.IncompatibleTargets([onCurrent, onEarlier, other, unknown]);

        Assert.Equal(["other", "unknown"], refused.Select(client => client.Name));
    }

    [Fact]
    public void Beginning_keeps_the_old_key_as_accepted_and_starts_a_new_epoch_before_anything_is_sent()
    {
        var settings = KeySettings();
        var rotation = new KeyRotationWorkflow(settings);
        var epoch = settings.KeyEpoch;

        rotation.Begin(NewKey, true, _ => { });

        Assert.Equal(NewKey, settings.Psk);
        Assert.Equal([OldKey], settings.PreviousPsks);
        Assert.True(settings.AutoFinishKeyRotation);
        Assert.True(settings.KeyEpoch >= epoch);
    }

    [Fact]
    public void Beginning_again_with_a_key_that_is_already_an_older_one_does_not_list_it_twice()
    {
        var settings = KeySettings();
        settings.PreviousPsks.Add(NewKey);
        var rotation = new KeyRotationWorkflow(settings);

        rotation.Begin(NewKey, false, _ => { });

        Assert.Equal([OldKey], settings.PreviousPsks);
    }

    [Fact]
    public void Computers_not_yet_on_the_new_key_are_counted_and_the_old_keys_go_only_when_every_one_has_it()
    {
        var settings = KeySettings();
        var rotation = new KeyRotationWorkflow(settings);
        rotation.Begin(NewKey, false, _ => { });
        var done = OnKey("done", NewKey);
        var waiting = OnKey("waiting", OldKey);
        var elsewhere = OnKey("elsewhere", "another-lab-key-4444");

        Assert.Equal(1, rotation.NotOnNewKey([done, waiting, elsewhere], NewKey));
        Assert.False(rotation.ForgetOldKeysIfDone([done, waiting, elsewhere]));
        Assert.Equal([OldKey], settings.PreviousPsks);

        waiting.KeyId = SharedKey.Fingerprint(NewKey);
        Assert.True(rotation.ForgetOldKeysIfDone([done, waiting, elsewhere]));
        Assert.Empty(settings.PreviousPsks);
        Assert.Equal(0, rotation.NotOnNewKey([done, waiting], NewKey));
    }

    [Fact]
    public void Nothing_is_forgotten_with_no_old_keys_no_computers_or_no_computer_on_these_keys()
    {
        var settings = KeySettings();
        var rotation = new KeyRotationWorkflow(settings);
        Assert.False(rotation.ForgetOldKeysIfDone([OnKey("a", OldKey)]));
        rotation.Begin(NewKey, false, _ => { });
        Assert.False(rotation.ForgetOldKeysIfDone([]));
        Assert.False(rotation.ForgetOldKeysIfDone([OnKey("elsewhere", "another-lab-key-4444")]));
        Assert.Equal([OldKey], settings.PreviousPsks);
    }
}
