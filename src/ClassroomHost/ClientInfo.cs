// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClassroomManager.Modules;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class ClientInfo : INotifyPropertyChanged
{
    string _address = "";
    string _name = "";
    string _alias = "";
    string _user = "";
    string _status = "Unknown";
    string _room = "";
    string _level = "";
    string _ping = "";
    string _clock = "";
    string _keyName = "";
    string _uptime = "";
    string _version = "";
    string _appVersion = "";
    string _windowsVersion = "";
    string _interfaceLanguage = "";
    string _openWindows = "";
    int? _blockedWebsiteCount;
    string _lastSeen = "";
    bool _isSelected;

    /// <summary>IP address or host name used to connect.</summary>
    public string Address { get => _address; set => Set(ref _address, value); }
    public int Port { get; set; }
    public string ClientId { get; set; } = "";
    public string Mac { get; set; } = "";

    /// <summary>Fingerprint of the shared key this computer last accepted ("" = not known yet).</summary>
    public string KeyId { get; set; } = "";
    public string KeyName { get => _keyName; set => Set(ref _keyName, value); }
    /// <summary>"x86" or "x64": what the Client program was built for. Empty until the computer has answered; a Client from before the 32-bit build does not say and is x64.</summary>
    public string Architecture { get; set; } = ""; 
    /// <summary>Role the client granted the key this Host connects with ("Teacher" or "Admin"); empty until checked or on older clients.</summary>
    public string Role { get; set; } = "";
    /// <summary>Newest .NET Desktop Runtime the client reported: null when it did not say (older client or not checked), empty when it has none.</summary>
    public string? DesktopRuntime { get; set; }

    /// <summary>Number of host actions currently running on this computer (the background refresh leaves it alone meanwhile).</summary>
    public int BusyCount;

    /// <summary>Non-empty while a disruptive operation owns this client and all other sessions must stop.</summary>
    public string ExclusiveOperation { get; set; } = "";

    /// <summary>True after a status check got no connection at <see cref="Address"/> (off, asleep, or the address changed).</summary>
    public bool Unreachable { get; set; }

    /// <summary>Clock difference client - host in seconds, when known.</summary>
    public double? ClockOffsetSec { get; set; }

    /// <summary>Optional command names advertised by protocol 1.2+ clients.</summary>
    public HashSet<string> Capabilities { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool CapabilitiesAdvertised { get; set; }
    public bool ExamModeApplied { get; set; }
    public string OnlineBaseStatus { get; set; } = "Online";
    public string CompatibilityWarning { get; set; } = "";
    public string CompatibleOnlineStatus => OnlineBaseStatus + CompatibilityWarning;
    public bool SupportsCommand(string command) =>
        Protocol.SupportsCommand(command, Version, Capabilities, CapabilitiesAdvertised);

    /// <summary>
    /// The capabilities a module needs that this Client does not offer, by the same rule as <see cref="SupportsCommand"/>: what the Client
    /// announced in Ping, or for a Client that announces nothing its protocol version. Empty means the module can work with this computer.
    /// </summary>
    public IReadOnlyList<string> MissingForModule(ModuleDescriptor module) =>
        module.ClientCapabilities.Where(capability => !SupportsCommand(capability)).ToArray();

    /// <summary>The Client opens a website in a chosen browser and maximized (protocol 1.15).</summary>
    public bool SupportsWebsiteOptions => WebsiteLaunch.Supported(Version);

    /// <summary>
    /// What the Host calls this computer: the teacher's own label when there is one, else its Windows computer
    /// name. Setting it sets the Windows name the computer reported, never the label.
    /// </summary>
    public string Name
    {
        get => _alias.Length > 0 ? _alias : _name;
        set => ComputerName = value;
    }

    /// <summary>The computer's own Windows name, as it reported it.</summary>
    public string ComputerName
    {
        get => _name;
        set
        {
            if (_name == value) return;
            var before = Name;
            _name = value;
            Changed(nameof(ComputerName));
            if (Name != before) Changed(nameof(Name));
        }
    }

    /// <summary>A label only the Host knows; "" = none. The computer itself is not renamed.</summary>
    public string Alias
    {
        get => _alias;
        set
        {
            value = ComputerLabel.Clean(value);
            if (_alias == value) return;
            _alias = value;
            Changed(nameof(Alias));
            Changed(nameof(Name));
        }
    }
    public string User { get => _user; set => Set(ref _user, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Room { get => _room; set => Set(ref _room, value); }

    /// <summary>"ok" | "idle" | "warn" | "bad" | "" (offline/unknown); drives the coloured dot.</summary>
    public string Level { get => _level; set => Set(ref _level, value); }

    public string Ping { get => _ping; set => Set(ref _ping, value); }
    public string Clock { get => _clock; set => Set(ref _clock, value); }
    public string Uptime { get => _uptime; set => Set(ref _uptime, value); }
    public string Version { get => _version; set => Set(ref _version, value); }

    /// <summary>The client build/file version (what "Update client..." replaces), separate from the wire protocol Version above.</summary>
    public string AppVersion { get => _appVersion; set => Set(ref _appVersion, value); }
    public string WindowsVersion { get => _windowsVersion; set => Set(ref _windowsVersion, value); }
    public string InterfaceLanguage { get => _interfaceLanguage; set => Set(ref _interfaceLanguage, value); }
    public string OpenWindows { get => _openWindows; set => Set(ref _openWindows, value); }
    public int? BlockedWebsiteCount { get => _blockedWebsiteCount; set => Set(ref _blockedWebsiteCount, value); }
    public string LastSeen { get => _lastSeen; set => Set(ref _lastSeen, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (Equals(field, value)) return;
        field = value;
        Changed(prop);
    }

    void Changed(string? prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
