// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

// T-218: a release is told apart, ordered and bound by its identity, not by a file name, a file version or a loose manifest.
public sealed class ReleaseIdentityTests
{
    static readonly string Sha1 = new('a', 64), Sha2 = new('b', 64);
    static readonly string Signer = new('5', 64), OtherSigner = new('6', 64);

    static ReleaseIdentity Release(string version = "1.2.0", long revision = 1, string audience = "school-a", string variant = "win-x64/shared-runtime",
        string? sha = null, string? id = null, string? signer = null, ReleaseChannel channel = ReleaseChannel.Customer) =>
        new(version, channel, audience, variant, revision, id ?? $"rel-{version}-{revision}", sha ?? new string((char)('a' + (int)(revision % 6)), 64), signer ?? Signer);

    static ReleaseDecision Decide(ReleaseIdentity? installed, ReleaseIdentity candidate, Version? legacy = null, bool recovery = false) =>
        ReleaseOrdering.Decide(installed, legacy, candidate, candidate.ExecutableSha256, candidate.SignerSha256, recovery);

    [Fact]
    public void A_customer_release_shows_three_numbers_and_a_windows_file_version_with_a_zero_fourth()
    {
        var release = Release("1.2.0");

        Assert.Equal("1.2.0.0", release.FileVersion);
        Assert.Empty(release.Problems());
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.0.435")]
    [InlineData("1.2.0-beta")]
    [InlineData("1.2.x")]
    [InlineData("1..0")]
    [InlineData("1.2.65536")]
    [InlineData("-1.2.0")]
    [InlineData("")]
    public void Only_three_whole_numbers_that_fit_a_windows_version_are_a_public_version(string version)
    {
        Assert.False(ReleaseIdentity.TryParsePublicVersion(version, out _));
        Assert.Contains("publicVersion", Release(version).Problems());
    }

    [Fact]
    public void A_newer_revision_of_the_same_public_version_replaces_an_older_one_in_the_same_file_version()
    {
        Assert.Equal(ReleaseDecision.Newer, Decide(Release(revision: 1), Release(revision: 2)));
        Assert.Equal(ReleaseDecision.NotNewer, Decide(Release(revision: 2), Release(revision: 1)));
    }

    [Fact]
    public void A_later_public_version_wins_whatever_the_revision_and_an_earlier_one_never_does()
    {
        Assert.Equal(ReleaseDecision.Newer, Decide(Release("1.2.0", 9), Release("1.2.1", 10)));
        Assert.Equal(ReleaseDecision.NotNewer, Decide(Release("1.3.0", 3), Release("1.2.9", 4)));
        Assert.Equal(ReleaseDecision.Newer, Decide(Release("1.9.0", 3), Release("1.10.0", 4)));   // numbers, not text
    }

    [Fact]
    public void The_same_release_again_is_already_installed_and_the_same_revision_with_other_bytes_is_a_conflict()
    {
        var installed = Release(revision: 2, sha: Sha1, id: "r2");

        Assert.Equal(ReleaseDecision.AlreadyInstalled, Decide(installed, Release(revision: 2, sha: Sha1, id: "r2")));
        Assert.Equal(ReleaseDecision.ConflictingBytes, Decide(installed, Release(revision: 2, sha: Sha2, id: "r2")));
        Assert.Equal(ReleaseDecision.ConflictingBytes, Decide(installed, Release(revision: 2, sha: Sha1, id: "other")));
        Assert.Equal(ReleaseDecision.ConflictingBytes, Decide(installed, Release(revision: 3, sha: Sha2, id: "r2")));   // an issued id is never reused
    }

    [Fact]
    public void A_package_made_for_another_school_is_not_an_upgrade_but_one_for_any_school_is()
    {
        Assert.Equal(ReleaseDecision.WrongAudience, Decide(Release(audience: "school-a"), Release(revision: 2, audience: "school-b")));
        Assert.Equal(ReleaseDecision.Newer, Decide(Release(audience: "school-a"), Release(revision: 2, audience: ReleaseIdentity.AnyAudience)));
        Assert.Equal(ReleaseDecision.Newer, Decide(Release(audience: "school-a"), Release(revision: 2, audience: "school-a")));
    }

    [Fact]
    public void Another_architecture_runtime_or_signer_is_never_taken_as_an_update()
    {
        var installed = Release(variant: "win-x64/shared-runtime");

        Assert.Equal(ReleaseDecision.WrongVariant, Decide(installed, Release(revision: 2, variant: "win-x86/shared-runtime")));
        Assert.Equal(ReleaseDecision.WrongVariant, Decide(installed, Release(revision: 2, variant: "win-x64/self-contained")));
        Assert.Equal(ReleaseDecision.WrongSigner, Decide(installed, Release(revision: 2, signer: OtherSigner)));
    }

    [Fact]
    public void Metadata_that_does_not_name_this_exact_file_and_signer_promotes_nothing()
    {
        var installed = Release(revision: 1);
        var claim = Release(revision: 5, sha: Sha1);

        Assert.Equal(ReleaseDecision.NotBoundToThisFile, ReleaseOrdering.Decide(installed, null, claim, Sha2, claim.SignerSha256));
        Assert.Equal(ReleaseDecision.NotBoundToThisFile, ReleaseOrdering.Decide(installed, null, claim, claim.ExecutableSha256, OtherSigner));
        Assert.Equal(ReleaseDecision.Newer, ReleaseOrdering.Decide(installed, null, claim, claim.ExecutableSha256.ToUpperInvariant(), claim.SignerSha256));
    }

    [Fact]
    public void An_internal_build_is_not_a_customer_release_and_a_malformed_identity_is_refused()
    {
        Assert.Equal(ReleaseDecision.NotACustomerRelease, Decide(Release(), Release(revision: 2, channel: ReleaseChannel.Internal)));
        Assert.Equal(ReleaseDecision.InvalidIdentity, Decide(Release(), Release(revision: 0)));
        Assert.Equal(ReleaseDecision.InvalidIdentity, Decide(Release(), Release(revision: 2, sha: "short")));
    }

    [Fact]
    public void An_ordinary_downgrade_is_refused_and_an_explicit_recovery_allows_only_the_order()
    {
        var installed = Release(revision: 5, audience: "school-a");

        Assert.Equal(ReleaseDecision.NotNewer, Decide(installed, Release(revision: 3, audience: "school-a")));
        Assert.Equal(ReleaseDecision.Newer, Decide(installed, Release(revision: 3, audience: "school-a"), recovery: true));
        // The other checks still hold in recovery.
        Assert.Equal(ReleaseDecision.WrongAudience, Decide(installed, Release(revision: 3, audience: "school-b"), recovery: true));
        Assert.Equal(ReleaseDecision.WrongSigner, Decide(installed, Release(revision: 3, audience: "school-a", signer: OtherSigner), recovery: true));
        Assert.Equal(ReleaseDecision.WrongVariant, Decide(installed, Release(revision: 3, audience: "school-a", variant: "x"), recovery: true));
    }

    [Theory]
    [InlineData("1.2.0.435", "1.2.0", ReleaseDecision.Newer)]       // the customer release of the version the internal build was heading for
    [InlineData("1.2.0.435", "1.2.1", ReleaseDecision.Newer)]
    [InlineData("1.2.0.435", "1.3.0", ReleaseDecision.Newer)]
    [InlineData("1.3.0.12", "1.2.9", ReleaseDecision.OlderThanInstalledVersion)]
    [InlineData("1.2.1.7", "1.2.0", ReleaseDecision.OlderThanInstalledVersion)]
    public void An_older_four_part_installation_migrates_to_a_customer_release_by_its_public_version(string installedFileVersion, string customer, ReleaseDecision expected)
    {
        Assert.Equal(expected, Decide(null, Release(customer, 1), Version.Parse(installedFileVersion)));
    }

    [Fact]
    public void A_legacy_installation_with_no_readable_version_cannot_be_ordered()
    {
        Assert.Equal(ReleaseDecision.InvalidIdentity, Decide(null, Release()));
    }

    [Fact]
    public void An_identity_survives_a_round_trip_and_nothing_malformed_is_read()
    {
        var release = Release("1.2.0", 7);

        Assert.Equal(release, ReleaseIdentity.TryParse(release.ToJson()));
        Assert.Null(ReleaseIdentity.TryParse(null));
        Assert.Null(ReleaseIdentity.TryParse(""));
        Assert.Null(ReleaseIdentity.TryParse("not json"));
        Assert.Null(ReleaseIdentity.TryParse("{}"));
        Assert.Null(ReleaseIdentity.TryParse(release.ToJson().Replace("\"schema\":1", "\"schema\":2")));
        Assert.Null(ReleaseIdentity.TryParse(release.ToJson().Replace("\"customer\"", "\"demo\"")));
        Assert.Null(ReleaseIdentity.TryParse(release.ToJson().Replace("1.2.0", "1.2")));
        Assert.Null(ReleaseIdentity.TryParse(new string('x', 5000)));
    }

    [Fact]
    public void The_line_a_program_carries_leaves_out_the_digest_and_signer_and_is_bound_by_the_reader()
    {
        var release = Release("1.2.0", 7, sha: Sha1);

        var line = release.ToEmbedded();

        Assert.Equal("cm-release/1|1.2.0|customer|school-a|win-x64/shared-runtime|7|rel-1.2.0-7", line);
        Assert.DoesNotContain(Sha1, line);
        var read = ReleaseIdentity.TryParseEmbedded(line, Sha1.ToUpperInvariant(), Signer.ToUpperInvariant());
        Assert.Equal(release, read);
        Assert.True(read!.IsBoundTo(Sha1, Signer));
        Assert.NotEqual(Sha1, ReleaseIdentity.TryParseEmbedded(line, Sha2, Signer)!.ExecutableSha256);   // the digest is whatever the reader computed
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Dausuva MB trademark text")]
    [InlineData("cm-release/2|1.2.0|customer|a|v|1|id")]
    [InlineData("cm-release/1|1.2.0|internal|a|v|1|id")]
    [InlineData("cm-release/1|1.2|customer|a|v|1|id")]
    [InlineData("cm-release/1|1.2.0|customer|a|v|0|id")]
    [InlineData("cm-release/1|1.2.0|customer|a|v|-1|id")]
    [InlineData("cm-release/1|1.2.0|customer|a|v|x|id")]
    [InlineData("cm-release/1|1.2.0|customer|a|v|1")]
    [InlineData("cm-release/1|1.2.0|customer|a|v|1|id|extra")]
    [InlineData("cm-release/1|1.2.0|customer||v|1|id")]
    public void Anything_that_is_not_exactly_the_embedded_format_is_not_an_identity(string? line)
    {
        Assert.Null(ReleaseIdentity.TryParseEmbedded(line, Sha1, Signer));
    }

    [Fact]
    public void A_line_cannot_be_made_from_fields_that_would_break_the_format_or_from_an_internal_build()
    {
        Assert.Throws<ArgumentException>(() => ReleaseIdentity.Embedded("1.2.0", "a|b", "v", 1, "id"));
        Assert.Throws<ArgumentException>(() => ReleaseIdentity.Embedded("1.2.0", "a", "v\n", 1, "id"));
        Assert.Throws<InvalidOperationException>(() => Release(channel: ReleaseChannel.Internal).ToEmbedded());
    }
}
