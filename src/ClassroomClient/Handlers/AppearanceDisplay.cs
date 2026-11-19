// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClassroomManager.Client;

/// <summary>
/// The primary display's size and orientation through the system's own calls. A change is dynamic (not stored): Windows goes back to the
/// stored mode when asked to, and at the next sign-in, so a mode that leaves nobody a picture does not outlive the session.
/// </summary>
static class DisplayModes
{
    const int ENUM_CURRENT_SETTINGS = -1;
    const uint DM_DISPLAYORIENTATION = 0x00000080;
    const uint DM_PELSWIDTH = 0x00080000;
    const uint DM_PELSHEIGHT = 0x00100000;
    const uint CDS_UPDATEREGISTRY = 0x00000001;
    const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    static extern int ChangeDisplaySettingsEx(string? device, ref DEVMODE devMode, IntPtr window, uint flags, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    static extern int ChangeDisplaySettingsEx(string? device, IntPtr devMode, IntPtr window, uint flags, IntPtr parameter);

    static DEVMODE Current()
    {
        var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode)) throw new IOException("Windows did not report the display mode.");
        return mode;
    }

    /// <summary>The current size as "1920x1080".</summary>
    public static string Size() { var mode = Current(); return $"{mode.dmPelsWidth}x{mode.dmPelsHeight}"; }

    /// <summary>0 landscape, 1 portrait, 2 landscape flipped, 3 portrait flipped.</summary>
    public static int Orientation() => (int)Current().dmDisplayOrientation;

    /// <summary>The sizes the display offers in the current colour depth and orientation, largest first.</summary>
    public static IReadOnlyList<(string Value, string Label)> Sizes()
    {
        var current = Current();
        var sizes = new SortedSet<(uint Width, uint Height)>();
        var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        for (var index = 0; EnumDisplaySettings(null, index, ref mode); index++)
            if (mode.dmBitsPerPel == current.dmBitsPerPel && mode.dmPelsWidth >= 800 && mode.dmPelsHeight >= 600) sizes.Add((mode.dmPelsWidth, mode.dmPelsHeight));
        return sizes.Reverse().Select(size => ($"{size.Width}x{size.Height}", $"{size.Width} x {size.Height}")).ToArray();
    }

    public static void SetSize(string value, bool store)
    {
        var parts = value.Split('x');
        if (parts.Length != 2 || !uint.TryParse(parts[0], out var width) || !uint.TryParse(parts[1], out var height)) throw new IOException("That is not a display size.");
        var mode = Current();
        mode.dmPelsWidth = width;
        mode.dmPelsHeight = height;
        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
        Apply(ref mode, store);
    }

    public static void SetOrientation(int orientation, bool store)
    {
        var mode = Current();
        // A turn between landscape and portrait swaps the two sides.
        if ((mode.dmDisplayOrientation % 2) != (orientation % 2)) (mode.dmPelsWidth, mode.dmPelsHeight) = (mode.dmPelsHeight, mode.dmPelsWidth);
        mode.dmDisplayOrientation = (uint)orientation;
        mode.dmFields = DM_DISPLAYORIENTATION | DM_PELSWIDTH | DM_PELSHEIGHT;
        Apply(ref mode, store);
    }

    /// <summary>Stores the current mode so it is the one Windows uses at the next sign-in.</summary>
    public static void Keep()
    {
        var mode = Current();
        mode.dmFields = DM_DISPLAYORIENTATION | DM_PELSWIDTH | DM_PELSHEIGHT;
        Apply(ref mode, store: true);
    }

    /// <summary>Goes back to the stored mode.</summary>
    public static void GoBackToStored() => ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

    static void Apply(ref DEVMODE mode, bool store)
    {
        var result = ChangeDisplaySettingsEx(null, ref mode, IntPtr.Zero, store ? CDS_UPDATEREGISTRY : 0, IntPtr.Zero);
        if (result != DISP_CHANGE_SUCCESSFUL) throw new IOException($"Windows did not accept the display mode (code {result}).");
    }
}

/// <summary>Asks the person at the computer to keep a display change, as Windows does; it goes back by itself when nobody answers.</summary>
sealed class DisplayConfirmDialog : IAppearanceConfirmer
{
    public void Ask(string what, int seconds, Action keep, Action revert)
    {
        var thread = new Thread(() =>
        {
            var remaining = seconds;
            var decided = false;
            using var form = new Form
            {
                Text = "Classroom Manager", TopMost = true, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = true, ClientSize = new Size(420, 130),
            };
            var label = new Label { Left = 16, Top = 14, Width = 388, Height = 60, Text = "" };
            var keepButton = new Button { Name = "KeepButton", Text = Loc.T("Keep changes"), Left = 16, Top = 84, Width = 180 };
            var revertButton = new Button { Name = "RevertButton", Text = Loc.T("Revert"), Left = 224, Top = 84, Width = 180 };
            void Decide(bool keepIt)
            {
                if (decided) return;
                decided = true;
                form.Close();
                if (keepIt) keep(); else revert();
            }
            void Show() => label.Text = Loc.T("Classroom Manager changed your screen settings.") + "\n" + Loc.F($"Keep them? Going back in {remaining} second(s).");
            keepButton.Click += (_, _) => Decide(true);
            revertButton.Click += (_, _) => Decide(false);
            form.FormClosed += (_, _) => Decide(false);
            using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (_, _) => { if (--remaining <= 0) Decide(false); else Show(); };
            form.Controls.AddRange([label, keepButton, revertButton]);
            Show();
            timer.Start();
            Application.Run(form);
        }) { IsBackground = true, Name = "appearance-confirm" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
