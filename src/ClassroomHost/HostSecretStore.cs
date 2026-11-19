// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

sealed class HostSecrets
{
    public string Psk { get; set; } = "";
    public string UploadToken { get; set; } = "";
    public Dictionary<string, string> AdditionalPsks { get; set; } = new(StringComparer.Ordinal);
    public List<string> PreviousPsks { get; set; } = new();
}

static class HostSecretStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClassroomManager.Host.Keys.v1");

    public static string PathFor(string settingsPath) => Path.ChangeExtension(settingsPath, ".keys");

    public static HostSecrets From(HostSettings settings) => new()
    {
        Psk = settings.Psk,
        UploadToken = settings.UploadToken,
        AdditionalPsks = settings.AdditionalHostKeys
            .Where(key => !string.IsNullOrWhiteSpace(key.Id) && !string.IsNullOrWhiteSpace(key.Psk))
            .ToDictionary(key => key.Id, key => key.Psk, StringComparer.Ordinal),
        PreviousPsks = settings.PreviousPsks.ToList(),
    };

    public static void Apply(HostSettings settings, HostSecrets secrets)
    {
        settings.Psk = secrets.Psk ?? "";
        settings.UploadToken = secrets.UploadToken ?? "";
        settings.PreviousPsks = secrets.PreviousPsks ?? new();
        secrets.AdditionalPsks ??= new(StringComparer.Ordinal);
        foreach (var key in settings.AdditionalHostKeys)
            key.Psk = secrets.AdditionalPsks.TryGetValue(key.Id, out var psk) ? psk : "";
    }

    public static HostSecrets Load(string path, bool preferBackup = false)
    {
        var backup = AtomicFile.BackupPath(path);
        if (preferBackup && File.Exists(backup))
        {
            var recovered = LoadExact(backup);
            AtomicFile.RestoreFromBackup(path);
            return recovered;
        }
        try { return LoadExact(path); }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException && File.Exists(backup))
        {
            var recovered = LoadExact(backup);
            AtomicFile.RestoreFromBackup(path);
            return recovered;
        }
    }

    static HostSecrets LoadExact(string path)
    {
        var encrypted = File.ReadAllBytes(path);
        var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<HostSecrets>(json, Protocol.Json)
               ?? throw new InvalidDataException("The protected Host key store is empty or invalid.");
    }

    public static void Save(string path, HostSecrets secrets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, Protect(secrets));
    }

    public static byte[] Protect(HostSecrets secrets) => ProtectedData.Protect(
        JsonSerializer.SerializeToUtf8Bytes(secrets, Protocol.Json), Entropy, DataProtectionScope.CurrentUser);

    public static (HostSecrets Secrets, bool ContainsSecrets) ReadLegacy(
        string json, IReadOnlyList<NamedHostKey> additionalKeys)
    {
        var secrets = new HostSecrets();
        var contains = false;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (TryProperty(root, "psk", out var psk) && psk.ValueKind == JsonValueKind.String)
        {
            secrets.Psk = psk.GetString() ?? "";
            contains |= secrets.Psk.Length > 0;
        }
        if (TryProperty(root, "previousPsks", out var previous) && previous.ValueKind == JsonValueKind.Array)
        {
            secrets.PreviousPsks = previous.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .Where(value => value.Length > 0)
                .ToList();
            contains |= secrets.PreviousPsks.Count > 0;
        }
        if (TryProperty(root, "additionalHostKeys", out var additional) && additional.ValueKind == JsonValueKind.Array)
        {
            var legacy = additional.EnumerateArray().ToList();
            for (var i = 0; i < Math.Min(legacy.Count, additionalKeys.Count); i++)
            {
                if (!TryProperty(legacy[i], "psk", out var value) || value.ValueKind != JsonValueKind.String) continue;
                var additionalPsk = value.GetString() ?? "";
                if (additionalPsk.Length == 0) continue;
                secrets.AdditionalPsks[additionalKeys[i].Id] = additionalPsk;
                contains = true;
            }
        }
        return (secrets, contains);
    }

    static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }
}
