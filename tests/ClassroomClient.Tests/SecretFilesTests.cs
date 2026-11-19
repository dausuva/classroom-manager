// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.AccessControl;
using System.Security.Principal;
using ClassroomManager.Client;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-182 on a scratch file: nothing here touches the real C:\ProgramData\ClassroomManager.</summary>
public sealed class SecretFilesTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "classroom-secretfiles-" + Guid.NewGuid().ToString("N"));
    // The test account stays on every file, so the test can read and delete it; SYSTEM and Administrators are the policy under test.
    static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    public SecretFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string NewFile(string content = "{\"psk\":\"x\"}")
    {
        var path = Path.Combine(_folder, "client.json");
        File.WriteAllText(path, content);
        return path;
    }

    static void Open(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [Fact]
    public void A_file_other_users_can_read_is_reported_repaired_and_then_reads_clean()
    {
        var path = NewFile();
        Open(path);
        Assert.Contains(SecretFiles.Exposed(path, Me), name => name.EndsWith("Users", StringComparison.OrdinalIgnoreCase));

        var message = SecretFiles.Ensure(path, Me);

        Assert.Contains("restricted", message);
        Assert.Contains("client.json", message);
        Assert.Empty(SecretFiles.Exposed(path, Me));
        Assert.Equal("{\"psk\":\"x\"}", File.ReadAllText(path));        // the content is untouched and still readable
        Assert.Equal("", SecretFiles.Ensure(path, Me));                  // a second look finds nothing to do
    }

    [Fact]
    public void A_file_inheriting_the_folders_open_rules_is_repaired_and_stops_inheriting()
    {
        var path = NewFile();
        var folder = new DirectoryInfo(_folder);
        var folderSecurity = folder.GetAccessControl();
        folderSecurity.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(folderSecurity);
        var inheriting = NewFile();                                      // created after the rule: inherits it
        Assert.NotEmpty(SecretFiles.Exposed(inheriting, Me));

        SecretFiles.Ensure(inheriting, Me);

        Assert.Empty(SecretFiles.Exposed(inheriting, Me));
        Assert.True(new FileInfo(inheriting).GetAccessControl().AreAccessRulesProtected);
        Assert.False(string.IsNullOrEmpty(path));
    }

    [Fact]
    public void A_protected_file_and_a_missing_file_are_left_alone()
    {
        var path = NewFile();
        SecretFiles.Restrict(path, Me);
        Assert.Equal("", SecretFiles.Ensure(path, Me));
        Assert.Equal("", SecretFiles.Ensure(Path.Combine(_folder, "not-there.pfx"), Me));
    }

    [Fact]
    public void Restricting_keeps_system_and_administrators_with_full_control()
    {
        var path = NewFile();
        SecretFiles.Restrict(path, Me);

        var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Contains(rules, rule => rule.IdentityReference.Value == "S-1-5-18" && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.Contains(rules, rule => rule.IdentityReference.Value == "S-1-5-32-544" && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.DoesNotContain(rules, rule => rule.IdentityReference.Value == Users.Value);
    }
}
