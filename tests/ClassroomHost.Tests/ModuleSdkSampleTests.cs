// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Modules;
using Xunit;

namespace ClassroomHost.Tests;

// The module author's path end to end: a project that references only the contract is built and packed by the SDK script, the Host
// verifies and loads the package, and the module runs with exactly the ports it asked for.
public sealed class ModuleSdkSampleTests : IDisposable
{
    const string Trusted = "BBBB0000BBBB0000BBBB0000BBBB0000BBBB0000BBBB0000BBBB0000BBBB0000";

    readonly string _scratch = Path.Combine(Path.GetTempPath(), "module-sdk-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    sealed class AcceptAll : IModuleSignatureVerifier
    {
        public SignatureCheck Check(string path) => new(true, Trusted, "");
    }

    static string SampleSource => Path.Combine(StringExtractor.RepositoryRoot(), "samples", "ModuleSdk", "Hello");

    [Fact]
    public void The_sample_names_nothing_that_goes_around_its_ports()
    {
        var problems = Directory.EnumerateFiles(SampleSource, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(@"\obj\") && !path.Contains(@"\bin\"))
            .SelectMany(path => ModuleAuthorityTests.Violations(File.ReadAllText(path)).Select(problem => $"{Path.GetFileName(path)} {problem}"));

        Assert.Empty(problems);
        var project = File.ReadAllText(Path.Combine(SampleSource, "ClassroomManager.Samples.Hello.csproj"));
        Assert.DoesNotContain("<PackageReference", project);
        Assert.DoesNotContain("<FrameworkReference", project);
    }

    [Fact]
    public async Task The_sdk_script_packs_the_sample_the_Host_verifies_loads_and_runs_with_its_ports()
    {
        Directory.CreateDirectory(_scratch);
        var (exit, output, errors) = await PowerShellScript.RunAsync(PowerShellEdition.Windows, @"samples\ModuleSdk\Pack-Module.ps1",
        [
            "-Project", Path.Combine(SampleSource, "ClassroomManager.Samples.Hello.csproj"), "-Id", "example.hello.sample", "-Publisher", "example",
            "-Name", "Hello sample", "-Version", "1.0.0", "-EntryType", "ClassroomManager.Samples.Hello.HelloModule", "-Output", _scratch,
        ], TimeSpan.FromMinutes(4));
        Assert.True(exit == 0, output + errors);

        var folder = Path.Combine(_scratch, "example.hello.sample");
        var verified = ModulePackage.Verify(folder, new AcceptAll(), new HashSet<string> { Trusted });
        Assert.True(verified.Accepted, string.Join("; ", verified.Problems));
        Assert.Equal(["Example.Hello.dll"], verified.Manifest!.Files.Select(file => file.Path));   // the contract is not shipped: the Host has it

        var loaded = ModuleLoader.Load(verified);
        Assert.True(loaded.Loaded, string.Join("; ", loaded.Problems));
        Assert.Equal("Hello sample", loaded.Descriptor!.Name);

        var registry = new ModuleRegistry();
        registry.AddPackages([verified]);
        var lines = new List<string>();
        registry.AttachAll(descriptor => ModuleBroker.Grant(descriptor, () => _scratch, lines.Add, new FixedTime(new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero))));

        Assert.Contains(lines, line => line.Contains("Hello from the sample module, at 2026-11-01 09:30 UTC."));
        Assert.True(registry.IsEnabled("example.hello.sample", null));
    }

    [Fact]
    public async Task A_package_signed_by_nobody_this_Host_trusts_is_refused_with_the_reason()
    {
        Directory.CreateDirectory(_scratch);
        var (exit, output, errors) = await PowerShellScript.RunAsync(PowerShellEdition.Windows, @"samples\ModuleSdk\Pack-Module.ps1",
        [
            "-Project", Path.Combine(SampleSource, "ClassroomManager.Samples.Hello.csproj"), "-Id", "example.hello.sample", "-Publisher", "example",
            "-Name", "Hello sample", "-Version", "1.0.0", "-EntryType", "ClassroomManager.Samples.Hello.HelloModule", "-Output", _scratch,
        ], TimeSpan.FromMinutes(4));
        Assert.True(exit == 0, output + errors);

        var asShipped = ModulePackage.Verify(Path.Combine(_scratch, "example.hello.sample"), new WinTrustSignatureVerifier(), BuiltInModules.TrustedSigners);

        Assert.False(asShipped.Accepted);
        Assert.Contains(asShipped.Problems, problem => problem.Contains("Example.Hello.dll"));
    }

    public sealed class LabSigningKeyFactAttribute : FactAttribute
    {
        public LabSigningKeyFactAttribute()
        {
            using var store = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
            var found = store.Certificates.Find(System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, "072CE6159C005061D05CAA90493593FD83DB7B33", false);
            if (found.Count == 0 || !found[0].HasPrivateKey) Skip = "Needs the Dausuva lab code signing key in the current user's store (the teacher PC has it).";
        }
    }

    [LabSigningKeyFact]
    public async Task A_package_signed_with_the_Dausuva_signing_key_is_accepted_through_the_real_signature_check_and_a_changed_file_is_not()
    {
        Directory.CreateDirectory(_scratch);
        var (exit, output, errors) = await PowerShellScript.RunAsync(PowerShellEdition.Windows, @"samples\ModuleSdk\Pack-Module.ps1",
        [
            "-Project", Path.Combine(SampleSource, "ClassroomManager.Samples.Hello.csproj"), "-Id", "example.hello.sample", "-Publisher", "example",
            "-Name", "Hello sample", "-Version", "1.0.0", "-EntryType", "ClassroomManager.Samples.Hello.HelloModule", "-Output", _scratch,
            "-Sign", "072CE6159C005061D05CAA90493593FD83DB7B33",
        ], TimeSpan.FromMinutes(4));
        Assert.True(exit == 0, output + errors);
        var folder = Path.Combine(_scratch, "example.hello.sample");

        var accepted = ModulePackage.Verify(folder, new WinTrustSignatureVerifier(), BuiltInModules.TrustedSigners);
        var wrongSigner = ModulePackage.Verify(folder, new WinTrustSignatureVerifier(), new HashSet<string> { Trusted });

        Assert.True(accepted.Accepted, string.Join("; ", accepted.Problems));
        Assert.False(wrongSigner.Accepted);
        Assert.Contains(wrongSigner.Problems, problem => problem.Contains("does not trust"));

        File.AppendAllText(Path.Combine(folder, "Example.Hello.dll"), "x");
        Assert.False(ModulePackage.Verify(folder, new WinTrustSignatureVerifier(), BuiltInModules.TrustedSigners).Accepted);
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
