// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Exam-time device controls, part of the Exam mode overlay: mute audio, turn cameras off, and block
/// removable drives. All three are machine-wide and run in the elevated service.
/// Each control records what it changed, so turning it off puts back exactly the earlier setting
/// and leaves anything it did not change (a policy already in force, a device the user had muted) alone.
/// <para>
/// Cameras and USB drives are blocked twice over. The Windows setting (camera access) or policy
/// (removable disks) covers devices this code cannot see, but neither is reliable alone: the camera
/// setting does not stop every desktop program or one that already has the camera open, and the disk
/// policy is not applied to a drive that is already connected. So the devices themselves are also
/// disabled, as Device Manager does, and enabled again afterwards.
/// </para>
/// </summary>
sealed class ExamDeviceHandler
{
    // The device-wide switch behind Settings > Privacy > Camera > "Camera access".
    const string CameraKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    const string CameraValue = "Value";
    const string CameraPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";
    // Windows' "Removable Disks: Deny read/write/execute access" policy values.
    const string UsbKey = @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}";
    static readonly string[] UsbValues = ["Deny_Read", "Deny_Write", "Deny_Execute"];
    // "WPD Devices: Deny read/write access": phones, cameras and media players, which are not disk drives.
    static readonly string[] PortableKeys =
    [
        @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{6AC27878-A6FA-4155-BA85-F98F491D4F33}",
        @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}",
    ];
    static readonly string[] PortableValues = ["Deny_Read", "Deny_Write"];

    sealed class State
    {
        public bool MuteAudio { get; set; }
        public bool DisableCamera { get; set; }
        public bool BlockUsbStorage { get; set; }
        /// <summary>Endpoints this handler muted; only these are unmuted afterwards.</summary>
        public List<string> MutedEndpoints { get; set; } = new();
        public bool CameraChanged { get; set; }
        public string? CameraPrevious { get; set; }
        public bool UsbKeyCreated { get; set; }
        /// <summary>Earlier data of each policy value this handler changed; null means the value was absent.</summary>
        public Dictionary<string, int?> UsbPrevious { get; set; } = new();
        /// <summary>The same for the portable-device policy, by "key\value"; and the keys this handler created.</summary>
        public Dictionary<string, int?> PortablePrevious { get; set; } = new();
        public List<string> PortableKeysCreated { get; set; } = new();
        /// <summary>
        /// Devices this handler disabled and has not enabled again. An entry outlives its control when
        /// the device was unplugged in between: it is enabled the next time it is seen.
        /// </summary>
        public List<string> DisabledCameras { get; set; } = new();
        public List<string> DisabledUsbDisks { get; set; } = new();
    }

    static string StatePath => Path.Combine(ClientConfig.DataDir, "exam-devices.json");

    readonly object _gate = new();
    State? _state;
    string _lastEnforceProblem = "";

    public Response Set(JsonObject a)
    {
        if (AgentContext.IsUserAgent) return Response.Fail("Exam device controls must be changed at the system level.");
        var muteAudio = a.Bool(ExamDeviceControls.MuteAudio) ?? false;
        var disableCamera = a.Bool(ExamDeviceControls.DisableCamera) ?? false;
        var blockUsb = a.Bool(ExamDeviceControls.BlockUsbStorage) ?? false;

        string summary;
        lock (_gate)
        {
            var state = Load();
            summary = ExamDeviceControls.Summarize(
            [
                (ExamDeviceControls.AudioLabel, Try(() => SetAudio(state, muteAudio))),
                (ExamDeviceControls.CameraLabel, Try(() => SetCamera(state, disableCamera))),
                (ExamDeviceControls.UsbStorageLabel, Try(() => SetUsb(state, blockUsb))),
            ]);
            Save(state);
        }
        Log.Write($"block.exam.devices: audio={muteAudio}, camera={disableCamera}, usb={blockUsb}" +
                  (summary.Length == 0 ? "" : $" - {summary}"));
        if (summary.Length > 0) return Response.Fail(summary);
        lock (_gate)
            return Response.Success(new JsonObject
            {
                [ExamDeviceControls.MuteAudio] = muteAudio,
                [ExamDeviceControls.DisableCamera] = disableCamera,
                [ExamDeviceControls.BlockUsbStorage] = blockUsb,
                ["camerasOff"] = Load().DisabledCameras.Count,
                ["usbDrivesOff"] = Load().DisabledUsbDisks.Count,
            });
    }

    /// <summary>Turns every control off again; used when Exam mode ends. Failures are logged, not thrown.</summary>
    public void Restore()
    {
        if (AgentContext.IsUserAgent) return;
        lock (_gate)
        {
            var state = Load();
            if (!state.MuteAudio && !state.DisableCamera && !state.BlockUsbStorage) return;
            var summary = ExamDeviceControls.Summarize(
            [
                (ExamDeviceControls.AudioLabel, Try(() => SetAudio(state, false))),
                (ExamDeviceControls.CameraLabel, Try(() => SetCamera(state, false))),
                (ExamDeviceControls.UsbStorageLabel, Try(() => SetUsb(state, false))),
            ]);
            Save(state);
            Log.Write("block.exam.devices: restored" + (summary.Length == 0 ? "." : $" with problems - {summary}"));
        }
    }

    /// <summary>
    /// Called by the service's watcher every few seconds: a student can unmute from the volume
    /// control, and a newly plugged headset, camera or memory stick arrives working, so each control
    /// has to be repeated. It also enables devices again that were unplugged when their control ended.
    /// </summary>
    public void Enforce()
    {
        if (AgentContext.IsUserAgent) return;
        lock (_gate)
        {
            var state = Load();
            var problems = new List<string>();
            try
            {
                var changed = state.MuteAudio && MuteAndRemember(state);
                if (state.DisableCamera)
                {
                    if (state.CameraChanged) DenyCamera();
                    changed |= DisableDevices(DeviceNodes.Cameras(), state.DisabledCameras, "camera", problems);
                }
                else changed |= EnableDevices(state.DisabledCameras);
                if (state.BlockUsbStorage)
                {
                    changed |= DisableDevices(DeviceNodes.UsbDisks(), state.DisabledUsbDisks, "USB drive", problems);
                    changed |= DisableDevices(DeviceNodes.UsbPortableDevices(), state.DisabledUsbDisks, "phone or media device", problems);
                }
                else changed |= EnableDevices(state.DisabledUsbDisks);
                if (changed) Save(state);
            }
            catch (Exception ex) { problems.Add(ex.Message); }
            // The same problem comes back every few seconds until it is solved; log it once.
            var problem = string.Join("; ", problems);
            if (problem.Length > 0 && problem != _lastEnforceProblem)
                Log.Write($"block.exam.devices: could not re-apply controls: {problem}");
            _lastEnforceProblem = problem;
        }
    }

    // ---- devices ------------------------------------------------------------------------------

    /// <summary>Disables each device and remembers it. Returns true when the remembered list grew.</summary>
    static bool DisableDevices(List<string> present, List<string> remembered, string kind, List<string> problems)
    {
        var changed = false;
        foreach (var id in present)
        {
            var problem = DeviceNodes.Disable(id);
            if (problem is not null)
            {
                problems.Add($"{kind} {id}: {problem}");
                continue;
            }
            if (remembered.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            remembered.Add(id);
            Log.Write($"block.exam.devices: disabled {kind} {id}.");
            changed = true;
        }
        return changed;
    }

    /// <summary>Enables the remembered devices that are connected now. Returns true when the list shrank.</summary>
    static bool EnableDevices(List<string> remembered)
    {
        var changed = false;
        foreach (var id in remembered.ToList())
        {
            if (!DeviceNodes.Enable(id)) continue;   // unplugged: tried again by Enforce
            remembered.Remove(id);
            Log.Write($"block.exam.devices: enabled {id} again.");
            changed = true;
        }
        return changed;
    }

    static void ThrowIfAny(List<string> problems, string kind)
    {
        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"{problems.Count} {kind}(s) could not be turned off yet and are tried again every few seconds ({string.Join("; ", problems)})");
    }

    // ---- audio --------------------------------------------------------------------------------

    static void SetAudio(State state, bool mute)
    {
        if (mute)
        {
            state.MuteAudio = true;
            MuteAndRemember(state);
            return;
        }
        if (!state.MuteAudio) return;
        AudioEndpoints.Unmute(state.MutedEndpoints);
        state.MutedEndpoints.Clear();
        state.MuteAudio = false;
    }

    static bool MuteAndRemember(State state)
    {
        var added = AudioEndpoints.MuteAll()
            .Where(id => !state.MutedEndpoints.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        state.MutedEndpoints.AddRange(added);
        return added.Count > 0;
    }

    // ---- camera -------------------------------------------------------------------------------

    static void SetCamera(State state, bool disable)
    {
        if (disable)
        {
            state.DisableCamera = true;
            // Where Group Policy owns the camera access setting it is left alone; disabling the
            // cameras themselves below does not depend on it.
            if (!CameraPolicyManaged())
            {
                if (!state.CameraChanged)
                {
                    using var machine = Machine();
                    using var key = machine.OpenSubKey(CameraKey);
                    state.CameraPrevious = key?.GetValue(CameraValue) as string;
                    state.CameraChanged = true;
                }
                DenyCamera();
            }
            CloseCameraApp();
            var problems = new List<string>();
            DisableDevices(DeviceNodes.Cameras(), state.DisabledCameras, "camera", problems);
            ThrowIfAny(problems, "camera");
            return;
        }
        if (!state.DisableCamera) return;
        EnableDevices(state.DisabledCameras);
        if (state.CameraChanged)
        {
            using var machine = Machine();
            using var key = machine.CreateSubKey(CameraKey);
            if (state.CameraPrevious is null) key.DeleteValue(CameraValue, throwOnMissingValue: false);
            else key.SetValue(CameraValue, state.CameraPrevious, RegistryValueKind.String);
        }
        state.CameraChanged = false;
        state.CameraPrevious = null;
        state.DisableCamera = false;
    }

    static bool CameraPolicyManaged()
    {
        using var machine = Machine();
        using var policy = machine.OpenSubKey(CameraPolicyKey);
        return policy?.GetValue("LetAppsAccessCamera") is not null;
    }

    /// <summary>Closes the Windows Camera app, which would otherwise stay open on an error page.</summary>
    static void CloseCameraApp()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("WindowsCamera"))
            using (process)
            {
                try { process.Kill(); }
                catch { /* already closed, or not ours to close */ }
            }
    }

    static void DenyCamera()
    {
        using var machine = Machine();
        using var key = machine.CreateSubKey(CameraKey);
        if (key.GetValue(CameraValue) as string != "Deny") key.SetValue(CameraValue, "Deny", RegistryValueKind.String);
    }

    // ---- removable storage --------------------------------------------------------------------

    static void SetUsb(State state, bool block)
    {
        using var machine = Machine();
        if (block)
        {
            using var existing = machine.OpenSubKey(UsbKey);
            if (existing is null && !state.BlockUsbStorage) state.UsbKeyCreated = true;
            using var key = machine.CreateSubKey(UsbKey);
            foreach (var name in UsbValues)
            {
                var current = key.GetValue(name) as int?;
                if (current == 1) continue; // already denied, by an earlier call or by real policy
                if (!state.UsbPrevious.ContainsKey(name)) state.UsbPrevious[name] = current;
                key.SetValue(name, 1, RegistryValueKind.DWord);
            }
            foreach (var path in PortableKeys)
            {
                using var before = machine.OpenSubKey(path);
                if (before is null && !state.PortableKeysCreated.Contains(path)) state.PortableKeysCreated.Add(path);
                using var portable = machine.CreateSubKey(path);
                foreach (var name in PortableValues)
                {
                    var current = portable.GetValue(name) as int?;
                    if (current == 1) continue;
                    if (!state.PortablePrevious.ContainsKey($@"{path}\{name}")) state.PortablePrevious[$@"{path}\{name}"] = current;
                    portable.SetValue(name, 1, RegistryValueKind.DWord);
                }
            }
            state.BlockUsbStorage = true;
            // Windows applies that policy to drives connected from now on, not to one already in use.
            var problems = new List<string>();
            DisableDevices(DeviceNodes.UsbDisks(), state.DisabledUsbDisks, "USB drive", problems);
            DisableDevices(DeviceNodes.UsbPortableDevices(), state.DisabledUsbDisks, "phone or media device", problems);
            ThrowIfAny(problems, "USB device");
            return;
        }
        if (!state.BlockUsbStorage) return;
        foreach (var path in PortableKeys)
        {
            using (var portable = machine.OpenSubKey(path, writable: true))
            {
                if (portable is not null)
                    foreach (var name in PortableValues)
                    {
                        if (!state.PortablePrevious.TryGetValue($@"{path}\{name}", out var previous)) continue;
                        if (previous is null) portable.DeleteValue(name, throwOnMissingValue: false);
                        else portable.SetValue(name, previous.Value, RegistryValueKind.DWord);
                    }
            }
            if (!state.PortableKeysCreated.Contains(path)) continue;
            using var left = machine.OpenSubKey(path);
            if (left is not null && left.ValueCount == 0 && left.SubKeyCount == 0)
                machine.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
        state.PortablePrevious.Clear();
        state.PortableKeysCreated.Clear();
        using (var key = machine.OpenSubKey(UsbKey, writable: true))
        {
            if (key is not null)
                foreach (var (name, previous) in state.UsbPrevious)
                {
                    if (previous is null) key.DeleteValue(name, throwOnMissingValue: false);
                    else key.SetValue(name, previous.Value, RegistryValueKind.DWord);
                }
        }
        if (state.UsbKeyCreated)
        {
            using var key = machine.OpenSubKey(UsbKey);
            if (key is not null && key.ValueCount == 0 && key.SubKeyCount == 0)
                machine.DeleteSubKey(UsbKey, throwOnMissingSubKey: false);
        }
        state.UsbPrevious.Clear();
        state.UsbKeyCreated = false;
        state.BlockUsbStorage = false;
        // After the policy is gone, so the drives come back readable.
        EnableDevices(state.DisabledUsbDisks);
    }

    // ---- state --------------------------------------------------------------------------------

    static RegistryKey Machine() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    static string? Try(Action change)
    {
        try
        {
            change();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    State Load()
    {
        if (_state is not null) return _state;
        try
        {
            if (File.Exists(StatePath))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Protocol.Json);
        }
        catch (Exception ex) { Log.Write($"block.exam.devices: could not load saved state: {ex.Message}"); }
        _state ??= new State();
        _state.MutedEndpoints ??= new();
        _state.UsbPrevious ??= new();
        _state.DisabledCameras ??= new();
        _state.DisabledUsbDisks ??= new();
        return _state;
    }

    static void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(ClientConfig.DataDir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Protocol.Json));
        }
        catch (Exception ex) { Log.Write($"block.exam.devices: could not save state: {ex.Message}"); }
    }
}
