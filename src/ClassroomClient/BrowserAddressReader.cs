// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;

namespace ClassroomManager.Client;

/// <summary>
/// Reads what the address bar of each open browser window shows, through the accessibility
/// interface every browser offers to screen readers (the same for Chrome, Edge, Firefox and their
/// relatives, and in any display language). It only reads: no window is activated, clicked or typed
/// into. The page itself is never walked, only the browser's own toolbar, so page content is not read
/// and a text box on a web page cannot be mistaken for the address bar.
/// </summary>
static class BrowserAddressReader
{
    /// <summary>Process names of the browsers looked at, with the name shown to the teacher.</summary>
    static readonly Dictionary<string, string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome", ["msedge"] = "Edge", ["firefox"] = "Firefox", ["brave"] = "Brave",
        ["opera"] = "Opera", ["vivaldi"] = "Vivaldi", ["librewolf"] = "LibreWolf", ["waterfox"] = "Waterfox",
    };

    const uint OBJID_CLIENT = 0xFFFFFFFC;
    const int ROLE_SYSTEM_DOCUMENT = 15;
    const int ROLE_SYSTEM_TEXT = 42;
    const int ROLE_SYSTEM_COMBOBOX = 46;
    const int MaxNodes = 900;
    const int MaxDepth = 16;
    static readonly TimeSpan PerWindow = TimeSpan.FromMilliseconds(1500);

    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object? accessible);

    [DllImport("oleacc.dll")]
    static extern int AccessibleChildren(IAccessible container, int start, int count, [Out] object?[] children, out int obtained);

    public sealed record Window(string Browser, string Title, string Address);

    /// <summary>Every visible browser window of this session with a title, and the text of its address bar ("" when not found).</summary>
    public static List<Window> Read()
    {
        // Accessibility objects belong to the thread that asked for them, and want a single-threaded one.
        var result = new List<Window>();
        var thread = new Thread(() =>
        {
            try { result = ReadOnThisThread(); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException) { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        // A browser that does not answer must not hold up the reply to the Host.
        return thread.Join(TimeSpan.FromSeconds(12)) ? result : [];
    }

    static List<Window> ReadOnThisThread()
    {
        int session = Process.GetCurrentProcess().SessionId;
        var windows = new List<(IntPtr Handle, string Browser, string Title)>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var title = new StringBuilder(512);
            if (GetWindowText(handle, title, title.Capacity) == 0) return true;
            GetWindowThreadProcessId(handle, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (process.SessionId == session && Browsers.TryGetValue(process.ProcessName, out var browser))
                    windows.Add((handle, browser, title.ToString()));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);

        return windows.Select(window => new Window(window.Browser, window.Title, AddressOf(window.Handle))).ToList();
    }

    /// <summary>
    /// Walks the window's toolbar level by level and returns the value of the first text box whose
    /// content is an address. The web page (a "document") is not entered.
    /// </summary>
    static string AddressOf(IntPtr window)
    {
        var iid = typeof(IAccessible).GUID;
        if (AccessibleObjectFromWindow(window, OBJID_CLIENT, ref iid, out var root) != 0 || root is not IAccessible start) return "";

        var deadline = Stopwatch.StartNew();
        var queue = new Queue<(IAccessible Node, int Depth)>();
        queue.Enqueue((start, 0));
        int visited = 0;
        while (queue.Count > 0 && visited < MaxNodes && deadline.Elapsed < PerWindow)
        {
            var (node, depth) = queue.Dequeue();
            visited++;
            int count;
            try { count = node.accChildCount; }
            catch (Exception ex) when (ex is COMException or NotImplementedException or ArgumentException) { continue; }
            if (count <= 0) continue;

            var children = new object?[Math.Min(count, 200)];
            if (AccessibleChildren(node, 0, children.Length, children, out var obtained) != 0 && obtained == 0) continue;
            for (int i = 0; i < obtained; i++)
            {
                // A child is either an object of its own, or a number that names a simple part of its parent.
                var (owner, id) = children[i] is IAccessible child ? (child, (object)0) : (node, children[i] ?? 0);
                int role = Role(owner, id);
                if (role is ROLE_SYSTEM_TEXT or ROLE_SYSTEM_COMBOBOX && Value(owner, id) is { Length: > 0 } value && LooksLikeAddress(value))
                    return value;
                if (children[i] is IAccessible deeper && role != ROLE_SYSTEM_DOCUMENT && depth < MaxDepth)
                    queue.Enqueue((deeper, depth + 1));
            }
        }
        return "";
    }

    static int Role(IAccessible node, object child)
    {
        try { return node.get_accRole(child) is int role ? role : 0; }
        catch (Exception ex) when (ex is COMException or NotImplementedException or ArgumentException) { return 0; }
    }

    static string Value(IAccessible node, object child)
    {
        try { return node.get_accValue(child) ?? ""; }
        catch (Exception ex) when (ex is COMException or NotImplementedException or ArgumentException) { return ""; }
    }

    /// <summary>A website address, or one of the browser's own pages (about:blank, edge://newtab): the address bar either way.</summary>
    static bool LooksLikeAddress(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Length > 2048 || text.Any(char.IsWhiteSpace)) return false;
        return Shared.BrowserSites.DomainOf(text).Length > 0 || text.Contains("://", StringComparison.Ordinal) ||
               text.StartsWith("about:", StringComparison.OrdinalIgnoreCase);
    }
}
