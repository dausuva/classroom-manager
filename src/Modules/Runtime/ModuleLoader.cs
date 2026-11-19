// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace ClassroomManager.Modules;

/// <summary>The outcome of loading a verified package: its entry object, or every reason it could not be used.</summary>
public sealed record ModuleLoadResult(IClassroomModule? Module, ModuleDescriptor? Descriptor, IReadOnlyList<string> Problems)
{
    public bool Loaded => Module is not null && Problems.Count == 0;

    public static ModuleLoadResult Failed(params string[] problems) => new(null, null, problems);
}

/// <summary>
/// Loads the code of a package that <see cref="ModulePackage.Verify"/> accepted. Each file is read once, compared again with the SHA-256 in
/// the manifest and loaded from those bytes, so a file replaced after the check is never run. The module lives in its own load context;
/// the contract assembly is shared with the Host so the entry type can be used through <see cref="IClassroomModule"/>.
/// </summary>
public static class ModuleLoader
{
    const string ContractAssembly = "ClassroomManager.Modules.Contract";

    public static ModuleLoadResult Load(ModulePackageResult package, int hostContract = ModuleContract.CurrentVersion)
    {
        if (!package.Accepted || package.Manifest is not { } manifest) return ModuleLoadResult.Failed("The package was not accepted, so it is not loaded.");
        if (hostContract < manifest.Contract.Min || hostContract > manifest.Contract.Max)
            return ModuleLoadResult.Failed($"The module was written for contract versions {manifest.Contract.Min} to {manifest.Contract.Max}; this Host speaks {hostContract}.");

        try
        {
            var context = new PackageLoadContext(package.Folder, manifest);
            var assembly = context.LoadListed(manifest.Entry.Assembly);
            var type = assembly.GetType(manifest.Entry.Type, throwOnError: false);
            if (type is null) return ModuleLoadResult.Failed($"The entry type \"{manifest.Entry.Type}\" is not in {manifest.Entry.Assembly}.");
            if (!type.IsPublic || type.IsAbstract || !typeof(IClassroomModule).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                return ModuleLoadResult.Failed($"\"{manifest.Entry.Type}\" must be a public, non-abstract class with a parameterless constructor that implements IClassroomModule.");

            var module = (IClassroomModule)Activator.CreateInstance(type)!;
            var descriptor = module.Describe();
            var problems = new List<string>();
            if (descriptor.Id != manifest.Id) problems.Add($"The module says it is \"{descriptor.Id}\" but its manifest says \"{manifest.Id}\".");
            if (descriptor.ContractVersion < manifest.Contract.Min || descriptor.ContractVersion > manifest.Contract.Max)
                problems.Add($"The module reports contract version {descriptor.ContractVersion}, outside its own range {manifest.Contract.Min} to {manifest.Contract.Max}.");
            problems.AddRange(ModuleDescriptorRules.Problems(descriptor));
            if (!descriptor.Ports.OrderBy(port => port.Name, StringComparer.Ordinal).SequenceEqual(manifest.RequestedPorts.OrderBy(port => port.Name, StringComparer.Ordinal)))
                problems.Add("The ports the module asks for differ from those in its manifest.");
            if (!descriptor.SystemCommands.Order(StringComparer.Ordinal).SequenceEqual(manifest.DeclaredSystemCommands.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                problems.Add("The system commands of the module differ from those in its manifest.");
            if (!descriptor.Sinks.Order(StringComparer.Ordinal).SequenceEqual(manifest.DeclaredSinks.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                problems.Add("The sinks of the module differ from those in its manifest.");
            if (!descriptor.Uploads.Order(StringComparer.Ordinal).SequenceEqual(manifest.DeclaredUploads.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                problems.Add("The uploads of the module differ from those in its manifest.");
            if (!descriptor.ClientCapabilities.Order(StringComparer.Ordinal).SequenceEqual(manifest.Client.Capabilities.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                problems.Add("The client capabilities in the module differ from those in its manifest.");
            return problems.Count == 0 ? new(module, descriptor, problems) : new(null, descriptor, problems);
        }
        // Package code runs here (its constructor and Describe); whatever it throws must refuse the module, never stop the Host.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ModuleLoadResult.Failed("The module could not be loaded: " + (exception is TargetInvocationException { InnerException: { } inner } ? inner : exception).Message);
        }
    }

    sealed class PackageLoadContext(string folder, ModuleManifest manifest) : AssemblyLoadContext("module:" + manifest.Id, isCollectible: true)
    {
        public Assembly LoadListed(string relative)
        {
            var file = manifest.Files.First(item => item.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
            var bytes = File.ReadAllBytes(ModuleManifest.Combine(folder, file.Path));
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"\"{file.Path}\" changed after it was checked.");
            return LoadFromStream(new MemoryStream(bytes, writable: false));
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null || name.Name == ContractAssembly) return null;
            var file = manifest.Files.FirstOrDefault(item => item.Path.Equals(name.Name + ".dll", StringComparison.OrdinalIgnoreCase));
            return file is null ? null : LoadListed(file.Path);
        }
    }
}
