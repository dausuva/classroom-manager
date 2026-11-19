// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ClientUpdateRuntimeTests : IDisposable
{
    readonly string _directory = Directory.CreateTempSubdirectory("cm-update-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    string Exe(long size)
    {
        var path = Path.Combine(_directory, "ClassroomClient.exe");
        using var stream = File.Create(path);
        stream.SetLength(size);
        return path;
    }

    void Marker(string json) => File.WriteAllText(Path.Combine(_directory, "RUNTIME.json"), json);

    [Fact]
    public void Shared_runtime_build_is_recognised_from_its_description()
    {
        Marker("""{ "mode": "shared-runtime", "majorVersion": 10, "displayName": ".NET 10 Desktop Runtime (x64)" }""");

        var build = ClientBuildRuntime.Describe(Exe(3_000_000));

        Assert.True(build.UsesSharedRuntime);
        Assert.Equal(10, build.MajorVersion);
        Assert.Equal(".NET 10 Desktop Runtime (x64)", build.DisplayName);
        Assert.False(build.Guessed);
    }

    [Fact]
    public void Build_that_includes_dotnet_needs_nothing_whatever_its_size()
    {
        Marker("""{ "mode": "self-contained", "majorVersion": 10 }""");

        Assert.False(ClientBuildRuntime.Describe(Exe(3_000_000)).UsesSharedRuntime);
    }

    [Theory]
    [InlineData(3_000_000, true)]
    [InlineData(48_000_000, false)]
    public void Without_a_description_a_small_file_is_treated_as_needing_the_runtime(long size, bool shared)
    {
        var build = ClientBuildRuntime.Describe(Exe(size));

        Assert.Equal(shared, build.UsesSharedRuntime);
        Assert.True(build.Guessed);
    }

    [Fact]
    public void An_unreadable_description_falls_back_to_the_size()
    {
        Marker("not json");

        Assert.True(ClientBuildRuntime.Describe(Exe(3_000_000)).UsesSharedRuntime);
    }

    [Theory]
    [InlineData("10.0.12", 10, RuntimeReadiness.Ready)]
    [InlineData("11.0.1", 10, RuntimeReadiness.Ready)]
    [InlineData("8.0.31", 10, RuntimeReadiness.Missing)]
    [InlineData("", 10, RuntimeReadiness.Missing)]
    [InlineData("garbage", 10, RuntimeReadiness.Missing)]
    [InlineData(null, 10, RuntimeReadiness.Unknown)]
    public void Computers_are_ready_only_with_a_reported_runtime_at_or_above_the_needed_one(string? reported, int major, RuntimeReadiness expected)
    {
        Assert.Equal(expected, ClientUpdateRuntime.Classify(reported, major));
    }

    [Fact]
    public void Explanation_separates_missing_runtime_from_clients_too_old_to_say()
    {
        var build = new ClientBuildRuntime(true, 10, ".NET 10 Desktop Runtime (x64)", Guessed: false);

        var text = ClientUpdateRuntime.Explain(build, ["PC-01", "PC-02"], ["PC-09"], ready: 5);

        Assert.Contains("No suitable runtime on 2: PC-01, PC-02", text);
        Assert.Contains("Install-DotNetRuntime-Winget.ps1", text);
        Assert.Contains("Client too old to report its runtime on 1: PC-09", text);
        Assert.Contains("become unreachable", text);
        Assert.EndsWith("Update only the 5 computer(s) that are ready?", text);
    }

    [Fact]
    public void Explanation_says_when_nothing_can_be_updated_and_shortens_long_lists()
    {
        var build = new ClientBuildRuntime(true, 10, ".NET 10 Desktop Runtime", Guessed: true);
        var many = Enumerable.Range(1, 11).Select(index => $"PC-{index:00}").ToList();

        var text = ClientUpdateRuntime.Explain(build, many, [], ready: 0);

        Assert.Contains("judging by its size", text);
        Assert.Contains("PC-08 + 3 more", text);
        Assert.DoesNotContain("too old", text);
        Assert.EndsWith("nothing will be updated.", text);
    }
}
