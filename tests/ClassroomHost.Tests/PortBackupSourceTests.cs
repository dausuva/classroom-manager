// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Modules;
using ClassroomManager.StudentWork;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// The student-work backup running only through the module ports: the real module code, the real broker checks and a fake computer.
/// What the Host's own backup tests prove for the paging and checkpoint rules, these prove for the port path.
/// </summary>
public sealed class PortBackupSourceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "cm-portbackup-" + Guid.NewGuid().ToString("N"));
    readonly string _sink;
    readonly List<string> _audit = [];
    static readonly DateTime Never = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public PortBackupSourceTests() => _sink = Directory.CreateDirectory(Path.Combine(_root, "Backups")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    sealed class Computer : IModuleConnection
    {
        public List<JsonObject> Scans { get; } = [];
        public List<string> Downloads { get; } = [];
        public Queue<string> Answers { get; } = new();
        public Func<string, Exception?>? DownloadFault { get; set; }

        public Task<string> CallUserAsync(string command, string argumentsJson, CancellationToken cancellation)
        {
            Assert.Equal("fs.recent", command);
            Scans.Add((JsonObject)JsonNode.Parse(argumentsJson)!);
            return Task.FromResult(Answers.Dequeue());
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken cancellation)
        {
            Downloads.Add(remotePath);
            if (DownloadFault?.Invoke(remotePath) is { } fault) throw fault;
            File.WriteAllText(localPath, "content of " + remotePath);
            return Task.CompletedTask;
        }
    }

    static string Item(string root, string path, string relative, long size = 5, string modified = "2026-10-30T08:00:00.0000000Z", bool folder = false) =>
        new JsonObject { ["root"] = root, ["path"] = path, ["relativePath"] = relative, ["isFolder"] = folder, ["size"] = size, ["modifiedUtc"] = modified }.ToJsonString();

    static string Page(IEnumerable<string> items, bool hasMore = false, bool truncated = false, string[]? missing = null, bool paging = true) =>
        "{\"items\":[" + string.Join(",", items) + $"],\"truncated\":{(truncated ? "true" : "false")},\"missing\":[{string.Join(",", (missing ?? []).Select(m => "\"" + m + "\""))}],\"supportsPaging\":{(paging ? "true" : "false")},\"hasMore\":{(hasMore ? "true" : "false")},\"errors\":[]}}";

    (IBackupSource Source, Computer Computer, ModuleClientSession Session) Open(Dictionary<string, string>? labels = null)
    {
        var computer = new Computer();
        var entry = new StudentBackupModuleEntry().Describe();
        var target = new ModuleTarget(new ModuleComputer(1, "PC-01"), computer, "PC-01_kid");
        var session = new ModuleClientSession(entry, [target], new Sinks(_sink), _audit.Add);
        var source = new PortBackupSource(session, session.Computers[0], labels ?? new() { ["%DESKTOP%"] = "Desktop", ["%DOCUMENTS%"] = "Documents" });
        return (source, computer, session);
    }

    sealed class Sinks(string folder) : IModuleSinkFolders
    {
        public string? Resolve(string sink) => sink == "backup" ? folder : null;
    }

    static BackupQuery Query(params string[] folders) => new(folders.Length == 0 ? ["%DESKTOP%", "%DOCUMENTS%"] : folders, Never, 5000);

    [Fact]
    public async Task A_two_page_backup_copies_every_file_below_the_computers_folder_and_records_the_checkpoint_once()
    {
        var (source, computer, _) = Open();
        computer.Answers.Enqueue(Page([Item("%DESKTOP%", @"C:\U\Desktop\b.txt", "b.txt", modified: "2026-10-30T09:00:00Z")], hasMore: true));
        computer.Answers.Enqueue(Page([Item("%DOCUMENTS%", @"C:\U\Documents\sub\a.txt", @"sub\a.txt", modified: "2026-10-30T08:00:00Z")]));
        var commits = new List<DateTime>();

        var summary = await StudentBackupRunner.RunAsync(Query(), source, commits.Add, TimeProvider.System);

        Assert.Equal(new BackupSummary(2, 0, 2), summary);
        Assert.Single(commits);
        Assert.Equal("content of C:\\U\\Desktop\\b.txt", File.ReadAllText(Path.Combine(_sink, "PC-01_kid", "Desktop", "b.txt")));
        Assert.Equal("content of C:\\U\\Documents\\sub\\a.txt", File.ReadAllText(Path.Combine(_sink, "PC-01_kid", "Documents", "sub", "a.txt")));
        Assert.Equal(2, computer.Scans.Count);
        Assert.Equal(new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(_sink, "PC-01_kid", "Desktop", "b.txt")));
    }

    [Fact]
    public async Task The_scan_asks_for_every_file_changed_since_the_last_backup_and_the_second_page_carries_the_cursor()
    {
        var (source, computer, _) = Open();
        var since = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        computer.Answers.Enqueue(Page([Item("%DESKTOP%", @"C:\U\Desktop\b.txt", "b.txt", modified: "2026-10-30T09:00:00Z")], hasMore: true));
        computer.Answers.Enqueue(Page([]));

        await StudentBackupRunner.RunAsync(new(["%DESKTOP%"], since, 5000), source, _ => { }, TimeProvider.System);

        var first = computer.Scans[0];
        Assert.True(first["allFiles"]!.GetValue<bool>());
        Assert.False(first["includeFolders"]!.GetValue<bool>());
        Assert.Equal(5000, first["maxItems"]!.GetValue<int>());
        Assert.Equal("%DESKTOP%", first["folders"]![0]!.GetValue<string>());
        Assert.StartsWith("2026-10-01T00:00:00", first["sinceUtc"]!.GetValue<string>());
        Assert.Null(first["afterModifiedUtc"]);
        Assert.StartsWith("2026-10-30T09:00:00", computer.Scans[1]["afterModifiedUtc"]!.GetValue<string>());
        Assert.Equal(@"C:\U\Desktop\b.txt", computer.Scans[1]["afterPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_folder_entry_is_expanded_with_a_second_scan_of_that_folder_only()
    {
        var (source, computer, _) = Open();
        computer.Answers.Enqueue(Page([Item("%DOCUMENTS%", @"C:\U\Documents\Project", "Project", folder: true)]));
        computer.Answers.Enqueue(Page([Item("%DOCUMENTS%", @"C:\U\Documents\Project\x.txt", "x.txt"), Item("%DOCUMENTS%", @"C:\U\Documents\Project\y\z.txt", @"y\z.txt")]));

        var summary = await StudentBackupRunner.RunAsync(Query(), source, _ => { }, TimeProvider.System);

        Assert.Equal(2, summary.Copied);
        Assert.Equal(@"C:\U\Documents\Project", computer.Scans[1]["folders"]![0]!.GetValue<string>());
        Assert.StartsWith("1980-01-01", computer.Scans[1]["sinceUtc"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_sink, "PC-01_kid", "Documents", "Project", "x.txt")));
        Assert.True(File.Exists(Path.Combine(_sink, "PC-01_kid", "Documents", "Project", "y", "z.txt")));
    }

    [Fact]
    public async Task A_second_backup_leaves_unchanged_copies_alone()
    {
        var (source, computer, _) = Open();
        computer.Answers.Enqueue(Page([Item("%DESKTOP%", @"C:\U\Desktop\b.txt", "b.txt", size: 29)]));
        await StudentBackupRunner.RunAsync(Query(), source, _ => { }, TimeProvider.System);
        var (again, second, _) = Open();
        second.Answers.Enqueue(Page([Item("%DESKTOP%", @"C:\U\Desktop\b.txt", "b.txt", size: 29)]));

        var summary = await StudentBackupRunner.RunAsync(Query(), again, _ => { }, TimeProvider.System);

        Assert.Equal(new BackupSummary(0, 1, 1), summary);
        Assert.Empty(second.Downloads);
    }

    [Fact]
    public async Task A_file_the_computer_cannot_give_fails_the_backup_without_a_checkpoint_and_the_others_are_still_copied()
    {
        var (source, computer, session) = Open();
        computer.Answers.Enqueue(Page([Item("%DESKTOP%", @"C:\U\Desktop\locked.txt", "locked.txt"), Item("%DESKTOP%", @"C:\U\Desktop\fine.txt", "fine.txt")]));
        computer.DownloadFault = path => path.EndsWith("locked.txt") ? new ModuleTransferException("the file is in use") : null;
        var commits = 0;

        var error = await Assert.ThrowsAsync<IOException>(() => StudentBackupRunner.RunAsync(Query(), source, _ => commits++, TimeProvider.System));

        Assert.Contains("Backup incomplete", error.Message);
        Assert.Contains("locked.txt: the file is in use", error.Message);
        Assert.Equal(0, commits);
        Assert.True(File.Exists(Path.Combine(_sink, "PC-01_kid", "Desktop", "fine.txt")));
        Assert.False(File.Exists(Path.Combine(_sink, "PC-01_kid", "Desktop", "locked.txt")));
        Assert.Empty(Directory.GetFiles(_sink, "*.cm-partial", SearchOption.AllDirectories));
        Assert.Equal(0, session.Refusals);
    }

    [Fact]
    public async Task A_lost_connection_ends_the_run_and_records_nothing()
    {
        var (source, computer, _) = Open();
        computer.Answers.Enqueue(Page([Item("%DESKTOP%", @"C:\U\Desktop\a.txt", "a.txt")]));
        computer.DownloadFault = _ => new EndOfStreamException("the computer went away");
        var commits = 0;

        await Assert.ThrowsAsync<EndOfStreamException>(() => StudentBackupRunner.RunAsync(Query(), source, _ => commits++, TimeProvider.System));

        Assert.Equal(0, commits);
    }

    [Theory]
    [InlineData("%OTHER%", "a.txt")]           // a root the Host never asked for
    [InlineData("%DESKTOP%", "CON.txt")]       // a name Windows cannot hold
    [InlineData("%DESKTOP%", "a.txt.")]        // a name that ends with a dot
    public async Task A_file_that_cannot_be_stored_safely_is_reported_as_failed_not_sent_and_not_a_refusal(string root, string relative)
    {
        var (source, computer, session) = Open();
        computer.Answers.Enqueue(Page([Item(root, @"C:\U\x\" + relative, relative)]));

        var error = await Assert.ThrowsAsync<IOException>(() => StudentBackupRunner.RunAsync(Query(), source, _ => { }, TimeProvider.System));

        Assert.Contains("would leave the destination folder", error.Message);
        Assert.Empty(computer.Downloads);
        Assert.Equal(0, session.Refusals);
    }

    [Fact]
    public async Task An_entry_that_names_a_parent_folder_is_left_out_of_the_scan_like_the_hosts_own_reader_does()
    {
        var (source, computer, _) = Open();
        computer.Answers.Enqueue(Page([Item("%DESKTOP%", @"C:\U\Desktop\..\x.txt", @"..\x.txt"), Item("%DESKTOP%", @"C:\U\Desktop\ok.txt", "ok.txt")]));

        var summary = await StudentBackupRunner.RunAsync(Query(), source, _ => { }, TimeProvider.System);

        Assert.Equal(1, summary.Copied);
        Assert.Equal([@"C:\U\Desktop\ok.txt"], computer.Downloads);
    }

    [Fact]
    public async Task Two_entries_for_the_same_destination_are_copied_once()
    {
        var (source, computer, _) = Open();
        computer.Answers.Enqueue(Page([Item("%DESKTOP%", @"C:\U\Desktop\A.txt", "A.txt"), Item("%DESKTOP%", @"C:\U\Desktop\a.txt", "a.txt")]));

        var summary = await StudentBackupRunner.RunAsync(Query(), source, _ => { }, TimeProvider.System);

        Assert.Equal(1, summary.Copied);
        Assert.Single(computer.Downloads);
    }

    [Fact]
    public async Task A_computer_that_cannot_page_or_has_missing_folders_is_refused_before_anything_is_copied()
    {
        var (source, computer, _) = Open();
        computer.Answers.Enqueue(Page([Item("%DESKTOP%", @"C:\U\Desktop\a.txt", "a.txt")], paging: false));
        var error = await Assert.ThrowsAsync<IOException>(() => StudentBackupRunner.RunAsync(Query(), source, _ => { }, TimeProvider.System));
        Assert.Contains("Update the Client", error.Message);
        Assert.Empty(computer.Downloads);

        var (second, other, _) = Open();
        other.Answers.Enqueue(Page([], missing: ["%DOCUMENTS%"]));
        var missing = await Assert.ThrowsAsync<IOException>(() => StudentBackupRunner.RunAsync(Query(), second, _ => { }, TimeProvider.System));
        Assert.Contains("%DOCUMENTS%", missing.Message);
    }

    [Fact]
    public async Task The_module_sends_only_the_scan_command_and_names_no_file_it_was_not_told_about()
    {
        var (source, computer, session) = Open();
        computer.Answers.Enqueue(Page([Item("%DESKTOP%", @"C:\U\Desktop\a.txt", "a.txt")]));

        await StudentBackupRunner.RunAsync(Query(), source, _ => { }, TimeProvider.System);

        Assert.All(computer.Scans, scan => Assert.NotNull(scan["folders"]));
        Assert.Equal(["fs.recent on PC-01"], _audit);
        Assert.Equal(0, session.Refusals);
        // The module asks only for what it declares: reading file bytes through a call would be refused.
        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(session.Computers[0], "fs.read", "{}"));
    }

    [Fact]
    public void The_module_declares_exactly_what_it_uses()
    {
        var descriptor = new StudentBackupModuleEntry().Describe();

        Assert.Equal(["fs.recent"], descriptor.ClientCapabilities);
        Assert.Equal(["backup"], descriptor.Sinks);
        Assert.Equal([ModulePorts.ClientCall, ModulePorts.ClientTransfer, ModulePorts.StorageSink], descriptor.Ports.Select(port => port.Name));
        Assert.Empty(ModuleDescriptorRules.Problems(descriptor));
    }
}
