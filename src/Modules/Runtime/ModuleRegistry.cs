// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Modules;

public enum ModuleSource { BuiltIn, Package }

/// <summary>One module the Host knows about, usable or refused. A refused package is kept so Settings can say why.</summary>
public sealed record ModuleRegistration(string Id, string Name, ModuleSource Source, string? Folder, ModuleDescriptor? Descriptor, IReadOnlyList<string> Problems)
{
    /// <summary>The running module object, for the Host to hand its ports to. Null for a refused package.</summary>
    public IClassroomModule? Instance { get; init; }

    public bool Usable => Descriptor is not null && Problems.Count == 0;
}

/// <summary>
/// Every module of this Host, built into it or installed as a package. A built-in module wins over a package with the same id.
/// Whether a module is switched on is the person's choice (a list of switched-off ids) and is answered here, in one place.
/// </summary>
public sealed class ModuleRegistry
{
    readonly List<ModuleRegistration> _all = [];

    public IReadOnlyList<ModuleRegistration> All => _all;

    public IReadOnlyList<ModuleRegistration> Usable => _all.Where(item => item.Usable).ToArray();

    /// <summary>The scheduled actions of the usable modules, each with the module it belongs to.</summary>
    public IReadOnlyList<(ModuleAction Action, string ModuleId)> ScheduledActions =>
        Usable.SelectMany(module => module.Descriptor!.ScheduledActions.Select(action => (action, module.Id))).ToArray();

    /// <summary>The module that provides the scheduled action <paramref name="code"/>, or null for an action the Host has itself.</summary>
    public string? ActionOwner(string code) => ScheduledActions.Where(item => item.Action.Code == code).Select(item => item.ModuleId).FirstOrDefault();

    public ModuleRegistration? Find(string id) => _all.FirstOrDefault(item => item.Id == id);

    public void AddBuiltIn(IClassroomModule module)
    {
        var descriptor = module.Describe();
        if (Find(descriptor.Id) is not null) throw new InvalidOperationException($"The module \"{descriptor.Id}\" is registered twice.");
        var problems = ModuleDescriptorRules.Problems(descriptor);
        if (problems.Count > 0) throw new InvalidOperationException($"The built-in module \"{descriptor.Id}\" is not valid: {string.Join(" ", problems)}");
        if (descriptor.ScheduledActions.FirstOrDefault(action => ActionOwner(action.Code) is not null) is { } taken)
            throw new InvalidOperationException($"The action \"{taken.Code}\" is already provided by \"{ActionOwner(taken.Code)}\".");
        _all.Add(new(descriptor.Id, descriptor.Name, ModuleSource.BuiltIn, null, descriptor, []) { Instance = module });
    }

    /// <summary>Registers the result of <see cref="ModuleDiscovery.Scan"/>. Accepted packages are loaded through <paramref name="load"/>.</summary>
    public void AddPackages(IEnumerable<ModulePackageResult> packages, Func<ModulePackageResult, ModuleLoadResult>? load = null)
    {
        load ??= package => ModuleLoader.Load(package);
        foreach (var package in packages)
        {
            var id = package.Manifest?.Id ?? Path.GetFileName(package.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var name = package.Manifest?.Name ?? id;
            if (!package.Accepted) { _all.Add(new(id, name, ModuleSource.Package, package.Folder, null, package.Problems)); continue; }
            if (Find(id) is { Usable: true })
            {
                _all.Add(new(id, name, ModuleSource.Package, package.Folder, null, [$"The module \"{id}\" is already provided; the first one wins."]));
                continue;
            }
            var loaded = load(package);
            var problems = loaded.Problems.ToList();
            if (loaded.Descriptor?.ScheduledActions.FirstOrDefault(action => ActionOwner(action.Code) is not null) is { } taken)
                problems.Add($"The action \"{taken.Code}\" is already provided by \"{ActionOwner(taken.Code)}\".");
            _all.Add(new(id, name, ModuleSource.Package, package.Folder, problems.Count == 0 ? loaded.Descriptor : null, problems) { Instance = problems.Count == 0 ? loaded.Module : null });
        }
    }

    /// <summary>Gives every usable module the ports it asked for, once. <paramref name="grant"/> builds the ports for one descriptor.</summary>
    public void AttachAll(Func<ModuleDescriptor, ModuleServices> grant)
    {
        foreach (var module in Usable)
            module.Instance?.Attach(grant(module.Descriptor!));
    }

    /// <summary>True when the module is known and usable and the person has not switched it off. Unknown ids are off.</summary>
    public bool IsEnabled(string id, IEnumerable<string>? switchedOff) =>
        Find(id) is { Usable: true } && !(switchedOff ?? []).Contains(id, StringComparer.Ordinal);
}

/// <summary>Host and Client compatibility: a module's feature is offered for a Client only when the Client announces what the module needs.</summary>
public static class ModuleCompatibility
{
    /// <summary>The capabilities the module needs that the Client did not announce. Empty means the Client can serve the module.</summary>
    public static IReadOnlyList<string> MissingClientCapabilities(ModuleDescriptor module, IEnumerable<string> announced)
    {
        var have = announced.ToHashSet(StringComparer.Ordinal);
        return module.ClientCapabilities.Where(need => !have.Contains(need)).ToArray();
    }
}
