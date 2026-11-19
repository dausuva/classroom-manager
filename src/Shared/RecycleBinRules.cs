// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

/// <summary>The answers to <see cref="Cmd.RecycleBinEmpty"/> and <see cref="Cmd.RecycleBinNameReset"/>: what the signed-in user's Recycle Bin held, and what it was called, before.</summary>
public static class RecycleBinRules
{
    public const string Items = "items";
    public const string Bytes = "bytes";
    /// <summary>The name the user had given the Recycle Bin; empty when it still had the Windows name.</summary>
    public const string CustomName = "customName";

    public static string DescribeName(JsonObject data) => DescribeName(data[CustomName]?.GetValue<string>() ?? "");

    public static string DescribeName(string customName) =>
        customName.Length == 0 ? "the Recycle Bin already had its Windows name" : $"name reset to the Windows default (was \"{customName}\")";

    public static string Describe(JsonObject data) =>
        Describe(data[Items]?.GetValue<long>() ?? 0, data[Bytes]?.GetValue<long>() ?? 0);

    public static string Describe(long items, long bytes) =>
        items <= 0 ? "the Recycle Bin was already empty" : $"{items} item(s) removed, {Size(bytes)} freed";

    // Written the same way whatever the Host's regional settings, like the rest of the log.
    static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => FormattableString.Invariant($"{bytes / (double)(1L << 30):0.#} GB"),
        >= 1L << 20 => FormattableString.Invariant($"{bytes / (double)(1L << 20):0.#} MB"),
        >= 1L << 10 => FormattableString.Invariant($"{bytes / (double)(1L << 10):0.#} KB"),
        _ => $"{Math.Max(0, bytes)} bytes",
    };
}
