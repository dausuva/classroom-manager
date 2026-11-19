// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using ClassroomManager.Shared;
using Microsoft.Win32;

namespace ClassroomManager.Client;

/// <summary>
/// Writes and removes the browser policies of <see cref="BrowserRules"/> under a registry root
/// (the 64-bit HKEY_LOCAL_MACHINE in the service; a scratch key in tests). Before a value is first
/// changed, what was there is remembered in a small file, so turning a policy off restores a value
/// someone else had set, or removes the value when there was none.
/// </summary>
sealed class BrowserPolicyStore
{
    /// <summary>What a value was before Classroom Manager touched it; Present false means it did not exist.</summary>
    public sealed class Saved
    {
        public bool Present { get; set; }
        public int Number { get; set; }
        public string[]? List { get; set; }
        public string? Text { get; set; }
    }

    readonly RegistryKey _root;
    readonly string _statePath;
    readonly object _gate = new();

    public BrowserPolicyStore(RegistryKey root, string statePath)
    {
        _root = root;
        _statePath = statePath;
    }

    static string Id(BrowserRules.Setting setting) => $@"{setting.Key}\{setting.Name}";

    Dictionary<string, Saved> LoadState()
    {
        try
        {
            return File.Exists(_statePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(_statePath)) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    void SaveState(Dictionary<string, Saved> state)
    {
        if (state.Count == 0)
        {
            if (File.Exists(_statePath)) File.Delete(_statePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
    }

    /// <summary>"on" when every value of the policy is in place, "off" when none is, otherwise "partly".</summary>
    public string State(string policy)
    {
        var settings = BrowserRules.Settings(policy);
        int inPlace = settings.Count(InPlace);
        return inPlace == settings.Count ? "on" : inPlace == 0 ? "off" : "partly";
    }

    bool InPlace(BrowserRules.Setting setting)
    {
        var current = Read(setting);
        if (setting.Text is not null) return current.Present && string.Equals(current.Text, setting.Text, StringComparison.OrdinalIgnoreCase);
        return current.Present && (setting.List is null
            ? current.Number == setting.Number
            : current.List is not null && setting.List.All(item => current.List.Contains(item, StringComparer.OrdinalIgnoreCase)));
    }

    Saved Read(BrowserRules.Setting setting)
    {
        if (setting.List is not null)
        {
            using var list = _root.OpenSubKey($@"{setting.Key}\{setting.Name}");
            return list is null ? new Saved()
                : new Saved { Present = true, List = list.GetValueNames().Select(name => list.GetValue(name)?.ToString() ?? "").ToArray() };
        }
        using var key = _root.OpenSubKey(setting.Key);
        if (setting.Text is not null)
            return key?.GetValue(setting.Name) is string text ? new Saved { Present = true, Text = text } : new Saved();
        return key?.GetValue(setting.Name) is int number ? new Saved { Present = true, Number = number } : new Saved();
    }

    void Write(BrowserRules.Setting setting, Saved value)
    {
        if (setting.List is not null)
        {
            _root.DeleteSubKeyTree($@"{setting.Key}\{setting.Name}", throwOnMissingSubKey: false);
            if (!value.Present) return;
            using var list = _root.CreateSubKey($@"{setting.Key}\{setting.Name}");
            var items = value.List ?? [];
            for (int i = 0; i < items.Length; i++) list.SetValue((i + 1).ToString(), items[i], RegistryValueKind.String);
            return;
        }
        if (value.Present)
        {
            using var key = _root.CreateSubKey(setting.Key);
            if (setting.Text is not null) key.SetValue(setting.Name, value.Text ?? "", RegistryValueKind.String);
            else key.SetValue(setting.Name, value.Number, RegistryValueKind.DWord);
        }
        else
        {
            using var key = _root.OpenSubKey(setting.Key, writable: true);
            key?.DeleteValue(setting.Name, throwOnMissingValue: false);
        }
    }

    /// <summary>Turns a policy on, or off again by restoring what each value was before.</summary>
    public void Set(string policy, bool on) => Set(BrowserRules.Settings(policy), on);

    /// <summary>Whether this program has these values in place (it wrote them and has not put them back yet).</summary>
    public bool Holds(BrowserRules.Setting setting)
    {
        lock (_gate) return LoadState().ContainsKey(Id(setting));
    }

    /// <summary>Writes the given values, or puts back what each was before this program first wrote it.</summary>
    public void Set(IEnumerable<BrowserRules.Setting> settings, bool on)
    {
        lock (_gate)
        {
            var state = LoadState();
            foreach (var setting in settings)
            {
                if (on)
                {
                    // Remembered once: a second "on" must not record our own value as the original.
                    if (!state.ContainsKey(Id(setting))) state[Id(setting)] = Read(setting);
                    Write(setting, new Saved { Present = true, Number = setting.Number, List = setting.List, Text = setting.Text });
                }
                else if (state.Remove(Id(setting), out var before)) Write(setting, before);
                // Never turned on by Classroom Manager: whatever is there belongs to someone else and stays.
            }
            SaveState(state);
        }
    }
}
