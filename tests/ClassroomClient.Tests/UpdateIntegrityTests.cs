// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using Xunit;

namespace ClassroomClient.Tests;

public sealed class UpdateIntegrityTests
{
    public static TheoryData<string> InvalidRequests => new()
    {
        "{}", "{\"sha256\":null}", "{\"sha256\":\"\"}", "{\"sha256\":\" \"}",
        "{\"sha256\":42}", "{\"sha256\":false}", "{\"sha256\":{}}", "{\"sha256\":[]}",
        new JsonObject { ["sha256"] = new string('a', 63) }.ToJsonString(),
        new JsonObject { ["sha256"] = new string('a', 65) }.ToJsonString(),
        new JsonObject { ["sha256"] = new string('g', 64) }.ToJsonString(),
        new JsonObject { ["sha256"] = new string('a', 63) + "\n" }.ToJsonString(),
        new JsonObject { ["sha256"] = new string('\uFF21', 64) }.ToJsonString()
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_hash_is_rejected_by_the_real_handler_even_with_force(string request)
    {
        // Invalid path proves rejection happens before file access, config loading or execution.
        // No staging, service, signing certificate or machine configuration is touched.
        foreach (var force in new[] { false, true })
        {
            var args = JsonNode.Parse(request)!.AsObject();
            args["path"] = "\0";
            args["force"] = force;
            var response = await new UpdateHandler().ApplyAsync(args, CancellationToken.None);
            Assert.False(response.Ok);
            Assert.Equal(UpdateFileIntegrity.InvalidHash, response.Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hexadecimal_hash_accepts_either_case(bool uppercase)
    {
        var hash = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
        Assert.True(UpdateFileIntegrity.TryReadHash(JsonValue.Create(uppercase ? hash : hash.ToLowerInvariant()), out var normalized));
        Assert.Equal(hash.ToLowerInvariant(), normalized);
    }

    [Fact]
    public async Task Actual_uploaded_bytes_are_verified_and_tampering_is_rejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            byte[] bytes = [(byte)'M', (byte)'Z', 1, 2, 3];
            await File.WriteAllBytesAsync(path, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            Assert.Null(await UpdateFileIntegrity.VerifyAsync(path, hash, CancellationToken.None));
            Assert.Null(await UpdateFileIntegrity.VerifyAsync(path, hash.ToLowerInvariant(), CancellationToken.None));
            await File.WriteAllBytesAsync(path, [(byte)'M', (byte)'Z', 1, 2, 4]);
            Assert.Equal(UpdateFileIntegrity.Mismatch, await UpdateFileIntegrity.VerifyAsync(path, hash, CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateFileIntegrity.VerifyAsync(path, hash, cancelled.Token));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task The_verification_boundary_rejects_an_invalid_hash_without_opening_a_file() =>
        Assert.Equal(UpdateFileIntegrity.InvalidHash, await UpdateFileIntegrity.VerifyAsync("\0", "", CancellationToken.None));
}
