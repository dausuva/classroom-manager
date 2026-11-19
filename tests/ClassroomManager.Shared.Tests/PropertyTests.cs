// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>
/// Rules that must hold for every input, checked over many random inputs from a fixed seed (property-based testing without a
/// framework). A failure names the seed and the case, so it can be replayed exactly; a longer search is a matter of raising
/// <see cref="Cases"/> or changing the seed.
/// </summary>
public sealed class PropertyTests
{
    const int Cases = 300;
    const int Seed = 20261024;

    sealed class TestClock : TimeProvider
    {
        DateTimeOffset _now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static string RandomText(Random random, int min, int max, string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789-_ ÄėŠž")
    {
        var chars = new char[random.Next(min, max + 1)];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }

    // ---- keys ---------------------------------------------------------------------------------

    [Fact]
    public void A_derived_key_is_fixed_by_master_id_and_role_acceptable_as_a_key_and_unlike_any_other()
    {
        var random = new Random(Seed);
        var seen = new HashSet<string>();
        for (var i = 0; i < Cases; i++)
        {
            var master = SharedKey.Generate();
            var id = Guid.NewGuid().ToString("N");
            var key = DeviceKeys.Derive(master, id);
            var where = $"seed {Seed}, case {i}";

            Assert.True(SharedKey.IsAcceptable(key, out var reason), $"{where}: {reason}");
            Assert.True(DeviceKeys.IsDerived(key), where);
            Assert.False(DeviceKeys.IsDerived(master), where);                                    // a typed key never looks derived
            Assert.Equal(key, DeviceKeys.Derive(master, id));                                      // the same inputs, the same key
            Assert.Equal(key, DeviceKeys.Derive(master, "  " + id.ToUpperInvariant() + " "));     // the id is read the way the Client reports it
            Assert.NotEqual(key, master);
            Assert.NotEqual(key, DeviceKeys.Derive(master, id, "teacher"));                        // a role cannot be turned into another
            Assert.NotEqual(key, DeviceKeys.Derive(master + "x", id));                             // another lab
            var other = id[..^1] + (id[^1] == '0' ? '1' : '0');
            Assert.NotEqual(key, DeviceKeys.Derive(master, other));                                // the next computer, one character apart
            Assert.True(seen.Add(key), $"{where}: a key came out twice");
        }
    }

    [Fact]
    public void A_generated_key_is_always_acceptable_readable_and_new()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < Cases * 5; i++)
        {
            var key = SharedKey.Generate();
            Assert.True(SharedKey.IsAcceptable(key, out var reason), reason);
            Assert.Equal(29, key.Length);
            Assert.All(key.Split('-'), group => Assert.Equal(5, group.Length));
            Assert.All(key.Replace("-", ""), c => Assert.Contains(c, SharedKey.Alphabet));
            Assert.True(seen.Add(key), "a generated key came out twice");
            Assert.Matches("^[0-9a-f]{8}$", SharedKey.Fingerprint(key));
        }
    }

    [Fact]
    public void Any_key_with_spaces_at_an_end_a_control_character_or_the_wrong_length_is_refused_with_a_reason()
    {
        var random = new Random(Seed);
        for (var i = 0; i < Cases; i++)
        {
            var good = RandomText(random, SharedKey.MinLength, 40, SharedKey.Alphabet);
            var where = $"seed {Seed}, case {i}: ";
            Assert.True(SharedKey.IsAcceptable(good, out _), where + good);
            foreach (var bad in new[] { " " + good, good + " ", good.Insert(random.Next(good.Length), "\u0007"), good[..(SharedKey.MinLength - 1)],
                                        new string('a', SharedKey.MaxLength + 1), "", "   " })
            {
                Assert.False(SharedKey.IsAcceptable(bad, out var reason), where + bad);
                Assert.False(string.IsNullOrWhiteSpace(reason), where + bad);
            }
        }
    }

    // ---- roles --------------------------------------------------------------------------------

    [Fact]
    public void Every_command_a_lower_role_may_send_a_higher_role_may_send_too_and_Admin_may_send_everything()
    {
        var commands = typeof(Cmd).GetFields().Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!).Where(command => command.Contains('.') || command == Cmd.Ping).ToList();
        Assert.NotEmpty(commands);
        foreach (var command in commands)
        {
            Assert.True(CommandAccess.IsAllowed(OperatorRole.Admin, command), command);
            if (CommandAccess.IsAllowed(OperatorRole.Observer, command)) Assert.True(CommandAccess.IsAllowed(OperatorRole.Teacher, command), command);
        }
        // A role name nobody knows is the least privileged role, never more.
        var random = new Random(Seed);
        for (var i = 0; i < Cases; i++)
        {
            var name = RandomText(random, 0, 12);
            if (name.Trim().ToLowerInvariant() is "admin" or "teacher") continue;
            Assert.Equal(OperatorRole.Observer, CommandAccess.ParseRole(name));
        }
    }

    // ---- the gate before authentication ------------------------------------------------------

    [Fact]
    public void The_gate_never_lets_more_connections_into_setup_than_its_limits_and_counts_them_exactly()
    {
        // Model-based: random sequences of enter, leave, fail, succeed and time passing, checked against a plain count kept beside the gate.
        var limits = new ConnectionLimits { MaxPending = 10, MaxPendingPerSource = 3, BurstPerSource = 6, RefillPerSecond = 2, FailuresBeforeBackoff = 3, FirstBackoff = TimeSpan.FromSeconds(2), MaxBackoff = TimeSpan.FromSeconds(60) };
        var sources = new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5" };
        for (var run = 0; run < 40; run++)
        {
            var random = new Random(Seed + run);
            var clock = new TestClock();
            var gate = new PreAuthGate(limits, clock);
            var admitted = sources.ToDictionary(source => source, _ => 0);
            for (var step = 0; step < 400; step++)
            {
                var source = sources[random.Next(sources.Length)];
                var where = $"seed {Seed + run}, step {step}";
                switch (random.Next(6))
                {
                    case 0:
                    case 1:
                        var result = gate.TryEnter(source);
                        if (result == Admission.Admitted) admitted[source]++;
                        else if (result == Admission.TooManyFromSource) Assert.Equal(limits.MaxPendingPerSource, admitted[source]);
                        else if (result == Admission.TooManyPending) Assert.Equal(limits.MaxPending, admitted.Values.Sum());
                        break;
                    case 2:
                        if (admitted[source] > 0) { gate.Leave(source); admitted[source]--; }
                        break;
                    case 3:
                        gate.Failed(source);
                        break;
                    case 4:
                        gate.Succeeded(source);
                        Assert.NotEqual(Admission.BackedOff, Peek(gate, source, admitted));   // a proven key is never made to wait
                        break;
                    default:
                        clock.Advance(TimeSpan.FromMilliseconds(random.Next(0, 3000)));
                        break;
                }
                Assert.Equal(admitted.Values.Sum(), gate.Pending);
                Assert.True(gate.Pending <= limits.MaxPending, where);
                Assert.All(admitted.Values, count => Assert.InRange(count, 0, limits.MaxPendingPerSource));
            }
        }
    }

    /// <summary>Asks the gate and gives the slot straight back, so looking does not change the state.</summary>
    static Admission Peek(PreAuthGate gate, string source, Dictionary<string, int> admitted)
    {
        var result = gate.TryEnter(source);
        if (result == Admission.Admitted) gate.Leave(source);
        return result;
    }

    [Fact]
    public void A_source_is_made_to_wait_only_after_more_failures_than_allowed_and_never_longer_than_the_maximum()
    {
        var limits = new ConnectionLimits { FailuresBeforeBackoff = 4, FirstBackoff = TimeSpan.FromSeconds(2), MaxBackoff = TimeSpan.FromSeconds(30), FailureMemory = TimeSpan.FromMinutes(10), BurstPerSource = 1000, RefillPerSecond = 1000 };
        var random = new Random(Seed);
        for (var i = 0; i < Cases; i++)
        {
            var clock = new TestClock();
            var gate = new PreAuthGate(limits, clock);
            var failures = random.Next(0, 12);
            for (var f = 0; f < failures; f++) gate.Failed("10.0.0.9");
            var where = $"seed {Seed}, case {i}, {failures} failures";

            var first = Peek(gate, "10.0.0.9", []);
            if (failures <= limits.FailuresBeforeBackoff) Assert.True(first == Admission.Admitted, where);   // the first N are forgiven
            else Assert.True(first == Admission.BackedOff, where);
            Assert.Equal(Admission.Admitted, Peek(gate, "10.0.0.10", []));                                    // another address is not affected

            clock.Advance(limits.MaxBackoff + TimeSpan.FromMilliseconds(1));
            Assert.True(Peek(gate, "10.0.0.9", []) == Admission.Admitted, where + ": still waiting after the maximum");
        }
    }

    // ---- the security event log ---------------------------------------------------------------

    static List<string> Chain(Random random, int length)
    {
        var lines = new List<string>();
        var prev = SecurityEventChain.Genesis;
        var when = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
        for (var seq = 1; seq <= length; seq++)
        {
            var data = new Dictionary<string, string>();
            for (var d = random.Next(0, 4); d > 0; d--) data["k" + d] = RandomText(random, 0, 20);
            when = when.AddSeconds(random.Next(1, 600));
            var (text, hash) = SecurityEventChain.Line(seq, when, RandomText(random, 3, 16, "abcdefghijklmnopqrstuvwxyz-"), data, prev);
            lines.Add(text);
            prev = hash;
        }
        return lines;
    }

    [Fact]
    public void Any_change_to_any_field_of_any_line_in_the_event_log_is_found()
    {
        var random = new Random(Seed);
        for (var i = 0; i < Cases; i++)
        {
            var lines = Chain(random, random.Next(1, 12));
            Assert.True(SecurityEventChain.Verify(lines, SecurityEventChain.Genesis).Ok);

            var at = random.Next(lines.Count);
            var line = JsonNode.Parse(lines[at])!.AsObject();
            var field = new[] { "seq", "t", "kind", "prev", "hash", "data" }[random.Next(6)];
            switch (field)
            {
                case "seq": line["seq"] = line["seq"]!.GetValue<long>() + random.Next(1, 5); break;
                case "t": line["t"] = "2026-10-24T09:00:0" + random.Next(10) + "Z"; break;
                case "data": line["data"]!.AsObject()["added"] = RandomText(random, 1, 5); break;
                default: line[field] = line[field]!.GetValue<string>() + "0"; break;
            }
            var changed = lines.ToList();
            changed[at] = line.ToJsonString();
            var where = $"seed {Seed}, case {i}: {field} of line {at + 1} of {lines.Count}";

            var result = SecurityEventChain.Verify(changed, SecurityEventChain.Genesis);
            Assert.False(result.Ok, where);
            Assert.True(result.BadLine <= at + 1 + 1, where);   // reported at the changed line or the one after it, whose link no longer fits
        }
    }

    [Fact]
    public void Removing_or_swapping_lines_is_found_and_cutting_off_the_end_shows_in_the_last_number()
    {
        var random = new Random(Seed);
        for (var i = 0; i < Cases; i++)
        {
            var lines = Chain(random, random.Next(3, 12));
            var full = SecurityEventChain.Verify(lines, SecurityEventChain.Genesis);
            var where = $"seed {Seed}, case {i}, {lines.Count} lines";

            var removed = lines.ToList();
            removed.RemoveAt(random.Next(removed.Count - 1));   // any line but the last
            Assert.False(SecurityEventChain.Verify(removed, SecurityEventChain.Genesis).Ok, where + ": a removed line");

            var swapped = lines.ToList();
            var s = random.Next(swapped.Count - 1);
            (swapped[s], swapped[s + 1]) = (swapped[s + 1], swapped[s]);
            Assert.False(SecurityEventChain.Verify(swapped, SecurityEventChain.Genesis).Ok, where + ": two lines swapped");

            // The chain alone cannot see its own end cut off; the last number and hash (which Ping reports) do.
            var cut = SecurityEventChain.Verify(lines.Take(lines.Count - 1), SecurityEventChain.Genesis);
            Assert.True(cut.Ok, where);
            Assert.NotEqual((full.LastSeq, full.LastHash), (cut.LastSeq, cut.LastHash));
        }
    }
}
