// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class DiagnosticTextTests
{
    [Fact]
    public void Known_credentials_and_canonical_escaped_forms_are_omitted_without_hiding_error_metadata()
    {
        const string secret = "synthetic-secret/\"λ-value";
        var escaped = JsonEncodedText.Encode(secret).ToString();
        var url = Uri.EscapeDataString(secret);
        var text = $"010002; fingerprint=1234abcd; {secret}; {escaped}; {url}";
        var result = DiagnosticText.OmitSecrets(text, [secret, "", secret[..16]]);
        foreach (var value in new[] { secret, escaped, url }) Assert.DoesNotContain(value, result);
        Assert.Contains("010002; fingerprint=1234abcd", result);
        Assert.Contains(DiagnosticText.Omitted, result);
    }

    [Fact]
    public void No_credentials_preserves_text_exactly() =>
        Assert.Equal("useful report", DiagnosticText.OmitSecrets("useful report", [""]));
}
