// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Modules;

namespace ClassroomManager.Appearance;

/// <summary>
/// What the module asks of a computer about appearance, through the run's ports only: the four generic commands. It sends as the signed-in
/// user, and a computer decides how each setting is applied; the module only names settings and values.
/// </summary>
public sealed class AppearanceOperations(IModuleClient client)
{
    public const string Catalog = "appearance.catalog";
    public const string Get = "appearance.get";
    public const string Set = "appearance.set";
    public const string Reset = "appearance.reset";

    public static IReadOnlyList<string> Commands { get; } = [Catalog, Get, Set, Reset];

    public async Task<AppearanceComputerCatalogue> CatalogAsync(ModuleComputer computer, CancellationToken cancellation) =>
        AppearanceWireReader.ReadCatalogue(await client.CallAsync(computer, Catalog, "{}", cancellation));

    public async Task<IReadOnlyDictionary<string, string>> GetAsync(ModuleComputer computer, IReadOnlyCollection<string> ids, CancellationToken cancellation)
    {
        var answer = JsonNode.Parse(await client.CallAsync(computer, Get, new JsonObject { ["ids"] = Ids(ids) }.ToJsonString(), cancellation)) as JsonObject;
        return (answer?["values"] as JsonObject ?? []).Where(pair => pair.Value is JsonValue)
            .ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal);
    }

    /// <summary>Sets several settings together. Every value is sent as text ("true", "dark", "125").</summary>
    public async Task<IReadOnlyList<AppearanceResult>> SetAsync(ModuleComputer computer, IReadOnlyDictionary<string, string> values, CancellationToken cancellation)
    {
        var request = new JsonObject { ["values"] = new JsonObject(values.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))) };
        return AppearanceWireReader.ReadResults(await client.CallAsync(computer, Set, request.ToJsonString(), cancellation));
    }

    /// <summary>Puts the named settings back to what they were before Classroom Manager changed them.</summary>
    public async Task<IReadOnlyList<AppearanceResult>> ResetAsync(ModuleComputer computer, IReadOnlyCollection<string> ids, CancellationToken cancellation) =>
        AppearanceWireReader.ReadResults(await client.CallAsync(computer, Reset, new JsonObject { ["ids"] = Ids(ids) }.ToJsonString(), cancellation));

    static JsonArray Ids(IReadOnlyCollection<string> ids) => new(ids.Select(id => (JsonNode)id).ToArray());
}
