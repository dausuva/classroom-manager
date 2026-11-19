// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>One key this client accepts, with the name and role of whoever holds it.</summary>
sealed record AccessKey(string Name, OperatorRole Role, string Psk, DateTimeOffset? NotAfter = null, IReadOnlyList<string>? Rooms = null)
{
    /// <summary>
    /// True when this key may be used on a computer in <paramref name="room"/> (T-190). A key with no rooms works everywhere; a key limited to
    /// rooms does not work on a computer that has no room label at all, so a mistake closes the door instead of opening it.
    /// </summary>
    public bool AppliesTo(string room) =>
        Rooms is not { Count: > 0 } || (room.Trim().Length > 0 && Rooms.Any(allowed => allowed.Trim().Equals(room.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>True once the key's end time has come (T-190). The lab's original key never expires.</summary>
    public bool IsExpired(DateTimeOffset now) => NotAfter is { } end && end <= now;

    public byte[] Bytes { get; } = Encoding.UTF8.GetBytes(Psk);
    public string Fingerprint => SharedKey.Fingerprint(Psk);
}

/// <summary>Who is on the other end of a connection, decided by which key completed the handshake.</summary>
sealed record CallerIdentity(string KeyName, OperatorRole Role)
{
    /// <summary>Requests the service has already authorised and passes on to its own user helper.</summary>
    public static readonly CallerIdentity Service = new("service", OperatorRole.Admin);
}

/// <summary>
/// The keys the service currently accepts. The first is always the lab's original key with the
/// Admin role; the rest are named keys an administrator added. CommandServer reads the list for
/// every new connection and at request admission. A continuously configured credential keeps its
/// object identity; replacing/removing it permanently invalidates sessions holding the old object.
/// </summary>
sealed class KeyStore
{
    volatile AccessKey[] _keys;
    readonly TimeProvider _time;
    public object SyncRoot { get; } = new();

    public KeyStore(string psk, IEnumerable<ClientAccessKey>? accessKeys = null, TimeProvider? time = null, DateTimeOffset? primaryChanged = null, int epoch = 0, string room = "")
    {
        Room = room ?? "";
        _time = time ?? TimeProvider.System;
        _keys = Build(psk, FromConfig(accessKeys ?? []));
        PrimaryChanged = primaryChanged;
        Epoch = epoch;
    }

    /// <summary>The room this computer was told it is in (by an Admin, when a key limited to rooms was added); "" when none. Keys limited to other rooms do not open it.</summary>
    public string Room { get => _room; set => _room = value ?? ""; }
    volatile string _room = "";

    /// <summary>Which rotation of the lab key this computer is on, as the Host counted it (T-189).</summary>
    public int Epoch { get; set; }

    /// <summary>When the lab's original key was last changed on this computer; null when unknown (never changed since this was recorded).</summary>
    public DateTimeOffset? PrimaryChanged { get; private set; }

    /// <summary>The keys that open a connection now: every key whose end time has not come.</summary>
    public IReadOnlyList<AccessKey> Current
    {
        get
        {
            var now = _time.GetUtcNow();
            var room = Room;
            return _keys.Where(key => !key.IsExpired(now) && key.AppliesTo(room)).ToArray();
        }
    }

    /// <summary>Every key that is stored, expired ones included (the Host lists and removes them).</summary>
    public IReadOnlyList<AccessKey> All => _keys;

    public AccessKey Primary => _keys[0];

    public IReadOnlyList<AccessKey> Named => _keys.Skip(1).ToList();

    /// <summary>Replaces the lab's original key (key rotation); named keys are unaffected.</summary>
    public void Set(string psk)
    {
        lock (SyncRoot)
        {
            if (Primary.Psk != psk) PrimaryChanged = _time.GetUtcNow();
            _keys = new[] { Primary.Psk == psk ? Primary : new AccessKey(AccessKeyNames.Primary, OperatorRole.Admin, psk) }
                .Concat(Named).ToArray();
        }
    }

    public void SetNamed(IEnumerable<AccessKey> named)
    {
        lock (SyncRoot)
        {
            var previous = _keys;
            var next = named.Select(key => previous.Skip(1).FirstOrDefault(current =>
                current.Name == key.Name && current.Role == key.Role && current.Psk == key.Psk && current.NotAfter == key.NotAfter
                && (current.Rooms ?? []).SequenceEqual(key.Rooms ?? []))
                // Do not reuse an input object that was removed earlier: re-adding the same
                // secret/name/role must never resurrect its previously authenticated sessions.
                ?? new AccessKey(key.Name, key.Role, key.Psk, key.NotAfter, key.Rooms)).ToArray();
            _keys = new[] { previous[0] }.Concat(next).ToArray();
        }
    }

    /// <summary>
    /// The admission cutoff is ordered with key persistence/mutation by SyncRoot. A request
    /// admitted before removal may finish; subsequent requests cannot reach the router.
    /// Reference identity avoids truncated fingerprints and credential remove/re-add ABA races.
    /// </summary>
    public bool IsCurrent(AccessKey credential)
    {
        lock (SyncRoot) return _keys.Any(key => ReferenceEquals(key, credential)) && !credential.IsExpired(_time.GetUtcNow()) && credential.AppliesTo(Room);
    }

    static AccessKey[] Build(string psk, IEnumerable<AccessKey> named) =>
        new[] { new AccessKey(AccessKeyNames.Primary, OperatorRole.Admin, psk) }.Concat(named).ToArray();

    /// <summary>Drops entries a hand-edited client.json could contain that would weaken or confuse the list.</summary>
    static List<AccessKey> FromConfig(IEnumerable<ClientAccessKey> configured)
    {
        var result = new List<AccessKey>();
        foreach (var entry in configured)
        {
            if (entry is null || !AccessKeyNames.IsAcceptable(entry.Name, out _) || !SharedKey.IsAcceptable(entry.Psk, out _)) continue;
            if (result.Any(key => key.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) || key.Psk == entry.Psk)) continue;
            result.Add(new AccessKey(entry.Name, CommandAccess.ParseRole(entry.Role), entry.Psk,
                entry.NotAfterUtc is { } end ? new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)) : null,
                (entry.Rooms ?? []).Select(room => room.Trim()).Where(room => room.Length > 0).ToList()));
        }
        return result;
    }
}

/// <summary>key.set: the host rotates the shared key (see "Rotate key..." in the host).</summary>
sealed class KeyHandler
{
    const int MaxNamedKeys = 50;
    const int MaxRooms = 20;
    const int MaxRoomName = 64;
    readonly KeyStore _keys;
    readonly TimeProvider _time;
    readonly Action<string> _savePrimary;
    readonly Action<IEnumerable<ClientAccessKey>> _saveNamed;
    readonly Action<int> _saveEpoch;
    readonly Action<string> _saveRoom;

    public KeyHandler(KeyStore keys, Action<string>? savePrimary = null,
        Action<IEnumerable<ClientAccessKey>>? saveNamed = null, TimeProvider? time = null, Action<int>? saveEpoch = null, Action<string>? saveRoom = null)
    {
        _saveEpoch = saveEpoch ?? ClientConfig.SaveEpoch;
        _saveRoom = saveRoom ?? ClientConfig.SaveRoom;
        _keys = keys;
        _time = time ?? TimeProvider.System;
        _savePrimary = savePrimary ?? ClientConfig.SaveKey;
        _saveNamed = saveNamed ?? ClientConfig.SaveAccessKeys;
    }

    public Response Set(JsonObject a)
    {
        lock (_keys.SyncRoot) return SetCore(a);
    }

    Response SetCore(JsonObject a)
    {
        var newKey = a.Str("key");
        if (!SharedKey.IsAcceptable(newKey, out var reason)) return Response.Fail(reason);
        if (_keys.Named.Any(key => key.Psk == newKey))
            return Response.Fail("That key is already in use as a named access key on this computer.");

        // The rotation counter is optional: a Host that does not count rotations sends none and the computer keeps its own.
        var epoch = a.Int("epoch");
        if (epoch is < 0 or > 1_000_000) return Response.Fail("The key epoch is not a sensible number.");

        _savePrimary(newKey);   // on disk first: if this fails, nothing changes
        _keys.Set(newKey);
        if (epoch is { } counted)
        {
            _keys.Epoch = counted;
            // Informational for the Host's report; the key itself is already safely changed, so a failed write is logged, not fatal.
            try { _saveEpoch(counted); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Could not record the key epoch: " + ex.Message); }
        }
        Log.Write($"Shared key saved by the host (key {SharedKey.Fingerprint(newKey)}, epoch {_keys.Epoch}); credential changes apply at request admission.");
        return Response.Success(new JsonObject { ["fingerprint"] = SharedKey.Fingerprint(newKey), ["epoch"] = _keys.Epoch });
    }

    // ---- named access keys (key.access.*) -------------------------------------------------------

    public Response ListAccessKeys() => Response.Success(Describe());

    public Response SetAccessKey(JsonObject a, CallerIdentity caller)
    {
        lock (_keys.SyncRoot) return SetAccessKeyCore(a, caller);
    }

    Response SetAccessKeyCore(JsonObject a, CallerIdentity caller)
    {
        var name = a.Str("name");
        var key = a.Str("key");
        var roleText = a.Str("role");
        if (!AccessKeyNames.IsAcceptable(name, out var reason)) return Response.Fail(reason);
        if (!SharedKey.IsAcceptable(key, out reason)) return Response.Fail(reason);
        if (!Enum.TryParse<OperatorRole>(roleText, ignoreCase: true, out var role) || !Enum.IsDefined(role))
            return Response.Fail("The role must be Observer, Teacher or Admin.");
        DateTimeOffset? notAfter = null;
        var expires = a.Str("expiresUtc", "");
        if (expires.Length > 0)
        {
            var now = _time.GetUtcNow();
            if (!DateTimeOffset.TryParse(expires, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var end))
                return Response.Fail("The expiry is not a date and time.");
            if (end <= now) return Response.Fail("The expiry is already past.");
            if (end > now.AddYears(10)) return Response.Fail("The expiry is more than ten years away; leave it empty for a key that does not expire.");
            notAfter = end;
        }

        if (!TryRooms(a.Str("rooms", ""), out var rooms, out reason)) return Response.Fail(reason);
        var computerRoom = a.Str("computerRoom", "").Trim();
        if (computerRoom.Length > MaxRoomName) return Response.Fail("The room name is too long.");

        var named = _keys.Named.Where(existing => !existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (key == _keys.Primary.Psk || named.Any(existing => existing.Psk == key))
            return Response.Fail("That key is already in use on this computer under another name. Generate a new key.");
        if (named.Count >= MaxNamedKeys) return Response.Fail($"This computer already has {MaxNamedKeys} named keys.");

        named.Add(new AccessKey(name, role, key, notAfter, rooms));
        _saveNamed(named.Select(ToConfig));   // on disk first, as for the lab key
        if (computerRoom.Length > 0 && !computerRoom.Equals(_keys.Room, StringComparison.Ordinal))
        {
            _saveRoom(computerRoom);
            _keys.Room = computerRoom;
        }
        _keys.SetNamed(named);
        Log.Write($"Access key '{name}' ({role}, {SharedKey.Fingerprint(key)}{(rooms.Count > 0 ? ", rooms " + string.Join("/", rooms) : "")}) set by '{caller.KeyName}'.");
        return Response.Success(Describe());
    }

    public Response RemoveAccessKey(JsonObject a, CallerIdentity caller)
    {
        lock (_keys.SyncRoot) return RemoveAccessKeyCore(a, caller);
    }

    Response RemoveAccessKeyCore(JsonObject a, CallerIdentity caller)
    {
        var name = a.Str("name");
        var named = _keys.Named.ToList();
        var removed = named.RemoveAll(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return Response.Fail($"This computer has no access key named '{name}'.");

        _saveNamed(named.Select(ToConfig));
        _keys.SetNamed(named);
        Log.Write($"Access key '{name}' removed by '{caller.KeyName}'; prior sessions cannot admit further requests.");
        return Response.Success(Describe());
    }

    /// <summary>Room names from the request: separated by commas or semicolons, at most 20, each at most 64 characters. Empty means every room.</summary>
    static bool TryRooms(string text, out List<string> rooms, out string reason)
    {
        reason = "";
        rooms = text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (rooms.Count > MaxRooms) reason = $"A key can be limited to at most {MaxRooms} rooms.";
        else if (rooms.Any(room => room.Length > MaxRoomName || room.Any(char.IsControl))) reason = "A room name is too long or has control characters.";
        return reason.Length == 0;
    }

    JsonObject Describe() => new()
    {
        ["room"] = _keys.Room,
        ["keys"] = new JsonArray(_keys.All.Select((key, index) => (JsonNode)new JsonObject
        {
            ["name"] = key.Name,
            ["role"] = key.Role.ToString(),
            ["fingerprint"] = key.Fingerprint,
            ["primary"] = index == 0,
            // Present only for a key that expires; a Host that asked for an expiry checks it is here (an older Client ignores the request).
            ["expiresUtc"] = key.NotAfter?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["expired"] = key.IsExpired(_time.GetUtcNow()),
            // Present only for a key limited to rooms; a Host that asked for rooms checks it is here (an older Client ignores the request).
            ["rooms"] = key.Rooms is { Count: > 0 } ? new JsonArray(key.Rooms.Select(room => (JsonNode)JsonValue.Create(room)!).ToArray()) : null,
        }).ToArray()),
    };

    static ClientAccessKey ToConfig(AccessKey key) =>
        new() { Name = key.Name, Role = key.Role.ToString(), Psk = key.Psk, NotAfterUtc = key.NotAfter?.UtcDateTime, Rooms = key.Rooms?.ToList() };
}
