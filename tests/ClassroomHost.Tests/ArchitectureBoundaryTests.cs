// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void Manual_discovery_adapter_and_roster_are_not_replacement_windows_or_general_command_brokers()
    {
        foreach (var file in new[] { "ManualDiscoveryController.cs", "HostRoster.cs" })
        {
            var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", file));
            foreach (var forbidden in new[] { "MainWindow", "System.Windows", "Dispatcher", "ClientSession", "Cmd.", "MoveTrust", "ExamModeScope" })
                Assert.DoesNotContain(forbidden, source);
        }
        var shell = MainWindowSource.ReadAll();
        Assert.DoesNotContain("EnrolNewComputersAsync", shell);
        Assert.Contains("new ManualDiscoveryController(this, _settings, _roster, _discoveryWork, SaveComputers)", shell);
        Assert.Contains("_roster.Merge(found)", shell);
    }
    [Fact]
    public void Deployment_workflows_have_no_ui_wire_settings_or_filesystem_dependency()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "SoftwareDeploymentWorkflow.cs"));
        foreach (var type in new[] { "System.Windows", "System.IO", "System.Text.Json", "ClientSession", "ClientInfo", "MainWindow", "HostSettings", "Cmd" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
    }

    [Fact]
    public void Deployment_controller_is_not_a_replacement_MainWindow_and_cannot_update_the_Client()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "SoftwareDeploymentController.cs"));
        foreach (var type in new[] { "MainWindow", "SelfUpdate", "ClaimTrust", "StepUp", "Licence", "KeyUsed", "BuiltInModules" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
        Assert.DoesNotMatch(@"\bthis\s*[,)]", source);
        Assert.Contains("BulkOperationReviewDecision.Back", source);
        Assert.Contains("BulkOperationReviewDecision.Cancel", source);
        Assert.Contains("maxParallel: batch", source);
        Assert.Contains("warnings: operationWarning", source);
        var shell = MainWindowSource.ReadAll();
        Assert.Contains("await _softwareDeployment.InstallAsync(SelectedRaw())", shell);
        Assert.Contains("await _softwareDeployment.StagedUploadsAsync()", shell);
        Assert.Contains("async computers => await _softwareDeployment.InstallAsync(computers.ToList())", shell);
        Assert.DoesNotContain("new InstallSoftwareDialog", shell);
        Assert.DoesNotContain("RunInstallerWithHeartbeatAsync", shell);
        Assert.Contains("ClientBuildRuntime.Describe(file)", shell);
        Assert.Contains("Cmd.SelfUpdate", shell);
        Assert.Contains("allowExclusiveOperation: true", shell);
    }

    [Fact]
    public void Deployment_adapter_is_non_owning_and_never_executes_Client_self_update()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "ClientSoftwareDeployment.cs"));
        foreach (var forbidden in new[] { "session.Dispose", "Session.Open", "HostSettings", "Cmd.SelfUpdate", "Cmd.CmdRun" })
            Assert.DoesNotContain(forbidden, source);
        Assert.Contains("SoftwareDeploymentWorkflow.InstallAsync", source);
        Assert.Contains("SoftwareDeploymentWorkflow.ImportRegistryAsync", source);
        Assert.Contains("InstallerHeartbeatWorkflow.RunAsync", source);
        Assert.Contains("UploadIfDifferentSizeAsync", source);
    }

    [Fact]
    public void Appearance_feature_controller_has_no_MainWindow_back_reference_or_credential_recovery_authority()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "AppearanceFeatureController.cs"));
        foreach (var type in new[] { "MainWindow", "UserSignIn", "RecycleBinEmpty", "ClaimTrust", "StepUp", "Licence", "KeyUsed" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
        Assert.Contains("AppearanceOperationRunner", source);
        Assert.Contains("BuiltInModules.Enabled(_settings, BuiltInModules.Appearance)", source);
        Assert.Contains("if (new BulkOperationReviewWindow(_owner, review).Ask() != BulkOperationReviewDecision.Run) return;", source);
        Assert.Contains("undo: restore ? null", source);
    }

    [Fact]
    public void MainWindow_appearance_handlers_are_only_feature_controller_forwarders()
    {
        var source = MainWindowSource.ReadAll();
        foreach (var name in new[] { "Wallpaper", "WallpaperReset", "ScreenSaver", "ManagedWallpapers", "RecentWallpapers", "WindowsThemes", "WindowsPreferences", "Mouse", "MouseReset", "DesktopLayoutReset" })
            Assert.Matches($@"void {name}_Click\(object sender, RoutedEventArgs e\) => (?:await )?_appearance\.", source);
        Assert.DoesNotContain("new WallpaperLibraryWindow", source);
        Assert.DoesNotContain("new ScreenSaverChangeRequest", source);
        Assert.DoesNotContain("LoadScreenSaverAsync", source);
        Assert.Contains("async void SignInUser_Click", source);
        Assert.Contains("async void RecycleBinEmpty_Click", source);
    }

    [Fact]
    public void Appearance_command_adapter_is_non_owning_and_calls_the_independent_workflows()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "ClientAppearanceActions.cs"));
        Assert.DoesNotContain("session.Dispose", source);
        Assert.DoesNotContain("Session.Open", source);
        Assert.DoesNotContain("HostSettings", source);
        // The wallpaper sequencing lives in the module and runs through its ports; the adapter hands it a handle, never a path.
        Assert.Contains("new WallpaperOperations(run, run).ChangeAsync", source);
        Assert.DoesNotContain("session.UploadAsync", source.Split("ChangeScreenSaverAsync")[0]);
        Assert.Contains("AppearanceActionWorkflow.ChangeScreenSaverAsync", source);
        Assert.Contains("AppearanceActionWorkflow.ChangePreferencesAsync", source);
    }

    [Theory]
    [InlineData("StatusRefreshWorkflow.cs")]
    [InlineData("StatusProbe.cs")]
    [InlineData("DiscoveryWorkCoordinator.cs")]
    [InlineData("ManualDiscoveryWorkflow.cs")]
    [InlineData("StatusProjection.cs")]
    public void Status_workflows_have_only_framework_and_typed_port_dependencies(string file)
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", file));
        foreach (var type in new[] { "System.Windows", "System.Text.Json", "Dispatcher", "MainWindow", "ClientSession", "HostSettings", "ClientInfo", "Cmd" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
    }

    [Fact]
    public void Status_adapter_exposes_only_the_existing_three_read_commands_and_does_not_dispose_its_session()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "ClientStatusConnection.cs"));
        Assert.Equal(new[] { "Ping", "ProcWindows", "WindowsPreferencesGet" },
            Regex.Matches(source, @"\bCmd\.(\w+)").Select(match => match.Groups[1].Value).Order());
        Assert.DoesNotContain("session.Dispose", source);
        Assert.Equal(2, Regex.Matches(source, @"CallAtAsync\(Levels.User,").Count);
    }

    [Fact]
    public void Shell_scans_share_admission_and_retain_core_trust_exam_and_session_ownership()
    {
        var source = MainWindowSource.ReadAll();
        Assert.Contains("_manualDiscovery.ScanAsync(cancellationToken)", source);
        var workflow = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "ManualDiscoveryWorkflow.cs"));
        Assert.Contains("coordinator.TryBeginScan()", workflow);
        Assert.Contains("Closed += (_, _) => _discoveryWork.Dispose();", source);
        Assert.Contains("_discoveryWork.FollowAsync(new AddressFollowRuntime(this), cancellationToken)", source);
        Assert.Contains("AddressFollowTargets.Plan(missing, owner._clients, owner.ClientInSelectedNetwork", source);
        Assert.Contains("Equals(_network, owner._networkScope)", source);
        Assert.Contains("var merged = owner.AddDiscoveredClient(answer);", source);
        Assert.Contains("owner._settings.MoveTrust(merged.Mac, merged.Address", source);
        Assert.Contains("owner._exam.FollowAddress(merged.ClientId, oldAddress, merged.Address);", source);
        var exam = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "ExamModeWorkflow.cs"));
        Assert.Contains("ExamModeScope.Remove(scope, \"\", oldAddress);", exam);
        Assert.Contains("owner.CheckOneAsync(target.Client)", source);
        Assert.DoesNotContain("_lastSearchForMoved", source);
        Assert.DoesNotContain("_searchingForMoved", source);
    }

    [Fact]
    public void Shell_keeps_identity_checks_before_trust_and_recovery_and_uses_the_extracted_probe()
    {
        var source = MainWindowSource.ReadAll();
        var start = source.IndexOf("    async Task CheckOneAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("    static void SetUnreachable(", start, StringComparison.Ordinal);
        var check = source[start..end];
        var previous = -1;
        foreach (var token in new[] { "using var s = await ClientSession.OpenAsync", "_statusProbe.PingAsync", "if (!IsCurrent()) return;",
            "AddressFollower.AnswersAsAnother", "_settings.ClaimTrust", "KeyRevocations.ApplyAsync", "_statusProbe.InterfaceLanguageAsync",
            "_statusProbe.OpenWindowsAsync", "presentation.ApplyOnline", "_exam.ReconcileAsync", "SharedKey.Fingerprint", "_keys.PushAsync" })
        {
            var index = check.IndexOf(token, previous + 1, StringComparison.Ordinal);
            Assert.True(index > previous, token);
            previous = index;
        }
        Assert.DoesNotContain("Cmd.Ping", check);
        Assert.DoesNotContain("Cmd.WindowsPreferencesGet,", check);
        Assert.DoesNotContain("Cmd.ProcWindows,", check);
        Assert.Contains("new StatusPresentationTarget(endpoint, _clients, Dispatcher.VerifyAccess)", check);
        Assert.Contains("presentation.ApplyMetadata(StatusProjection.Metadata", check);
        Assert.DoesNotContain("c.Capabilities.Clear()", check);
    }

    [Fact]
    public void Status_presentation_adapter_has_no_transport_settings_or_implicit_dispatcher_dependency()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "StatusPresentationTarget.cs"));
        foreach (var type in new[] { "System.Windows", "System.Text.Json", "Dispatcher", "ClientSession", "HostSettings", "Cmd", "Loc" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
        Assert.Contains("verifyAccess();", source);
        Assert.Contains("target.Matches() && roster.Contains(target.Client)", source);
    }

    /// <summary>A module project may reference the module contract and nothing else.</summary>
    static void OnlyTheModuleContract(XDocument project) =>
        Assert.All(project.Descendants("ProjectReference"), reference => Assert.EndsWith("ClassroomManager.Modules.Contract.csproj", (string?)reference.Attribute("Include")));

    [Theory]
    [InlineData("Appearance", "ClassroomManager.Appearance")]
    [InlineData("SoftwareInventory", "ClassroomManager.SoftwareInventory")]
    [InlineData("Maintenance", "ClassroomManager.Maintenance")]
    public void Inventory_projection_module_has_no_application_wire_or_io_dependency(string folder, string assembly)
    {
        var root = Path.Combine(StringExtractor.RepositoryRoot(), "src", "Modules", folder);
        var project = XDocument.Load(Path.Combine(root, assembly + ".csproj"));
        OnlyTheModuleContract(project);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("FrameworkReference"));
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(IsSource))
        {
            // A module that talks to its ports (Maintenance, SoftwareInventory) reads and writes JSON text; the others only project values they are given.
            var forbidden = new List<string> { "System.Windows", "System.IO", "ClassroomManager.Shared", "ClassroomManager.Host", "ClientSession", "HostSettings", "Cmd",
                "FileStream", "FileInfo", "DirectoryInfo", "Socket", "TcpClient", "HttpClient", "Process", "Registry", "Assembly" };
            if (folder is not ("Maintenance" or "SoftwareInventory" or "Appearance")) forbidden.Add("System.Text.Json");
            foreach (var type in forbidden)
                Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", File.ReadAllText(file));
            Assert.DoesNotMatch(@"\b(File|Directory)\.", File.ReadAllText(file));
        }
    }

    [Fact]
    public void Roster_persistence_has_no_window_or_transport_dependency()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "HostRosterPersistence.cs"));
        foreach (var type in new[] { "System.Windows", "Dispatcher", "MainWindow", "ClientSession", "MessageBox", "FormDialog" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
    }

    [Fact]
    public void Settings_edit_rules_have_no_window_or_transport_dependency()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "HostSettingsEditor.cs"));
        foreach (var type in new[] { "System.Windows", "Dispatcher", "MainWindow", "ClientSession", "MessageBox", "FormDialog" })
            Assert.DoesNotMatch($@"{Regex.Escape(type)}", source);
    }

    // Changes to this allowlist require a deliberate architecture decision, not a silent dependency.
    static readonly Dictionary<string, string[]> References = new()
    {
        ["ClassroomManager.Shared"] = [],
        ["ClassroomHost"] = ["ClassroomManager.Shared", "ClassroomManager.Reporting", "ClassroomManager.BrowserInventory", "ClassroomManager.Automation", "ClassroomManager.StudentWork", "ClassroomManager.Appearance", "ClassroomManager.SoftwareInventory", "ClassroomManager.Maintenance", "ClassroomManager.Modules.Runtime"],
        ["ClassroomManager.Maintenance"] = ["ClassroomManager.Modules.Contract"],
        ["ClassroomManager.SoftwareInventory"] = ["ClassroomManager.Modules.Contract"],
        ["ClassroomManager.Appearance"] = ["ClassroomManager.Modules.Contract"],
        ["ClassroomManager.StudentWork"] = ["ClassroomManager.Modules.Contract"],
        ["ClassroomManager.Automation"] = ["ClassroomManager.Modules.Contract"],
        ["ClassroomManager.BrowserInventory"] = ["ClassroomManager.Modules.Contract"],
        ["ClassroomManager.Reporting"] = ["ClassroomManager.Modules.Contract"],
        // The contract is what a module author references: plain types only. The runtime checks, loads and registers packages and knows no Host or Client type.
        ["ClassroomManager.Modules.Contract"] = [],
        ["ClassroomManager.Modules.Runtime"] = ["ClassroomManager.Modules.Contract"],
        ["ClassroomClient"] = ["ClassroomManager.Shared"],
        ["ClassroomClientHealth"] = ["ClassroomManager.Shared"],
        // The seller's desk (T-213) shares only the licence format with the Host; it never reads Host settings or keys.
        ["ClassroomDesk"] = ["ClassroomManager.Shared"],
        ["MatrixGeometryScreensaver"] = [],
        // Legacy exceptions: these tools currently reuse Host settings/transport implementations.
        ["ClassroomHealth"] = ["ClassroomHost"],
        ["ClassroomCleanup"] = ["ClassroomHost"],
        ["ClassroomHostCli"] = ["ClassroomHost"],
        ["ClassroomTrustMigration"] = ["ClassroomHost"],
    };

    [Fact]
    public void Production_project_references_follow_the_documented_graph()
    {
        var root = Path.Combine(StringExtractor.RepositoryRoot(), "src");
        var projects = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories);
        // The seller's desk and the extra apps (Health, Client Health, trust migration, screen saver, command-line wrapper) are not in the
        // public source (T-219): they may be absent, but a project that is present must be one of the documented ones.
        var present = projects.Select(Path.GetFileNameWithoutExtension).ToHashSet();
        string[] leftOutOfPublicSource = ["ClassroomDesk", "ClassroomCleanup", "ClassroomHealth", "ClassroomClientHealth", "ClassroomTrustMigration", "MatrixGeometryScreensaver", "ClassroomHostCli"];
        Assert.Equal(References.Keys.Where(name => !leftOutOfPublicSource.Contains(name) || present.Contains(name)).Order(), present.Order());
        foreach (var project in projects)
        {
            var references = XDocument.Load(project).Descendants("ProjectReference")
                .Select(node => Path.GetFileNameWithoutExtension(node.Attribute("Include")!.Value)).Order();
            Assert.Equal(References[Path.GetFileNameWithoutExtension(project)].Order(), references);
        }
    }

    [Fact]
    public void Shared_has_no_desktop_framework_or_application_assembly_dependency()
    {
        var root = Path.Combine(StringExtractor.RepositoryRoot(), "src", "Shared");
        var project = XDocument.Load(Path.Combine(root, "ClassroomManager.Shared.csproj"));
        Assert.DoesNotContain(project.Descendants(), node => node.Name.LocalName is "UseWPF" or "UseWindowsForms" && node.Value == "true");
        Assert.Empty(project.Descendants("FrameworkReference"));
        Assert.Empty(project.Descendants("Reference"));
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(IsSource))
            Assert.DoesNotMatch(@"(?m)^\s*(?:global\s+)?using\s+(?:\w+\s*=\s*)?(?:global::)?(?:System\.Windows|ClassroomManager\.(?:Host|Client))(?:[.;\s])", File.ReadAllText(file));
    }

    [Fact]
    public void Extracted_browser_workflow_remains_independent_of_ui_and_concrete_transport()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "BrowserInventoryLoader.cs"));
        foreach (var type in new[] { "System.Windows", "ClientInfo", "ClientSession", "FormDialog", "MainWindow" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
    }

    [Fact]
    public void Browser_inventory_module_has_no_wire_ui_settings_or_application_import()
    {
        var root = Path.Combine(StringExtractor.RepositoryRoot(), "src", "Modules", "BrowserInventory");
        var project = XDocument.Load(Path.Combine(root, "ClassroomManager.BrowserInventory.csproj"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("FrameworkReference"));
        OnlyTheModuleContract(project);
        Assert.DoesNotContain(project.Descendants(), node => node.Name.LocalName is "UseWPF" or "UseWindowsForms" && node.Value == "true");
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(IsSource))
        {
            var source = File.ReadAllText(file);
            foreach (var type in new[] { "System.Windows", "System.Text.Json", "ClassroomManager.Shared", "ClientSession", "HostSettings", "SecureChannel", "Cmd" })
                Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
        }
    }

    static bool IsSource(string path) => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");

    [Fact]
    public void Extracted_schedule_workflow_has_no_ui_or_concrete_connection_dependency()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "ScheduleWorkflow.cs"));
        foreach (var type in new[] { "System.Windows", "ClientSession", "HostSettings", "FormDialog", "MainWindow" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
    }

    [Fact]
    public void Scheduling_module_has_no_application_wire_or_persistent_storage_dependency()
    {
        var root = Path.Combine(StringExtractor.RepositoryRoot(), "src", "Modules", "Automation");
        var project = XDocument.Load(Path.Combine(root, "ClassroomManager.Automation.csproj"));
        Assert.Empty(project.Descendants("PackageReference"));
        OnlyTheModuleContract(project);
        Assert.Empty(project.Descendants("FrameworkReference"));
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(IsSource))
            foreach (var type in new[] { "System.Windows", "System.IO", "System.Text.Json", "ClassroomManager.Host", "ClassroomManager.Shared",
                "ClientSession", "HostSettings", "SavedComputer", "ScheduledAction", "CliResult", "ScheduleRuns", "ScheduleOwnership", "Cmd" })
                Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", File.ReadAllText(file));
    }

    [Fact]
    public void Student_work_module_has_no_file_payload_transport_settings_or_application_dependency()
    {
        var root = Path.Combine(StringExtractor.RepositoryRoot(), "src", "Modules", "StudentWork");
        var project = XDocument.Load(Path.Combine(root, "ClassroomManager.StudentWork.csproj"));
        Assert.Empty(project.Descendants("PackageReference"));
        OnlyTheModuleContract(project);
        Assert.Empty(project.Descendants("FrameworkReference"));
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(IsSource))
        {
            // The module speaks JSON text to its ports, so System.Text.Json is allowed; it has no file, socket or process of its own.
            foreach (var type in new[] { "System.Windows", "System.IO", "ClassroomManager.Host", "ClassroomManager.Shared",
                "ClientSession", "HostSettings", "RecentEntry", "RecentQuery", "RecentScan", "RecentFilesCopier", "JsonRecordFile", "Cmd",
                "FileStream", "FileInfo", "DirectoryInfo", "Socket", "TcpClient", "HttpClient", "Process", "Registry", "Assembly" })
                Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", File.ReadAllText(file));
            Assert.DoesNotMatch(@"\b(File|Directory)\.", File.ReadAllText(file));
        }
    }

    [Fact]
    public void Selection_projection_has_no_window_settings_or_transport_dependency()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "HostSelectionSnapshot.cs"));
        foreach (var type in new[] { "System.Windows", "MainWindow", "HostSettings", "ClientSession", "ICollectionView", "Dispatcher", "MessageBox" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
    }

    [Fact]
    public void Recurring_work_owner_has_no_ui_settings_or_transport_dependency()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "RecurringWorkLifetime.cs"));
        foreach (var type in new[] { "System.Windows", "Dispatcher", "DispatcherTimer", "MainWindow", "HostSettings", "ClientSession" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
    }

    [Fact]
    public void Window_callback_adapter_does_not_own_dialogs_settings_or_transport()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "HostWindowCallbacks.cs"));
        foreach (var type in new[] { "MainWindow", "HostSettings", "ClientSession", "Dispatcher", "MessageBox" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
    }

    [Fact]
    public void Window_work_lifetime_does_not_depend_on_ui_settings_or_transport()
    {
        var source = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "WindowWorkLifetime.cs"));
        foreach (var type in new[] { "System.Windows", "Dispatcher", "MainWindow", "HostSettings", "ClientSession", "MessageBox" })
            Assert.DoesNotMatch($@"\b{Regex.Escape(type)}\b", source);
    }
}
