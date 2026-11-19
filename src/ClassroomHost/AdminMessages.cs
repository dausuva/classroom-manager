// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;

namespace ClassroomManager.Host;

/// <summary>A note a teacher left for whoever looks after the lab.</summary>
public sealed class AdminMessage
{
    public string Id { get; set; } = "";
    public string WrittenUtc { get; set; } = "";
    public string From { get; set; } = "";       // the Windows account that wrote it
    public string Computer { get; set; } = "";
    public string Text { get; set; } = "";
    public string HostVersion { get; set; } = "";

    public string Heading => $"{(DateTime.TryParse(WrittenUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : WrittenUtc)}  {From}";
}

/// <summary>
/// Notes from teachers to the lab administrator. The Feedback action goes to the product's
/// support address and stays in the teacher's own Windows account; this is for the lab itself:
/// "PC-07 has no sound", "please unblock example.org". A note is one small file in a folder every
/// Windows account of the teacher PC can reach, so an administrator signed in under another
/// account finds it the next time they open Classroom Manager there.
/// </summary>
public sealed class AdminMessages
{
    public const int MaxLength = 4000;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClassroomManager", "HostMessages");

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Folder { get; }

    public AdminMessages(string? folder = null) => Folder = folder ?? DefaultFolder;

    /// <summary>Stores a note. Returns what is wrong with it, or an empty text when it was stored.</summary>
    public string Leave(string text, string from, string hostVersion, DateTime? nowUtc = null)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return "Write the message first.";
        if (text.Length > MaxLength) return $"The message is limited to {MaxLength} characters.";
        var now = nowUtc ?? DateTime.UtcNow;
        var message = new AdminMessage
        {
            Id = $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            WrittenUtc = now.ToString("O"), From = from, Computer = Environment.MachineName, Text = text, HostVersion = hostVersion,
        };
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, message.Id + ".json"), JsonSerializer.Serialize(message, Json));
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "The message could not be stored: " + ex.Message; }
    }

    /// <summary>The notes waiting, oldest first. A file that cannot be read is left out.</summary>
    public List<AdminMessage> Waiting()
    {
        var messages = new List<AdminMessage>();
        try
        {
            if (!Directory.Exists(Folder)) return messages;
            foreach (var file in Directory.GetFiles(Folder, "*.json").OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (JsonSerializer.Deserialize<AdminMessage>(File.ReadAllText(file), Json) is { Text.Length: > 0 } message)
                    {
                        message.Id = Path.GetFileNameWithoutExtension(file);
                        messages.Add(message);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return messages;
    }

    /// <summary>Removes notes that were dealt with; returns how many were removed.</summary>
    public int Remove(IEnumerable<AdminMessage> messages)
    {
        int removed = 0;
        foreach (var message in messages)
        {
            // The id is a file name this class made; anything else is not ours to delete.
            if (message.Id.Length == 0 || message.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            try
            {
                var file = Path.Combine(Folder, message.Id + ".json");
                if (!File.Exists(file)) continue;
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
}
