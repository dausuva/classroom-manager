// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// The "turn off the display after" time of the active Windows power plan while the computer is plugged
/// in. It is one machine-wide setting, so it runs in the elevated service. The time on battery is read
/// but never changed. The value found before the first change is kept, so it can be put back.
/// </summary>
sealed class DisplayPowerHandler
{
    static readonly Guid VideoSubgroup = new("7516B95F-F776-4464-8C53-06167F40CC99");   // GUID_VIDEO_SUBGROUP
    static readonly Guid VideoIdle = new("3C0BC021-C8A8-4E07-A973-6B14CBCB2B7E");       // GUID_VIDEO_POWERDOWN_TIMEOUT
    const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\3C0BC021-C8A8-4E07-A973-6B14CBCB2B7E";

    sealed class SavedState
    {
        public Guid Scheme { get; set; }
        public uint AcSeconds { get; set; }
    }

    static string StatePath => Path.Combine(ClientConfig.DataDir, "display-power.json");

    readonly object _gate = new();

    public Response Get()
    {
        try { return Response.Success(Describe()); }
        catch (Exception ex) { return Response.Fail("Could not read the display power setting: " + ex.Message); }
    }

    public Response Set(JsonObject a)
    {
        if (AgentContext.IsUserAgent) return Response.Fail("The display power setting must be changed at the system level.");
        try
        {
            lock (_gate)
            {
                if (IsPolicyManaged())
                    return Response.Fail("A Group Policy controls when this computer turns its display off, so Classroom Manager cannot change it.");

                if (a.Bool(DisplayPowerRules.Restore) == true)
                {
                    if (!File.Exists(StatePath)) return Response.Success(Describe());   // nothing was changed
                    var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath)) ?? new SavedState();
                    // Back into the plan it came from; when that plan is gone, into the one in use now.
                    try { WriteAc(saved.Scheme, saved.AcSeconds); }
                    catch (InvalidOperationException) { WriteAc(ActiveScheme(), saved.AcSeconds); }
                    File.Delete(StatePath);
                    Log.Write($"power.display.set: restored to {DisplayPowerRules.Describe((int)saved.AcSeconds)}.");
                    return Response.Success(Describe());
                }

                if (a.Int(DisplayPowerRules.Seconds) is not { } requested)
                    return Response.Fail("No display time was given.");
                var seconds = (uint)DisplayPowerRules.Clamp(requested);
                var scheme = ActiveScheme();
                if (!File.Exists(StatePath))
                {
                    Directory.CreateDirectory(ClientConfig.DataDir);
                    File.WriteAllText(StatePath, JsonSerializer.Serialize(new SavedState { Scheme = scheme, AcSeconds = ReadAc(scheme) }));
                }
                WriteAc(scheme, seconds);
                Log.Write($"power.display.set: display turns off {DisplayPowerRules.Describe((int)seconds)} when plugged in.");
                return Response.Success(Describe());
            }
        }
        catch (Exception ex) { return Response.Fail("Could not change the display power setting: " + ex.Message); }
    }

    static JsonObject Describe()
    {
        var scheme = ActiveScheme();
        var sub = VideoSubgroup;
        var setting = VideoIdle;
        var battery = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var dc) == 0 ? (int)dc : -1;
        return new JsonObject
        {
            [DisplayPowerRules.Seconds] = (int)ReadAc(scheme),
            ["dcSec"] = battery,
            ["policyManaged"] = IsPolicyManaged(),
            ["canRestore"] = File.Exists(StatePath),
        };
    }

    static bool IsPolicyManaged()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(PolicyKey);
        return key?.GetValue("ACSettingIndex") is not null;
    }

    static Guid ActiveScheme()
    {
        var result = PowerGetActiveScheme(IntPtr.Zero, out var pointer);
        if (result != 0) throw new InvalidOperationException($"Windows did not name the active power plan (error {result}).");
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    static uint ReadAc(Guid scheme)
    {
        var sub = VideoSubgroup;
        var setting = VideoIdle;
        var result = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var seconds);
        if (result != 0) throw new InvalidOperationException($"Windows did not return the display time (error {result}).");
        return seconds;
    }

    static void WriteAc(Guid scheme, uint seconds)
    {
        var sub = VideoSubgroup;
        var setting = VideoIdle;
        var result = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, seconds);
        if (result != 0) throw new InvalidOperationException($"Windows refused the display time (error {result}).");
        // A changed value only takes effect when its plan is made active again.
        var active = ActiveScheme();
        if (active == scheme) PowerSetActiveScheme(IntPtr.Zero, ref active);
    }

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr rootKey, out IntPtr scheme);

    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveScheme(IntPtr rootKey, ref Guid scheme);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadDCValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerWriteACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
}
