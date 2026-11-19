// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-186: nothing in a file can turn TLS off.</summary>
public sealed class TlsAlwaysOnTests
{
    [Fact]
    public void A_client_json_that_says_tls_is_off_is_read_as_on_and_the_difference_is_remembered()
    {
        var off = ClientConfig.Parse("""{ "psk": "synthetic-primary-key", "useTls": false }""")!;
        var on = ClientConfig.Parse("""{ "psk": "synthetic-primary-key", "useTls": true }""")!;
        var missing = ClientConfig.Parse("""{ "psk": "synthetic-primary-key" }""")!;

        Assert.Equal((true, true), (off.UseTls, off.TlsWasOff));
        Assert.Equal((true, false), (on.UseTls, on.TlsWasOff));
        Assert.Equal((true, false), (missing.UseTls, missing.TlsWasOff));
    }

    [Fact]
    public void A_host_settings_file_that_says_tls_is_off_is_loaded_as_on()
    {
        var folder = Path.Combine(Path.GetTempPath(), "classroom-tlson-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "host.json");
            File.WriteAllText(path, """{ "useTls": false, "tcpPort": 5050 }""");

            var loaded = HostSettings.Load(path);

            Assert.True(loaded.UseTls);
            Assert.True(loaded.TlsWasOff);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void The_saved_client_config_never_carries_the_remembered_difference()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(ClientConfig.Parse("""{ "psk": "k", "useTls": false }"""), Protocol.Json);
        Assert.DoesNotContain("tlsWasOff", json, StringComparison.OrdinalIgnoreCase);
    }
}
