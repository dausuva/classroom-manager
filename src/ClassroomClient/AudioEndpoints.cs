// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;

namespace ClassroomManager.Client;

/// <summary>
/// Mute state of the active playback and recording devices through the Windows Core Audio API.
/// An endpoint's master mute is one machine-wide setting, not a per-user one.
/// </summary>
static class AudioEndpoints
{
    public sealed record Endpoint(string Id, bool Recording, bool Muted);

    const int Render = 0;
    const int Capture = 1;
    const int DeviceStateActive = 1;
    const int ClsCtxAll = 23;

    public static List<Endpoint> List()
    {
        var result = new List<Endpoint>();
        Visit((id, recording, volume) =>
        {
            if (volume.GetMute(out var muted) == 0) result.Add(new Endpoint(id, recording, muted));
        });
        return result;
    }

    /// <summary>Mutes every active endpoint that is not already muted and returns the ids it changed.</summary>
    public static List<string> MuteAll()
    {
        var changed = new List<string>();
        Visit((id, _, volume) =>
        {
            if (volume.GetMute(out var muted) != 0 || muted) return;
            var context = Guid.Empty;
            if (volume.SetMute(true, ref context) == 0) changed.Add(id);
        });
        return changed;
    }

    /// <summary>Unmutes only the named endpoints, leaving any the user had muted themselves alone.</summary>
    public static void Unmute(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return;
        Visit((id, _, volume) =>
        {
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase)) return;
            var context = Guid.Empty;
            volume.SetMute(false, ref context);
        });
    }

    static void Visit(Action<string, bool, IAudioEndpointVolume> action)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            foreach (var flow in new[] { Render, Capture })
            {
                if (enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out var devices) != 0) continue;
                try
                {
                    if (devices.GetCount(out var count) != 0) continue;
                    for (var index = 0; index < count; index++)
                    {
                        if (devices.Item(index, out var device) != 0) continue;
                        try
                        {
                            var iid = typeof(IAudioEndpointVolume).GUID;
                            if (device.GetId(out var id) != 0 ||
                                device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var activated) != 0) continue;
                            try { action(id, flow == Capture, (IAudioEndpointVolume)activated); }
                            finally { Marshal.ReleaseComObject(activated); }
                        }
                        finally { Marshal.ReleaseComObject(device); }
                    }
                }
                finally { Marshal.ReleaseComObject(devices); }
            }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    // ---- Core Audio COM interfaces: only the leading methods that are called are declared by name. ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, int classContext, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object activated);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        // Eleven methods precede SetMute in the interface; they keep the later slots in the right place.
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(int channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
