// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Client;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class StagingOwnershipTests : IDisposable
{
    readonly string _directory = Directory.CreateTempSubdirectory("cm-staging-").FullName;
    readonly string _staging;
    readonly string _owners;

    public StagingOwnershipTests()
    {
        _staging = Path.Combine(_directory, "Staging");
        _owners = Path.Combine(_directory, "staging-owners.json");
        Directory.CreateDirectory(_staging);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    StagingOwnership Ownership() => new(_staging, _owners);

    [Fact]
    public void A_teacher_key_owns_the_staging_folder_it_writes_to_first()
    {
        var path = Path.Combine(_staging, "teacher-host-a", "send", "worksheet.docx");

        Assert.True(Ownership().MayWrite("Room 12 teacher", path));
        // The decision is remembered across service restarts, and the name is not case-sensitive.
        Assert.True(Ownership().MayWrite("room 12 TEACHER", Path.Combine(_staging, "teacher-host-a", "wallpaper.png")));
    }

    [Fact]
    public void A_teacher_key_may_remove_only_what_is_in_a_folder_it_created()
    {
        var mine = Path.Combine(_staging, "Hosts", "teacher-host-id");
        Directory.CreateDirectory(Path.Combine(_staging, "Hosts", "admin-host-id"));
        Assert.True(Ownership().MayWrite("Room 12 teacher", Path.Combine(mine, "send", "worksheet.docx")));

        Assert.True(Ownership().Owns("Room 12 teacher", Path.Combine(mine, "send", "worksheet.docx")));
        Assert.True(Ownership().Owns("room 12 TEACHER", Path.Combine(mine, "anything")));
        Assert.False(Ownership().Owns("Room 14 teacher", Path.Combine(mine, "send", "worksheet.docx")));
        Assert.False(Ownership().Owns("Room 12 teacher", Path.Combine(_staging, "Hosts", "admin-host-id", "setup.exe")));
        // The folder itself, the shared Hosts folder, a way out of it, and anything outside staging.
        Assert.False(Ownership().Owns("Room 12 teacher", mine));
        Assert.False(Ownership().Owns("Room 12 teacher", Path.Combine(_staging, "Hosts")));
        Assert.False(Ownership().Owns("Room 12 teacher", Path.Combine(mine, "..", "admin-host-id", "setup.exe")));
        Assert.False(Ownership().Owns("Room 12 teacher", Path.Combine(_directory, "client.json")));
        Assert.False(Ownership().Owns("Room 12 teacher", null));

        // Asking never makes a key the owner of a folder nobody has written to yet.
        Assert.False(Ownership().Owns("Room 14 teacher", Path.Combine(_staging, "Hosts", "new-host", "a.txt")));
        Assert.True(Ownership().MayWrite("Room 12 teacher", Path.Combine(_staging, "Hosts", "new-host", "a.txt")));
    }

    [Fact]
    public void Another_teacher_key_cannot_write_into_that_folder()
    {
        Assert.True(Ownership().MayWrite("Room 12 teacher", Path.Combine(_staging, "teacher-host-a", "a.txt")));

        Assert.False(Ownership().MayWrite("Room 14 teacher", Path.Combine(_staging, "teacher-host-a", "a.txt")));
    }

    [Fact]
    public void A_folder_that_already_exists_without_an_owner_belongs_to_an_administrator()
    {
        // An administrator's Host staged an installer here before any Teacher key wrote anything.
        Directory.CreateDirectory(Path.Combine(_staging, "admin-host"));

        Assert.False(Ownership().MayWrite("Room 12 teacher", Path.Combine(_staging, "admin-host", "setup.exe")));
        Assert.False(File.Exists(_owners));
    }

    [Fact]
    public void Each_host_folder_under_Hosts_is_owned_separately()
    {
        // This is the layout the Host really uses: Staging\Hosts\<host id>\...
        var teacherHost = Path.Combine(_staging, "Hosts", "teacher-host-id");
        Directory.CreateDirectory(Path.Combine(_staging, "Hosts", "admin-host-id"));

        Assert.True(Ownership().MayWrite("Room 12 teacher", Path.Combine(teacherHost, "send", "worksheet.docx")));
        Assert.True(Ownership().MayWrite("Room 12 teacher", Path.Combine(teacherHost, "wallpaper.png")));
        // The shared Hosts folder already existing must not block a new Host folder inside it...
        Assert.False(Ownership().MayWrite("Room 14 teacher", Path.Combine(teacherHost, "wallpaper.png")));
        // ...and an administrator's Host folder stays out of reach.
        Assert.False(Ownership().MayWrite("Room 12 teacher", Path.Combine(_staging, "Hosts", "admin-host-id", "setup.exe")));
    }

    [Fact]
    public void Nothing_can_be_written_directly_into_the_shared_Hosts_folder()
    {
        Assert.False(Ownership().MayWrite("Room 12 teacher", Path.Combine(_staging, "Hosts", "loose-file.exe")));
        Assert.False(Ownership().MayWrite("Room 12 teacher", Path.Combine(_staging, "hosts", "teacher-host-id")));
        Assert.False(File.Exists(_owners));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("loose-file.exe")]
    [InlineData("folder\\")]
    [InlineData("..\\client.json")]
    [InlineData("teacher-host-a\\..\\..\\client.json")]
    public void Paths_outside_a_staging_subfolder_are_refused(string? relative)
    {
        var path = relative is null or "" ? relative : Path.Combine(_staging, relative);

        Assert.False(Ownership().MayWrite("Room 12 teacher", path));
    }

    [Fact]
    public void Paths_elsewhere_on_the_disk_are_refused()
    {
        Assert.False(Ownership().MayWrite("Room 12 teacher", @"C:\Users\Public\Desktop\note.txt"));
        Assert.False(Ownership().MayWrite("Room 12 teacher", _staging + "Other\\folder\\file.txt"));
    }

    [Fact]
    public void An_unreadable_owner_file_never_grants_access_to_existing_folders()
    {
        Directory.CreateDirectory(Path.Combine(_staging, "admin-host"));
        File.WriteAllText(_owners, "not json");

        Assert.False(Ownership().MayWrite("Room 12 teacher", Path.Combine(_staging, "admin-host", "setup.exe")));
    }
}
