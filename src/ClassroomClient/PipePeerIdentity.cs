// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ClassroomManager.Client;

sealed record HelperIdentity(string Sid, string Name, int Session);

/// <summary>Windows evidence only: pipe payloads and executable names are not identity proof.</summary>
static class PipePeerIdentity
{
    public const string ServiceName = "ClassroomManagerClient";

    public static HelperIdentity ReadHelper(NamedPipeServerStream pipe)
    {
        HelperIdentity? peer = null;
        // Synchronous token queries only: never await, execute commands or touch files while impersonating.
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
            if (identity?.User is null || !identity.IsAuthenticated || identity.IsSystem)
                throw new SecurityException("Unverified helper identity.");
            if (!GetTokenInformation(identity.AccessToken, 12 /* TokenSessionId */, out uint session, sizeof(uint), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (session > int.MaxValue) throw new SecurityException("Invalid helper session.");
            peer = new(identity.User.Value, identity.Name, (int)session);
        });
        return peer ?? throw new SecurityException("Unverified helper identity.");
    }

    public static void ValidateHello(PipeMessage hello, HelperIdentity peer)
    {
        if (hello.Id != 0 || hello.Req is not null || hello.Res is not null || hello.Session != peer.Session ||
            peer.Session <= 0 || string.IsNullOrWhiteSpace(peer.Sid) || string.IsNullOrWhiteSpace(peer.Name))
            throw new SecurityException("Invalid helper registration.");
        // hello.User is compatibility metadata only, never used to select a user or authorize a request.
    }

    public static void VerifyService(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var service = ReadService();
        ValidateService(serverPid, service.State, service.ProcessId);
    }

    public static void VerifySessionOwner(HelperIdentity peer)
    {
        // The service runs as LocalSystem. A run-as process in another user's session
        // must not become that session's helper merely by having the same session number.
        if (!WTSQueryUserToken((uint)peer.Session, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
            ValidateSessionOwner(peer, owner.User?.Value);
    }

    internal static void ValidateSessionOwner(HelperIdentity peer, string? sessionOwnerSid)
    {
        if (peer.Session <= 0 || string.IsNullOrEmpty(sessionOwnerSid) || peer.Sid != sessionOwnerSid)
            throw new SecurityException("The helper is not the signed-in session owner.");
    }

    internal static void ValidateService(uint serverPid, uint state, uint servicePid)
    {
        // Start/stop pending do not provide a reliable service PID. The helper retries after startup.
        if (state != 4 /* SERVICE_RUNNING */ || servicePid == 0 || serverPid != servicePid)
            throw new SecurityException("The pipe peer is not the running Classroom Manager service.");
    }

    static ServiceStatus ReadService()
    {
        using var manager = OpenSCManager(null, null, 1 /* SC_MANAGER_CONNECT */);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenService(manager, ServiceName, 4 /* SERVICE_QUERY_STATUS */);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus
    {
        public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags;
    }

    sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out uint information, int length, out int returned);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ServiceHandle OpenService(ServiceHandle manager, string serviceName, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseServiceHandle(IntPtr handle);
}
