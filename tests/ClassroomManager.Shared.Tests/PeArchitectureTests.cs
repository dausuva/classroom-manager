// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Buffers.Binary;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class PeArchitectureTests
{
    static byte[] Pe(ushort machine, int peOffset = 0x80)
    {
        var bytes = new byte[peOffset + 64];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), peOffset);
        bytes[peOffset] = (byte)'P'; bytes[peOffset + 1] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 4), machine);
        return bytes;
    }

    [Theory]
    [InlineData((ushort)0x014C, "x86")]
    [InlineData((ushort)0x8664, "x64")]
    [InlineData((ushort)0xAA64, "arm64")]
    [InlineData((ushort)0x01C4, "unknown")]
    public void The_machine_field_of_the_header_names_the_processor(ushort machine, string expected) =>
        Assert.Equal(expected, PeArchitecture.Read(new MemoryStream(Pe(machine))));

    [Fact]
    public void Anything_that_is_not_a_windows_program_is_unknown_and_never_throws()
    {
        Assert.Equal("unknown", PeArchitecture.Read(new MemoryStream([])));
        Assert.Equal("unknown", PeArchitecture.Read(new MemoryStream(new byte[200])));
        Assert.Equal("unknown", PeArchitecture.Read(new MemoryStream(Pe(0x8664, peOffset: 100000))));
        var noPe = Pe(0x8664); noPe[0x80] = (byte)'X';
        Assert.Equal("unknown", PeArchitecture.Read(new MemoryStream(noPe)));
    }

    [Fact]
    public void The_program_that_is_running_is_read_as_the_architecture_of_the_process()
    {
        // The test host is a native program of the same architecture as the process it runs.
        Assert.Equal(PeArchitecture.Current, PeArchitecture.Read(Environment.ProcessPath!));
    }

    [Theory]
    [InlineData("x86", "x86", false)]
    [InlineData("x64", "x64", false)]
    [InlineData("x86", "x64", true)]
    [InlineData("x64", "x86", true)]
    [InlineData("x64", "arm64", true)]
    [InlineData("x64", "unknown", false)]       // cannot tell: the start-up check decides
    [InlineData("unknown", "x64", false)]
    public void An_update_for_another_processor_is_refused_with_advice(string running, string uploaded, bool refused)
    {
        var reason = PeArchitecture.RefuseUpdate(running, uploaded);

        Assert.Equal(refused, reason is not null);
        if (running == "x86" && uploaded == "x64") Assert.Contains("win-x86", reason);
        if (running == "x64" && uploaded == "x86") Assert.Contains("win-x64", reason);
    }
}
