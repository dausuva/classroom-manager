// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ClassroomManager.Client;

/// <summary>Who is signed in to a Windows session, as the service (SYSTEM) can ask Windows.</summary>
static class SessionUser
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>The security id (S-1-5-21-...) of the user of a session, or null when it has none or cannot be read.</summary>
    public static string? Sid(int sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out var token)) return null;
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value;
        }
        finally { CloseHandle(token); }
    }
}
