// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;

namespace ClassroomManager.Host;

/// <summary>The Settings dialog (T-222, packet MW-D): builds the form and reads it back. The rules are in <see cref="HostSettingsEditor"/>.</summary>
static class HostSettingsDialog
{
    public static FormDialog Build(Window owner, HostSettings settings, IReadOnlyList<SubnetChoice> subnetChoices)
    {
        var dialog = new FormDialog("Settings", owner)
            .Note("The default shared key must be identical on its client computers. Use Lab keys in the Settings tab to add independently keyed classrooms. " +
                  "Network discovery sends only to the selected connected IPv4 subnet. Automatic mode prefers a physical Ethernet LAN, then Wi-Fi, and avoids VPN or virtual adapters when possible.")
            .Text("keyName", "Default lab name", settings.NormalizedDefaultKeyName())
            .Text("psk", "Default shared key", settings.Psk, password: true)
            .Text("tcp", "Client TCP port", settings.TcpPort.ToString())
            .Text("udp", "Discovery UDP port", settings.UdpPort.ToString())
            .Combo("subnet", "Discovery subnet", subnetChoices.Select(choice => choice.Label).ToArray(),
                HostSettingsEditor.SelectedSubnetIndex(subnetChoices, settings.DiscoverySubnet))
            .Check("autoEnrol", "Add and approve new computers by themselves: a newly installed Client announces its certificate, and only to a Host with this lab's key", settings.AutoEnrolNewComputers)
            .Check("sched", "Run scheduled actions from this computer (turn off on a second host so they don't run twice)", settings.RunSchedules)
            .Check("schedClosed", "Run schedules also when the Host is closed (a Windows Task Scheduler task of this Windows user, while you are signed in)",
                settings.RunSchedulesWhenClosed)
            .Check("autoRefresh", "Automatically refresh assigned computers every 30 seconds", settings.AutoRefreshEnabled)
            .Check("crashLogging", "Capture Host crash details locally", settings.CrashLoggingEnabled);
        // A packaged Host is updated by Windows and never looks for a newer version itself, so the choice is not offered.
        if (FirstLaunchNotice.OffersUpdateCheck(ClassroomManager.Shared.PackageIdentity.IsPackaged))
            dialog.Check("updates", "Look once a day whether a newer version is published (contacts github.com; nothing is downloaded)", settings.CheckForUpdates);
        dialog
            .Check("activityLog", "Keep an activity log on this computer (every line of the Log view, one file per day)", settings.ActivityLogEnabled)
            .Combo("activityLogDays", "Keep activity log files for", ActivityLogFile.RetentionChoices.Select(ActivityLogFile.RetentionLabel).ToArray(),
                Math.Max(0, Array.IndexOf(ActivityLogFile.RetentionChoices, settings.ActivityLogKeepDays)));
        BuiltInModules.AddSettings(dialog, settings);
        dialog.ShowWhenChecked(BuiltInModules.Automation, "sched", "schedClosed");
        return dialog;
    }

    public static HostSettingsInput Read(FormDialog dialog) => new(
        DefaultKeyName: dialog.S("keyName"),
        DefaultKey: dialog.S("psk"),
        TcpPort: dialog.S("tcp"),
        UdpPort: dialog.S("udp"),
        SubnetLabel: dialog.S("subnet"),
        AutoEnrol: dialog.B("autoEnrol"),
        RunSchedules: dialog.B("sched"),
        RunSchedulesWhenClosed: dialog.B("schedClosed"),
        AutoRefresh: dialog.B("autoRefresh"),
        CrashLogging: dialog.B("crashLogging"),
        CheckForUpdates: !ClassroomManager.Shared.PackageIdentity.IsPackaged && dialog.B("updates"),
        ActivityLog: dialog.B("activityLog"),
        ActivityLogKeepLabel: dialog.S("activityLogDays"));
}
