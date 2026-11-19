// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Tells Windows a change was made, so it shows without signing out. A failure here never fails the change.</summary>
interface IAppearanceShell
{
    void Refresh(IReadOnlySet<AppearanceRefresh> needed);
}

/// <summary>Asks the person at the computer whether to keep a change that can leave nobody a picture. When nobody answers, <c>revert</c> runs.</summary>
interface IAppearanceConfirmer
{
    void Ask(string what, int seconds, Action keep, Action revert);
}

/// <summary>
/// The Appearance catalogue of the signed-in user (runs in the per-user helper): which settings this Windows build has, their current
/// values, and a change of several together. The first time a setting is changed its stored values are saved, so a reset puts back exactly
/// what was there (including "no value"), never a guessed default.
/// </summary>
sealed class AppearanceHandler(IAppearanceStore store, IAppearanceShell shell, string originalsPath, int windowsBuild, IAppearanceConfirmer? confirmer = null)
{
    /// <summary>How long the person at the computer has to keep a change that needs confirming.</summary>
    public const int ConfirmSeconds = 15;

    readonly object _gate = new();
    List<AppearanceSetting>? _awaiting;

    /// <summary>Windows 11 starts at build 22000; everything below that, from 10240, is Windows 10.</summary>
    string WindowsName => windowsBuild >= 22000 ? "11" : "10";

    public Response Catalog()
    {
        try
        {
            lock (_gate)
            {
                var originals = LoadOriginals();
                var settings = AppearanceSettings.All.Where(setting => setting.SupportedOn(windowsBuild) && IsAvailable(setting)).Select(setting => Describe(setting, originals)).ToArray();
                return Response.Success(new JsonObject
                {
                    ["windows"] = WindowsName, ["build"] = windowsBuild,
                    ["settings"] = new JsonArray(settings.Cast<JsonNode>().ToArray()),
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            return Response.Fail("Could not read the appearance settings: " + ex.Message);
        }
    }

    public Response Get(JsonObject a)
    {
        if (!Ids(a, out var ids, out var problem)) return Response.Fail(problem);
        try
        {
            lock (_gate)
            {
                var values = new JsonObject();
                var unsupported = new JsonArray();
                foreach (var id in ids)
                {
                    if (AppearanceSettings.Find(id) is { } setting && setting.SupportedOn(windowsBuild)) values[id] = Read(setting);
                    else unsupported.Add(id);
                }
                return Response.Success(new JsonObject { ["values"] = values, ["unsupported"] = unsupported });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Response.Fail("Could not read the appearance settings: " + ex.Message);
        }
    }

    public Response Set(JsonObject a)
    {
        if (a["values"] is not JsonObject values || values.Count == 0) return Response.Fail("Name at least one setting in \"values\".");
        if (values.Count > AppearanceWire.MaxSettingsPerRequest) return Response.Fail($"At most {AppearanceWire.MaxSettingsPerRequest} settings can be set at once.");
        lock (_gate)
        {
            var results = new JsonArray();
            var refreshes = new HashSet<AppearanceRefresh>();
            var confirmed = new List<AppearanceSetting>();
            Dictionary<string, RawValue?[]?> originals;
            try { originals = LoadOriginals(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return Response.Fail("Could not read what Classroom Manager saved before changing the settings: " + ex.Message);
            }
            foreach (var (id, node) in values)
            {
                var wanted = node is JsonValue value && value.TryGetValue<string>(out var text) ? text
                    : node is JsonValue other && other.TryGetValue<bool>(out var flag) ? (flag ? "true" : "false")
                    : node is JsonValue number && number.TryGetValue<int>(out var whole) ? whole.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                var result = SetOne(id, wanted, originals, refreshes);
                results.Add(result);
                if (result["status"]!.GetValue<string>() == AppearanceWire.Applied && AppearanceSettings.Find(id) is { Risk: AppearanceWire.RiskConfirm } risky)
                {
                    confirmed.Add(risky);
                    result["confirmSeconds"] = ConfirmSeconds;
                }
            }
            Refresh(refreshes);
            if (confirmed.Count > 0) AskThePerson(confirmed);
            return Response.Success(new JsonObject { ["results"] = results });
        }
    }

    public Response Reset(JsonObject a)
    {
        lock (_gate)
        {
            Dictionary<string, RawValue?[]?> originals;
            try { originals = LoadOriginals(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return Response.Fail("Could not read what Classroom Manager saved before changing the settings: " + ex.Message);
            }
            IReadOnlyList<string> ids;
            if (a["all"] is JsonValue all && all.TryGetValue<bool>(out var everything) && everything) ids = originals.Keys.Order(StringComparer.Ordinal).ToArray();
            else if (!Ids(a, out ids, out var problem)) return Response.Fail(problem);

            var results = new JsonArray();
            var refreshes = new HashSet<AppearanceRefresh>();
            foreach (var id in ids) results.Add(ResetOne(id, originals, refreshes));
            Refresh(refreshes);
            return Response.Success(new JsonObject { ["results"] = results });
        }
    }

    JsonObject SetOne(string id, string? wanted, Dictionary<string, RawValue?[]?> originals, HashSet<AppearanceRefresh> refreshes)
    {
        if (AppearanceSettings.Find(id) is not { } setting || !setting.SupportedOn(windowsBuild)) return Result(id, AppearanceWire.Unsupported);
        var raw = wanted is null ? null : setting.ToRaw(wanted);
        if (raw is null || raw.Count != setting.Bindings.Count || (setting.DynamicChoices is not null && !setting.OfferedChoices.Any(choice => choice.Value == wanted)))
            return Result(id, AppearanceWire.Failed, "That is not a value this setting can have.");
        if (setting.Risk == AppearanceWire.RiskConfirm)
        {
            if (confirmer is null) return Result(id, AppearanceWire.Failed, "This computer cannot ask the person at it to confirm the change, so it was not made.");
            if (_awaiting is not null) return Result(id, AppearanceWire.Failed, "A change is already waiting to be confirmed on this computer.");
        }
        try
        {
            var before = ReadRaw(setting);
            if (setting.Current(before) == setting.Current(raw)) return Result(id, AppearanceWire.Unchanged);
            if (!originals.ContainsKey(id))
            {
                originals[id] = before.ToArray();
                SaveOriginals(originals);
            }
            for (var index = 0; index < setting.Bindings.Count; index++) WriteRaw(setting.Bindings[index], raw[index]);
            if (setting.Refresh != AppearanceRefresh.None) refreshes.Add(setting.Refresh);
            return Result(id, AppearanceWire.Applied);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Result(id, AppearanceWire.Failed, ex.Message);
        }
    }

    JsonObject ResetOne(string id, Dictionary<string, RawValue?[]?> originals, HashSet<AppearanceRefresh> refreshes)
    {
        if (!originals.TryGetValue(id, out var saved) || saved is null) return Result(id, AppearanceWire.Unchanged);
        var setting = AppearanceSettings.Find(id);
        if (setting is null || saved.Length != setting.Bindings.Count) return Result(id, AppearanceWire.Failed, "What was saved no longer fits this setting.");
        try
        {
            for (var index = 0; index < setting.Bindings.Count; index++) WriteRaw(setting.Bindings[index], saved[index]);
            originals.Remove(id);
            SaveOriginals(originals);
            if (setting.Refresh != AppearanceRefresh.None) refreshes.Add(setting.Refresh);
            // Going back to what was there is safe, so it needs no question; it is made permanent at once.
            if (setting.Risk == AppearanceWire.RiskConfirm) foreach (var binding in setting.Bindings) store.Persist(binding.Path, binding.Name);
            return Result(id, AppearanceWire.Restored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Result(id, AppearanceWire.Failed, ex.Message);
        }
    }

    JsonObject Describe(AppearanceSetting setting, Dictionary<string, RawValue?[]?> originals)
    {
        var description = new JsonObject
        {
            ["id"] = setting.Id, ["category"] = setting.Category, ["kind"] = setting.Kind, ["label"] = setting.Label,
            ["apply"] = setting.Apply, ["risk"] = setting.Risk, ["value"] = Read(setting), ["changed"] = originals.ContainsKey(setting.Id),
        };
        var offered = setting.OfferedChoices;
        if (offered.Count > 0)
            description["choices"] = new JsonArray(offered.Select(choice => (JsonNode)new JsonObject { ["value"] = choice.Value, ["label"] = choice.Label }).ToArray());
        if (setting.Kind == AppearanceWire.KindNumber) { description["min"] = setting.Min; description["max"] = setting.Max; description["step"] = setting.Step; }
        return description;
    }

    // One question for everything that was changed in this request. Keep makes it permanent; no answer, or "revert", puts every one back.
    void AskThePerson(List<AppearanceSetting> changed)
    {
        _awaiting = changed;
        var what = string.Join(", ", changed.Select(setting => setting.Label.ToLowerInvariant()));
        confirmer!.Ask($"Classroom Manager changed the {what}.", ConfirmSeconds,
            keep: () => { lock (_gate) { Settle(keep: true); } },
            revert: () => { lock (_gate) { Settle(keep: false); } });
    }

    void Settle(bool keep)
    {
        if (_awaiting is not { } changed) return;
        _awaiting = null;
        var originals = LoadOriginals();
        foreach (var setting in changed)
        {
            try
            {
                if (keep) { foreach (var binding in setting.Bindings) store.Persist(binding.Path, binding.Name); continue; }
                if (originals.TryGetValue(setting.Id, out var saved) && saved is not null)
                    for (var index = 0; index < setting.Bindings.Count; index++) WriteRaw(setting.Bindings[index], saved[index]);
                originals.Remove(setting.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { Log.Write($"appearance: could not settle {setting.Id}: {ex.Message}"); }
        }
        if (!keep) SaveOriginals(originals);
    }

    // A setting whose values cannot be read here (a computer with no display to ask) is left out rather than failing the whole catalogue.
    bool IsAvailable(AppearanceSetting setting)
    {
        try { _ = setting.OfferedChoices; _ = Read(setting); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return false; }
    }

    string Read(AppearanceSetting setting) => setting.Current(ReadRaw(setting));

    RawValue?[] ReadRaw(AppearanceSetting setting) => setting.Bindings.Select(binding => store.Read(binding.Path, binding.Name)).ToArray();

    void WriteRaw(AppearanceBinding binding, RawValue? value)
    {
        if (value is { } raw) store.Write(binding.Path, binding.Name, raw);
        else store.Delete(binding.Path, binding.Name);
    }

    void Refresh(HashSet<AppearanceRefresh> needed)
    {
        if (needed.Count == 0) return;
        try { shell.Refresh(needed); }
        // The values are written; Windows shows them at the next sign-in at the latest.
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write("Appearance refresh failed: " + ex.Message); }
    }

    static JsonObject Result(string id, string status, string? reason = null)
    {
        var result = new JsonObject { ["id"] = id, ["status"] = status };
        if (reason is not null) result["reason"] = reason;
        return result;
    }

    static bool Ids(JsonObject a, out IReadOnlyList<string> ids, out string problem)
    {
        ids = [];
        problem = "";
        if (a["ids"] is not JsonArray list || list.Count == 0) { problem = "Name at least one setting in \"ids\"."; return false; }
        if (list.Count > AppearanceWire.MaxSettingsPerRequest) { problem = $"At most {AppearanceWire.MaxSettingsPerRequest} settings can be named at once."; return false; }
        var found = new List<string>();
        foreach (var node in list)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var id) || id.Length == 0 || id.Length > 100) { problem = "Every id must be a short text."; return false; }
            found.Add(id);
        }
        ids = found.Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }

    // What was there before Classroom Manager's first change, per setting: one entry per stored value; a null entry means "no value".
    Dictionary<string, RawValue?[]?> LoadOriginals()
    {
        var originals = new Dictionary<string, RawValue?[]?>(StringComparer.Ordinal);
        if (!File.Exists(originalsPath)) return originals;
        if (JsonNode.Parse(File.ReadAllText(originalsPath)) is not JsonObject saved) return originals;
        foreach (var (id, node) in saved)
        {
            if (node is not JsonArray entries) continue;
            originals[id] = entries.Select(entry => entry switch
            {
                JsonObject o when o["n"] is JsonValue n && n.TryGetValue<int>(out var number) => (RawValue?)RawValue.Dword(number),
                JsonObject o when o["s"] is JsonValue s && s.TryGetValue<string>(out var text) => RawValue.Str(text),
                _ => null,
            }).ToArray();
        }
        return originals;
    }

    void SaveOriginals(Dictionary<string, RawValue?[]?> originals)
    {
        var saved = new JsonObject();
        foreach (var (id, entries) in originals.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            saved[id] = new JsonArray((entries ?? []).Select(entry => entry switch
            {
                { Number: { } number } => (JsonNode?)new JsonObject { ["n"] = number },
                { Text: { } text } => new JsonObject { ["s"] = text },
                _ => null,
            }).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(originalsPath)!);
        var temporary = originalsPath + ".tmp";
        File.WriteAllText(temporary, saved.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, originalsPath, overwrite: true);
    }
}
