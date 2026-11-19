// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Reflection;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>What the student-side helper knows about itself, for the status window. Set by <see cref="UserAgent"/>.</summary>
static class HelperState
{
    static long _connectedTicks;   // 0: not connected

    public static DateTime? ServiceConnectedSince =>
        Interlocked.Read(ref _connectedTicks) is var ticks and > 0 ? new DateTime(ticks, DateTimeKind.Local) : null;

    public static void ServiceConnected() => Interlocked.Exchange(ref _connectedTicks, DateTime.Now.Ticks);

    public static void ServiceLost() => Interlocked.Exchange(ref _connectedTicks, 0);
}

/// <summary>
/// The window the notification-area icon opens: a read-only statement, for the student, that this computer is managed and what that means. It has no setting
/// and no button that could weaken the Client; it only says what is true (T-238, docs/windows/STORE-AND-WINGET.md section 7).
/// </summary>
sealed class StatusWindow : Form
{
    public const string InformationAddress = "https://dausuva.com/school/";

    readonly Label _service = new() { AutoSize = true, MaximumSize = new Size(460, 0) };
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };

    public StatusWindow()
    {
        Text = Loc.T("Classroom Manager status");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        panel.Controls.Add(new Label { Text = $"Classroom Manager Client {VersionText()}", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) });
        panel.Controls.Add(new Label
        {
            Text = Loc.T("This computer is managed with Classroom Manager. Your teacher can send you messages and lock the screen, and your school can run actions on this computer."),
            AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 8),
        });
        panel.Controls.Add(new Label
        {
            Text = Loc.T("This program does not see or record what is on your screen."),
            AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 8),
        });
        panel.Controls.Add(_service);
        panel.Controls.Add(new Label { Text = Loc.F($"Signed in as {Environment.UserName} on {Environment.MachineName}"), AutoSize = true, Margin = new Padding(0, 8, 0, 8) });
        var link = new LinkLabel { Text = Loc.T("About Classroom Manager and privacy"), AutoSize = true };
        link.LinkClicked += (_, _) => OpenInformation();
        panel.Controls.Add(link);
        var close = new Button { Text = Loc.T("Close"), AutoSize = true, DialogResult = DialogResult.OK, Margin = new Padding(0, 12, 0, 0) };
        panel.Controls.Add(close);
        AcceptButton = close;
        CancelButton = close;
        Controls.Add(panel);

        _refresh.Tick += (_, _) => UpdateService();
        UpdateService();
        _refresh.Start();
        FormClosed += (_, _) => _refresh.Dispose();
    }

    void UpdateService() =>
        _service.Text = HelperState.ServiceConnectedSince is { } since
            ? Loc.F($"The Classroom Manager service on this computer is running; this helper has been connected to it since {since:HH:mm}.")
            : Loc.T("The Classroom Manager service on this computer is not reachable from this helper yet.");

    static string VersionText() => typeof(StatusWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    static void OpenInformation()
    {
        try { Process.Start(new ProcessStartInfo(InformationAddress) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Status window: {ex.Message}"); }
    }
}
