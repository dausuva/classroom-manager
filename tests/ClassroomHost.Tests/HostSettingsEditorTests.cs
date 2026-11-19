// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The edit, validation and apply rules of the Settings dialog (T-222, packet MW-D).</summary>
public sealed class HostSettingsEditorTests
{
    const string GoodKey = "correct-horse-battery";

    sealed class Effects : IHostSettingsEffects
    {
        public string? ScheduleProblem;
        public List<bool> ScheduleCalls { get; } = [];
        public List<bool> CrashCalls { get; } = [];
        public List<int> RetentionCalls { get; } = [];

        public string? ApplyBackgroundSchedules(bool whenClosed) { ScheduleCalls.Add(whenClosed); return ScheduleProblem; }
        public void SetCrashLogging(bool enabled) => CrashCalls.Add(enabled);
        public void RemoveOldActivityLogs(int keepDays) => RetentionCalls.Add(keepDays);
    }

    static HostSettingsInput Input(Func<HostSettingsInput, HostSettingsInput>? change = null)
    {
        var input = new HostSettingsInput("Lab A", GoodKey, "5050", "5051", "Automatic - prefer physical Ethernet LAN, then Wi-Fi",
            AutoEnrol: false, RunSchedules: true, RunSchedulesWhenClosed: false, AutoRefresh: true, CrashLogging: true,
            CheckForUpdates: false, ActivityLog: true, ActivityLogKeepLabel: "30 days");
        return change is null ? input : change(input);
    }

    static IReadOnlyList<SubnetChoice> Choices(string saved = "") =>
        HostSettingsEditor.SubnetChoices([("Ethernet 10.0.0.0/24", "10.0.0.0/24"), ("Wi-Fi 192.168.1.0/24", "192.168.1.0/24")], saved);

    [Fact]
    public void Subnet_choices_start_with_automatic_and_list_every_connected_subnet()
    {
        var choices = Choices();
        Assert.Equal(["", "10.0.0.0/24", "192.168.1.0/24"], choices.Select(choice => choice.Key));
        Assert.StartsWith("Automatic", choices[0].Label);
    }

    [Fact]
    public void A_saved_subnet_that_is_no_longer_connected_stays_selectable()
    {
        var choices = Choices("172.16.0.0/16");
        Assert.Equal("172.16.0.0/16", choices[^1].Key);
        Assert.Equal("Unavailable saved subnet - 172.16.0.0/16", choices[^1].Label);
        Assert.Equal(choices.Count - 1, HostSettingsEditor.SelectedSubnetIndex(choices, "172.16.0.0/16"));
    }

    [Fact]
    public void A_connected_saved_subnet_is_not_listed_twice_and_matches_without_regard_to_case()
    {
        var choices = Choices("192.168.1.0/24");
        Assert.Equal(3, choices.Count);
        Assert.Equal(2, HostSettingsEditor.SelectedSubnetIndex(choices, "192.168.1.0/24"));
        Assert.Equal(1, HostSettingsEditor.SelectedSubnetIndex(Choices(), "10.0.0.0/24"));
    }

    [Fact]
    public void An_unknown_saved_subnet_selects_automatic()
    {
        Assert.Equal(0, HostSettingsEditor.SelectedSubnetIndex(Choices(), "unknown"));
        Assert.Equal(0, HostSettingsEditor.SelectedSubnetIndex(Choices(), ""));
    }

    [Fact]
    public void Valid_input_has_no_problem()
    {
        Assert.Null(HostSettingsEditor.Validate(new HostSettings(), Input()));
    }

    [Fact]
    public void An_empty_lab_name_is_refused()
    {
        Assert.Equal("Enter a name for the default lab key.", HostSettingsEditor.Validate(new HostSettings(), Input(i => i with { DefaultKeyName = "" })));
    }

    [Fact]
    public void A_key_the_shared_key_rules_refuse_is_refused_with_their_reason()
    {
        var problem = HostSettingsEditor.Validate(new HostSettings(), Input(i => i with { DefaultKey = "short" }));
        Assert.Equal($"The key must be at least {ClassroomManager.Shared.SharedKey.MinLength} characters.", problem);
    }

    [Fact]
    public void An_empty_key_is_allowed_so_a_Host_without_keys_can_still_save_other_settings()
    {
        Assert.Null(HostSettingsEditor.Validate(new HostSettings(), Input(i => i with { DefaultKey = "" })));
    }

    [Theory]
    [InlineData("lab a", "another-long-secret-1")]   // same name, different case
    [InlineData("Other", GoodKey)]                  // same key
    public void The_default_key_may_not_repeat_the_name_or_key_of_an_additional_lab(string name, string key)
    {
        var settings = new HostSettings { AdditionalHostKeys = [new NamedHostKey { Name = "Lab A", Psk = GoodKey }] };
        var problem = HostSettingsEditor.Validate(settings, Input(i => i with { DefaultKeyName = name, DefaultKey = key }));
        Assert.Equal("The default lab name and shared key must be different from every additional lab key.", problem);
    }

    [Fact]
    public void Validation_changes_nothing()
    {
        var settings = new HostSettings { TcpPort = 6000, Psk = "old-old-old-old-key" };
        Assert.NotNull(HostSettingsEditor.Validate(settings, Input(i => i with { DefaultKeyName = "", TcpPort = "7000" })));
        Assert.Equal(6000, settings.TcpPort);
        Assert.Equal("old-old-old-old-key", settings.Psk);
    }

    [Fact]
    public void Apply_copies_every_value_and_runs_the_crash_log_and_retention_effects()
    {
        var settings = new HostSettings();
        var effects = new Effects();
        var choices = Choices();
        var input = Input(i => i with
        {
            TcpPort = "6060", UdpPort = "6061", SubnetLabel = "Wi-Fi 192.168.1.0/24", AutoEnrol = true, AutoRefresh = false,
            CrashLogging = false, CheckForUpdates = true, ActivityLog = false, ActivityLogKeepLabel = "7 days",
        });

        var applied = HostSettingsEditor.Apply(settings, input, choices, effects);

        Assert.Equal("Lab A", settings.DefaultKeyName);
        Assert.Equal(GoodKey, settings.Psk);
        Assert.Equal(6060, settings.TcpPort);
        Assert.Equal(6061, settings.UdpPort);
        Assert.Equal("192.168.1.0/24", settings.DiscoverySubnet);
        Assert.True(settings.AutoEnrolNewComputers);
        Assert.False(settings.AutoRefreshEnabled);
        Assert.False(settings.CrashLoggingEnabled);
        Assert.True(settings.CheckForUpdates);
        Assert.False(settings.ActivityLogEnabled);
        Assert.Equal(7, settings.ActivityLogKeepDays);
        Assert.Equal([false], effects.CrashCalls);
        Assert.Equal([7], effects.RetentionCalls);
        Assert.Null(applied.LogMessage);
        Assert.Null(applied.Warning);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    public void A_port_that_is_not_a_valid_number_keeps_the_old_one(string port)
    {
        var settings = new HostSettings { TcpPort = 6000, UdpPort = 6001 };
        HostSettingsEditor.Apply(settings, Input(i => i with { TcpPort = port, UdpPort = port }), Choices(), new Effects());
        Assert.Equal(6000, settings.TcpPort);
        Assert.Equal(6001, settings.UdpPort);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    public void The_ports_at_both_ends_of_the_valid_range_are_accepted(string port, int expected)
    {
        var settings = new HostSettings();
        HostSettingsEditor.Apply(settings, Input(i => i with { TcpPort = port, UdpPort = port }), Choices(), new Effects());
        Assert.Equal(expected, settings.TcpPort);
        Assert.Equal(expected, settings.UdpPort);
    }

    [Fact]
    public void A_subnet_label_that_is_not_offered_selects_automatic()
    {
        var settings = new HostSettings { DiscoverySubnet = "10.0.0.0/24" };
        HostSettingsEditor.Apply(settings, Input(i => i with { SubnetLabel = "not in the list" }), Choices(), new Effects());
        Assert.Equal("", settings.DiscoverySubnet);
    }

    [Fact]
    public void Turning_on_schedules_when_closed_registers_the_task_and_says_so()
    {
        var settings = new HostSettings();
        var effects = new Effects();

        var applied = HostSettingsEditor.Apply(settings, Input(i => i with { RunSchedulesWhenClosed = true }), Choices(), effects);

        Assert.Equal([true], effects.ScheduleCalls);
        Assert.True(settings.RunSchedulesWhenClosed);
        Assert.Equal("Schedules now also run when the Host is closed (Windows Task Scheduler, every minute).", applied.LogMessage);
        Assert.Null(applied.Warning);
    }

    [Fact]
    public void Turning_them_off_removes_the_task_and_says_so()
    {
        var settings = new HostSettings { RunSchedules = true, RunSchedulesWhenClosed = true };
        var effects = new Effects();

        var applied = HostSettingsEditor.Apply(settings, Input(i => i with { RunSchedulesWhenClosed = false }), Choices(), effects);

        Assert.Equal([false], effects.ScheduleCalls);
        Assert.False(settings.RunSchedulesWhenClosed);
        Assert.Equal("Schedules run only while the Host is open.", applied.LogMessage);
    }

    [Fact]
    public void A_task_that_cannot_be_registered_leaves_the_setting_off_and_returns_the_problem()
    {
        var settings = new HostSettings();
        var effects = new Effects { ScheduleProblem = "Access is denied." };

        var applied = HostSettingsEditor.Apply(settings, Input(i => i with { RunSchedulesWhenClosed = true }), Choices(), effects);

        Assert.False(settings.RunSchedulesWhenClosed);
        Assert.Equal("Access is denied.", applied.Warning);
        Assert.Null(applied.LogMessage);
    }

    [Fact]
    public void Schedules_cannot_run_when_closed_while_they_are_switched_off()
    {
        var settings = new HostSettings { RunSchedules = true, RunSchedulesWhenClosed = true };
        var effects = new Effects();

        HostSettingsEditor.Apply(settings, Input(i => i with { RunSchedules = false, RunSchedulesWhenClosed = true }), Choices(), effects);

        Assert.False(settings.RunSchedules);
        Assert.Equal([false], effects.ScheduleCalls);
        Assert.False(settings.RunSchedulesWhenClosed);
    }

    [Fact]
    public void An_unchanged_when_closed_choice_does_not_touch_Task_Scheduler()
    {
        var settings = new HostSettings { RunSchedules = true, RunSchedulesWhenClosed = true };
        var effects = new Effects();

        var applied = HostSettingsEditor.Apply(settings, Input(i => i with { RunSchedulesWhenClosed = true }), Choices(), effects);

        Assert.Empty(effects.ScheduleCalls);
        Assert.Null(applied.LogMessage);
        Assert.True(settings.RunSchedulesWhenClosed);
    }
}
