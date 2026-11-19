// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class AboutWindow : Window
{
    /// <param name="computersListed">How many computers the Host lists, to compare with the licence.</param>
    public AboutWindow(Window owner, int computersListed = 0)
    {
        Owner = owner;
        Title = "About Classroom Manager";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var assembly = Assembly.GetEntryAssembly() ?? typeof(AboutWindow).Assembly;
        var fileVersion = Environment.ProcessPath is { } processPath
            ? FileVersionInfo.GetVersionInfo(processPath).FileVersion
            : assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        fileVersion = ClassroomManager.Shared.ReleaseVersions.Display(fileVersion ?? assembly.GetName().Version?.ToString() ?? "Unknown");
        var product = new TextBlock
        {
            Text = "Dausuva Classroom Manager",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
        };
        var description = new TextBlock
        {
            Text = "Teacher console for securely managing and monitoring classroom Windows computers.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 16),
        };
        var details = new TextBlock
        {
            Text = $"Host version: {fileVersion}\nProtocol version: {Protocol.Version}\n" +
                   $"Runtime: {RuntimeInformation.FrameworkDescription}\nOS: {RuntimeInformation.OSDescription}\n\n" +
                   $"Copyright © {DateTime.Now.Year} Dausuva\nLicensed under the Apache License 2.0.",
            TextWrapping = TextWrapping.Wrap,
        };
        // The school's licence (T-213): shown here; the official Host's free tier of 3 computers is applied when an action is sent (LicenceCapacity).
        var licenceHeadline = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 2), TextWrapping = TextWrapping.Wrap };
        var licenceDetail = Ui.MutedText("");
        var licenceNote = Ui.MutedText("");
        // The id a licence is tied to (T-262): the same for every Windows user of this PC. It is given to whoever issues the licence.
        var computerId = HostComputerId.Current;
        var computerIdText = Ui.MutedText(computerId.Length > 0 ? $"Computer ID: {computerId}" : "Computer ID: not available on this computer");
        computerIdText.ToolTip = "A licence is issued for one computer. Give this ID when you ask for a licence; it is the same for every Windows user of this PC.";
        void ShowLicence()
        {
            var (headline, detail, note) = HostLicence.Describe(HostLicence.Read(DateOnly.FromDateTime(DateTime.Now)), computersListed, LicenceKeys.Trusted.Length > 0, BuildEdition.HasFreeLimit && LicenceKeys.Trusted.Length > 0);
            licenceHeadline.Text = headline;
            licenceDetail.Text = detail;
            licenceNote.Text = note;
            licenceNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        ShowLicence();
        var addLicence = new Button { Content = "Add licence _file...", MinWidth = 130, Margin = new Thickness(0, 4, 8, 4) };
        addLicence.Click += (_, _) =>
        {
            // Several files may be chosen (a school's kit holds one per Host); the one issued for this computer is kept.
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Classroom Manager licence|*" + Licences.FileExtension, Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var (installed, _, problem) = HostLicence.InstallFirstMatching(dialog.FileNames, DateOnly.FromDateTime(DateTime.Now));
                if (installed is null) MessageBox.Show(this, "The licence file was not added: " + problem, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, "The licence file was not added: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            ShowLicence();
        };
        var copyId = new Button { Content = "Copy computer _ID", MinWidth = 130, Margin = new Thickness(0, 4, 8, 4), IsEnabled = computerId.Length > 0 };
        copyId.Click += (_, _) =>
        {
            try { Clipboard.SetText(computerId); }
            catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "The clipboard is in use. Try again.", Title); }
        };
        var licenses = new Button { Content = "Open-source _licenses...", MinWidth = 150, Margin = new Thickness(0, 4, 0, 4) };
        licenses.Click += (_, _) => new OpenSourceLicensesWindow(this).ShowDialog();
        var close = new Button
        {
            Content = "_Close",
            IsDefault = true,
            IsCancel = true,
            MinWidth = 88,
            Margin = new Thickness(8, 4, 0, 4),
        };
        // Four buttons do not fit one row at this width (longer in Lithuanian), so they wrap onto a second row instead of being cut off.
        var buttons = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(addLicence);
        buttons.Children.Add(copyId);
        buttons.Children.Add(licenses);
        buttons.Children.Add(close);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(product);
        root.Children.Add(description);
        root.Children.Add(details);
        root.Children.Add(licenceHeadline);
        root.Children.Add(licenceDetail);
        root.Children.Add(licenceNote);
        root.Children.Add(computerIdText);
        root.Children.Add(buttons);
        Content = root;
    }
}
