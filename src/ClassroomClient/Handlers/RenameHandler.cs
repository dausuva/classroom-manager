// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Gives this computer a new Windows computer name. Windows applies it after a restart, which the Host offers;
/// nothing is restarted here. A computer joined to a domain is refused: its name is also the name of its
/// account in the domain, and changing it here would break that trust.
/// </summary>
sealed class RenameHandler
{
    const int ComputerNamePhysicalDnsHostname = 5;
    const int NetSetupDomainName = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetComputerNameExW(int nameType, string name);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);

    [DllImport("netapi32.dll")]
    static extern int NetApiBufferFree(IntPtr buffer);

    public Response Rename(JsonObject a)
    {
        var name = a.Str(ComputerNameRules.Name).Trim();
        if (ComputerNameRules.Problem(name) is { } problem) return Response.Fail(problem);
        var old = Environment.MachineName;
        if (ComputerNameRules.Same(name, old)) return Response.Fail("The computer already has this name.");
        if (IsInDomain()) return Response.Fail("This computer is joined to a domain. Rename it with the domain's own tools, not here.");

        try
        {
            if (!SetComputerNameExW(ComputerNamePhysicalDnsHostname, name))
                return Response.Fail("Windows refused the new name: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Response.Fail("Renaming is not available on this Windows: " + ex.Message);
        }
        Log.Write($"system.rename {old} -> {name}; takes effect after a restart");
        return Response.Success(new JsonObject
        {
            [ComputerNameRules.OldName] = old,
            [ComputerNameRules.NewName] = name.ToUpperInvariant(),
            [ComputerNameRules.RestartRequired] = true,
        });
    }

    /// <summary>True when the computer is a member of a domain (a workgroup is not).</summary>
    static bool IsInDomain()
    {
        try
        {
            if (NetGetJoinInformation(null, out var buffer, out var status) != 0) return false;
            NetApiBufferFree(buffer);
            return status == NetSetupDomainName;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}
