// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Modules;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>The Host's modules and the person's on/off choices for them. Not licensing or authorization.</summary>
public static class BuiltInModules
{
    public const string Reporting = "dausuva.reporting.inventory";
    public const string Browser = "dausuva.browser.inventory";
    public const string Automation = "dausuva.automation.scheduling";
    public const string StudentWork = "dausuva.student-work.backup";
    public const string Appearance = "dausuva.appearance.inventory";
    public const string Software = "dausuva.software.inventory";
    public const string Maintenance = "dausuva.maintenance.inventory";

    /// <summary>Where installed module packages live, next to the program.</summary>
    public static string ModulesFolder { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "modules");

    /// <summary>What the start-up applied from earlier installs, removals and rollbacks, for Settings and the command line.</summary>
    public static IReadOnlyList<string> PackageChanges { get; private set; } = [];

    /// <summary>Every module of this Host: those built in, and the installed packages that passed their checks.</summary>
    public static ModuleRegistry Registry { get; } = CreateRegistry();

    public static IReadOnlyList<(string Id, string Label)> Available =>
        Registry.Usable.Select(module => (module.Id, module.Name)).ToArray();

    static ModuleRegistry CreateRegistry()
    {
        var registry = new ModuleRegistry();
#if REPORTING_MODULE
        registry.AddBuiltIn(new ClassroomManager.Reporting.InventoryReportingModuleEntry());
#endif
#if BROWSER_INVENTORY_MODULE
        registry.AddBuiltIn(new ClassroomManager.BrowserInventory.BrowserInventoryModuleEntry());
#endif
        registry.AddBuiltIn(new ClassroomManager.Automation.SchedulingModuleEntry());
        registry.AddBuiltIn(new ClassroomManager.StudentWork.StudentBackupModuleEntry());
        registry.AddBuiltIn(new ClassroomManager.Appearance.AppearanceInventoryModuleEntry());
        registry.AddBuiltIn(new ClassroomManager.SoftwareInventory.SoftwareInventoryModuleEntry());
        registry.AddBuiltIn(new ClassroomManager.Maintenance.MaintenanceInventoryModuleEntry());
        // What was staged by an install, removal or rollback applies now, before any package code is loaded.
        PackageChanges = ModulePackageStore.ApplyPending(ModulesFolder);
        // Only packages signed by a signer in TrustedSigners load; any other is listed as refused with the reason.
        registry.AddPackages(ModuleDiscovery.Scan(ModulesFolder, new WinTrustSignatureVerifier(), TrustedSigners));
        registry.AttachAll(descriptor => ModuleBroker.Grant(descriptor, () => HostSettings.DataFolder, line => LogSink?.Invoke(line), TimeProvider.System));
        return registry;
    }

    /// <summary>Where the lines modules write go: the Host's activity log. The main window sets it; until then the lines are dropped.</summary>
    public static Action<string>? LogSink { get; set; }

    static string AdministratorSentence => "This module acts with administrator rights on the computers you select.";

    /// <summary>What a module asked for, in plain words for the person who can switch it off. Empty when it asks for nothing.</summary>
    public static IReadOnlyList<string> PortSentences(ModuleDescriptor descriptor) => descriptor.Ports.Select(port => port.Name switch
    {
        ModulePorts.Log => "This module writes lines to the activity log.",
        ModulePorts.Time => "This module reads the clock.",
        ModulePorts.StoragePrivate => "This module keeps small files of its own (up to 20 MB).",
        ModulePorts.ClientCall => "This module sends commands to the computers you select.",
        ModulePorts.ClientTransfer => "This module copies files from the computers you select.",
        ModulePorts.StorageSink => "This module saves files in a folder you chose for it.",
        ModulePorts.StorageSource => "This module can use a file you pick for it, without seeing where the file is.",
        ModulePorts.ClientUpload => "This module sends a file you picked to the computers you select.",
        _ => "This module asks for a service this Host does not know.",
    }).Concat(descriptor.SystemCommands.Count > 0 ? [AdministratorSentence] : []).ToArray();

    /// <summary>The Dausuva Classroom Lab Code Signing certificate: the same one that signs the Client and Host builds, so it is already the trust root for updates.</summary>
    public const string LabSigner = "C17E38C19C1166BEFC87D54A7EA1B551909498661DF832E16846A953AAF6F141";

    /// <summary>SHA-256 fingerprints of the signer certificates whose packages are accepted. Only Dausuva's own for now.</summary>
    public static IReadOnlySet<string> TrustedSigners { get; } = new HashSet<string>(StringComparer.Ordinal) { LabSigner };

    /// <summary>
    /// Modules that are part of the core: every build contains them (<c>ClassroomModules.props</c> cannot leave one out). For the person they are modules like
    /// the others: ticked in Host settings, off when unticked.
    /// </summary>
    public static IReadOnlySet<string> Core { get; } = new HashSet<string>(StringComparer.Ordinal) { Appearance };

    /// <summary>On in Host settings and not stopped by the broker (see <see cref="ModuleBreaker"/>).</summary>
    public static bool Enabled(HostSettings settings, string id) => Registry.IsEnabled(id, settings.DisabledModules) && !ModuleBreaker.IsTripped(id);

    public static string Disabled => Loc.T("This module is turned off in Host settings.");

    // Capture admission once per sweep. Accepted work drains normally; no leases/checkpoints are discarded.
    public static IEnumerable<ScheduledAction> AdmittedSchedules(HostSettings settings) =>
        !Enabled(settings, Automation) ? [] : settings.Schedules
            .Where(schedule => ActionOwner(schedule.Action) is not { } owner || Enabled(settings, owner)).ToArray();

    /// <summary>The module that provides a scheduled action, or null for an action the Host has itself.</summary>
    public static string? ActionOwner(string code) => Registry.ActionOwner(code);

    /// <summary>The scheduled actions the modules add, in registration order.</summary>
    public static IReadOnlyList<(string Code, string Label)> ModuleActions =>
        Registry.ScheduledActions.Select(item => (item.Action.Code, item.Action.Label)).ToArray();

    public static void AddSettings(FormDialog dialog, HostSettings settings)
    {
        dialog.Note("Built-in modules: changes apply to new operations. Work already started finishes normally. Saved schedules, backup checkpoints and recovery controls are kept. Re-enabling scheduling resumes eligible catch-up actions.");
        foreach (var refused in Registry.All.Where(module => !module.Usable))
            dialog.Note(Loc.T("Not loaded:") + " " + refused.Name + " - " + string.Join(" ", refused.Problems), preserveText: true);
        foreach (var module in Registry.Usable)
        {
            dialog.Check(module.Id, module.Name, Enabled(settings, module.Id));
            if (ModuleBreaker.Reason(module.Id) is { } stopped)
                dialog.Note(Loc.T("Switched off after repeated refused calls. Tick it to try again.") + " " + stopped, preserveText: true);
            if (module.Descriptor!.Icon.Count > 0) dialog.Art(module.Descriptor.Icon);
            var sentences = PortSentences(module.Descriptor);
            if (sentences.Count > 0)
                dialog.Note(string.Join(" ", sentences.Select(Loc.T)), preserveText: true);
        }
    }

    public static void ReadSettings(FormDialog dialog, HostSettings settings)
    {
        // Preserve unknown/absent module preferences for another build or downgrade.
        var disabled = (settings.DisabledModules ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var module in Available)
        {
            if (dialog.B(module.Id)) { disabled.Remove(module.Id); ModuleBreaker.Clear(module.Id); }
            // A module the broker stopped shows unticked; leaving it so does not turn it off for good.
            else if (!ModuleBreaker.IsTripped(module.Id)) disabled.Add(module.Id);
        }
        settings.DisabledModules = disabled.Order(StringComparer.Ordinal).ToList();
    }
}
