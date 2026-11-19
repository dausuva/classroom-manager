// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

internal interface IFeedbackSecretSource
{
    IEnumerable<string> FeedbackSecrets { get; }
}

public static class FeedbackPrivacy
{
    public static FeedbackEntry Redact(FeedbackEntry entry, IEnumerable<string> secrets)
    {
        var values = secrets.ToArray();
        string Clean(string text) => DiagnosticText.OmitSecrets(text, values);
        return new FeedbackEntry
        {
            Id = entry.Id, CreatedUtc = entry.CreatedUtc, UpdatedUtc = entry.UpdatedUtc,
            Status = Clean(entry.Status), ContextId = Clean(entry.ContextId), WindowTitle = Clean(entry.WindowTitle),
            WindowType = Clean(entry.WindowType), TabTitle = Clean(entry.TabTitle), HostVersion = Clean(entry.HostVersion), Text = Clean(entry.Text)
        };
    }
}

public sealed class FeedbackEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = FeedbackStatuses.Saved;
    public string ContextId { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string WindowType { get; set; } = "";
    public string TabTitle { get; set; } = "";
    public string HostVersion { get; set; } = "";
    public string Text { get; set; } = "";
}

public static class FeedbackStatuses
{
    public const string Saved = "Saved for later";
    public const string ShareOpened = "Email draft opened";
}

public sealed class ProductSupportSettings
{
    const string ResourceName = "ClassroomManager.ProductSupport";
    public string SupportEmail { get; init; } = "support@dausuva.com";

    public static ProductSupportSettings Load()
    {
        try
        {
            var assembly = typeof(ProductSupportSettings).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return new ProductSupportSettings();
            var settings = JsonSerializer.Deserialize<ProductSupportSettings>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return settings is { SupportEmail.Length: > 3 } ? settings : new ProductSupportSettings();
        }
        catch
        {
            return new ProductSupportSettings();
        }
    }
}

public static class HostBuildInfo
{
    public static string Version
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(HostBuildInfo).Assembly;
            var fileVersion = Environment.ProcessPath is { } path
                ? FileVersionInfo.GetVersionInfo(path).FileVersion
                : assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            return ClassroomManager.Shared.ReleaseVersions.Display(fileVersion ?? assembly.GetName().Version?.ToString() ?? "Unknown");
        }
    }
}

public static class HostWindowTitle
{
    public static string Format(string version) => $"Dausuva Classroom Manager {version}";
}

public static class FeedbackReport
{
    public static string Format(IEnumerable<FeedbackEntry> entries, ProductSupportSettings support,
        DateTime? generatedUtc = null, IEnumerable<string>? knownSecrets = null)
    {
        var list = entries.OrderBy(x => x.CreatedUtc).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("Dausuva Classroom Manager feedback report");
        sb.AppendLine($"Support: {support.SupportEmail}");
        sb.AppendLine($"Host version: {HostBuildInfo.Version}");
        sb.AppendLine($"Generated UTC: {(generatedUtc ?? DateTime.UtcNow):O}");
        sb.AppendLine($"Items: {list.Count}");

        foreach (var entry in list)
        {
            sb.AppendLine();
            sb.AppendLine(new string('-', 72));
            sb.AppendLine($"Feedback ID: {entry.Id}");
            sb.AppendLine($"Created UTC: {entry.CreatedUtc:O}");
            sb.AppendLine($"Host version when captured: {entry.HostVersion}");
            sb.AppendLine($"Context ID: {entry.ContextId}");
            sb.AppendLine($"Window: {entry.WindowTitle} ({entry.WindowType})");
            sb.AppendLine($"Visible tab: {(entry.TabTitle.Length > 0 ? entry.TabTitle : "Not applicable")}");
            sb.AppendLine("Feedback:");
            sb.AppendLine(entry.Text);
        }

        return DiagnosticText.OmitSecrets(sb.ToString(), knownSecrets ?? []);
    }
}

public sealed class FeedbackStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly string _path;

    public FeedbackStore(string? path = null)
    {
        _path = path ?? Path.Combine(HostSettings.DataFolder, "feedback.json");
    }

    public IReadOnlyList<FeedbackEntry> Load()
    {
        if (!File.Exists(_path)) return Array.Empty<FeedbackEntry>();
        var document = JsonSerializer.Deserialize<FeedbackDocument>(File.ReadAllText(_path), Json)
                       ?? throw new InvalidDataException("The feedback file is empty or invalid.");
        return document.Items ?? new List<FeedbackEntry>();
    }

    public void Upsert(FeedbackEntry entry)
    {
        var entries = Load().ToList();
        var index = entries.FindIndex(x => x.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
        entry.UpdatedUtc = DateTime.UtcNow;
        if (index >= 0) entries[index] = entry;
        else entries.Add(entry);
        Save(entries);
    }

    public int RemoveShared()
    {
        var entries = Load().ToList();
        var removed = entries.RemoveAll(x => x.Status == FeedbackStatuses.ShareOpened);
        if (removed > 0) Save(entries);
        return removed;
    }

    public void Clear() => Save(Array.Empty<FeedbackEntry>());

    void Save(IEnumerable<FeedbackEntry> entries)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        var document = new FeedbackDocument { Items = entries.ToList() };
        File.WriteAllText(temp, JsonSerializer.Serialize(document, Json));
        File.Move(temp, _path, true);
    }

    sealed class FeedbackDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<FeedbackEntry> Items { get; set; } = new();
    }
}
