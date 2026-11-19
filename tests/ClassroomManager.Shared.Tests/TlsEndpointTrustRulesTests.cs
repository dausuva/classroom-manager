// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>
/// The rules of a certificate pin, at their edges. Found by mutation testing (Stryker, 2026-10-24): a broken character range or a pin
/// counted twice changed no test's result.
/// </summary>
public sealed class TlsEndpointTrustRulesTests
{
    static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=synthetic-pin-test", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    static string Pin(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    [Theory]
    [InlineData('0')] [InlineData('9')] [InlineData('a')] [InlineData('f')] [InlineData('A')] [InlineData('F')] [InlineData('5')] [InlineData('c')]
    public void Every_hex_digit_at_the_edges_of_its_range_is_part_of_a_fingerprint(char c)
    {
        Assert.True(TlsEndpointTrust.IsFingerprint(new string(c, 64)));
    }

    [Theory]
    [InlineData('/')] [InlineData(':')]   // just outside 0-9
    [InlineData('`')] [InlineData('g')]   // just outside a-f
    [InlineData('@')] [InlineData('G')]   // just outside A-F
    [InlineData(' ')] [InlineData('z')] [InlineData('-')]
    public void A_character_just_outside_a_hex_range_makes_it_no_fingerprint_even_once(char c)
    {
        Assert.False(TlsEndpointTrust.IsFingerprint(new string(c, 64)));
        Assert.False(TlsEndpointTrust.IsFingerprint(new string('a', 63) + c));   // one bad character among good ones is enough
        Assert.False(TlsEndpointTrust.IsFingerprint(c + new string('a', 63)));
    }

    [Theory]
    [InlineData(0)] [InlineData(63)] [InlineData(65)] [InlineData(128)]
    public void Only_exactly_64_characters_are_a_fingerprint(int length)
    {
        Assert.False(TlsEndpointTrust.IsFingerprint(new string('a', length)));
        Assert.False(TlsEndpointTrust.IsFingerprint(null));
    }

    [Fact]
    public void Trust_needs_one_or_two_pins_and_every_one_must_be_a_fingerprint()
    {
        var good = new string('a', 64);
        Assert.Throws<AuthenticationException>(() => TlsEndpointTrust.Validator([]));
        Assert.Throws<AuthenticationException>(() => TlsEndpointTrust.Validator([good, good, good]));
        Assert.Throws<AuthenticationException>(() => TlsEndpointTrust.Validator([good, "not-a-pin"]));   // a bad second pin is refused too
        Assert.Throws<AuthenticationException>(() => TlsEndpointTrust.Validator(["not-a-pin", good]));
        Assert.NotNull(TlsEndpointTrust.Validator([good]));
        Assert.NotNull(TlsEndpointTrust.Validator([good, new string('B', 64)]));
    }

    [Fact]
    public void The_approved_certificate_is_accepted_in_either_place_and_also_when_it_is_listed_twice()
    {
        using var approved = Certificate();
        using var other = Certificate();
        var pin = Pin(approved);

        Assert.True(TlsEndpointTrust.Validator([pin])(this, approved, null, SslPolicyErrors.None));
        Assert.True(TlsEndpointTrust.Validator([Pin(other), pin])(this, approved, null, SslPolicyErrors.None));
        Assert.True(TlsEndpointTrust.Validator([pin, Pin(other)])(this, approved, null, SslPolicyErrors.None));
        Assert.True(TlsEndpointTrust.Validator([pin, pin.ToLowerInvariant()])(this, approved, null, SslPolicyErrors.None));   // two matches do not cancel out
        Assert.False(TlsEndpointTrust.Validator([Pin(other)])(this, approved, null, SslPolicyErrors.None));
        Assert.False(TlsEndpointTrust.Validator([pin])(this, null, null, SslPolicyErrors.None));
    }

    [Fact]
    public void The_pins_are_read_once_so_changing_the_list_later_changes_nothing()
    {
        using var approved = Certificate();
        var pins = new List<string> { Pin(approved) };
        var validate = TlsEndpointTrust.Validator(pins);

        pins[0] = new string('0', 64);

        Assert.True(validate(this, approved, null, SslPolicyErrors.None));
    }
}
