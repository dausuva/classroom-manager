// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Reflection;
using System.Text;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class CommandAccessTests
{
    static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    static IEnumerable<string> AllCommands() => typeof(Cmd)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!);

    [Theory]
    [InlineData(Cmd.Ping)]
    [InlineData(Cmd.LockSet)]
    [InlineData(Cmd.LockClear)]
    [InlineData(Cmd.MessageShow)]
    [InlineData(Cmd.WebsiteOpen)]
    [InlineData(Cmd.FsDeliver)]
    [InlineData(Cmd.FsFind)]
    [InlineData(Cmd.WallpaperSet)]
    [InlineData(Cmd.ExamModeSet)]
    [InlineData(Cmd.ExamModeClear)]
    [InlineData(Cmd.ExamDevicesSet)]
    [InlineData(Cmd.PowerAction)]
    [InlineData(Cmd.BlockHostsList)]
    [InlineData(Cmd.BlockHostsSet)]
    [InlineData(Cmd.BlockHostsClear)]
    [InlineData(Cmd.BlockHostsReset)]
    [InlineData(Cmd.BlockHostsAdd)]
    [InlineData(Cmd.BlockAppsList)]
    [InlineData(Cmd.BlockAppsSet)]
    [InlineData(Cmd.BlockAppsClear)]
    [InlineData(Cmd.FsDelete)]        // only inside the staging folder the key created; the client checks
    [InlineData(Cmd.StagingList)]
    [InlineData(Cmd.StagingDelete)]
    public void Classroom_actions_are_open_to_a_teacher_key(string command)
    {
        Assert.True(CommandAccess.RequiredRole(command) <= OperatorRole.Teacher);      // Teacher, or Observer for the commands that only read
        Assert.True(CommandAccess.IsAllowed(OperatorRole.Teacher, command));
        Assert.True(CommandAccess.IsAllowed(OperatorRole.Admin, command));
    }

    [Theory]
    [InlineData(Cmd.CmdRun)]
    [InlineData(Cmd.TermStart)]
    [InlineData(Cmd.InstallRun)]
    [InlineData(Cmd.InstallWinget)]
    [InlineData(Cmd.ProgramsUninstall)]
    [InlineData(Cmd.SelfUpdate)]
    [InlineData(Cmd.KeySet)]
    [InlineData(Cmd.AccessKeysList)]
    [InlineData(Cmd.AccessKeysSet)]
    [InlineData(Cmd.AccessKeysRemove)]
    [InlineData(Cmd.AccountCreate)]
    [InlineData(Cmd.AccountSetPassword)]
    [InlineData(Cmd.RegistrySetValue)]
    [InlineData(Cmd.RegistryImport)]
    [InlineData(Cmd.TasksDelete)]
    [InlineData(Cmd.AutorunRemove)]
    [InlineData(Cmd.ProcKill)]
    [InlineData(Cmd.FsList)]
    [InlineData(Cmd.FsMkdir)]
    [InlineData(Cmd.FsRename)]
    [InlineData(Cmd.BrowserPolicySet)]
    [InlineData(Cmd.UserSignIn)]
    [InlineData(Cmd.SignInImageSet)]
    [InlineData(Cmd.ScreenSaverSet)]
    [InlineData(Cmd.WindowsUpdateInstall)]
    [InlineData(Cmd.TimeSync)]
    public void System_changing_actions_need_an_admin_key(string command)
    {
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(command));
        Assert.False(CommandAccess.IsAllowed(OperatorRole.Teacher, command));
        Assert.True(CommandAccess.IsAllowed(OperatorRole.Admin, command));
    }

    [Fact]
    public void Commands_nobody_has_classified_are_admin_only()
    {
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole("something.new"));
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(""));
    }

    [Fact]
    public void An_admin_key_may_send_every_command_and_a_teacher_key_only_a_minority()
    {
        var commands = AllCommands().ToList();

        Assert.All(commands, command => Assert.True(CommandAccess.IsAllowed(OperatorRole.Admin, command)));
        var teacher = commands.Count(command => CommandAccess.IsAllowed(OperatorRole.Teacher, command));
        Assert.InRange(teacher, 10, commands.Count / 3);
    }

    [Theory]
    [InlineData("Admin", OperatorRole.Admin)]
    [InlineData(" admin ", OperatorRole.Admin)]
    [InlineData("Teacher", OperatorRole.Teacher)]
    [InlineData(" Observer", OperatorRole.Observer)]
    [InlineData("", OperatorRole.Observer)]
    [InlineData(null, OperatorRole.Observer)]
    [InlineData("Administrator", OperatorRole.Observer)]
    public void An_unrecognised_stored_role_is_the_least_privileged_one(string? stored, OperatorRole role)
    {
        Assert.Equal(role, CommandAccess.ParseRole(stored));
    }

    [Fact]
    public void An_observer_key_may_only_read_and_every_one_of_its_commands_is_also_open_to_a_teacher()
    {
        var observer = AllCommands().Where(command => CommandAccess.IsAllowed(OperatorRole.Observer, command)).ToList();

        Assert.Equal(8, observer.Count);
        Assert.All(observer, command => Assert.True(CommandAccess.IsAllowed(OperatorRole.Teacher, command)));
        Assert.False(CommandAccess.IsAllowed(OperatorRole.Observer, Cmd.LockSet));
        Assert.False(CommandAccess.IsAllowed(OperatorRole.Observer, Cmd.MessageShow));
        Assert.False(CommandAccess.IsAllowed(OperatorRole.Observer, Cmd.PowerAction));
        Assert.False(CommandAccess.IsAllowed(OperatorRole.Observer, Cmd.FsWrite));
        Assert.False(CommandAccess.IsAllowed(OperatorRole.Observer, Cmd.KeySet));
        Assert.False(CommandAccess.IsAllowed(OperatorRole.Observer, "something.new"));
        Assert.True(CommandAccess.IsAllowed(OperatorRole.Observer, Cmd.Ping));
    }

    [Fact]
    public void The_default_role_value_is_the_least_privileged_one()
    {
        Assert.Equal(OperatorRole.Observer, default(OperatorRole));
        Assert.True(OperatorRole.Observer < OperatorRole.Teacher && OperatorRole.Teacher < OperatorRole.Admin);
    }

    [Fact]
    public void Refusal_names_the_command_the_needed_role_and_the_key()
    {
        Assert.Equal("Not allowed with this key: 'cmd.run' needs an Admin key, and 'Room 12 teacher' is a Teacher key.",
            CommandAccess.Refusal(Cmd.CmdRun, OperatorRole.Teacher, "Room 12 teacher"));
    }

    [Theory]
    [InlineData("Room 12 teacher", true)]
    [InlineData("A", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(" padded", false)]
    [InlineData("Lab key", false)]
    [InlineData("lab KEY", false)]
    [InlineData("tab\tname", false)]
    public void Key_names_are_validated(string name, bool acceptable)
    {
        Assert.Equal(acceptable, AccessKeyNames.IsAcceptable(name, out var reason));
        Assert.Equal(acceptable, reason.Length == 0);
        Assert.False(AccessKeyNames.IsAcceptable(new string('x', AccessKeyNames.MaxLength + 1), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Handshake_reports_which_of_several_keys_the_caller_used(int used)
    {
        byte[][] keys =
        [
            Encoding.UTF8.GetBytes("primary-admin-key"),
            Encoding.UTF8.GetBytes("room-12-teacher-key"),
            Encoding.UTF8.GetBytes("room-14-teacher-key"),
        ];
        using var pair = await LoopbackStreamPair.CreateAsync();
        var ct = Timeout();

        var acceptTask = SecureChannel.AcceptAsync(pair.Server, keys, ct);
        var connectTask = SecureChannel.ConnectAsync(pair.Client, keys[used], ct);
        var (accepted, keyIndex) = await acceptTask;
        using var server = accepted;
        using var client = await connectTask;

        Assert.Equal(used, keyIndex);
        await client.SendAsync(Encoding.UTF8.GetBytes("hello"), ct);
        Assert.Equal("hello", Encoding.UTF8.GetString(await server.ReceiveAsync(ct)));
    }

    [Fact]
    public async Task Handshake_rejects_a_key_that_is_not_in_the_list()
    {
        byte[][] keys = [Encoding.UTF8.GetBytes("primary-admin-key"), Encoding.UTF8.GetBytes("room-12-teacher-key")];
        using var pair = await LoopbackStreamPair.CreateAsync();
        var ct = Timeout();

        var acceptTask = SecureChannel.AcceptAsync(pair.Server, keys, ct);
        var connectTask = SecureChannel.ConnectAsync(pair.Client, Encoding.UTF8.GetBytes("revoked-teacher-key"), ct);

        await Assert.ThrowsAsync<AuthException>(() => acceptTask);
        pair.Server.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => connectTask);
    }
}
