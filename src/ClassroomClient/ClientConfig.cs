// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>A named key in client.json's "accessKeys" list (see KeyStore and CommandAccess).</summary>
public sealed class ClientAccessKey
{
    public string Name { get; set; } = "";
    /// <summary>"Observer", "Teacher" or "Admin"; anything else is read as Observer.</summary>
    public string Role { get; set; } = "";
    public string Psk { get; set; } = "";
    /// <summary>When the key stops working (UTC); null = never. The lab's original key has none.</summary>
    public DateTime? NotAfterUtc { get; set; }
    /// <summary>The rooms this key works in (T-190); empty = every room. Compared with the room label of this computer.</summary>
    public List<string>? Rooms { get; set; }
}

public sealed class ClientConfig
{
    /// <summary>The lab's original shared key. It always has the Admin role.</summary>
    public string Psk { get; set; } = "";

    /// <summary>When the lab's original key was last changed on this computer (UTC); null on a computer that has not changed it since this was recorded (T-189).</summary>
    public DateTime? KeyChangedUtc { get; set; }

    /// <summary>The Host's count of lab key rotations that this computer has taken part in (T-189); 0 until a Host that counts them changes the key.</summary>
    public int KeyEpoch { get; set; }

    /// <summary>The room label this computer was given with a key limited to rooms (T-190); "" until then.</summary>
    public string Room { get; set; } = "";

    /// <summary>Additional named keys, each limited to its role. Managed by the Host through key.access.*.</summary>
    public List<ClientAccessKey> AccessKeys { get; set; } = new();
    /// <summary>Random identity created once per client installation and retained across IP changes and upgrades.</summary>
    public string ClientId { get; set; } = "";
    public int TcpPort { get; set; } = Protocol.DefaultTcpPort;
    public int UdpPort { get; set; } = Protocol.DefaultUdpPort;

    /// <summary>
    /// Wrap the TCP connection in TLS (a self-signed certificate the service generates on first
    /// run) before the SecureChannel handshake runs. Encrypts traffic against LAN sniffing; the
    /// shared key is still what authenticates the connection (see TlsCertificateStore). Must match
    /// the host's own setting, or the two sides simply fail to connect.
    /// </summary>
    public bool UseTls { get; set; } = true;

    /// <summary>True when client.json said TLS was off; <see cref="Load"/> ignored that (T-186) and the service logs it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TlsWasOff { get; set; }

    /// <summary>
    /// What a connection may do before it has proven a key (T-183): how many may be in setup, how fast one address may open
    /// them, how long a failing address waits, and how long setup may take. The defaults suit a lesson of 30 computers.
    /// </summary>
    public ConnectionLimits Limits { get; set; } = new();

    /// <summary>
    /// Commands per minute that one Teacher key may send, by command name; added to or replacing <see cref="KeyCommandLimiter.DefaultPerMinute"/>
    /// (0 = no limit for that command). Admin keys are not limited.
    /// </summary>
    public Dictionary<string, int> TeacherCommandLimits { get; set; } = new();

    /// <summary>
    /// Refuse a Host whose key proof does not cover the TLS certificate (protocol 1.17, T-187). Off by default so that Hosts not yet
    /// updated keep working; turn it on once every Host of the lab is updated.
    /// </summary>
    public bool RequireChannelBinding { get; set; }

    /// <summary>How often the UDP discovery question is answered (T-184): per source, all together, and whether to answer other subnets.</summary>
    public DiscoveryLimits Discovery { get; set; } = new();

    /// <summary>
    /// Keep answering the old unauthenticated discovery question (T-185). On by default for one release so that Hosts not yet updated keep
    /// finding this computer; turn it off once every Host sends the authenticated probe, and a stranger's question gets no answer at all.
    /// </summary>
    public bool AllowLegacyDiscovery { get; set; } = true;

    /// <summary>
    /// How many days a newly installed Client announces its certificate to a Host that proves the lab key (T-185, enrolment mode),
    /// until the first Host connects. 0 turns the announcement off; the computer is then added with an enrolment file or by hand.
    /// </summary>
    public int EnrolmentWindowDays { get; set; } = 7;

    /// <summary>Folders the host may browse / modify. Default: all user profiles.</summary>
    public List<string> AllowedRoots { get; set; } = new() { @"C:\Users" };

    /// <summary>
    /// SHA-256 fingerprints of Authenticode leaf certificates allowed to sign self-updates.
    /// Signed setup writes this from SIGNING.json and can replace it during planned rotation.
    /// </summary>
    public List<string> AllowedUpdateSignerCertificateSha256 { get; set; } = new();

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClassroomManager");

    /// <summary>Where the host uploads installers / wallpapers. Always allowed. Readable by users, writable by admins/SYSTEM only.</summary>
    public static string StagingDir => Path.Combine(DataDir, "Staging");

    /// <summary>Per-user scratch folder (used by the user helper).</summary>
    public static string UserDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassroomManager");

    public static string ConfigPath => Path.Combine(DataDir, "client.json");

    /// <summary>Reads client.json text. TLS cannot be turned off (T-186): a false is noted in <see cref="TlsWasOff"/> and ignored.</summary>
    internal static ClientConfig? Parse(string json)
    {
        var config = JsonSerializer.Deserialize<ClientConfig>(json, Protocol.Json);
        if (config is { UseTls: false }) { config.TlsWasOff = true; config.UseTls = true; }
        return config;
    }

    public static ClientConfig? Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var config = Parse(File.ReadAllText(ConfigPath));
            if (config is not null && string.IsNullOrWhiteSpace(config.ClientId))
            {
                config.ClientId = Guid.NewGuid().ToString("N");
                SaveProperty("clientId", config.ClientId);
            }
            return config;
        }
        catch { return null; }
    }

    /// <summary>
    /// Replaces only the shared key in client.json, keeping every other setting (including ones this
    /// build doesn't know about). Writes a temporary file in the same, admin-only folder and swaps it in,
    /// so a crash never leaves a half-written file; File.Replace keeps the original file's permissions.
    /// </summary>
    public static void SaveKey(string newKey)
        => SaveProperties(("psk", newKey), ("keyChangedUtc", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));

    /// <summary>Records which rotation of the lab key this computer is on, after <see cref="SaveKey"/>.</summary>
    public static void SaveEpoch(int epoch) => SaveProperties(("keyEpoch", epoch));

    /// <summary>Records the room label of this computer.</summary>
    public static void SaveRoom(string room) => SaveProperties(("room", room));

    /// <summary>Replaces only the named access keys in client.json, with the same crash-safe swap as <see cref="SaveKey"/>.</summary>
    public static void SaveAccessKeys(IEnumerable<ClientAccessKey> keys)
        => SaveProperty("accessKeys", JsonSerializer.SerializeToNode(keys.ToList(), Protocol.Json)!);

    static void SaveProperty(string propertyName, JsonNode value) => SaveProperties((propertyName, value));

    static void SaveProperties(params (string Name, JsonNode Value)[] properties)
    {
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject ?? new JsonObject(); }
        catch (FileNotFoundException) { root = new JsonObject(); }

        foreach (var (propertyName, value) in properties)
        {
            // Drop differently-cased duplicates so there is exactly one canonical property.
            foreach (var name in root.Select(p => p.Key).Where(k => k.Equals(propertyName, StringComparison.OrdinalIgnoreCase)).ToList())
                root.Remove(name);
            root[propertyName] = value;
        }

        var temp = ConfigPath + ".new";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        // The new file is created with ProgramData's open default rules; close them before it takes the old file's place.
        if (SecretFiles.Ensure(temp) is { Length: > 0 } finding) Log.Write("Secret file: " + finding);
        if (File.Exists(ConfigPath)) File.Replace(temp, ConfigPath, destinationBackupFileName: null);
        else File.Move(temp, ConfigPath);
    }
}

/// <summary>Which of the two processes we are.</summary>
static class AgentContext
{
    /// <summary>True in the per-user helper, false in the SYSTEM service.</summary>
    public static bool IsUserAgent { get; set; }
}

static class Log
{
    static readonly object Gate = new();

    /// <summary>Log file; set at startup by Program.</summary>
    public static string? FilePath { get; set; }
    public static Func<IEnumerable<string>>? KnownSecrets { get; set; }

    public static void Write(string message)
    {
        try
        {
            if (FilePath is null) return;
            message = DiagnosticText.OmitSecrets(message, KnownSecrets?.Invoke() ?? []);
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
