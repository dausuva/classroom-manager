// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;

namespace ClassroomManager.BrowserInventory;

public enum InventoryReadStatus { Success, Unsupported, Unavailable, Failed, NoUser }
public sealed record InventoryRead<T>(InventoryReadStatus Status, T? Value = null) where T : class;
public sealed record BrowserInventoryTarget(int Id, string Computer, bool HasUser, bool SupportsBrowsers, bool SupportsProfiles);
public sealed record BrowserIdentity(string Id, string Name);
public sealed record BrowserPolicy(string Id, string State);
public sealed record BrowserProfile(string Browser, string Name, string Account, bool IsFirst)
{
    public override string ToString() => $"{Browser} \"{Name}\"{(Account.Length > 0 ? $" ({Account})" : "")}";
}
public sealed record BrowserSnapshot(IReadOnlyList<BrowserIdentity> Installed, string Default, string Managed);
public sealed record BrowserInventoryResult(BrowserInventoryTarget Target, InventoryRead<IReadOnlyList<BrowserPolicy>> Policies,
    InventoryRead<BrowserSnapshot> Browsers, InventoryRead<IReadOnlyList<BrowserProfile>> Profiles);

/// <summary>Read-only capabilities; no generic command, path, credential or write hook.</summary>
public interface IBrowserInventoryReader : IDisposable
{
    Task<IReadOnlyList<BrowserPolicy>> ReadPoliciesAsync(CancellationToken cancellation);
    Task<BrowserSnapshot> ReadBrowsersAsync(bool hasUser, CancellationToken cancellation);
    Task<IReadOnlyList<BrowserProfile>> ReadProfilesAsync(CancellationToken cancellation);
}

/// <summary>One invocation owns its selected scope, connection limit and disposable readers.</summary>
public sealed class BrowserInventoryWorkflow(Func<BrowserInventoryTarget, CancellationToken, Task<IBrowserInventoryReader>> connect)
{
    public async Task<IReadOnlyList<BrowserInventoryResult>> ReadAsync(IReadOnlyList<BrowserInventoryTarget> targets,
        CancellationToken cancellation = default, int maxParallel = 8)
    {
        var selected = targets.ToArray();
        if (selected.Select(target => target.Id).Distinct().Count() != selected.Length) throw new ArgumentException(nameof(targets));
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));
        var results = await Task.WhenAll(selected.Select(async target =>
        {
            await gate.WaitAsync(cancellation);
            try
            {
                var opened = await Read(() => connect(target, cancellation), cancellation);
                if (opened.Value is not { } reader)
                    return new BrowserInventoryResult(target, new(opened.Status), new(opened.Status), new(opened.Status));
                using (reader)
                {
                    var policies = await Read(async () => Copy(await reader.ReadPoliciesAsync(cancellation)), cancellation);
                    var browsers = target.SupportsBrowsers
                        ? await Read(async () =>
                        {
                            var snapshot = await reader.ReadBrowsersAsync(target.HasUser, cancellation);
                            return snapshot with { Installed = Copy(snapshot.Installed) };
                        }, cancellation)
                        : new InventoryRead<BrowserSnapshot>(InventoryReadStatus.Unsupported);
                    var profiles = !target.HasUser ? new InventoryRead<IReadOnlyList<BrowserProfile>>(InventoryReadStatus.NoUser)
                        : !target.SupportsProfiles ? new InventoryRead<IReadOnlyList<BrowserProfile>>(InventoryReadStatus.Unsupported)
                        : await Read(async () => Copy(await reader.ReadProfilesAsync(cancellation)), cancellation);
                    return new BrowserInventoryResult(target, policies, browsers, profiles);
                }
            }
            finally { gate.Release(); }
        }));
        return Array.AsReadOnly(results);
    }

    static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source) => Array.AsReadOnly(source.ToArray());

    static async Task<InventoryRead<T>> Read<T>(Func<Task<T>> action, CancellationToken cancellation) where T : class
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var value = await action();
            if (cancellation.IsCancellationRequested && value is IDisposable disposable) disposable.Dispose();
            cancellation.ThrowIfCancellationRequested();
            if (value is null) throw new InvalidOperationException();
            return new(InventoryReadStatus.Success, value);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (NotSupportedException) { return new(InventoryReadStatus.Unsupported); }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        { return new(InventoryReadStatus.Unavailable); }
        catch (Exception) { return new(InventoryReadStatus.Failed); }
    }
}

public sealed record BrowserPresence(BrowserIdentity Browser, int Present, IReadOnlyList<string> Missing);
public sealed record BrowserCount(string Name, int Count);
public sealed record BrowserPolicyCount(string Id, int On);
public sealed record BrowserExtraProfiles(string Computer, IReadOnlyList<BrowserProfile> Profiles);
public sealed record BrowserInventoryReport(int Total, int BrowsersRead, int PoliciesRead, int ProfilesRead,
    IReadOnlyList<BrowserPresence> MainBrowsers, IReadOnlyList<BrowserCount> Others,
    IReadOnlyList<BrowserCount> Defaults, IReadOnlyList<BrowserCount> Managed,
    IReadOnlyList<BrowserPolicyCount> Policies, IReadOnlyList<BrowserExtraProfiles> ExtraProfiles,
    IReadOnlyList<BrowserInventoryResult> Incomplete, IReadOnlyList<BrowserIdentity> DefaultChoices);

public static class BrowserInventoryModule
{
    public const string Id = "dausuva.browser.inventory";
    public const int ContractVersion = 1;

    /// <summary>Pure report projection; the core supplies its catalog, never wire payloads.</summary>
    public static BrowserInventoryReport Project(IReadOnlyList<BrowserInventoryResult> answers,
        IReadOnlyList<BrowserIdentity> main, IReadOnlyList<BrowserIdentity> known, IReadOnlyList<string> policyIds)
    {
        var read = answers.Where(answer => answer.Browsers.Status == InventoryReadStatus.Success).ToArray();
        var policies = answers.Where(answer => answer.Policies.Status == InventoryReadStatus.Success).ToArray();
        var profiles = answers.Where(answer => answer.Profiles.Status == InventoryReadStatus.Success).ToArray();
        var presence = main.Select(browser =>
        {
            var missing = read.Where(answer => answer.Browsers.Value!.Installed.All(item => item.Id != browser.Id))
                .Select(answer => answer.Target.Computer).ToArray();
            return new BrowserPresence(browser, read.Length - missing.Length, Array.AsReadOnly(missing));
        }).ToArray();
        string Name(string id) => known.FirstOrDefault(browser => browser.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;
        BrowserCount[] Count(IEnumerable<string> names) => names.GroupBy(name => name).Select(group => new BrowserCount(group.Key, group.Count())).ToArray();
        var choices = main.ToList();
        foreach (var browser in read.SelectMany(answer => answer.Browsers.Value!.Installed))
            if (choices.All(choice => !choice.Id.Equals(browser.Id, StringComparison.OrdinalIgnoreCase))) choices.Add(browser);
        return new(answers.Count, read.Length, policies.Length, profiles.Length,
            Array.AsReadOnly(presence),
            Array.AsReadOnly(read.SelectMany(answer => answer.Browsers.Value!.Installed)
                .Where(browser => !main.Any(item => item.Id.Equals(browser.Id, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(browser => browser.Name, StringComparer.OrdinalIgnoreCase).Select(group => new BrowserCount(group.Key, group.Count())).ToArray()),
            Array.AsReadOnly(Count(read.Select(answer => answer.Browsers.Value!.Default).Where(id => id.Length > 0).Select(Name))),
            Array.AsReadOnly(Count(read.Select(answer => answer.Browsers.Value!.Managed).Where(id => id.Length > 0).Select(Name))),
            Array.AsReadOnly(policyIds.Select(id => new BrowserPolicyCount(id, policies.Count(answer => answer.Policies.Value!.Any(policy => policy.Id == id && policy.State == "on")))).ToArray()),
            Array.AsReadOnly(profiles.Select(answer => new BrowserExtraProfiles(answer.Target.Computer,
                Array.AsReadOnly(answer.Profiles.Value!.Where(profile => !profile.IsFirst).ToArray()))).Where(extra => extra.Profiles.Count > 0).ToArray()),
            Array.AsReadOnly(answers.Where(answer => answer.Policies.Status != InventoryReadStatus.Success ||
                answer.Browsers.Status != InventoryReadStatus.Success || answer.Profiles.Status != InventoryReadStatus.Success).ToArray()),
            Array.AsReadOnly(choices.ToArray()));
    }
}
