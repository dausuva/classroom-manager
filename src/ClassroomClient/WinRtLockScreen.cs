// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;

namespace ClassroomManager.Client;

/// <summary>
/// Sets the signed-in user's own lock screen picture through Windows.System.UserProfile.LockScreen, the
/// interface the Settings app uses. The client is not built against the Windows SDK projection (that would
/// make it much larger), so the interface is reached by plain COM: an activation factory, then methods by
/// their place in the interface. The places and ids are those of the Windows SDK headers
/// (windows.system.userprofile.h, windows.storage.h, asyncinfo.h, windows.foundation.h). Runs in the user
/// helper: the picture belongs to the user who is signed in.
/// </summary>
static class WinRtLockScreen
{
    // ILockScreenStatics: after IInspectable's six methods come get_OriginalImageFile, GetImageStream, SetImageFileAsync.
    static readonly Guid LockScreenStatics = new("3ee9d3ad-b607-40ae-b426-7631d9821269");
    const int SetImageFileAsyncSlot = 8;
    // IStorageFileStatics: GetFileFromPathAsync is its first method.
    static readonly Guid StorageFileStatics = new("5984c710-daf2-43c8-8bb4-a4d3eacfd03f");
    const int GetFileFromPathAsyncSlot = 6;
    // IAsyncInfo: get_Id, get_Status, get_ErrorCode. IAsyncOperation<T>: put_Completed, get_Completed, GetResults.
    static readonly Guid AsyncInfo = new("00000036-0000-0000-c000-000000000046");
    const int StatusSlot = 7, ErrorCodeSlot = 8, GetResultsSlot = 8;
    const int Started = 0, Completed = 1, Canceled = 2;

    [DllImport("combase.dll")] static extern int RoInitialize(int initType);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string text, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StringToObject(IntPtr self, IntPtr text, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ObjectToObject(IntPtr self, IntPtr argument, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetObject(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetInt(IntPtr self, out int result);

    static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    /// <summary>Makes the picture at <paramref name="path"/> the signed-in user's lock screen. Throws with Windows' reason when it refuses.</summary>
    public static void SetImage(string path, TimeSpan timeout)
    {
        // A new thread in the multithreaded apartment, whatever the caller's thread is.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { SetImageOnThisThread(path, timeout); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    static void SetImageOnThisThread(string path, TimeSpan timeout)
    {
        RoInitialize(1);   // multithreaded; "already initialised" answers are fine
        var file = IntPtr.Zero;
        var files = Factory("Windows.Storage.StorageFile", StorageFileStatics);
        try
        {
            Check(WindowsCreateString(path, path.Length, out var text), "Could not pass the path to Windows");
            IntPtr opening;
            try { Check(Method<StringToObject>(files, GetFileFromPathAsyncSlot)(files, text, out opening), "Windows could not open the picture"); }
            finally { WindowsDeleteString(text); }
            try
            {
                Wait(opening, timeout, "Windows could not open the picture");
                Check(Method<GetObject>(opening, GetResultsSlot)(opening, out file), "Windows could not open the picture");
            }
            finally { Marshal.Release(opening); }

            var lockScreen = Factory("Windows.System.UserProfile.LockScreen", LockScreenStatics);
            try
            {
                Check(Method<ObjectToObject>(lockScreen, SetImageFileAsyncSlot)(lockScreen, file, out var setting), "Windows refused the lock screen picture");
                try { Wait(setting, timeout, "Windows refused the lock screen picture"); }
                finally { Marshal.Release(setting); }
            }
            finally { Marshal.Release(lockScreen); }
        }
        finally
        {
            if (file != IntPtr.Zero) Marshal.Release(file);
            Marshal.Release(files);
        }
    }

    static IntPtr Factory(string className, Guid iid)
    {
        Check(WindowsCreateString(className, className.Length, out var name), "Could not name " + className);
        try
        {
            Check(RoGetActivationFactory(name, ref iid, out var factory), $"{className} is not available on this computer");
            return factory;
        }
        finally { WindowsDeleteString(name); }
    }

    /// <summary>Waits for a Windows asynchronous operation and throws when it did not complete.</summary>
    static void Wait(IntPtr operation, TimeSpan timeout, string what)
    {
        var iid = AsyncInfo;
        Check(Marshal.QueryInterface(operation, in iid, out var info), what);
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            int status;
            while (true)
            {
                Check(Method<GetInt>(info, StatusSlot)(info, out status), what);
                if (status != Started) break;
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"{what}: Windows did not answer within {timeout.TotalSeconds:0} seconds.");
                Thread.Sleep(50);
            }
            if (status == Completed) return;
            if (status == Canceled) throw new OperationCanceledException(what + ": the operation was cancelled.");
            Method<GetInt>(info, ErrorCodeSlot)(info, out var error);
            throw new InvalidOperationException($"{what}: {Marshal.GetExceptionForHR(error)?.Message ?? $"error 0x{error:X8}"}");
        }
        finally { Marshal.Release(info); }
    }

    static void Check(int hresult, string what)
    {
        if (hresult < 0) throw new InvalidOperationException($"{what}: {Marshal.GetExceptionForHR(hresult)?.Message ?? $"error 0x{hresult:X8}"}");
    }
}
