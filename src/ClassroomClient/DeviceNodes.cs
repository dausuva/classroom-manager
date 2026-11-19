// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClassroomManager.Client;

/// <summary>
/// Finds cameras and USB disk drives in the Windows device tree and disables or enables them, the same
/// operation as "Disable device" in Device Manager. A disabled device is gone for every program at once,
/// including one that already had it open, which a privacy setting or a policy does not guarantee.
/// Listing needs no special rights; disabling and enabling need the elevated service.
/// </summary>
static class DeviceNodes
{
    // Device interface classes: what a device offers, whatever kind of driver it uses.
    static readonly Guid VideoCamera = new("E5323777-F976-4F5B-9B55-B94699C46E44");   // KSCATEGORY_VIDEO_CAMERA
    static readonly Guid Disk = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");          // GUID_DEVINTERFACE_DISK
    static readonly Guid Portable = new("6AC27878-A6FA-4155-BA85-F98F491D4F33");      // GUID_DEVINTERFACE_WPD

    const uint Success = 0;                    // CR_SUCCESS
    const uint RemoveVetoed = 0x17;            // CR_REMOVE_VETOED
    const uint LocateNormal = 0;               // present devices only
    const uint DisableAbsolute = 0x1;          // do not let the driver refuse
    const uint DisableUiNotOk = 0x4;           // never show a prompt
    const uint HasProblem = 0x400;             // DN_HAS_PROBLEM
    const uint ProblemDisabled = 22;           // CM_PROB_DISABLED
    const uint IoctlStorageGetDeviceNumber = 0x2D1080;

    /// <summary>Instance ids of the cameras that are present and working now.</summary>
    public static List<string> Cameras() => WithInterface(VideoCamera).Select(found => found.InstanceId)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Instance ids of the disk drives connected through USB that are present and working now: memory
    /// sticks, external disks, card readers. The disk Windows runs from is never returned, even when it
    /// is itself a USB disk, and nothing is returned when that disk cannot be identified.
    /// </summary>
    public static List<string> UsbDisks()
    {
        var systemDisk = DiskNumber(@"\\.\" + Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'));
        if (systemDisk is null) return new();
        return WithInterface(Disk)
            .Where(found => IsOnUsb(found.InstanceId) && DiskNumber(found.InterfacePath) != systemDisk)
            .Select(found => found.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Instance ids of the phones, cameras and media players connected through USB that offer their
    /// files as a portable device (MTP or PTP) instead of as a disk drive.
    /// </summary>
    public static List<string> UsbPortableDevices() => WithInterface(Portable)
        .Where(found => IsOnUsb(found.InstanceId))
        .Select(found => found.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when the device, or anything it is connected through, sits on the USB bus.</summary>
    public static bool IsOnUsb(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var node, instanceId, LocateNormal) != Success) return false;
        for (var depth = 0; depth < 16; depth++)
        {
            var id = DeviceId(node);
            if (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase)) return true;
            if (CM_Get_Parent(out node, node, 0) != Success) return false;
        }
        return false;
    }

    /// <summary>Returns null when the device is now disabled, otherwise why it is not.</summary>
    public static string? Disable(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var node, instanceId, LocateNormal) != Success) return "the device is no longer connected";
        // Deliberately without CM_DISABLE_PERSIST: the caller keeps its own list and applies it again
        // after a restart, so a device is not left disabled for good if this program is removed meanwhile.
        var result = CM_Disable_DevNode(node, DisableAbsolute | DisableUiNotOk);
        return result switch
        {
            Success => null,
            RemoveVetoed => "it is in use and Windows would not release it",
            _ => $"Windows refused to disable it (code {result})",
        };
    }

    /// <summary>
    /// Returns true when the device is enabled again or is not disabled. False means it is not
    /// connected at the moment (or Windows refused), so the caller should try again later.
    /// </summary>
    public static bool Enable(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var node, instanceId, LocateNormal) != Success) return false;
        if (!IsDisabled(node)) return true;
        return CM_Enable_DevNode(node, 0) == Success;
    }

    static bool IsDisabled(uint node) =>
        CM_Get_DevNode_Status(out var status, out var problem, node, 0) == Success &&
        (status & HasProblem) != 0 && problem == ProblemDisabled;

    // ---- device tree ----------------------------------------------------------------------------

    static List<(string InterfacePath, string InstanceId)> WithInterface(Guid interfaceClass)
    {
        var found = new List<(string, string)>();
        // The list can grow between asking for its size and reading it; try again a few times.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out var length, ref interfaceClass, null, 0) != Success || length <= 1) return found;
            var buffer = new char[length];
            if (CM_Get_Device_Interface_ListW(ref interfaceClass, null, buffer, length, 0) != Success) continue;
            foreach (var path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (InstanceIdOf(path) is { Length: > 0 } id) found.Add((path, id));
            return found;
        }
        return found;
    }

    static string? InstanceIdOf(string interfacePath)
    {
        var key = new DevPropKey { FormatId = new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), PropertyId = 256 }; // DEVPKEY_Device_InstanceId
        var buffer = new byte[1024];
        var size = (uint)buffer.Length;
        if (CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, buffer, ref size, 0) != Success || size < 2) return null;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
    }

    static string DeviceId(uint node)
    {
        var buffer = new char[512];
        if (CM_Get_Device_IDW(node, buffer, (uint)buffer.Length, 0) != Success) return "";
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>The number Windows gives a physical disk ("Disk 1"), from a volume or a disk interface path.</summary>
    static uint? DiskNumber(string path)
    {
        // No access rights are requested: asking a device for its number needs none.
        using var handle = CreateFileW(path, 0, 3 /* share read+write */, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        return DeviceIoControl(handle, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, out var number,
            (uint)Marshal.SizeOf<StorageDeviceNumber>(), out _, IntPtr.Zero) && number.DeviceType == 7 /* FILE_DEVICE_DISK */
            ? number.DeviceNumber
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StorageDeviceNumber
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_PropertyW(string interfacePath, ref DevPropKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_IDW(uint node, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Disable_DevNode(uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Enable_DevNode(uint node, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize,
        out StorageDeviceNumber output, uint outputSize, out uint returned, IntPtr overlapped);
}
