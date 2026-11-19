// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ClientUpdateArchitectureTests
{
    [Theory]
    [InlineData("x64", "x64", false)]
    [InlineData("x86", "x86", false)]
    [InlineData("x64", "x86", true)]
    [InlineData("x86", "x64", true)]
    [InlineData("x64", "", false)]          // a Client from before the 32-bit build does not say, and is x64
    [InlineData("x86", "", true)]
    [InlineData("unknown", "x64", false)]   // a file whose processor cannot be read is left to the Client's own start-up check
    public void A_build_fits_the_client_when_both_are_for_the_same_processor(string build, string reported, bool mismatch) =>
        Assert.Equal(mismatch, ClientUpdateArchitecture.Mismatch(build, reported));

    [Fact]
    public void The_explanation_names_the_computers_and_says_what_to_do()
    {
        var text = ClientUpdateArchitecture.Explain("x64", ["410-3", "410-4"], fitting: 5);

        Assert.Contains("64-bit Windows (x64)", text);
        Assert.Contains("410-3, 410-4", text);
        Assert.Contains("Update only the 5 computer(s)", text);
        Assert.Contains("nothing will be updated", ClientUpdateArchitecture.Explain("x86", ["410-3"], fitting: 0));
        Assert.Contains("32-bit Windows (x86)", ClientUpdateArchitecture.ExplainOne("x86", "x64"));
    }
}
