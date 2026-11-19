// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class SecurityEventChainTests
{
    static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    static List<string> Chain(int count)
    {
        var lines = new List<string>();
        var prev = SecurityEventChain.Genesis;
        for (var i = 1; i <= count; i++)
        {
            var (text, hash) = SecurityEventChain.Line(i, Start.AddSeconds(i), i % 2 == 0 ? "handshake-failed" : "throttled",
                new Dictionary<string, string> { ["source"] = "10.42.4." + i, ["count"] = i.ToString() }, prev);
            lines.Add(text);
            prev = hash;
        }
        return lines;
    }

    [Fact]
    public void An_untouched_chain_verifies_and_reports_its_last_number_and_hash()
    {
        var result = SecurityEventChain.Verify(Chain(10));

        Assert.True(result.Ok);
        Assert.Equal((10, 10L), (result.Count, result.LastSeq));
        Assert.Equal(16, result.LastHash.Length);
        Assert.True(SecurityEventChain.Verify([]).Ok);
    }

    [Fact]
    public void A_line_that_is_changed_is_found_as_the_first_bad_line()
    {
        var lines = Chain(10);
        lines[4] = lines[4].Replace("10.42.4.5", "10.42.4.99");

        var result = SecurityEventChain.Verify(lines);

        Assert.False(result.Ok);
        Assert.Equal(5, result.BadLine);
        Assert.Contains("changed", result.Reason);
    }

    [Fact]
    public void A_line_removed_from_the_middle_breaks_the_chain_at_the_next_line()
    {
        var lines = Chain(10);
        lines.RemoveAt(4);

        var result = SecurityEventChain.Verify(lines);

        Assert.False(result.Ok);
        Assert.Equal(5, result.BadLine);
        Assert.Contains("missing", result.Reason);
    }

    [Fact]
    public void Lines_swapped_or_inserted_from_another_chain_are_found()
    {
        var lines = Chain(6);
        (lines[2], lines[3]) = (lines[3], lines[2]);
        Assert.False(SecurityEventChain.Verify(lines).Ok);

        var other = Chain(6);
        var spliced = Chain(6);
        spliced.Insert(3, other[3]);
        Assert.False(SecurityEventChain.Verify(spliced).Ok);
    }

    [Fact]
    public void A_hash_recomputed_by_someone_who_edits_one_line_still_breaks_the_next_link()
    {
        var lines = Chain(5);
        var (forged, _) = SecurityEventChain.Line(3, Start.AddSeconds(3), "throttled", new Dictionary<string, string> { ["source"] = "10.9.9.9" },
            SecurityEventChain.Verify(lines.Take(2)).LastHash);
        lines[2] = forged;                                       // line 3 is now self-consistent, but line 4 still points to the old hash

        var result = SecurityEventChain.Verify(lines);

        Assert.False(result.Ok);
        Assert.Equal(4, result.BadLine);
    }

    [Fact]
    public void A_rotated_file_that_starts_in_the_middle_of_a_chain_verifies_when_told_where_it_starts()
    {
        var all = Chain(10);
        var tail = all.Skip(5).ToList();
        var anchor = SecurityEventChain.Verify(all.Take(5)).LastHash;

        Assert.True(SecurityEventChain.Verify(tail).Ok);                         // the first line is its own starting point
        Assert.True(SecurityEventChain.Verify(tail, anchor).Ok);                 // and it can be checked against the end of the old file
        Assert.False(SecurityEventChain.Verify(tail, "ffffffffffffffff").Ok);
    }

    [Fact]
    public void Garbage_lines_are_reported_not_thrown()
    {
        Assert.False(SecurityEventChain.Verify(["not json"]).Ok);
        Assert.False(SecurityEventChain.Verify(["{}"]).Ok);
        Assert.False(SecurityEventChain.Verify(["[1,2,3]"]).Ok);
        Assert.False(SecurityEventChain.Verify(["{\"seq\":\"x\"}"]).Ok);
    }

    [Fact]
    public void A_line_holds_what_it_was_given_and_nothing_else()
    {
        var (text, _) = SecurityEventChain.Line(1, Start, "handshake-failed", new Dictionary<string, string> { ["source"] = "10.42.4.5", ["reason"] = "wrong key" }, SecurityEventChain.Genesis);

        Assert.Contains("\"source\":\"10.42.4.5\"", text);
        Assert.Contains("2026-10-23T12:00:00Z", text);
        Assert.DoesNotContain("psk", text, StringComparison.OrdinalIgnoreCase);
    }
}
