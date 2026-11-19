// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The committed NuGet lock files and the software bill of materials made from them (scripts/Export-Sbom.ps1).</summary>
public sealed class SupplyChainTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "classroom-sbom-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    static string Root => StringExtractor.RepositoryRoot();

    [Fact]
    public void Every_project_has_its_lock_file_beside_it_and_a_publish_for_either_runtime_does_not_need_a_new_one()
    {
        var projects = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "tests"), "*.csproj", SearchOption.AllDirectories))
            .Where(path => !Regex.IsMatch(path, @"[\/](obj|bin)[\/]"))
            .ToList();
        Assert.NotEmpty(projects);
        foreach (var project in projects)
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")), $"{project} has no packages.lock.json: run dotnet restore and commit it.");

        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props);
        Assert.Contains("<RuntimeIdentifiers>win-x64;win-x86</RuntimeIdentifiers>", props);
    }

    [Theory]
    [InlineData(PowerShellEdition.Windows)]
    [InlineData(PowerShellEdition.Core)]
    public async Task The_SBOM_lists_the_packages_of_the_lock_files_with_their_hashes(PowerShellEdition edition)
    {
        var output = Path.Combine(_folder, "sbom.cdx.json");
        var (exit, text, errors) = await PowerShellScript.RunAsync(edition, Path.Combine("scripts", "Export-Sbom.ps1"), ["-Output", output], TimeSpan.FromMinutes(2));
        Assert.True(exit == 0, text + errors);

        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var bom = document.RootElement;
        Assert.Equal("CycloneDX", bom.GetProperty("bomFormat").GetString());
        Assert.Equal("1.5", bom.GetProperty("specVersion").GetString());
        var version = Regex.Match(File.ReadAllText(Path.Combine(Root, "Directory.Build.props")), @"<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Equal(version, bom.GetProperty("metadata").GetProperty("component").GetProperty("version").GetString());

        var components = bom.GetProperty("components").EnumerateArray().ToList();
        Assert.NotEmpty(components);
        foreach (var component in components)
        {
            var name = component.GetProperty("name").GetString();
            Assert.Equal($"pkg:nuget/{name}@{component.GetProperty("version").GetString()}", component.GetProperty("purl").GetString());
            Assert.Matches("^[0-9a-f]{128}$", component.GetProperty("hashes")[0].GetProperty("content").GetString()!);
        }
        Assert.DoesNotContain(components, component => component.GetProperty("name").GetString()!.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(components.Count, bom.GetProperty("dependencies")[0].GetProperty("dependsOn").GetArrayLength());
    }
}
