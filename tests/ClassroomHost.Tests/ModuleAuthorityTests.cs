// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using ClassroomManager.Modules;
using Xunit;

namespace ClassroomHost.Tests;

// "No ambient authority": a module reaches the computers, files and clock only through the ports it was given. This keeps the code of every
// module from naming the things that would let it go around them: files, sockets, processes, the registry, reflection and native calls.
public sealed class ModuleAuthorityTests
{
    static readonly string[] ModuleFolders = ["Appearance", "Automation", "BrowserInventory", "Maintenance", "Reporting", "SoftwareInventory", "StudentWork"];

    static readonly Regex ForbiddenNames = new(
        @"\b(File|Directory|FileStream|FileInfo|DirectoryInfo|FileSystemWatcher|HttpClient|WebClient|Socket|TcpClient|TcpListener|UdpClient|NamedPipeClientStream|NamedPipeServerStream|" +
        @"Process|ProcessStartInfo|Registry|RegistryKey|DllImport|LibraryImport|Assembly|AssemblyLoadContext|Activator|AppDomain|Marshal|SecureChannel)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex ForbiddenNamespaces = new(
        @"\b(System\.Net|Microsoft\.Win32|System\.Diagnostics\.Process|System\.Reflection|System\.Runtime\.InteropServices|System\.IO\.Pipes|System\.Management|ClassroomManager\.(Host|Client|Shared|Modules\.Runtime))\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex Environmental = new(@"\bEnvironment\s*\.\s*(GetEnvironmentVariable|GetFolderPath|SetEnvironmentVariable|ExpandEnvironmentVariables|CurrentDirectory)\b", RegexOptions.Compiled);

    static string ModulesRoot => Path.Combine(StringExtractor.RepositoryRoot(), "src", "Modules");

    /// <summary>The code with comments and text removed, so a sentence that mentions a file is not mistaken for code that opens one.</summary>
    internal static string CodeOnly(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"//[^\r\n]*", " ");
        source = Regex.Replace(source, @"@""(?:[^""]|"""")*""", "\"\"");
        source = Regex.Replace(source, @"""""""[\s\S]*?""""""", "\"\"");
        return Regex.Replace(source, @"""(?:\\.|[^""\\\r\n])*""", "\"\"");
    }

    internal static IReadOnlyList<string> Violations(string source)
    {
        var code = CodeOnly(source);
        return ForbiddenNames.Matches(code).Select(match => $"uses {match.Value}")
            .Concat(ForbiddenNamespaces.Matches(code).Select(match => $"names {match.Value}"))
            .Concat(Environmental.Matches(code).Select(match => $"reads {match.Value.Replace(" ", "")}"))
            .Distinct().ToArray();
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void The_code_of_a_module_names_nothing_that_goes_around_its_ports(string folder)
    {
        var problems = Directory.EnumerateFiles(Path.Combine(ModulesRoot, folder), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(@"\obj\") && !path.Contains(@"\bin\"))
            .SelectMany(path => Violations(File.ReadAllText(path)).Select(problem => $"{Path.GetRelativePath(ModulesRoot, path)} {problem}"))
            .ToArray();

        Assert.Empty(problems);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void A_module_project_references_only_the_contract_and_no_packages(string folder)
    {
        var project = Directory.EnumerateFiles(Path.Combine(ModulesRoot, folder), "*.csproj").Single();
        var text = File.ReadAllText(project);

        var references = Regex.Matches(text, @"<ProjectReference\s+Include=""([^""]+)""").Select(match => match.Groups[1].Value.Replace('/', '\\')).ToArray();

        Assert.Equal([@"..\Contract\ClassroomManager.Modules.Contract.csproj"], references);
        Assert.DoesNotContain("<PackageReference", text);
        Assert.DoesNotContain("<FrameworkReference", text);
        Assert.DoesNotContain("<Reference ", text);
    }

    [Theory]
    [InlineData(typeof(ClassroomManager.Appearance.AppearanceInventoryModuleEntry))]
    [InlineData(typeof(ClassroomManager.Automation.SchedulingModuleEntry))]
    [InlineData(typeof(ClassroomManager.BrowserInventory.BrowserInventoryModuleEntry))]
    [InlineData(typeof(ClassroomManager.Maintenance.MaintenanceInventoryModuleEntry))]
    [InlineData(typeof(ClassroomManager.Reporting.InventoryReportingModuleEntry))]
    [InlineData(typeof(ClassroomManager.SoftwareInventory.SoftwareInventoryModuleEntry))]
    [InlineData(typeof(ClassroomManager.StudentWork.StudentBackupModuleEntry))]
    public void The_built_assembly_of_a_module_refers_to_no_assembly_that_could_reach_the_machine(Type entry)
    {
        var referenced = entry.Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToArray();

        Assert.DoesNotContain(referenced, name => name.StartsWith("ClassroomManager.") && name != "ClassroomManager.Modules.Contract");
        Assert.DoesNotContain(referenced, name => name.StartsWith("Microsoft.Win32") || name.StartsWith("System.Net") ||
            name is "System.Diagnostics.Process" or "System.Management" or "System.IO.Pipes" or "System.Reflection.Emit" or "System.Runtime.Loader");
    }

    // The check must notice what it is meant to notice; a rule that never fires protects nothing.
    [Theory]
    [InlineData("File.ReadAllBytes(path);", "uses File")]
    [InlineData("using System.Net.Http;", "names System.Net")]
    [InlineData("var p = Process.Start(\"cmd\");", "uses Process")]
    [InlineData("Registry.LocalMachine.OpenSubKey(\"x\");", "uses Registry")]
    [InlineData("var s = Environment.GetEnvironmentVariable(\"PATH\");", "reads Environment.GetEnvironmentVariable")]
    [InlineData("[DllImport(\"user32.dll\")] static extern int F();", "uses DllImport")]
    [InlineData("using ClassroomManager.Host;", "names ClassroomManager.Host")]
    [InlineData("var t = Assembly.LoadFrom(x);", "uses Assembly")]
    public void The_check_catches_each_kind_of_ambient_authority(string code, string expected) =>
        Assert.Contains(expected, Violations(code));

    [Fact]
    public void The_check_ignores_comments_and_text_that_only_mention_these_words()
    {
        var code = "// the File and Registry are not touched\n/* Process */ var label = \"File Explorer\"; var raw = @\"C:\\Directory\\x\"; var ok = Path.GetFileName(name);";

        Assert.Empty(Violations(code));
    }

    public static IEnumerable<object[]> Modules() => ModuleFolders.Select(folder => new object[] { folder });
}
