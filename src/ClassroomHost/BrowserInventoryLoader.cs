// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.BrowserInventory;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

// Host-owned wire seam, retained for protocol/adapter fixtures. Never exposed to the module.
public interface IBrowserInventoryConnection : IDisposable
{
    Task<JsonObject> ReadAsync(string level, string command, CancellationToken cancellation);
}

/// <summary>Adapts authenticated wire reads to the optional typed inventory workflow.</summary>
public sealed class BrowserInventoryLoader(Func<BrowserInventoryTarget, CancellationToken, Task<IBrowserInventoryConnection>> connect)
{
    public Task<IReadOnlyList<BrowserInventoryResult>> ReadAsync(IReadOnlyList<BrowserInventoryTarget> targets,
        CancellationToken cancellation = default, int maxParallel = 8) =>
        new BrowserInventoryWorkflow(async (target, ct) =>
        {
            try { return new Reader(await connect(target, ct)); }
            catch (UnsupportedFeatureException) { throw new NotSupportedException(); }
        }).ReadAsync(targets, cancellation, maxParallel);

    sealed class Reader(IBrowserInventoryConnection connection) : IBrowserInventoryReader
    {
        async Task<T> Read<T>(Func<Task<T>> action)
        {
            try { return await action(); }
            catch (UnsupportedFeatureException) { throw new NotSupportedException(); }
        }

        public Task<IReadOnlyList<BrowserPolicy>> ReadPoliciesAsync(CancellationToken cancellation) => Read<IReadOnlyList<BrowserPolicy>>(async () =>
        {
            var data = await connection.ReadAsync(Levels.System, Cmd.BrowserPolicyGet, cancellation);
            if (BrowserRules.Policies.Any(policy => data[policy]?.GetValue<string>() is not ("on" or "off" or "partly"))) throw new FormatException();
            return BrowserRules.Policies.Select(policy => new BrowserPolicy(policy, data[policy]!.GetValue<string>())).ToArray();
        });

        public Task<BrowserSnapshot> ReadBrowsersAsync(bool hasUser, CancellationToken cancellation) => Read(async () =>
        {
            JsonObject data;
            try { data = await connection.ReadAsync(hasUser ? Levels.User : Levels.System, Cmd.BrowserList, cancellation); }
            catch (RemoteException) when (hasUser) { data = await connection.ReadAsync(Levels.System, Cmd.BrowserList, cancellation); }
            if (data[BrowserCatalog.Browsers] is not JsonArray items || items.Any(item => item is not JsonObject)) throw new FormatException();
            return new BrowserSnapshot(BrowserCatalog.InstalledFrom(data).Select(browser => new BrowserIdentity(browser.Id, browser.Name)).ToArray(),
                data[BrowserCatalog.Default]?.GetValue<string>() ?? "", data[BrowserCatalog.Managed]?.GetValue<string>() ?? "");
        });

        public Task<IReadOnlyList<BrowserProfile>> ReadProfilesAsync(CancellationToken cancellation) => Read<IReadOnlyList<BrowserProfile>>(async () =>
        {
            var data = await connection.ReadAsync(Levels.User, Cmd.BrowserProfilesList, cancellation);
            if (data[BrowserRules.Profiles] is not JsonArray items || items.Any(item => item is not JsonObject)) throw new FormatException();
            return BrowserRules.ProfilesFrom(data).Select(profile => new BrowserProfile(profile.Browser, profile.Name, profile.Account, profile.IsFirst)).ToArray();
        });

        public void Dispose() => connection.Dispose();
    }
}
