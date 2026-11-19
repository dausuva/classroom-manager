// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ClassroomManager.Modules;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>Module packages (T-221/T-222): the manifest rules, the hash, link and signature checks, and discovery. Nothing is loaded.</summary>
public sealed class ModulePackageTests : IDisposable
{
    const string Id = "dausuva.sample.demo";
    const string Trusted = "AAAA0000AAAA0000AAAA0000AAAA0000AAAA0000AAAA0000AAAA0000AAAA0000";

    readonly string _root = Path.Combine(Path.GetTempPath(), "module-package-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            // Remove the junctions themselves first, so deleting the folder never follows one.
            foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).ToList())
                if (Directory.Exists(directory) && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(directory, false);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A verifier that answers from a table, so these tests need no signed files.</summary>
    sealed class FakeSignatures : IModuleSignatureVerifier
    {
        public Dictionary<string, SignatureCheck> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SignatureCheck Default { get; set; } = new(true, Trusted, "");
        public SignatureCheck Check(string path) => Answers.TryGetValue(Path.GetFileName(path), out var answer) ? answer : Default;
    }

    static readonly IReadOnlySet<string> TrustedSet = new HashSet<string> { Trusted };

    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    static object ManifestObject(string id, params (string Path, byte[] Content)[] files) => new
    {
        schema = 1,
        id,
        publisher = "dausuva",
        name = "Demo module",
        version = "1.0.0",
        contract = new { min = 1, max = 1 },
        os = "windows",
        architecture = "x64",
        entry = new { assembly = files[0].Path, type = "Demo.Entry" },
        client = new { minProtocol = "1.14", capabilities = new[] { "inventory.snapshot" } },
        files = files.Select(file => new { path = file.Path, sha256 = Sha(file.Content) }).ToArray(),
    };

    /// <summary>Writes a well-formed module folder and returns it. <paramref name="change"/> may break it afterwards.</summary>
    string MakeModule(string folderName = Id, string id = Id, Action<string>? change = null, params (string Path, byte[] Content)[] extraFiles)
    {
        var folder = Path.Combine(_root, folderName);
        Directory.CreateDirectory(folder);
        var files = new List<(string Path, byte[] Content)> { ("Demo.dll", [1, 2, 3, 4]) };
        files.AddRange(extraFiles);
        foreach (var (path, content) in files)
        {
            var target = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }
        File.WriteAllText(Path.Combine(folder, "module.json"), JsonSerializer.Serialize(ManifestObject(id, files.ToArray())));
        change?.Invoke(folder);
        return folder;
    }

    static ModuleManifest Parse(object manifest)
    {
        var parsed = ModuleManifest.TryParse(JsonSerializer.Serialize(manifest), out var error);
        Assert.True(parsed is not null, error);
        return parsed!;
    }

    // ---- manifest ---------------------------------------------------------------------------------

    [Fact]
    public void A_well_formed_manifest_has_no_problems_and_reads_every_field()
    {
        var manifest = Parse(ManifestObject(Id, ("Demo.dll", [1, 2, 3, 4])));
        Assert.Empty(manifest.Validate());
        Assert.Equal(Id, manifest.Id);
        Assert.Equal(new ContractRange(1, 1), manifest.Contract);
        Assert.Equal("1.14", manifest.Client.MinProtocol);
        Assert.Equal(["inventory.snapshot"], manifest.Client.Capabilities);
        Assert.Equal("Demo.dll", manifest.Entry.Assembly);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    public void Text_that_is_not_a_manifest_is_refused_with_a_reason(string text)
    {
        Assert.Null(ModuleManifest.TryParse(text, out var error));
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData("Demo")]                  // upper case, one part
    [InlineData("dausuva.demo")]          // two parts
    [InlineData("dausuva..demo")]
    [InlineData("other.sample.demo")]     // does not start with the publisher id
    [InlineData("dausuva.sample.demo!")]
    public void A_module_id_must_be_dotted_lower_case_words_that_start_with_the_publisher(string id)
    {
        Assert.NotEmpty(Parse(ManifestObject(id, ("Demo.dll", [1]))).Validate());
    }

    [Theory]
    [InlineData("../Demo.dll")]
    [InlineData("sub/../Demo.dll")]
    [InlineData("/Demo.dll")]
    [InlineData("C:/Demo.dll")]
    [InlineData("sub\\Demo.dll")]
    [InlineData("Demo.dll:stream")]
    [InlineData("sub//Demo.dll")]
    [InlineData("NUL.dll")]
    [InlineData("com1/Demo.dll")]
    [InlineData("Demo.dll.")]
    [InlineData("")]
    public void An_unsafe_file_path_is_a_manifest_problem(string path)
    {
        var problems = Parse(ManifestObject(Id, (path, [1]))).Validate();
        Assert.Contains(problems, problem => problem.Contains("File") || problem.Contains("entry assembly"));
    }

    [Fact]
    public void A_file_listed_twice_even_in_another_case_is_a_problem()
    {
        var problems = Parse(ManifestObject(Id, ("Demo.dll", [1]), ("demo.DLL", [2]))).Validate();
        Assert.Contains(problems, problem => problem.Contains("listed twice"));
    }

    [Fact]
    public void The_entry_assembly_must_be_one_of_the_listed_files()
    {
        var manifest = Parse(ManifestObject(Id, ("Demo.dll", [1]))) with { Entry = new ModuleEntry("Other.dll", "Demo.Entry") };
        Assert.Contains(manifest.Validate(), problem => problem.Contains("not one of the listed files"));
    }

    [Fact]
    public void Unsupported_schema_contract_range_platform_and_version_are_each_reported()
    {
        var manifest = Parse(ManifestObject(Id, ("Demo.dll", [1]))) with
        {
            Schema = 2, Contract = new ContractRange(3, 2), Os = "linux", Architecture = "x86", Version = "1",
        };
        var problems = manifest.Validate();
        Assert.Contains(problems, problem => problem.Contains("schema"));
        Assert.Contains(problems, problem => problem.Contains("contract range"));
        Assert.Contains(problems, problem => problem.Contains("\"windows\""));
        Assert.Contains(problems, problem => problem.Contains("\"x64\""));
        Assert.Contains(problems, problem => problem.Contains("version"));
    }

    [Fact]
    public void A_bad_sha256_and_a_bad_client_requirement_are_reported()
    {
        var manifest = Parse(ManifestObject(Id, ("Demo.dll", [1]))) with
        {
            Files = [new ModuleFile("Demo.dll", "xyz")],
            Client = new ClientRequirement("one", ["Bad Name", "Bad Name"]),
        };
        var problems = manifest.Validate();
        Assert.Contains(problems, problem => problem.Contains("SHA-256"));
        Assert.Contains(problems, problem => problem.Contains("minProtocol"));
        Assert.Contains(problems, problem => problem.Contains("not a valid capability"));
        Assert.Contains(problems, problem => problem.Contains("listed twice"));
    }

    [Fact]
    public void More_files_than_the_limit_are_refused()
    {
        var files = Enumerable.Range(0, ModuleManifest.MaxFiles + 1).Select(i => ($"f{i}.dll", new byte[] { (byte)i })).ToArray();
        Assert.Contains(Parse(ManifestObject(Id, files)).Validate(), problem => problem.Contains("limit"));
    }

    // ---- package ----------------------------------------------------------------------------------

    [Fact]
    public void A_correct_signed_package_is_accepted()
    {
        var result = ModulePackage.Verify(MakeModule(), new FakeSignatures(), TrustedSet);
        Assert.True(result.Accepted, string.Join("; ", result.Problems));
        Assert.Equal(Id, result.Manifest!.Id);
    }

    [Fact]
    public void A_folder_without_a_manifest_is_refused()
    {
        var folder = Path.Combine(_root, Id);
        Directory.CreateDirectory(folder);
        var result = ModulePackage.Verify(folder, new FakeSignatures(), TrustedSet);
        Assert.False(result.Accepted);
        Assert.Contains("module.json is missing.", result.Problems);
    }

    [Fact]
    public void A_folder_named_differently_from_the_module_id_is_refused()
    {
        var result = ModulePackage.Verify(MakeModule(folderName: "something-else"), new FakeSignatures(), TrustedSet);
        Assert.Contains(result.Problems, problem => problem.Contains("folder is named"));
    }

    [Fact]
    public void A_changed_file_is_refused()
    {
        var folder = MakeModule(change: f => File.WriteAllBytes(Path.Combine(f, "Demo.dll"), [9, 9, 9]));
        var result = ModulePackage.Verify(folder, new FakeSignatures(), TrustedSet);
        Assert.Contains(result.Problems, problem => problem.Contains("does not match the SHA-256"));
    }

    [Fact]
    public void A_missing_file_is_refused()
    {
        var folder = MakeModule(change: f => File.Delete(Path.Combine(f, "Demo.dll")));
        Assert.Contains(ModulePackage.Verify(folder, new FakeSignatures(), TrustedSet).Problems, problem => problem.Contains("missing"));
    }

    [Fact]
    public void A_file_the_manifest_does_not_list_is_refused_even_when_it_is_not_a_program()
    {
        var folder = MakeModule(change: f => File.WriteAllText(Path.Combine(f, "extra.txt"), "x"));
        Assert.Contains(ModulePackage.Verify(folder, new FakeSignatures(), TrustedSet).Problems, problem => problem.Contains("extra.txt") && problem.Contains("does not list"));
    }

    [Fact]
    public void An_unsigned_program_file_is_refused_with_the_verifier_reason()
    {
        var signatures = new FakeSignatures { Default = SignatureCheck.Failed("The file is not signed.") };
        var result = ModulePackage.Verify(MakeModule(), signatures, TrustedSet);
        Assert.Contains(result.Problems, problem => problem.Contains("The file is not signed."));
    }

    [Fact]
    public void A_validly_signed_file_from_an_untrusted_signer_is_refused()
    {
        var signatures = new FakeSignatures { Default = new SignatureCheck(true, new string('B', 64), "") };
        Assert.Contains(ModulePackage.Verify(MakeModule(), signatures, TrustedSet).Problems, problem => problem.Contains("signer this Host does not trust"));
    }

    [Fact]
    public void The_signer_fingerprint_is_compared_without_regard_to_letter_case()
    {
        var signatures = new FakeSignatures { Default = new SignatureCheck(true, Trusted.ToLowerInvariant(), "") };
        Assert.True(ModulePackage.Verify(MakeModule(), signatures, TrustedSet).Accepted);
    }

    [Fact]
    public void Every_program_file_must_be_signed_not_only_the_entry_assembly()
    {
        var folder = MakeModule(extraFiles: ("Helper.dll", [7, 7]));
        var signatures = new FakeSignatures();
        signatures.Answers["Helper.dll"] = SignatureCheck.Failed("The file is not signed.");
        var result = ModulePackage.Verify(folder, signatures, TrustedSet);
        Assert.Contains(result.Problems, problem => problem.StartsWith("\"Helper.dll\""));
        Assert.DoesNotContain(result.Problems, problem => problem.StartsWith("\"Demo.dll\""));
    }

    [Fact]
    public void Data_files_are_hash_checked_but_need_no_signature()
    {
        var folder = MakeModule(extraFiles: ("data/strings.po", [5, 5]));
        var signatures = new FakeSignatures { Default = SignatureCheck.Failed("never asked for data files") };
        signatures.Answers["Demo.dll"] = new SignatureCheck(true, Trusted, "");
        Assert.True(ModulePackage.Verify(folder, signatures, TrustedSet).Accepted);
    }

    [Fact]
    public void A_junction_inside_the_module_is_refused()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "Lib.dll"), [3, 3]);
        var folder = MakeModule(extraFiles: ("lib/Lib.dll", [3, 3]));
        // Replace the real lib folder with a junction to a folder outside the module that holds identical bytes.
        Directory.Delete(Path.Combine(folder, "lib"), true);
        Assert.True(MakeJunction(Path.Combine(folder, "lib"), outside), "mklink /J failed");

        var result = ModulePackage.Verify(folder, new FakeSignatures(), TrustedSet);
        Assert.Contains(result.Problems, problem => problem.Contains("link"));
    }

    static bool MakeJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    // ---- discovery --------------------------------------------------------------------------------

    [Fact]
    public void A_missing_modules_folder_means_no_modules()
    {
        Assert.Empty(ModuleDiscovery.Scan(Path.Combine(_root, "nope"), new FakeSignatures(), TrustedSet));
    }

    [Fact]
    public void Discovery_reports_every_folder_with_its_own_verdict_in_name_order()
    {
        MakeModule();
        MakeModule(folderName: "dausuva.sample.broken", id: "dausuva.sample.broken", change: f => File.WriteAllBytes(Path.Combine(f, "Demo.dll"), [0]));

        var results = ModuleDiscovery.Scan(_root, new FakeSignatures(), TrustedSet);

        Assert.Equal(["dausuva.sample.broken", Id], results.Select(r => Path.GetFileName(r.Folder)));
        Assert.False(results[0].Accepted);
        Assert.True(results[1].Accepted);
    }

    [Fact]
    public void A_second_folder_claiming_an_accepted_id_is_refused()
    {
        MakeModule(folderName: Id);
        var folderB = MakeModule(folderName: "aaa-copy");
        // Folder names must equal the id, so give the copy a manifest that matches its folder name but case-folds onto the first id.
        var json = File.ReadAllText(Path.Combine(folderB, "module.json")).Replace("\"" + Id + "\"", "\"" + Id.ToUpperInvariant() + "\"");
        File.WriteAllText(Path.Combine(folderB, "module.json"), json);

        var results = ModuleDiscovery.Scan(_root, new FakeSignatures(), TrustedSet);

        Assert.Single(results, r => r.Accepted);
    }

    [Fact]
    public void A_module_folder_that_is_a_link_is_refused_without_reading_it()
    {
        var real = MakeModule(folderName: "real-source");
        var modules = Path.Combine(_root, "modules");
        Directory.CreateDirectory(modules);
        Assert.True(MakeJunction(Path.Combine(modules, Id), real), "mklink /J failed");

        var results = ModuleDiscovery.Scan(modules, new FakeSignatures(), TrustedSet);

        Assert.Single(results);
        Assert.False(results[0].Accepted);
        Assert.Contains("The module folder is a link.", results[0].Problems);
    }

    // ---- Windows signature check ------------------------------------------------------------------

    static string SignedProgram()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        var dotnet = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", "dotnet.exe"));
        Assert.True(File.Exists(dotnet), "dotnet.exe was expected at " + dotnet);
        return dotnet;
    }

    [Fact]
    public void Windows_accepts_the_embedded_signature_of_a_signed_program_and_reports_its_signer()
    {
        var check = new WinTrustSignatureVerifier().Check(SignedProgram());
        Assert.True(check.Valid, check.Reason);
        Assert.Equal(64, check.SignerSha256.Length);
    }

    [Fact]
    public void Windows_refuses_a_signed_program_changed_after_signing()
    {
        Directory.CreateDirectory(_root);
        var copy = Path.Combine(_root, "dotnet-changed.exe");
        var bytes = File.ReadAllBytes(SignedProgram());
        bytes[bytes.Length / 3] ^= 0xFF;
        File.WriteAllBytes(copy, bytes);

        var check = new WinTrustSignatureVerifier().Check(copy);

        Assert.False(check.Valid);
        Assert.Equal("", check.SignerSha256);
    }

    [Fact]
    public void Windows_refuses_an_unsigned_file_and_a_missing_file()
    {
        Directory.CreateDirectory(_root);
        var unsigned = Path.Combine(_root, "plain.dll");
        File.WriteAllBytes(unsigned, [1, 2, 3]);
        var verifier = new WinTrustSignatureVerifier();

        Assert.False(verifier.Check(unsigned).Valid);
        Assert.Equal("The file does not exist.", verifier.Check(Path.Combine(_root, "none.dll")).Reason);
    }
}
