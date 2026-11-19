// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Shows a small always-on-top message box on the student's screen (non-blocking). Runs in the user helper.</summary>
sealed class MessageHandler
{
    readonly List<Form> _openForms = new();

    public async Task<Response> ShowAsync(JsonObject a)
    {
        var title = a.Str("title", Loc.T("Message from your teacher"));
        var text = a.Str("text");
        int seconds = Math.Clamp(a.Int("seconds") ?? 0, 0, 3600);
        if (text.Length > 2000) text = text[..2000];
        if (title.Length > 100) title = title[..100];

        await Ui.RunAsync(() =>
        {
            var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
            var form = new Form
            {
                Text = title,
                TopMost = true,
                ShowInTaskbar = true,
                StartPosition = FormStartPosition.Manual,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ClientSize = new Size(520, 240),
                Font = new Font("Segoe UI", 12),
            };
            form.Location = new Point(
                screen.WorkingArea.Left + (screen.WorkingArea.Width - form.Width) / 2,
                screen.WorkingArea.Top + (screen.WorkingArea.Height - form.Height) / 3);

            var label = new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                Font = new Font("Segoe UI", 14),
            };
            var ok = new Button { Text = "OK", Dock = DockStyle.Bottom, Height = 42 };
            ok.Click += (_, _) => form.Close();
            form.Controls.Add(label);
            Label? countdown = null;
            if (seconds > 0)
            {
                countdown = new Label
                {
                    Dock = DockStyle.Bottom,
                    Height = 32,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 10, FontStyle.Regular),
                    ForeColor = SystemColors.GrayText,
                };
                form.Controls.Add(countdown);
            }
            form.Controls.Add(ok);
            form.AcceptButton = ok;

            System.Windows.Forms.Timer? timer = null;
            form.FormClosed += (_, _) =>
            {
                timer?.Stop();
                timer?.Dispose();
                _openForms.Remove(form);
            };
            _openForms.Add(form);

            if (seconds > 0)
            {
                var closesAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
                void UpdateCountdown()
                {
                    if (countdown is null || form.IsDisposed) return;
                    var remaining = Math.Max(0, (int)Math.Ceiling((closesAt - DateTimeOffset.UtcNow).TotalSeconds));
                    countdown.Text = remaining == 1
                        ? Loc.T("Closes automatically in 1 second")
                        : Loc.F($"Closes automatically in {remaining} seconds");
                    if (remaining == 0) form.Close();
                }
                UpdateCountdown();
                timer = new System.Windows.Forms.Timer { Interval = 1000 };
                timer.Tick += (_, _) => UpdateCountdown();
                timer.Start();
            }
            form.Show();
            form.Activate();
        });
        return Response.Success();
    }

    public async Task<Response> ClearAsync()
    {
        var closed = 0;
        await Ui.RunAsync(() =>
        {
            var forms = _openForms.ToArray();
            closed = forms.Length;
            foreach (var form in forms)
                if (!form.IsDisposed) form.Close();
        });
        return Response.Success(new JsonObject { ["closed"] = closed });
    }
}
