// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// What an administrator hands a teacher in one file: a Teacher access key, how to reach the
/// computers it works on, and optionally the classroom presets of the administrator's Host.
/// It never carries the lab's own key or any other key the exporting Host stores.
/// </summary>
public sealed class TeacherSettings
{
    public string KeyName { get; set; } = "";
    public string Key { get; set; } = "";
    public string CreatedUtc { get; set; } = "";

    public int TcpPort { get; set; } = Protocol.DefaultTcpPort;
    public int UdpPort { get; set; } = Protocol.DefaultUdpPort;
    public bool UseTls { get; set; } = true;

    public List<SavedComputer> Computers { get; set; } = new();
    public TeacherPresets? Presets { get; set; }
}

/// <summary>Lists and interface choices a teacher's Host can start from.</summary>
public sealed class TeacherPresets
{
    public List<string> ExamModeDomains { get; set; } = new();
    public List<string> ExamModeApps { get; set; } = new();
    public bool ExamModeMuteAudio { get; set; }
    public bool ExamModeDisableCamera { get; set; }
    public bool ExamModeBlockUsbStorage { get; set; }
    public List<string> BlockedAppCatalog { get; set; } = new();
    public List<string> DefaultWebsiteBlocklist { get; set; } = new();
    public List<string> SavedWebsites { get; set; } = new();
    public List<SavedProgramLaunch> SavedPrograms { get; set; } = new();

    public List<string> QuickActions { get; set; } = new();
    public string QuickActionMessage { get; set; } = "";
    public List<CustomQuickAction> CustomQuickActions { get; set; } = new();
    public Dictionary<string, ToolbarLayout> ToolbarLayouts { get; set; } = new();
    public int ToolbarLayoutVersion { get; set; }
    public List<string> TeacherVisibleCommands { get; set; } = new();
}

public static class TeacherSettingsFile
{
    public const string Format = "classroom-teacher-settings";
    public const string DefaultFileName = "teacher-settings.json";
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
        public bool Protected { get; set; }
        public TeacherSettings? Settings { get; set; }
        // Present when Protected: the settings encrypted with a key derived from the password.
        public string? Salt { get; set; }
        public string? Nonce { get; set; }
        public string? Tag { get; set; }
        public string? Data { get; set; }
    }

    /// <summary>Builds the settings to hand over; <paramref name="key"/> is the new Teacher key, already on the computers.</summary>
    public static TeacherSettings Create(HostSettings host, IEnumerable<SavedComputer> computers, string keyName, string key, bool includePresets)
    {
        var fingerprint = SharedKey.Fingerprint(key);
        return new TeacherSettings
        {
            KeyName = keyName,
            Key = key,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            TcpPort = host.TcpPort,
            UdpPort = host.UdpPort,
            UseTls = host.UseTls,
            // The teacher's Host should try the Teacher key on these computers, not the administrator's.
            Computers = computers.Select(computer => new SavedComputer
            {
                Address = computer.Address, Name = computer.Name, Alias = computer.Alias, ClientId = computer.ClientId, Mac = computer.Mac,
                Port = computer.Port, Room = computer.Room, KeyId = fingerprint,
            }).ToList(),
            Presets = includePresets ? new TeacherPresets
            {
                ExamModeDomains = [.. host.ExamModeDomains],
                ExamModeApps = [.. host.ExamModeApps],
                ExamModeMuteAudio = host.ExamModeMuteAudio,
                ExamModeDisableCamera = host.ExamModeDisableCamera,
                ExamModeBlockUsbStorage = host.ExamModeBlockUsbStorage,
                BlockedAppCatalog = [.. host.BlockedAppCatalog],
                DefaultWebsiteBlocklist = [.. host.DefaultWebsiteBlocklist],
                SavedWebsites = [.. host.SavedWebsites],
                SavedPrograms = host.SavedPrograms.Select(program => new SavedProgramLaunch { Program = program.Program, Arguments = program.Arguments }).ToList(),
                QuickActions = [.. host.QuickActions],
                QuickActionMessage = host.QuickActionMessage,
                CustomQuickActions = host.CustomQuickActions.Select(action => action.Copy()).ToList(),
                ToolbarLayouts = host.ToolbarLayouts.ToDictionary(pair => pair.Key, pair => pair.Value.Copy()),
                ToolbarLayoutVersion = host.ToolbarLayoutVersion,
                TeacherVisibleCommands = [.. host.TeacherVisibleCommands],
            } : null,
        };
    }

    /// <summary>The file's text. With a password the settings are encrypted; without one the key is readable by anyone who opens the file.</summary>
    public static string Write(TeacherSettings settings, string? password)
    {
        if (string.IsNullOrEmpty(password))
            return JsonSerializer.Serialize(new Envelope { Format = Format, Version = Version, Settings = settings }, Json);

        var content = PasswordCipher.Encrypt(JsonSerializer.SerializeToUtf8Bytes(settings, Json), password);
        return JsonSerializer.Serialize(new Envelope
        {
            Format = Format, Version = Version, Protected = true,
            Salt = content.Salt, Nonce = content.Nonce, Tag = content.Tag, Data = content.Data,
        }, Json);
    }

    static Envelope ReadEnvelope(string text)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(text, Json); }
        catch (JsonException) { envelope = null; }
        if (envelope is null || envelope.Format != Format)
            throw new InvalidDataException("This is not a teacher settings file exported from Classroom Manager.");
        if (envelope.Version > Version)
            throw new InvalidDataException("This teacher settings file was made by a newer Classroom Manager. Update this one first.");
        return envelope;
    }

    /// <summary>True when the file needs its password to be read.</summary>
    public static bool IsProtected(string text) => ReadEnvelope(text).Protected;

    /// <summary>Reads a file written by <see cref="Write"/>; throws <see cref="InvalidDataException"/> with a message for the person importing.</summary>
    public static TeacherSettings Read(string text, string? password)
    {
        var envelope = ReadEnvelope(text);
        var settings = envelope.Settings;
        if (envelope.Protected)
        {
            if (string.IsNullOrEmpty(password)) throw new InvalidDataException("This teacher settings file is protected with a password.");
            try
            {
                var plain = PasswordCipher.Decrypt(new PasswordCipher.Sealed(envelope.Salt ?? "", envelope.Nonce ?? "", envelope.Tag ?? "", envelope.Data ?? ""), password);
                settings = JsonSerializer.Deserialize<TeacherSettings>(plain, Json);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or JsonException)
            {
                throw new InvalidDataException("The password is wrong, or the file was changed after it was exported.");
            }
        }
        if (settings is null || !SharedKey.IsAcceptable(settings.Key, out _))
            throw new InvalidDataException("The teacher settings file does not contain a usable key.");
        settings.KeyName = string.IsNullOrWhiteSpace(settings.KeyName) ? "Teacher" : settings.KeyName.Trim();
        settings.Computers = (settings.Computers ?? new()).Where(computer => computer is not null && !string.IsNullOrWhiteSpace(computer.Address)).ToList();
        return settings;
    }

    /// <summary>What a file holds, for the person to confirm before anything is changed. Never includes the key itself.</summary>
    public static string Describe(TeacherSettings settings)
    {
        var rooms = settings.Computers.Select(computer => computer.Room).Where(room => room.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lines = new List<string>
        {
            $"Key: {settings.KeyName} (fingerprint {SharedKey.Fingerprint(settings.Key)})",
            $"Connection: TCP port {settings.TcpPort}, discovery port {settings.UdpPort}, TLS {(settings.UseTls ? "on" : "off")}",
            settings.Computers.Count == 0 ? "Computers: none"
                : $"Computers: {settings.Computers.Count}" + (rooms.Count > 0 ? $" in {string.Join(", ", rooms.Take(8))}{(rooms.Count > 8 ? ", ..." : "")}" : ", not assigned to a room"),
            settings.Presets is { } presets
                ? $"Presets: {presets.ExamModeDomains.Count} exam website(s), {presets.ExamModeApps.Count} exam program(s), " +
                  $"{presets.DefaultWebsiteBlocklist.Count} default blocked website(s), {presets.BlockedAppCatalog.Count} program(s) in the block catalog, " +
                  $"{presets.SavedWebsites.Count} saved website(s), {presets.SavedPrograms.Count} saved program(s)"
                : "Presets: none",
        };
        if (DateTime.TryParse(settings.CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var created))
            lines.Add($"Exported: {created.ToLocalTime():yyyy-MM-dd HH:mm}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Puts the key and connection settings into a Host's settings, and the presets when asked.
    /// The computers are merged by the main window, which owns the list. Nothing is saved here.
    /// Returns what changed, one line each.
    /// </summary>
    public static List<string> Apply(TeacherSettings teacher, HostSettings host, bool presets, bool interfaceChoices)
    {
        var changes = new List<string>();

        if (host.CurrentHostKeys().Any(key => key.Psk == teacher.Key)) changes.Add($"The key '{teacher.KeyName}' was already stored.");
        else if (string.IsNullOrWhiteSpace(host.Psk))
        {
            host.Psk = teacher.Key;
            host.DefaultKeyName = teacher.KeyName;
            changes.Add($"The key '{teacher.KeyName}' is now this Host's key.");
        }
        else
        {
            var taken = host.CurrentHostKeys().Select(key => key.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = teacher.KeyName;
            for (int n = 2; taken.Contains(name); n++) name = $"{teacher.KeyName} ({n})";
            host.AdditionalHostKeys.Add(new NamedHostKey { Name = name, Psk = teacher.Key });
            changes.Add($"The key '{name}' was added to Lab keys; the keys already stored are kept.");
        }

        if (host.TcpPort != teacher.TcpPort || host.UdpPort != teacher.UdpPort)
        {
            host.TcpPort = teacher.TcpPort;
            host.UdpPort = teacher.UdpPort;
            changes.Add($"Connection settings changed to TCP port {teacher.TcpPort}, discovery port {teacher.UdpPort}.");
        }

        if (teacher.Presets is not { } from) return changes;
        if (presets)
        {
            static List<string> Merge(IEnumerable<string> mine, IEnumerable<string> theirs) =>
                mine.Concat(theirs ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            host.ExamModeDomains = Merge(host.ExamModeDomains, from.ExamModeDomains);
            host.ExamModeApps = Merge(host.ExamModeApps, from.ExamModeApps);
            host.ExamModeMuteAudio |= from.ExamModeMuteAudio;
            host.ExamModeDisableCamera |= from.ExamModeDisableCamera;
            host.ExamModeBlockUsbStorage |= from.ExamModeBlockUsbStorage;
            host.BlockedAppCatalog = Merge(host.BlockedAppCatalog, from.BlockedAppCatalog);
            host.DefaultWebsiteBlocklist = Merge(host.DefaultWebsiteBlocklist, from.DefaultWebsiteBlocklist);
            host.SavedWebsites = Merge(host.SavedWebsites, from.SavedWebsites);
            foreach (var program in from.SavedPrograms ?? [])
                if (!string.IsNullOrWhiteSpace(program?.Program) && !host.SavedPrograms.Any(saved =>
                        saved.Program.Equals(program.Program, StringComparison.OrdinalIgnoreCase) && saved.Arguments.Equals(program.Arguments, StringComparison.OrdinalIgnoreCase)))
                    host.SavedPrograms.Add(new SavedProgramLaunch { Program = program.Program, Arguments = program.Arguments ?? "" });
            changes.Add("Exam mode lists, block lists and saved websites and programs were added to the ones already here.");
        }
        if (interfaceChoices)
        {
            host.HostMode = HostModes.Teacher;
            if (from.TeacherVisibleCommands is { Count: > 0 }) host.TeacherVisibleCommands = TeacherCommandCatalog.Normalize(from.TeacherVisibleCommands);
            if (from.QuickActions is { Count: > 0 }) host.QuickActions = QuickActionCatalog.Normalize(from.QuickActions);
            if (!string.IsNullOrWhiteSpace(from.QuickActionMessage)) host.QuickActionMessage = from.QuickActionMessage;
            // The administrator's own quick actions are added; one the teacher already has (same id) is left as the teacher keeps it.
            // The toolbar as the administrator arranged it, tab by tab.
            foreach (var (tab, layout) in from.ToolbarLayouts ?? [])
                host.ToolbarLayouts[tab] = layout.Copy();
            host.ToolbarLayoutVersion = Math.Min(host.ToolbarLayoutVersion, from.ToolbarLayoutVersion);
            foreach (var custom in from.CustomQuickActions ?? [])
                if (custom.Problem().Length == 0 && host.CustomQuickActions.All(mine => mine.Id != custom.Id))
                    host.CustomQuickActions.Add(custom.Copy());
            changes.Add("The toolbar is in Teacher mode with the administrator's choice of visible commands and quick actions.");
        }
        return changes;
    }
}
