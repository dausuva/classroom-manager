// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// The Recycle Bin of the signed-in user (runs in the per-user helper: each account has its own bin
/// and its own name for it, and the service's would be the wrong ones). Emptying it removes what is
/// in it for good; resetting the name undoes a rename of the desktop icon.
/// </summary>
sealed class RecycleBinHandler
{
    const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr window, string? rootPath, uint flags);

    // Explorer keeps a user's own name for the Recycle Bin here; without it the Windows name is shown.
    const string NameKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}";
    const uint SHCNE_ASSOCCHANGED = 0x08000000;

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    public Response ResetName()
    {
        try
        {
            var custom = "";
            using (var key = Registry.CurrentUser.OpenSubKey(NameKey, writable: true))
            {
                if (key is not null)
                {
                    custom = key.GetValue("") as string ?? "";
                    key.DeleteValue("", throwOnMissingValue: false);
                    key.DeleteValue("LocalizedString", throwOnMissingValue: false);
                }
            }
            // Makes Explorer read the name again, so the desktop shows it without signing out.
            if (custom.Length > 0) SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
            Log.Write($"recyclebin.name.reset: {RecycleBinRules.DescribeName(custom)}.");
            return Response.Success(new JsonObject { [RecycleBinRules.CustomName] = custom });
        }
        catch (Exception ex) { return Response.Fail("Could not reset the name of the Recycle Bin: " + ex.Message); }
    }

    public Response Empty()
    {
        try
        {
            var before = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            bool counted = SHQueryRecycleBin(null, ref before) == 0;
            int result = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            // Windows reports an error for a bin that has nothing in it; that is the wanted state.
            if (result != 0 && !(counted && before.i64NumItems == 0))
                return Response.Fail($"Windows could not empty the Recycle Bin (error 0x{result:X8}).");
            Log.Write($"recyclebin.empty: {RecycleBinRules.Describe(before.i64NumItems, before.i64Size)}.");
            return Response.Success(new JsonObject
            {
                [RecycleBinRules.Items] = counted ? before.i64NumItems : 0,
                [RecycleBinRules.Bytes] = counted ? before.i64Size : 0,
            });
        }
        catch (Exception ex) { return Response.Fail("Could not empty the Recycle Bin: " + ex.Message); }
    }
}
