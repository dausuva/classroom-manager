// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>One entry of the "Discovery subnet" choice: what the teacher reads and the key that is saved.</summary>
public sealed record SubnetChoice(string Label, string Key);

/// <summary>What the Settings dialog collected, as plain values. Nothing here knows about windows.</summary>
public sealed record HostSettingsInput(
    string DefaultKeyName,
    string DefaultKey,
    string TcpPort,
    string UdpPort,
    string SubnetLabel,
    bool AutoEnrol,
    bool RunSchedules,
    bool RunSchedulesWhenClosed,
    bool AutoRefresh,
    bool CrashLogging,
    bool CheckForUpdates,
    bool ActivityLog,
    string ActivityLogKeepLabel);

/// <summary>The side effects of saving Host settings that reach outside the settings object.</summary>
public interface IHostSettingsEffects
{
    /// <summary>Creates or removes the Windows Task Scheduler task for schedules; returns a problem text, or null when it worked.</summary>
    string? ApplyBackgroundSchedules(bool whenClosed);

    void SetCrashLogging(bool enabled);

    /// <summary>Deletes activity log files older than the retention period.</summary>
    void RemoveOldActivityLogs(int keepDays);
}

/// <summary>What <see cref="HostSettingsEditor.Apply"/> wants the window to tell the teacher.</summary>
public sealed record HostSettingsApplied(string? LogMessage, string? Warning);

/// <summary>
/// Edit, validation and apply rules for the Settings dialog (T-222, packet MW-D). The dialog only collects
/// <see cref="HostSettingsInput"/> and the window refreshes itself afterwards; the rules live here so they can be tested.
/// </summary>
public static class HostSettingsEditor
{
    /// <summary>The automatic choice, every connected subnet, and the saved subnet when it is no longer connected.</summary>
    public static IReadOnlyList<SubnetChoice> SubnetChoices(IEnumerable<(string DisplayName, string Key)> connected, string savedSubnet)
    {
        var choices = new List<SubnetChoice> { new("Automatic - prefer physical Ethernet LAN, then Wi-Fi", "") };
        choices.AddRange(connected.Select(subnet => new SubnetChoice(subnet.DisplayName, subnet.Key)));
        if (savedSubnet.Length > 0 && choices.All(choice => !choice.Key.Equals(savedSubnet, StringComparison.OrdinalIgnoreCase)))
            choices.Add(new SubnetChoice($"Unavailable saved subnet - {savedSubnet}", savedSubnet));
        return choices;
    }

    /// <summary>Index of the saved subnet in <paramref name="choices"/>; the automatic choice when it is not there.</summary>
    public static int SelectedSubnetIndex(IReadOnlyList<SubnetChoice> choices, string savedSubnet)
    {
        for (var i = 0; i < choices.Count; i++)
            if (choices[i].Key.Equals(savedSubnet, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary>The reason the input cannot be saved, or null. Nothing is changed.</summary>
    public static string? Validate(HostSettings settings, HostSettingsInput input)
    {
        if (input.DefaultKeyName.Length == 0) return "Enter a name for the default lab key.";
        if (input.DefaultKey.Length > 0 && !SharedKey.IsAcceptable(input.DefaultKey, out var reason)) return reason;
        if (settings.AdditionalHostKeys.Any(key =>
                key.Name.Equals(input.DefaultKeyName, StringComparison.OrdinalIgnoreCase) || key.Psk == input.DefaultKey))
            return "The default lab name and shared key must be different from every additional lab key.";
        return null;
    }

    /// <summary>
    /// Copies validated input into <paramref name="settings"/>. A port that is not a number from 1 to 65535 keeps the old one.
    /// The caller saves the settings and refreshes the window afterwards.
    /// </summary>
    public static HostSettingsApplied Apply(HostSettings settings, HostSettingsInput input,
        IReadOnlyList<SubnetChoice> subnetChoices, IHostSettingsEffects effects)
    {
        settings.AutoEnrolNewComputers = input.AutoEnrol;
        settings.RunSchedules = input.RunSchedules;

        string? log = null, warning = null;
        var whenClosed = input.RunSchedulesWhenClosed && settings.RunSchedules;
        if (whenClosed != settings.RunSchedulesWhenClosed)
        {
            var problem = effects.ApplyBackgroundSchedules(whenClosed);
            if (problem is null)
            {
                settings.RunSchedulesWhenClosed = whenClosed;
                log = whenClosed ? "Schedules now also run when the Host is closed (Windows Task Scheduler, every minute)."
                                 : "Schedules run only while the Host is open.";
            }
            else warning = problem;
        }

        settings.AutoRefreshEnabled = input.AutoRefresh;
        settings.DefaultKeyName = input.DefaultKeyName;
        settings.Psk = input.DefaultKey;
        if (int.TryParse(input.TcpPort, out var tcp) && tcp is > 0 and < 65536) settings.TcpPort = tcp;
        if (int.TryParse(input.UdpPort, out var udp) && udp is > 0 and < 65536) settings.UdpPort = udp;
        settings.DiscoverySubnet = subnetChoices.FirstOrDefault(choice => choice.Label == input.SubnetLabel)?.Key ?? "";

        settings.CrashLoggingEnabled = input.CrashLogging;
        effects.SetCrashLogging(settings.CrashLoggingEnabled);
        settings.CheckForUpdates = input.CheckForUpdates;
        settings.ActivityLogEnabled = input.ActivityLog;
        settings.ActivityLogKeepDays = ActivityLogFile.ParseRetention(input.ActivityLogKeepLabel);
        // A shorter period takes effect now; switching the log off keeps the files until they reach that age.
        effects.RemoveOldActivityLogs(settings.ActivityLogKeepDays);
        return new HostSettingsApplied(log, warning);
    }
}
