// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.Cryptography;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The "do you trust this certificate?" step of the Host: what it tells the person and what a yes records.</summary>
public sealed class CertificateApprovalTests
{
    static readonly string Pin = Convert.ToHexString(SHA256.HashData("computer 205"u8.ToArray()));
    static readonly string OtherPin = Convert.ToHexString(SHA256.HashData("another"u8.ToArray()));
    const string Card = "002264B3B63A";

    static SavedComputer Computer(string mac = "") => new() { Address = "192.168.1.205", Name = "USER-PC", Mac = mac };

    [Fact]
    public void A_certificate_nobody_approved_is_new_and_can_be_approved()
    {
        var assessment = CertificateApproval.Assess(new HostSettings(), Computer(), Pin, Card);

        Assert.Equal(ApprovalKind.New, assessment.Kind);
        Assert.True(assessment.CanApprove);
        Assert.Equal("", assessment.Warning);
    }

    [Fact]
    public void No_certificate_means_nothing_to_approve()
    {
        var assessment = CertificateApproval.Assess(new HostSettings(), Computer(), "", "");

        Assert.Equal(ApprovalKind.Unreachable, assessment.Kind);
        Assert.False(assessment.CanApprove);
    }

    [Fact]
    public void The_certificate_already_approved_for_the_address_is_reported_as_such()
    {
        var settings = new HostSettings();
        settings.EnrollTrust("192.168.1.205", 5050, Card, [Pin]);

        var assessment = CertificateApproval.Assess(settings, Computer(Card), Pin, Card);

        Assert.Equal(ApprovalKind.AlreadyApproved, assessment.Kind);
        Assert.False(assessment.CanApprove);
    }

    [Fact]
    public void A_different_certificate_at_an_approved_address_is_a_change_with_a_warning()
    {
        var settings = new HostSettings();
        settings.EnrollTrust("192.168.1.205", 5050, Card, [OtherPin]);

        var assessment = CertificateApproval.Assess(settings, Computer(Card), Pin, Card);

        Assert.Equal(ApprovalKind.Changed, assessment.Kind);
        Assert.True(assessment.CanApprove);
        Assert.NotEqual("", assessment.Warning);
    }

    [Fact]
    public void A_certificate_approved_for_another_address_is_named_with_that_address()
    {
        var settings = new HostSettings();
        settings.EnrollTrust("192.168.1.99", 5050, "", [Pin]);

        var assessment = CertificateApproval.Assess(settings, Computer(), Pin, Card);

        Assert.Equal(ApprovalKind.ApprovedElsewhere, assessment.Kind);
        Assert.Contains(assessment.Facts, fact => fact.Contains("192.168.1.99"));
    }

    [Fact]
    public void Another_network_card_at_the_address_is_warned_about()
    {
        var assessment = CertificateApproval.Assess(new HostSettings(), Computer("AABBCCDDEEFF"), Pin, Card);

        Assert.Equal(ApprovalKind.New, assessment.Kind);
        Assert.Contains("different computer", assessment.Warning);
    }

    [Fact]
    public void A_yes_approves_the_shown_certificate_and_records_the_card_the_network_showed()
    {
        var settings = new HostSettings();

        Assert.True(CertificateApproval.Approve(settings, Computer(), Pin.ToLowerInvariant(), Pin, Card));

        Assert.Equal([Pin], settings.TlsPins("192.168.1.205", 5050, Card));
    }

    [Fact]
    public void A_certificate_that_changed_between_showing_and_yes_approves_nothing()
    {
        var settings = new HostSettings();

        Assert.False(CertificateApproval.Approve(settings, Computer(), Pin, OtherPin, Card));
        Assert.False(CertificateApproval.Approve(settings, Computer(), "", "", Card));

        Assert.Empty(settings.TrustedTlsEndpoints ?? []);
    }

    [Fact]
    public void The_fingerprint_is_shown_in_groups_of_eight_digits()
    {
        var spaced = CertificateApproval.Spaced(Pin);

        Assert.Equal(8, spaced.Split(' ').Length);
        Assert.Equal(Pin, spaced.Replace(" ", ""));
    }
}
