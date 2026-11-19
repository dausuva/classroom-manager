// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>A computer remembered between sessions (found by scan or added by hand).</summary>
public sealed class SavedComputer
{
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The teacher's own label for the computer; "" = none.</summary>
    public string Alias { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Mac { get; set; } = "";   // for Wake-on-LAN, learned when the computer was online
    public int Port { get; set; }
    public string Room { get; set; } = "";
    public string KeyId { get; set; } = "";  // fingerprint of the shared key this computer last accepted (see SharedKey.Fingerprint)
}

/// <summary>A named shared key for another independently keyed classroom or lab.</summary>
public sealed class NamedHostKey
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    [JsonIgnore]
    public string Psk { get; set; } = "";
}

/// <summary>Public TLS trust metadata, approved independently of discovery or the PSK handshake.</summary>
public sealed class TrustedTlsEndpoint
{
    public string Address { get; set; } = "";
    public int Port { get; set; } = Protocol.DefaultTcpPort;
    public List<string> CertificateSha256 { get; set; } = [];
    /// <summary>
    /// The network card address (12 hex digits) of the computer this certificate was verified for; empty for an
    /// entry made before computers were told apart this way. Lets the approval follow the computer when DHCP
    /// gives it another address, and keeps it from being used for whichever computer gets the old address.
    /// </summary>
    public string Mac { get; set; } = "";
}

/// <summary>An action the host runs by itself at a set time (only while the host app is open).</summary>
public sealed class ScheduledAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Time { get; set; } = "08:00";                       // HH:mm, local time
    public List<string> Days { get; set; } = new() { "Mon", "Tue", "Wed", "Thu", "Fri" };
    public string Action { get; set; } = "lock";                     // lock | unlock | shutdown | restart | wake | message | command
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";                            // message text, or the command line for "command"
    public string Room { get; set; } = "";                            // target room, or empty to use Addresses
    /// <summary>Every saved computer, whatever its room; Room and Addresses are then not used.</summary>
    public bool AllComputers { get; set; }
    /// <summary>
    /// Computers that are off at the time get the action as soon as they are online, until the next time comes (each computer once per
    /// run). Only for actions harmless at any hour; see ScheduleRuns.CatchUpActions.
    /// </summary>
    public bool CatchUp { get; set; }
    /// <summary>Only the first of the chosen weekdays in each month (the first Monday, say) instead of every week.</summary>
    public bool Monthly { get; set; }
    public List<string> Addresses { get; set; } = new();
    public string LastRunDate { get; set; } = "";                     // yyyy-MM-dd, so it runs once a day
}

/// <summary>A saved set of Appearance settings the teacher can load into the Appearance window (see the Appearance module).</summary>
public sealed class AppearancePresetRecord
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
}

public sealed class WindowsUpdateSnapshot
{
    public string Address { get; set; } = "";
    public string Computer { get; set; } = "";
    public string CheckedUtc { get; set; } = "";
    public string Status { get; set; } = "";
    public List<WindowsUpdateSnapshotItem> Updates { get; set; } = new();
}

public sealed class WindowsUpdateSnapshotItem
{
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
}

public static class FileTransferStatuses
{
    public const string Delivered = "Delivered";
    public const string UndoUnavailable = "Delivered - undo unavailable until Client update";
    public const string Undone = "Undone";
    public const string UndoFailed = "Undo failed";
}

public sealed class FileTransferRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string Computer { get; set; } = "";
    public string Address { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Status { get; set; } = FileTransferStatuses.Delivered;
    public string StatusDetails { get; set; } = "";
    public string UndoneUtc { get; set; } = "";
    public bool CanUndo => (Status == FileTransferStatuses.Delivered || Status == FileTransferStatuses.UndoFailed) &&
                           Sha256.Length == 64 && DestinationPath.Length > 0;
}

public sealed class SavedProgramLaunch
{
    public string Program { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public sealed class WindowFilterSettings
{
    public string Text { get; set; } = "";
    public Dictionary<string, string> Selections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Table columns the user hid in this window; unlisted columns stay visible after an upgrade.</summary>
    public List<string> HiddenColumns { get; set; } = new();

    public string Selection(string key, string fallback) =>
        Selections.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
}
