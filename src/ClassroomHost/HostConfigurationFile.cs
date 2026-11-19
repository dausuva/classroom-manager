// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>What a configuration file holds, shown before it is imported. Never includes a key.</summary>
public sealed record HostConfigurationInfo(string ExportedUtc, string Computer, string HostVersion, int Computers, IReadOnlyList<string> Rooms, IReadOnlyList<string> KeyNames, int Schedules)
{
    public string Describe() => string.Join("\n", new[]
    {
        DateTime.TryParse(ExportedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var exported)
            ? $"Exported: {exported.ToLocalTime():yyyy-MM-dd HH:mm} on {Computer} (version {HostVersion})" : $"Exported on {Computer}",
        $"Keys: {(KeyNames.Count == 0 ? "none" : string.Join(", ", KeyNames))}",
        $"Computers: {Computers}{(Rooms.Count > 0 ? $" in {string.Join(", ", Rooms.Take(8))}{(Rooms.Count > 8 ? ", ..." : "")}" : "")}",
        $"Schedules: {Schedules}",
        "Also: Exam mode lists, block lists, saved websites and programs, favourite commands, quick actions, interface choices, transfer history.",
    });
}

/// <summary>
/// The whole configuration of a Host in one password-protected file: every setting and every key,
/// including the lab's own Admin key. For a backup kept somewhere else, a move to another computer
/// or Windows account, or setting up a second teacher PC. It always needs a password, because
/// whoever can read it controls every computer the keys reach.
/// </summary>
public static class HostConfigurationFile
{
    public const string Format = "classroom-host-configuration";
    public const string DefaultFileName = "classroom-manager-configuration.cmconfig";
    public const int MinimumPasswordLength = 8;
    const int Version = 1;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    sealed class Envelope
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string? Salt { get; set; }
        public string? Nonce { get; set; }
        public string? Tag { get; set; }
        public string? Data { get; set; }
    }

    sealed class Content
    {
        public string ExportedUtc { get; set; } = "";
        public string Computer { get; set; } = "";
        public string HostVersion { get; set; } = "";
        /// <summary>The settings as host.json has them (no keys).</summary>
        public JsonElement Settings { get; set; }
        public HostSecrets Secrets { get; set; } = new();
    }

    public static bool PasswordIsAcceptable(string password, out string reason)
    {
        reason = password.Length >= MinimumPasswordLength ? ""
            : $"The password must have at least {MinimumPasswordLength} characters: the file contains the keys to every computer.";
        return reason.Length == 0;
    }

    public static string Write(HostSettings settings, string password, string hostVersion)
    {
        if (!PasswordIsAcceptable(password, out var reason)) throw new ArgumentException(reason, nameof(password));
        var content = new Content
        {
            ExportedUtc = DateTime.UtcNow.ToString("O"),
            Computer = Environment.MachineName,
            HostVersion = hostVersion,
            Settings = JsonSerializer.SerializeToElement(settings, Protocol.Json),
            Secrets = HostSecretStore.From(settings),
        };
        var sealedContent = PasswordCipher.Encrypt(JsonSerializer.SerializeToUtf8Bytes(content, Json), password);
        return JsonSerializer.Serialize(new Envelope
        {
            Format = Format, Version = Version,
            Salt = sealedContent.Salt, Nonce = sealedContent.Nonce, Tag = sealedContent.Tag, Data = sealedContent.Data,
        }, Json);
    }

    /// <summary>True when the text is a configuration file (whatever its password).</summary>
    public static bool IsConfiguration(string text)
    {
        try { return JsonSerializer.Deserialize<Envelope>(text, Json)?.Format == Format; }
        catch (JsonException) { return false; }
    }

    /// <summary>Reads a file written by <see cref="Write"/>; throws <see cref="InvalidDataException"/> with a message for the person importing.</summary>
    public static (HostSettings Settings, HostConfigurationInfo Info) Read(string text, string password)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(text, Json); }
        catch (JsonException) { envelope = null; }
        if (envelope is null || envelope.Format != Format)
            throw new InvalidDataException("This is not a configuration file exported from Classroom Manager.");
        if (envelope.Version > Version)
            throw new InvalidDataException("This configuration file was made by a newer Classroom Manager. Update this one first.");

        Content? content;
        try
        {
            var plain = PasswordCipher.Decrypt(new PasswordCipher.Sealed(envelope.Salt ?? "", envelope.Nonce ?? "", envelope.Tag ?? "", envelope.Data ?? ""), password);
            content = JsonSerializer.Deserialize<Content>(plain, Json);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or JsonException)
        {
            throw new InvalidDataException("The password is wrong, or the file was changed after it was exported.");
        }
        HostSettings? settings = null;
        try { if (content is not null) settings = content.Settings.Deserialize<HostSettings>(Protocol.Json); }
        catch (JsonException) { }
        if (content is null || settings is null) throw new InvalidDataException("The configuration file is damaged.");

        settings.AdditionalHostKeys ??= new();
        settings.Computers ??= new();
        HostSecretStore.Apply(settings, content.Secrets ?? new HostSecrets());
        var info = new HostConfigurationInfo(content.ExportedUtc, content.Computer, content.HostVersion,
            settings.Computers.Count,
            settings.Computers.Select(computer => computer.Room).Where(room => !string.IsNullOrEmpty(room)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            settings.CurrentHostKeys().Select(key => key.Name).ToList(),
            settings.Schedules?.Count ?? 0);
        return (settings, info);
    }

    /// <summary>
    /// Replaces the settings and keys at <paramref name="settingsPath"/> with the imported ones,
    /// after keeping the current ones as a backup generation. With <paramref name="asSecondHost"/>
    /// the imported Host gets an identity of its own, so it can run beside the one it came from:
    /// its own upload folder on the student computers, no scheduled actions (they would run twice)
    /// and no exam in progress.
    /// </summary>
    public static void Install(string settingsPath, HostSettings imported, bool asSecondHost)
    {
        HostBackups.Snapshot(settingsPath, HostBackups.BeforeImport);
        if (asSecondHost)
        {
            imported.HostId = Guid.NewGuid().ToString("N")[..8];
            imported.UploadToken = Guid.NewGuid().ToString("N");
            imported.RunSchedules = false;
            imported.RunSchedulesWhenClosed = false;
            imported.ExamModeEnabled = false;
            imported.ExamModeComputers = new();
            imported.ExamModePendingClear = new();
        }
        imported.Save(settingsPath);
    }
}
