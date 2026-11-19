// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Expands folder tokens such as %DESKTOP% in the context of the current (user) process.</summary>
static class PathTokens
{
    public static string Expand(string path)
    {
        // A path pasted from Explorer's "Copy as path" comes in quotes.
        path = PathText.Clean(path);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["%DESKTOP%"] = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ["%DOCUMENTS%"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["%DOWNLOADS%"] = Path.Combine(profile, "Downloads"),
        };
        foreach (var kv in map)
            path = path.Replace(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase);
        return Environment.ExpandEnvironmentVariables(path);
    }
}

/// <summary>Runs in the user helper: puts files where the student sees them and finds files to collect.</summary>
sealed class DeliveryHandler
{
    /// <summary>Copies a file the host uploaded to Staging into the user's folder, owned by the user.</summary>
    public Response Deliver(JsonObject a)
    {
        var source = Path.GetFullPath(a.Str("source"));
        var staging = Path.GetFullPath(ClientConfig.StagingDir).TrimEnd('\\') + "\\";
        if (!source.StartsWith(staging, StringComparison.OrdinalIgnoreCase))
            return Response.Fail("Source must be in the staging folder.");
        if (!File.Exists(source)) return Response.Fail("Uploaded file not found.");

        var name = Path.GetFileName(a.Str("name"));
        if (name.Length == 0) return Response.Fail("Missing file name.");
        var folder = PathTokens.Expand(a.Str("destination", "%DESKTOP%"));
        bool overwrite = a.Bool("overwrite") ?? true;

        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        File.Copy(source, target, overwrite);
        Log.Write($"Delivered {name} to {folder}");
        var info = new FileInfo(target);
        return Response.Success(new JsonObject
        {
            ["path"] = target,
            ["size"] = info.Length,
            ["sha256"] = Sha256(target),
        });
    }

    /// <summary>Deletes a previously delivered file only while its exact recorded content remains unchanged.</summary>
    public Response DeleteDelivered(JsonObject a)
    {
        try
        {
            var path = Path.GetFullPath(a.Str("path"));
            var expectedSize = a.Long("size");
            var expectedHash = a.Str("sha256").Trim();
            if (expectedHash.Length != 64 || expectedHash.Any(ch => !Uri.IsHexDigit(ch)))
                return Response.Fail("The transfer record has no valid SHA-256 fingerprint.");
            if (!File.Exists(path)) return Response.Fail("The delivered file no longer exists.");
            var currentSize = new FileInfo(path).Length;
            if (currentSize != expectedSize)
                return Response.Fail("The delivered file has changed size; it was not deleted.");
            if (!Sha256(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                return Response.Fail("The delivered file content has changed; it was not deleted.");
            File.Delete(path);
            Log.Write($"Undid delivered file {path}");
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail("Could not undo the file delivery: " + ex.Message); }
    }

    static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Lists files under a folder (tokens allowed) so the host can download them.</summary>
    public Response Find(JsonObject a)
    {
        var folder = Path.GetFullPath(PathTokens.Expand(a.Str("folder")));
        var pattern = a.Str("pattern", "*");
        bool recursive = a.Bool("recursive") ?? false;
        int max = Math.Clamp(a.Int("maxFiles") ?? 500, 1, 5000);

        if (!Directory.Exists(folder)) return Response.Fail($"Folder not found: {folder}");

        var files = new JsonArray();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint,
        };
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles(pattern, options))
        {
            if (files.Count >= max) break;
            files.Add(new JsonObject { ["path"] = f.FullName, ["size"] = f.Length, ["modified"] = f.LastWriteTime.ToString("s") });
        }
        return Response.Success(new JsonObject { ["folder"] = folder, ["files"] = files });
    }

    /// <summary>
    /// Recent files (T-212): what the signed-in user changed in the watched folders in the time window. Runs as the user, so it sees
    /// that user's Desktop and Documents (also when they are in OneDrive) and nothing the user could not open. Keeps nothing.
    /// </summary>
    public static Response Recent(JsonObject a, DateTime nowUtc)
    {
        var query = RecentQuery.FromArgs(a, nowUtc);
        var roots = query.Folders.Select(folder => (folder, PathTokens.Expand(folder)));
        return Response.Success(RecentFiles.Scan(roots, query).ToJson());
    }
}

static class NetInfo
{
    /// <summary>The adapters' addresses and the main adapter's MAC, read in one enumeration.</summary>
    internal sealed record LocalNetwork(HashSet<System.Net.IPAddress> Addresses, string PrimaryMac);

    // Enumerating adapters costs about 150 ms on a Celeron N2940 (15 ms on a Core i5) and used to run for every ping and
    // every connection (T-161). The snapshot is dropped when Windows reports an address change; the age limit is a fallback.
    static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromMinutes(5);
    static readonly object SnapshotGate = new();
    static LocalNetwork? _snapshot;
    static long _snapshotTicks;

    /// <summary>Reads the adapters; replaced by tests. A failed read returns null and is never cached.</summary>
    internal static Func<LocalNetwork?> Collect { get; set; } = CollectFromWindows;

    static NetInfo()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Invalidate();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) => Invalidate();
    }

    internal static void Invalidate() { lock (SnapshotGate) _snapshot = null; }

    static LocalNetwork? Snapshot()
    {
        lock (SnapshotGate)
        {
            if (_snapshot is not null && Environment.TickCount64 - _snapshotTicks < SnapshotMaxAge.TotalMilliseconds) return _snapshot;
            _snapshot = Collect();
            _snapshotTicks = Environment.TickCount64;
            return _snapshot;
        }
    }

    static LocalNetwork? CollectFromWindows()
    {
        try
        {
            var addresses = new HashSet<System.Net.IPAddress>();
            var mac = "";
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var properties = nic.GetIPProperties();
                foreach (var ua in properties.UnicastAddresses) addresses.Add(ua.Address);
                if (mac.Length > 0) continue;
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                bool hasGateway = properties.GatewayAddresses
                    .Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                              !g.Address.Equals(System.Net.IPAddress.Any));
                if (!hasGateway) continue;
                var candidate = nic.GetPhysicalAddress().ToString();
                if (candidate.Length == 12) mac = candidate;
            }
            return new LocalNetwork(addresses, mac);
        }
        catch { return null; }
    }

    /// <summary>MAC of the main network adapter (up, has an IPv4 gateway), as 12 hex digits; used for Wake-on-LAN.</summary>
    public static string PrimaryMac() => Snapshot()?.PrimaryMac ?? "";

    /// <summary>
    /// True if <paramref name="addr"/> is this computer's own loopback address or one of its
    /// network adapters' addresses - i.e. whoever connected to us is running on this same machine.
    /// Used to refuse commands that would shut down, restart, lock or update the host's own PC
    /// (see CommandRouter).
    /// </summary>
    public static bool IsLocalAddress(System.Net.IPAddress? addr)
    {
        if (addr is null) return false;
        var v4 = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr;
        if (System.Net.IPAddress.IsLoopback(addr) || System.Net.IPAddress.IsLoopback(v4)) return true;

        return Snapshot() is { } network && (network.Addresses.Contains(addr) || network.Addresses.Contains(v4));
    }
}
