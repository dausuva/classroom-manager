// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Full-screen "screen locked" overlay with a message, on every monitor. Runs in the user helper.
/// It is a deterrent for a classroom, not a security boundary: Ctrl+Alt+Del cannot be blocked and
/// a standard user can end their own helper process from Task Manager (the service re-applies the lock
/// when the helper reconnects).
/// </summary>
sealed class LockHandler
{
    readonly List<Form> _forms = new();
    System.Windows.Forms.Timer? _timer;
    string _title = "";
    string _message = "";

    // low-level keyboard hook to swallow Win key, Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4
    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    static readonly LowLevelKeyboardProc HookProc = HookCallback; // kept in a static so it is never GC'd
    static IntPtr _hook = IntPtr.Zero;

    const int WH_KEYBOARD_LL = 13;
    const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_F4 = 0x73, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const int LLKHF_ALTDOWN = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);            // KBDLLHOOKSTRUCT.vkCode
            int flags = Marshal.ReadInt32(lParam, 8);      // KBDLLHOOKSTRUCT.flags
            bool alt = (flags & LLKHF_ALTDOWN) != 0;
            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;

            bool block = vk is VK_LWIN or VK_RWIN
                         || (alt && vk is VK_TAB or VK_ESCAPE or VK_F4)
                         || (ctrl && vk == VK_ESCAPE);
            if (block) return (IntPtr)1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public Task<Response> SetAsync(JsonObject a)
    {
        var message = a.Str("message", Loc.T("This screen is locked by your teacher."));
        var title = a.Str("title", "Screen locked");
        if (message.Length > 1000) message = message[..1000];
        if (title.Length > 100) title = title[..100];

        return RunOnUi(() =>
        {
            _title = title;
            _message = message;
            Rebuild();

            if (_timer is null)
            {
                _timer = new System.Windows.Forms.Timer { Interval = 500 };
                _timer.Tick += (_, _) => Keepalive();
                _timer.Start();
            }
            InstallHook();
        });
    }

    public Task<Response> ClearAsync() => RunOnUi(() =>
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        RemoveHook();
        CloseForms();
    });

    static async Task<Response> RunOnUiCore(Action a)
    {
        await Ui.RunAsync(a);
        return Response.Success();
    }

    Task<Response> RunOnUi(Action a) => RunOnUiCore(a);

    // ---- UI thread only ------------------------------------------------------------

    void Rebuild()
    {
        CloseForms();
        foreach (var screen in Screen.AllScreens)
        {
            var title = new Label
            {
                Text = _title,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 40, FontStyle.Bold),
                AutoSize = false,
                TextAlign = ContentAlignment.BottomCenter,
                Dock = DockStyle.Top,
                Height = screen.Bounds.Height / 2 - 20,
            };
            var text = new Label
            {
                Text = _message,
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 24),
                AutoSize = false,
                TextAlign = ContentAlignment.TopCenter,
                Dock = DockStyle.Fill,
                Padding = new Padding(80, 20, 80, 0),
            };

            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = screen.Bounds,
                BackColor = Color.FromArgb(24, 32, 48),
                TopMost = true,
                ShowInTaskbar = false,
                ControlBox = false,
                KeyPreview = true,
            };
            form.Controls.Add(text);
            form.Controls.Add(title);
            form.KeyDown += (_, e) => e.Handled = e.SuppressKeyPress = true;
            form.FormClosing += (_, e) =>
            {
                // only we may close the overlay
                if (_timer is not null && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            };
            form.Show();
            form.Activate();
            _forms.Add(form);
        }
    }

    void Keepalive()
    {
        // monitors plugged / unplugged: rebuild
        if (Screen.AllScreens.Length != _forms.Count) { Rebuild(); return; }
        foreach (var f in _forms)
        {
            f.TopMost = true;
            f.BringToFront();
        }
        if (_forms.Count > 0) _forms[0].Activate();
    }

    void CloseForms()
    {
        var old = _forms.ToList();
        _forms.Clear();
        foreach (var f in old)
        {
            f.Close();
            f.Dispose();
        }
    }

    static void InstallHook()
    {
        if (_hook != IntPtr.Zero) return;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, HookProc, GetModuleHandle(module?.ModuleName), 0);
    }

    static void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
