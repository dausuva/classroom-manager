// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClassroomManager.Client;

/// <summary>
/// The files that hold a secret (the lab key in client.json, the private key in tls-cert.pfx) may be read and changed by SYSTEM and
/// Administrators only (T-182). C:\ProgramData lets every user read what is created in it, so the protection is set on the file
/// itself, checked every time the service starts, and put back when something has opened it.
/// </summary>
static class SecretFiles
{
    static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    // Any of these on an allow rule is a way to read or change the file.
    const FileSystemRights Reaches = FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>The accounts, other than SYSTEM, Administrators and <paramref name="alsoKeep"/>, that can reach the file.</summary>
    public static IReadOnlyList<string> Exposed(string path, params SecurityIdentifier[] alsoKeep)
    {
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        var exposed = new List<string>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & Reaches) == 0) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (sid.Equals(LocalSystem) || sid.Equals(Administrators) || alsoKeep.Contains(sid)) continue;
            var name = Describe(sid);
            if (!exposed.Contains(name)) exposed.Add(name);
        }
        return exposed;
    }

    /// <summary>Removes every other account: only SYSTEM, Administrators and <paramref name="alsoKeep"/> (used by tests) stay, with no inheritance.</summary>
    public static void Restrict(string path, params SecurityIdentifier[] alsoKeep)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        foreach (var extra in alsoKeep)
            security.AddAccessRule(new FileSystemAccessRule(extra, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>
    /// Checks one file and repairs it if others can reach it. Returns what to write in the log, or an empty string when the file is
    /// absent or already protected. Never throws: a file that cannot be repaired is reported, and the service still starts.
    /// </summary>
    public static string Ensure(string path, params SecurityIdentifier[] alsoKeep)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var exposed = Exposed(path, alsoKeep);
            if (exposed.Count == 0) return "";
            var name = Path.GetFileName(path);
            try
            {
                Restrict(path, alsoKeep);
                return $"{name} could be reached by {string.Join(", ", exposed)}. Its permissions were restricted to SYSTEM and Administrators.";
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                return $"{name} can be reached by {string.Join(", ", exposed)} and its permissions could not be restricted ({ex.GetType().Name}). Run: icacls \"{path}\" /inheritance:r /grant:r \"*S-1-5-18:(F)\" \"*S-1-5-32-544:(F)\"";
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or SystemException)
        {
            return $"{Path.GetFileName(path)}: the permissions could not be checked ({ex.GetType().Name}).";
        }
    }

    static string Describe(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return sid.Value; }
    }
}
