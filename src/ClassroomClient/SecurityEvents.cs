// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// The Client's security event log (T-191): failed handshakes, refused connections, throttling, revoked-key use, refused commands and
/// start-up findings, one hash-chained line each (<see cref="SecurityEventChain"/>), with counters that Ping reports to the Host.
/// Lines hold sources, key fingerprints and counts, never a key and never student content. A burst is written at a limited rate and the
/// rest is counted in one "events-suppressed" line, so the log cannot be used to fill the disk. The file is rotated at a size limit and the
/// chain continues in the new file.
/// </summary>
sealed class SecurityEvents
{
    /// <summary>The log of the running service; null where none is wanted (the user helper, most tests).</summary>
    public static SecurityEvents? Current { get; set; }

    readonly string _path;
    readonly long _maxBytes;
    readonly TimeProvider _time;
    readonly object _lock = new();
    readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    readonly RequestBucket _bucket;
    long _seq;
    string _lastHash = SecurityEventChain.Genesis;
    long _suppressed;

    public SecurityEvents(string path, TimeProvider? time = null, long maxBytes = 2 * 1024 * 1024, int burst = 50, double perSecond = 10)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        _maxBytes = maxBytes;
        _bucket = new RequestBucket(burst, perSecond, _time);
        try
        {
            if (File.Exists(path))
            {
                var result = SecurityEventChain.Verify(File.ReadLines(path));
                // A broken chain is not repaired here: the new events continue from the last good line and the break stays visible to Verify.
                _seq = result.LastSeq;
                _lastHash = result.LastHash;
                if (!result.Ok) Write("log-chain-broken", ("line", result.BadLine.ToString()), ("reason", result.Reason));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public string Path => _path;

    /// <summary>Counts the event and, within the rate limit, writes it. Never throws: logging must not stop the service.</summary>
    public void Write(string kind, params (string Key, string Value)[] fields)
    {
        lock (_lock)
        {
            _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
            if (!_bucket.TryTake()) { _suppressed++; return; }
            try
            {
                if (_suppressed > 0)
                {
                    Append("events-suppressed", [("count", _suppressed.ToString())]);
                    _suppressed = 0;
                }
                Append(kind, fields);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    void Append(string kind, (string Key, string Value)[] fields)
    {
        var data = fields.ToDictionary(field => field.Key, field => field.Value ?? "");
        var (text, hash) = SecurityEventChain.Line(_seq + 1, _time.GetUtcNow(), kind, data, _lastHash);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        if (File.Exists(_path) && new FileInfo(_path).Length > _maxBytes)
            File.Move(_path, _path + ".1", overwrite: true);
        File.AppendAllText(_path, text + "\n");
        _seq++;
        _lastHash = hash;
    }

    /// <summary>The counters since the service started, and where the chain ends, for Ping.</summary>
    public JsonObject Snapshot()
    {
        lock (_lock)
        {
            var counts = new JsonObject();
            foreach (var pair in _counts.OrderBy(pair => pair.Key, StringComparer.Ordinal)) counts[pair.Key] = pair.Value;
            return new JsonObject { ["counts"] = counts, ["lastSeq"] = _seq, ["lastHash"] = _lastHash };
        }
    }
}
