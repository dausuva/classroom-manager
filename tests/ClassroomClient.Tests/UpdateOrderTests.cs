// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.IO;
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

// T-218 slice B: an uploaded program replaces the installed one only in the order its own signed identity says, bound to the digest and signer
// computed here; programs with no identity keep the file-version rule.
public sealed class UpdateOrderTests : IDisposable
{
    static readonly string ShaInstalled = new('a', 64), ShaCandidate = new('b', 64), Signer = new('5', 64), OtherSigner = new('6', 64);
    const string Variant = "win-x64/shared-runtime";

    readonly string _scratch = Path.Combine(Path.GetTempPath(), "update-order-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true); }
        catch (IOException) { }
    }

    static string Line(long revision, string version = "1.2.0", string audience = "school-a", string variant = Variant, string? id = null) =>
        ReleaseIdentity.Embedded(version, audience, variant, revision, id ?? $"rel-{version}-{revision}");

    static UpdateFile Installed(string? claim, string version = "1.2.0.0", string sha = "", string signer = "") =>
        new(sha.Length > 0 ? sha : ShaInstalled, signer.Length > 0 ? signer : Signer, Version.Parse(version), claim);

    static UpdateFile Candidate(string? claim, string version = "1.2.0.0", string signer = "") =>
        new(ShaCandidate, signer.Length > 0 ? signer : Signer, Version.Parse(version), claim);

    [Fact]
    public void Two_releases_of_the_same_public_version_update_in_revision_order_without_force()
    {
        var newer = UpdateOrder.Evaluate(Installed(Line(1)), Candidate(Line(2)), force: false);
        var older = UpdateOrder.Evaluate(Installed(Line(2)), Candidate(Line(1)), force: false);

        Assert.True(newer.Allowed);
        Assert.False(newer.UseFileVersions);   // both have FileVersion 1.2.0.0: the file version could not have told them apart
        Assert.False(older.Allowed);
        Assert.Equal(ErrorCatalog.UpdateNotNewer, older.Error);
    }

    [Fact]
    public void Force_allows_an_older_revision_and_nothing_else()
    {
        var installed = Installed(Line(5));

        Assert.True(UpdateOrder.Evaluate(installed, Candidate(Line(3)), force: true).Allowed);
        Assert.False(UpdateOrder.Evaluate(installed, Candidate(Line(3, audience: "school-b")), force: true).Allowed);
        Assert.False(UpdateOrder.Evaluate(installed, Candidate(Line(3, variant: "win-x86/shared-runtime")), force: true).Allowed);
        Assert.False(UpdateOrder.Evaluate(installed, Candidate(Line(3), signer: OtherSigner), force: true).Allowed);
    }

    [Fact]
    public void A_release_for_another_school_or_architecture_or_signer_is_refused_with_a_failed_check()
    {
        var installed = Installed(Line(1));

        foreach (var candidate in new[] { Candidate(Line(2, audience: "school-b")), Candidate(Line(2, variant: "win-x86/shared-runtime")), Candidate(Line(2), signer: OtherSigner) })
        {
            var result = UpdateOrder.Evaluate(installed, candidate, force: false);
            Assert.False(result.Allowed);
            Assert.Equal(ErrorCatalog.UpdateFailedChecks, result.Error);
        }
    }

    [Fact]
    public void A_release_for_any_school_may_replace_one_made_for_a_school()
    {
        Assert.True(UpdateOrder.Evaluate(Installed(Line(1)), Candidate(Line(2, audience: ReleaseIdentity.AnyAudience)), force: false).Allowed);
    }

    [Fact]
    public void A_claim_that_is_not_a_valid_identity_is_refused_even_with_force()
    {
        foreach (var claim in new[] { "cm-release/1|1.2|customer|a|v|2|x", "cm-release/1|1.2.0|internal|a|v|2|x", "cm-release/1|1.2.0|customer|a|v|0|x", "cm-release/2|1.2.0|customer|a|v|2|x", "cm-release/1|1.2.0|customer|a|v|2" })
        {
            var result = UpdateOrder.Evaluate(Installed(Line(1)), Candidate(claim), force: true);
            Assert.False(result.Allowed);
            Assert.Equal(ErrorCatalog.UpdateFailedChecks, result.Error);
        }
    }

    [Fact]
    public void A_program_with_no_identity_cannot_replace_a_customer_release_unless_forced()
    {
        var plain = Candidate(null, "1.2.0.900");

        var refused = UpdateOrder.Evaluate(Installed(Line(4)), plain, force: false);
        var forced = UpdateOrder.Evaluate(Installed(Line(4)), plain, force: true);

        Assert.False(refused.Allowed);
        Assert.Equal(ErrorCatalog.UpdateFailedChecks, refused.Error);
        Assert.True(forced.Allowed);
        Assert.True(forced.UseFileVersions);
    }

    [Fact]
    public void Programs_without_identities_keep_the_file_version_rule()
    {
        var result = UpdateOrder.Evaluate(Installed(null, "1.2.0.440"), Candidate(null, "1.2.0.443"), force: false);

        Assert.True(result.Allowed);
        Assert.True(result.UseFileVersions);
    }

    [Theory]
    [InlineData("1.2.0.435", "1.2.0", true)]    // the customer release of the version the internal build was heading for
    [InlineData("1.2.0.435", "1.2.1", true)]
    [InlineData("1.3.0.12", "1.2.9", false)]
    [InlineData("1.2.1.7", "1.2.0", false)]
    public void An_older_four_part_installation_moves_to_a_customer_release_by_public_version(string installedFileVersion, string customer, bool allowed)
    {
        var result = UpdateOrder.Evaluate(Installed(null, installedFileVersion), Candidate(Line(1, customer), customer + ".0"), force: false);

        Assert.Equal(allowed, result.Allowed);
        Assert.False(result.UseFileVersions);
    }

    [Fact]
    public void The_same_release_pushed_again_says_it_is_already_installed()
    {
        var line = Line(3);
        var installed = new UpdateFile(ShaCandidate, Signer, Version.Parse("1.2.0.0"), line);   // the same bytes as the candidate

        var result = UpdateOrder.Evaluate(installed, Candidate(line), force: false);

        Assert.False(result.Allowed);
        Assert.Equal(ErrorCatalog.UpdateNotNewer, result.Error);
        Assert.Contains("already installed", result.Message);
    }

    [Fact]
    public void A_release_number_reused_for_other_bytes_is_a_conflict_not_an_update()
    {
        var result = UpdateOrder.Evaluate(Installed(Line(3), sha: new string('c', 64)), Candidate(Line(3)), force: true);

        Assert.False(result.Allowed);
        Assert.Contains("other bytes", result.Message);
    }

    // The claim is read from the program's own version resource, which its signature covers. A real build shows that the field survives the toolchain.
    [Fact]
    public async Task The_claim_is_read_from_a_built_programs_version_resource_and_a_plain_build_has_none()
    {
        Directory.CreateDirectory(_scratch);
        var project = Path.Combine(_scratch, "Fixture.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>Fixture</AssemblyName><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_scratch, "Program.cs"), "Console.WriteLine(1);\n");
        var claim = Line(7, "1.2.0", "school-a");

        var plain = await BuildAsync(project, Path.Combine(_scratch, "plain"), []);
        var carrying = await BuildAsync(project, Path.Combine(_scratch, "carrying"), [$"-p:Trademark={claim}"]);

        Assert.Null(UpdateOrder.ReadClaim(plain));
        Assert.Equal(claim, UpdateOrder.ReadClaim(carrying));
        var identity = ReleaseIdentity.TryParseEmbedded(UpdateOrder.ReadClaim(carrying), ShaCandidate, Signer);
        Assert.NotNull(identity);
        Assert.Equal(7, identity!.Revision);
    }

    static async Task<string> BuildAsync(string project, string output, string[] extra)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "build", project, "-c", "Release", "-o", output, "--nologo", "-v:q" }.Concat(extra)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await text + await errors);
        return Path.Combine(output, "Fixture.dll");
    }
}
