// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Signs a user in on a computer nobody is using, so a teacher can prepare a room before a lesson.
/// Windows has no supported way to sign someone in at an already-displayed sign-in screen without
/// a custom credential provider, so this uses Windows' own one-time automatic sign-in and restarts:
/// the password is checked first, stored as a protected LSA secret (never in the registry or a
/// file), Winlogon is told to sign that account in once, and the computer restarts. After the
/// restart <see cref="RunPendingCleanupAsync"/> removes the secret and puts the earlier Winlogon
/// values back, whether or not the sign-in happened.
/// </summary>
sealed class SignInHandler
{
    const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    const string SecretName = "DefaultPassword";
    static readonly string[] ManagedValues = ["AutoAdminLogon", "DefaultUserName", "DefaultDomainName", "AutoLogonCount"];
    static readonly TimeSpan CleanupWait = TimeSpan.FromMinutes(10);

    static string PendingPath => Path.Combine(ClientConfig.DataDir, "signin-pending.json");

    /// <summary>Winlogon values as they were before the request; a null entry means the value was absent. Holds no password.</summary>
    sealed class Pending
    {
        public Dictionary<string, string?> Previous { get; set; } = new();
        public Dictionary<string, string> Kinds { get; set; } = new();
    }

    public async Task<Response> RequestAsync(JsonObject a, CancellationToken ct)
    {
        if (AgentContext.IsUserAgent) return Response.Fail("A remote sign-in must be requested at the system level.");
        try
        {
            var account = SignInAccount.Parse(a.Str("user"));
            if (account is null) return Response.Fail("Enter the account as user, .\\user, DOMAIN\\user, or user@domain.");
            var password = a.Str("password");
            if (password.Length == 0)
                return Response.Fail("Windows does not sign in an account with an empty password automatically.");

            if (SignedInUsers() is { Count: > 0 } users)
                return Response.Fail($"{string.Join(", ", users)} is signed in on this computer. A remote sign-in restarts the computer, so it is only done when nobody is signed in.");
            if (File.Exists(PendingPath))
                return Response.Fail("A remote sign-in is already in progress on this computer.");

            using var machine = Machine();
            using (var existing = machine.OpenSubKey(WinlogonKey))
                if (existing?.GetValue("AutoAdminLogon") as string == "1")
                    return Response.Fail("Automatic sign-in is already configured on this computer by someone else; Classroom Manager will not replace it.");

            // A user principal name (user@domain) carries its own domain, so none is passed beside it.
            var principalName = account.User.Contains('@');
            var domain = principalName ? "" : account.IsLocal ? Environment.MachineName : account.Domain;
            if (!LogonUser(account.User, principalName ? null : domain, password, LogonInteractive, LogonProviderDefault, out var token))
            {
                var error = Marshal.GetLastWin32Error();
                return Response.Fail(error == ErrorLogonFailure
                    ? "The user name or password is incorrect."
                    : $"Windows would not sign this account in here: {new System.ComponentModel.Win32Exception(error).Message}");
            }
            CloseHandle(token);

            // Record what to put back before changing anything, so a crash mid-way is still cleaned up.
            var pending = new Pending();
            using (var key = machine.CreateSubKey(WinlogonKey))
            {
                foreach (var name in ManagedValues)
                {
                    var value = key.GetValue(name);
                    pending.Previous[name] = value?.ToString();
                    if (value is not null) pending.Kinds[name] = key.GetValueKind(name).ToString();
                }
                Directory.CreateDirectory(ClientConfig.DataDir);
                await File.WriteAllTextAsync(PendingPath, JsonSerializer.Serialize(pending), ct);

                try
                {
                    StoreSecret(password);
                    key.SetValue("DefaultUserName", account.User, RegistryValueKind.String);
                    key.SetValue("DefaultDomainName", domain, RegistryValueKind.String);
                    // Windows counts this down and switches automatic sign-in off again by itself.
                    key.SetValue("AutoLogonCount", 1, RegistryValueKind.DWord);
                    key.DeleteValue("DefaultPassword", throwOnMissingValue: false);
                    key.SetValue("AutoAdminLogon", "1", RegistryValueKind.String);
                }
                catch
                {
                    Cleanup("the request could not be completed");
                    throw;
                }
            }

            var restart = await InstallHandler.RunAsync(
                new ProcessStartInfo("shutdown.exe", "/r /f /t 5 /c \"Restarting to sign in for the lesson.\"")
                { UseShellExecute = false, CreateNoWindow = true }, 30, ct);
            if (!restart.Ok)
            {
                Cleanup("the restart could not be started");
                return Response.Fail("The computer could not be restarted, so the sign-in was cancelled: " + restart.Error);
            }
            Log.Write($"user.signin: restarting to sign in {account.Display}.");
            return Response.Success(new JsonObject { ["user"] = account.Display, ["restarting"] = true });
        }
        catch (Exception ex) { return Response.Fail("Could not prepare the sign-in: " + ex.Message); }
    }

    /// <summary>
    /// Runs in the service after every start. If a sign-in was requested before the restart, waits for
    /// a user to appear (or gives up after ten minutes) and then removes every trace of the request.
    /// </summary>
    public async Task RunPendingCleanupAsync(CancellationToken ct)
    {
        if (AgentContext.IsUserAgent || !File.Exists(PendingPath)) return;
        var deadline = DateTime.UtcNow + CleanupWait;
        try
        {
            while (DateTime.UtcNow < deadline && SignedInUsers().Count == 0)
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException) { /* service stopping: clean up now rather than leave the secret behind */ }
        Cleanup(SignedInUsers().Count > 0 ? "a user is signed in" : "nobody signed in within ten minutes");
    }

    static void Cleanup(string reason)
    {
        try { DeleteSecret(); }
        catch (Exception ex) { Log.Write($"user.signin: could not delete the stored password: {ex.Message}"); }
        try
        {
            var pending = File.Exists(PendingPath)
                ? JsonSerializer.Deserialize<Pending>(File.ReadAllText(PendingPath)) ?? new Pending()
                : new Pending();
            using var machine = Machine();
            using var key = machine.CreateSubKey(WinlogonKey);
            key.DeleteValue("DefaultPassword", throwOnMissingValue: false);
            foreach (var name in ManagedValues)
            {
                if (!pending.Previous.TryGetValue(name, out var previous) || previous is null)
                {
                    // Never leave automatic sign-in switched on, even when the saved state is unreadable.
                    if (name == "AutoAdminLogon") key.SetValue(name, "0", RegistryValueKind.String);
                    else if (name == "AutoLogonCount") key.DeleteValue(name, throwOnMissingValue: false);
                    else if (pending.Previous.ContainsKey(name)) key.DeleteValue(name, throwOnMissingValue: false);
                    continue;
                }
                if (pending.Kinds.GetValueOrDefault(name) == nameof(RegistryValueKind.DWord) && int.TryParse(previous, out var number))
                    key.SetValue(name, number, RegistryValueKind.DWord);
                else key.SetValue(name, previous, RegistryValueKind.String);
            }
            File.Delete(PendingPath);
            Log.Write($"user.signin: automatic sign-in removed ({reason}).");
        }
        catch (Exception ex) { Log.Write($"user.signin: could not restore Winlogon settings: {ex.Message}"); }
    }

    static RegistryKey Machine() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    // ---- sessions -----------------------------------------------------------------------------

    /// <summary>Names of users with a session on this computer, including locked and switched-away ones.</summary>
    static List<string> SignedInUsers()
    {
        var users = new List<string>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessions, out var count)) return users;
        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<WtsSessionInfo>(IntPtr.Add(sessions, i * size));
                if (session.SessionId == 0) continue;
                if (!WTSQuerySessionInformation(IntPtr.Zero, session.SessionId, WtsUserName, out var buffer, out _)) continue;
                try
                {
                    var name = Marshal.PtrToStringUni(buffer) ?? "";
                    if (name.Length > 0) users.Add(name);
                }
                finally { WTSFreeMemory(buffer); }
            }
        }
        finally { WTSFreeMemory(sessions); }
        return users;
    }

    // ---- LSA secret ---------------------------------------------------------------------------

    static void StoreSecret(string value) => WriteSecret(value);

    static void DeleteSecret() => WriteSecret(null);

    static void WriteSecret(string? value)
    {
        var attributes = new LsaObjectAttributes { Length = Marshal.SizeOf<LsaObjectAttributes>() };
        Check(LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyCreateSecret, out var policy), "open the security policy");
        var name = LsaString(SecretName);
        var data = value is null ? default : LsaString(value);
        var dataPointer = IntPtr.Zero;
        try
        {
            if (value is not null)
            {
                dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<LsaUnicodeString>());
                Marshal.StructureToPtr(data, dataPointer, false);
            }
            Check(LsaStorePrivateData(policy, ref name, dataPointer), value is null ? "delete the stored password" : "store the password");
        }
        finally
        {
            if (dataPointer != IntPtr.Zero) Marshal.FreeHGlobal(dataPointer);
            // Overwrite the copy of the password before releasing it.
            if (data.Buffer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(data.Buffer);
            if (name.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(name.Buffer);
            LsaClose(policy);
        }
    }

    static LsaUnicodeString LsaString(string value)
    {
        if (value.Length > 0x7FFE) throw new ArgumentException("The value is too long.");
        return new LsaUnicodeString
        {
            Buffer = Marshal.StringToHGlobalUni(value),
            Length = (ushort)(value.Length * sizeof(char)),
            MaximumLength = (ushort)((value.Length + 1) * sizeof(char)),
        };
    }

    static void Check(uint status, string what)
    {
        if (status == 0) return;
        throw new InvalidOperationException(
            $"Windows would not {what}: {new System.ComponentModel.Win32Exception(LsaNtStatusToWinError(status)).Message}");
    }

    // ---- Win32 --------------------------------------------------------------------------------

    const int LogonInteractive = 2;
    const int LogonProviderDefault = 0;
    const int ErrorLogonFailure = 1326;
    const uint PolicyCreateSecret = 0x00000020;
    const int WtsUserName = 5;

    [StructLayout(LayoutKind.Sequential)]
    struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LogonUser(string user, string? domain, string password, int logonType, int logonProvider, out IntPtr token);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll")]
    static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes attributes, uint access, out IntPtr policy);

    [DllImport("advapi32.dll")]
    static extern uint LsaStorePrivateData(IntPtr policy, ref LsaUnicodeString keyName, IntPtr privateData);

    [DllImport("advapi32.dll")]
    static extern uint LsaClose(IntPtr policy);

    [DllImport("advapi32.dll")]
    static extern int LsaNtStatusToWinError(uint status);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);
}
