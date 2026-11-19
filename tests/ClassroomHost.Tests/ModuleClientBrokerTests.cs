// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.IO;
using System.Text;
using ClassroomManager.Modules;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// The ports client.call, client.transfer and storage.sink. A hostile module is simulated by asking for everything it must not have;
/// each ask has to be refused, logged and counted, and none may reach the (fake) connection.
/// </summary>
public sealed class ModuleClientBrokerTests : IDisposable
{
    const string Id = "dausuva.demo.client";
    readonly string _root = Path.Combine(Path.GetTempPath(), "cm-client-" + Guid.NewGuid().ToString("N"));
    readonly string _sink;
    readonly List<string> _audit = [];

    public ModuleClientBrokerTests()
    {
        _sink = Directory.CreateDirectory(Path.Combine(_root, "sink")).FullName;
    }

    public void Dispose()
    {
        foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Where(path => new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)))
            try { Directory.Delete(directory); } catch (IOException) { }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    sealed class FakeConnection : IModuleConnection
    {
        public List<(string Command, string Args)> Calls { get; } = [];
        public List<(string Remote, string Local)> Downloads { get; } = [];
        public string Answer { get; set; } = """{"items":[{"path":"C:\\Users\\kid\\Desktop\\a.txt","size":3}]}""";
        public Func<CancellationToken, Task>? Delay { get; set; }
        public Exception? DownloadFails { get; set; }
        public byte[] Content { get; set; } = [1, 2, 3];

        public async Task<string> CallUserAsync(string command, string argumentsJson, CancellationToken cancellation)
        {
            Calls.Add((command, argumentsJson));
            if (Delay is not null) await Delay(cancellation);
            return Answer;
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken cancellation)
        {
            Downloads.Add((remotePath, localPath));
            if (DownloadFails is not null) throw DownloadFails;
            File.WriteAllBytes(localPath, Content);
            return Task.CompletedTask;
        }
    }

    sealed class Folders(Dictionary<string, string> map) : IModuleSinkFolders
    {
        public string? Resolve(string sink) => map.GetValueOrDefault(sink);
    }

    static ModuleDescriptor Module(string[]? ports = null, string[]? commands = null, string[]? sinks = null) =>
        new(Id, "Demo", 1, commands ?? ["fs.recent"])
        {
            Ports = (ports ?? [ModulePorts.ClientCall, ModulePorts.ClientTransfer, ModulePorts.StorageSink]).Select(name => new ModulePortRequest(name, 1)).ToArray(),
            Sinks = sinks ?? ["backup"],
        };

    (ModuleClientSession Session, FakeConnection Connection, ModuleComputer Computer) Open(ModuleDescriptor? module = null, ModuleClientLimits? limits = null, string? sinkFolder = "")
    {
        var connection = new FakeConnection();
        var computer = new ModuleComputer(1, "PC-01");
        var map = new Dictionary<string, string>();
        if (sinkFolder is not null) map["backup"] = sinkFolder.Length == 0 ? _sink : sinkFolder;
        var session = new ModuleClientSession(module ?? Module(), [new(computer, connection, "PC-01_kid")], new Folders(map), _audit.Add, limits);
        return (session, connection, computer);
    }

    static ModuleTransfer Transfer(string remote = @"C:\Users\kid\Desktop\a.txt", string relative = @"Desktop\a.txt", bool overwrite = true,
        long size = 3, DateTime? modified = null) =>
        new(remote, "backup", relative, size, modified ?? new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc), overwrite);

    [Fact]
    public async Task A_declared_command_reaches_the_computer_as_a_json_object_and_its_answer_comes_back()
    {
        var (session, connection, computer) = Open();

        var answer = await session.CallAsync(computer, "fs.recent", """{"max":5}""");

        Assert.Contains("a.txt", answer);
        Assert.Equal(("fs.recent", """{"max":5}"""), Assert.Single(connection.Calls));
        Assert.Contains("fs.recent on PC-01", _audit);
        Assert.Equal(0, session.Refusals);
    }

    [Theory]
    [InlineData("fs.delete")]
    [InlineData("FS.RECENT")]
    [InlineData("")]
    [InlineData("proc.kill")]
    public async Task A_command_the_module_did_not_declare_is_refused_before_it_is_sent(string command)
    {
        var (session, connection, computer) = Open();

        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(computer, command, "{}"));

        Assert.Empty(connection.Calls);
        Assert.Equal(1, session.Refusals);
        Assert.Contains(_audit, line => line.StartsWith("refused:"));
    }

    [Fact]
    public async Task A_computer_that_was_not_chosen_for_the_run_is_refused()
    {
        var (session, connection, _) = Open();

        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(new ModuleComputer(2, "PC-02"), "fs.recent", "{}"));
        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(new ModuleComputer(1, "10.0.0.5"), "fs.recent", "{}"));

        Assert.Empty(connection.Calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("\"text\"")]
    public async Task Arguments_that_are_not_a_json_object_are_refused(string arguments)
    {
        var (session, connection, computer) = Open();

        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(computer, "fs.recent", arguments));

        Assert.Empty(connection.Calls);
    }

    [Fact]
    public async Task Oversize_arguments_and_oversize_answers_are_refused()
    {
        var (session, connection, computer) = Open();
        var big = "{\"x\":\"" + new string('a', ModulePorts.MaxCallArgumentBytes) + "\"}";
        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(computer, "fs.recent", big));
        Assert.Empty(connection.Calls);

        connection.Answer = "{\"x\":\"" + new string('a', ModulePorts.MaxCallAnswerBytes) + "\"}";
        await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(computer, "fs.recent", "{}"));
    }

    [Fact]
    public async Task A_module_that_did_not_ask_for_a_port_cannot_use_it()
    {
        var (callOnly, _, computer) = Open(Module(ports: [ModulePorts.ClientCall]));
        await callOnly.CallAsync(computer, "fs.recent", "{}");
        await Assert.ThrowsAsync<ModulePortException>(() => callOnly.TransferAsync(computer, Transfer()));

        var (none, connection, other) = Open(Module(ports: [ModulePorts.Log]));
        await Assert.ThrowsAsync<ModulePortException>(() => none.CallAsync(other, "fs.recent", "{}"));
        Assert.Empty(connection.Calls);

        var (noSink, _, third) = Open(Module(ports: [ModulePorts.ClientCall, ModulePorts.ClientTransfer]));
        await noSink.CallAsync(third, "fs.recent", "{}");
        await Assert.ThrowsAsync<ModulePortException>(() => noSink.TransferAsync(third, Transfer()));
    }

    [Fact]
    public async Task The_run_stops_after_three_refusals_even_for_a_good_call()
    {
        var (session, connection, computer) = Open();

        for (var i = 0; i < ModulePorts.MaxRefusalsPerRun; i++)
            await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(computer, "proc.kill", "{}"));

        Assert.True(session.Stopped);
        var stopped = await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(computer, "fs.recent", "{}"));
        Assert.Contains("stopped", stopped.Message);
        Assert.Empty(connection.Calls);
        Assert.Equal(ModulePorts.MaxRefusalsPerRun, session.Refusals);
    }

    [Fact]
    public async Task A_call_that_takes_too_long_is_cut_off_and_counted()
    {
        var (session, connection, computer) = Open(limits: new(TimeSpan.FromMilliseconds(50), 3));
        connection.Delay = token => Task.Delay(TimeSpan.FromSeconds(30), token);

        var error = await Assert.ThrowsAsync<ModulePortException>(() => session.CallAsync(computer, "fs.recent", "{}"));

        Assert.Contains("timed out", error.Message);
        Assert.Equal(1, session.Refusals);
    }

    [Fact]
    public async Task Cancelling_the_run_is_not_a_refusal()
    {
        var (session, connection, computer) = Open();
        connection.Delay = token => Task.Delay(TimeSpan.FromSeconds(30), token);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CallAsync(computer, "fs.recent", "{}", cancel.Token));

        Assert.Equal(0, session.Refusals);
    }

    [Fact]
    public async Task Calls_to_one_computer_run_one_at_a_time()
    {
        var (session, connection, computer) = Open();
        var running = 0;
        var most = 0;
        connection.Delay = async token =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await Task.Delay(30, token);
            Interlocked.Decrement(ref running);
        };

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => session.CallAsync(computer, "fs.recent", "{}")));

        Assert.Equal(1, most);
    }

    [Fact]
    public async Task A_named_file_is_copied_into_the_sink_below_the_computers_folder_with_its_change_time()
    {
        var (session, connection, computer) = Open();
        await session.CallAsync(computer, "fs.recent", "{}");
        var modified = new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);

        var result = await session.TransferAsync(computer, Transfer(modified: modified));

        Assert.True(result.Copied);
        var local = Path.Combine(_sink, "PC-01_kid", "Desktop", "a.txt");
        Assert.Equal([1, 2, 3], File.ReadAllBytes(local));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(local));
        Assert.False(File.Exists(local + ".cm-partial"));
        Assert.Equal(@"C:\Users\kid\Desktop\a.txt", Assert.Single(connection.Downloads).Remote);
    }

    [Fact]
    public async Task A_path_no_answer_named_is_refused_even_when_it_is_a_real_file()
    {
        var (session, connection, computer) = Open();

        await Assert.ThrowsAsync<ModulePortException>(() => session.TransferAsync(computer, Transfer()));
        await session.CallAsync(computer, "fs.recent", "{}");
        await Assert.ThrowsAsync<ModulePortException>(() => session.TransferAsync(computer, Transfer(remote: @"C:\Windows\System32\config\SAM")));

        Assert.Empty(connection.Downloads);
    }

    [Theory]
    [InlineData(@"..\escape.txt")]
    [InlineData(@"Desktop\..\..\escape.txt")]
    [InlineData(@"C:\escape.txt")]
    [InlineData(@"\\server\share\x.txt")]
    [InlineData(@"\rooted.txt")]
    [InlineData("a.txt:stream")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("trailing.")]
    [InlineData("")]
    public async Task A_destination_that_would_leave_the_sink_or_is_not_a_plain_path_is_refused(string relative)
    {
        var (session, connection, computer) = Open();
        await session.CallAsync(computer, "fs.recent", "{}");

        await Assert.ThrowsAsync<ModulePortException>(() => session.TransferAsync(computer, Transfer(relative: relative)));

        Assert.Empty(connection.Downloads);
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task An_undeclared_sink_or_a_sink_without_a_chosen_folder_is_refused()
    {
        var (session, connection, computer) = Open();
        await session.CallAsync(computer, "fs.recent", "{}");
        await Assert.ThrowsAsync<ModulePortException>(() => session.TransferAsync(computer, Transfer() with { Sink = "documents" }));

        var (unset, other, third) = Open(sinkFolder: null);
        await unset.CallAsync(third, "fs.recent", "{}");
        await Assert.ThrowsAsync<ModulePortException>(() => unset.TransferAsync(third, Transfer()));

        Assert.Empty(connection.Downloads);
        Assert.Empty(other.Downloads);
    }

    [Fact]
    public async Task An_unchanged_copy_is_left_alone_and_a_changed_one_is_replaced_only_when_overwriting()
    {
        var (session, connection, computer) = Open();
        await session.CallAsync(computer, "fs.recent", "{}");
        await session.TransferAsync(computer, Transfer());

        var same = await session.TransferAsync(computer, Transfer());
        Assert.True(same.UpToDate);
        Assert.Single(connection.Downloads);

        var keepFirst = await session.TransferAsync(computer, Transfer(size: 99, overwrite: false));
        Assert.True(keepFirst.UpToDate);
        Assert.Single(connection.Downloads);

        connection.Content = [7, 7, 7, 7, 7];
        var replaced = await session.TransferAsync(computer, Transfer(size: 5, modified: new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc)));
        Assert.True(replaced.Copied);
        Assert.Equal(5, File.ReadAllBytes(Path.Combine(_sink, "PC-01_kid", "Desktop", "a.txt")).Length);
    }

    [Fact]
    public async Task A_failed_download_leaves_the_earlier_copy_and_no_partial_file()
    {
        var (session, connection, computer) = Open();
        await session.CallAsync(computer, "fs.recent", "{}");
        await session.TransferAsync(computer, Transfer());
        var local = Path.Combine(_sink, "PC-01_kid", "Desktop", "a.txt");
        connection.DownloadFails = new IOException("connection lost");

        await Assert.ThrowsAsync<IOException>(() => session.TransferAsync(computer, Transfer(size: 9, modified: new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc))));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(local));
        Assert.False(File.Exists(local + ".cm-partial"));
    }

    [Fact]
    public async Task A_junction_inside_the_sink_cannot_carry_a_copy_out_of_it()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var computerFolder = Directory.CreateDirectory(Path.Combine(_sink, "PC-01_kid")).FullName;
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(computerFolder, "Desktop")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var process = Process.Start(start)!) process.WaitForExit();
        Assert.True(new DirectoryInfo(Path.Combine(computerFolder, "Desktop")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        var (session, connection, computer) = Open();
        await session.CallAsync(computer, "fs.recent", "{}");

        await Assert.ThrowsAsync<ModulePortException>(() => session.TransferAsync(computer, Transfer()));

        Assert.Empty(Directory.GetFiles(outside));
        Assert.Empty(connection.Downloads);
    }

    [Fact]
    public async Task Names_in_nested_answers_are_remembered_for_the_computer_that_gave_them_only()
    {
        var first = new FakeConnection { Answer = """{"a":{"b":[{"path":"C:\\one.txt"}]}}""" };
        var second = new FakeConnection { Answer = """{"path":"C:\\two.txt"}""" };
        var pc1 = new ModuleComputer(1, "PC-01");
        var pc2 = new ModuleComputer(2, "PC-02");
        var session = new ModuleClientSession(Module(), [new(pc1, first, "one"), new(pc2, second, "two")], new Folders(new() { ["backup"] = _sink }), _audit.Add);
        await session.CallAsync(pc1, "fs.recent", "{}");
        await session.CallAsync(pc2, "fs.recent", "{}");

        Assert.True((await session.TransferAsync(pc1, Transfer(remote: @"C:\one.txt", relative: "one.txt"))).Copied);
        await Assert.ThrowsAsync<ModulePortException>(() => session.TransferAsync(pc1, Transfer(remote: @"C:\two.txt", relative: "two.txt")));
        Assert.True((await session.TransferAsync(pc2, Transfer(remote: @"C:\two.txt", relative: "two.txt"))).Copied);
        Assert.True(File.Exists(Path.Combine(_sink, "one", "one.txt")));
        Assert.True(File.Exists(Path.Combine(_sink, "two", "two.txt")));
    }

    [Fact]
    public void A_manifest_may_list_sinks_only_with_the_sink_port_and_only_valid_names()
    {
        var withPort = new[] { new ModulePortRequest(ModulePorts.StorageSink, 1) };

        Assert.Empty(ModuleManifest.SinkProblems(["backup", "export-2"], withPort));
        Assert.Empty(ModuleManifest.SinkProblems(null, null));
        Assert.NotEmpty(ModuleManifest.SinkProblems(["backup"], []));
        Assert.NotEmpty(ModuleManifest.SinkProblems(["Backup"], withPort));
        Assert.NotEmpty(ModuleManifest.SinkProblems(["a/b"], withPort));
        Assert.NotEmpty(ModuleManifest.SinkProblems(["backup", "backup"], withPort));
        Assert.NotEmpty(ModuleManifest.SinkProblems([new string('a', 33)], withPort));
    }

    [Fact]
    public void A_built_in_module_that_lists_sinks_without_the_port_cannot_be_registered()
    {
        var registry = new ModuleRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.AddBuiltIn(new Plain(new("dausuva.demo.sinks", "Demo", 1, []) { Sinks = ["backup"] })));
        registry.AddBuiltIn(new Plain(new("dausuva.demo.sinks", "Demo", 1, []) { Sinks = ["backup"], Ports = [new(ModulePorts.StorageSink, 1)] }));
        Assert.Single(registry.Usable);
    }

    sealed class Plain(ModuleDescriptor descriptor) : IClassroomModule
    {
        public ModuleDescriptor Describe() => descriptor;
    }

    [Fact]
    public async Task The_log_of_a_run_never_contains_file_contents_or_arguments()
    {
        // The audit names the command and the computer only; what the module sent and what came back stay out of the activity log.
        var (session, connection, computer) = Open();
        connection.Answer = """{"secret":"hunter2"}""";

        await session.CallAsync(computer, "fs.recent", """{"password":"hunter2"}""");

        Assert.All(_audit, line => Assert.DoesNotContain("hunter2", line, StringComparison.Ordinal));
        Assert.Equal("fs.recent on PC-01", Assert.Single(_audit));
        Assert.Equal(Encoding.UTF8.GetByteCount("fs.recent on PC-01"), Encoding.UTF8.GetByteCount(_audit[0]));
    }
}
