// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// The free tier's limit of 3 computers online at once applies to the official builds and to the unsigned release builds of the public repository,
/// and not to a build you make yourself from the source. The unsigned release builds also have no copies under names without the version.
/// </summary>
public sealed class FreeLimitBuildTests
{
    static Assembly WithMetadata(params (string Key, string Value)[] items)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("limit-" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        foreach (var (key, value) in items) assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [key, value]));
        return assembly;
    }

    [Fact]
    public void A_build_from_the_source_has_no_limit_and_the_two_flags_each_switch_it_on()
    {
        Assert.False(BuildEdition.ReadFreeLimit(WithMetadata()));
        Assert.False(BuildEdition.ReadFreeLimit(WithMetadata(("ClassroomOfficial", "false"), ("ClassroomFreeLimit", "false"))));
        Assert.True(BuildEdition.ReadFreeLimit(WithMetadata(("ClassroomFreeLimit", "true"))));       // an unsigned release build
        Assert.True(BuildEdition.ReadFreeLimit(WithMetadata(("ClassroomOfficial", "true"))));        // an official build
        Assert.False(BuildEdition.Read(WithMetadata(("ClassroomFreeLimit", "true"))));               // the unsigned release build is not "official"
    }

    [Fact]
    public void The_public_release_builds_pass_the_flag_and_the_build_you_make_yourself_does_not()
    {
        var root = StringExtractor.RepositoryRoot();
        Assert.Contains("-p:ClassroomFreeLimit=true", File.ReadAllText(Path.Combine(root, "scripts/Build-PublicRelease.ps1")));
        Assert.DoesNotContain("ClassroomFreeLimit", File.ReadAllText(Path.Combine(root, "Build-Free.ps1")));
        Assert.Contains("<ClassroomFreeLimit Condition=\"'$(ClassroomFreeLimit)' == ''\">false</ClassroomFreeLimit>", File.ReadAllText(Path.Combine(root, "Directory.Build.props")));
        Assert.Contains("ClassroomFreeLimit", File.ReadAllText(Path.Combine(root, "src/ClassroomHost/ClassroomHost.csproj")));
    }

    [Fact]
    public void The_limit_is_decided_by_the_free_limit_flag_everywhere_it_is_applied()
    {
        var root = StringExtractor.RepositoryRoot();
        foreach (var file in new[] { "src/ClassroomHost/LicenceCapacity.cs", "src/ClassroomHost/MainWindow.Licence.cs", "src/ClassroomHost/AboutWindow.cs" })
            Assert.Contains("BuildEdition.HasFreeLimit", File.ReadAllText(Path.Combine(root, file)));
    }

    [Fact]
    public void A_release_has_only_the_files_with_the_version_in_their_names()
    {
        var workflow = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), ".github/workflows/release.yml"));
        Assert.DoesNotContain("Same files under names without the version", workflow);
        Assert.DoesNotContain("-win-x64.exe\" >> SHA256SUMS.txt", workflow.Replace("${kind}", "K"));   // no checksum line for a copy without the version
        Assert.Contains("Dausuva-Classroom-Host-$env:VERSION-win-x64.exe", workflow);
    }
}
