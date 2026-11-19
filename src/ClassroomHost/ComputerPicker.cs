// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// The drop-down at the left of a single-computer window's first row that changes which computer the window
/// shows. Choosing another computer calls <c>switched</c>; the window then loads that computer, puts its name in
/// the title and says so in its status line. A window with work under way gives a reason through
/// <c>blocker</c>, and the choice springs back with that reason instead of changing under the work.
/// </summary>
public sealed class ComputerPicker : ComboBox
{
    /// <summary>The computers the Host lists; set by the main window. Empty (so only the window's own computer) until then.</summary>
    public static Func<IReadOnlyList<ClientInfo>> Computers { get; set; } = () => [];

    /// <summary>How a refusal is told to the teacher; a test replaces it.</summary>
    public static Action<Window?, string> Tell { get; set; } = (owner, reason) =>
        MessageBox.Show(owner, reason, "Computer", MessageBoxButton.OK, MessageBoxImage.Information);

    sealed record Choice(ClientInfo Client)
    {
        public override string ToString() => Label(Client);
    }

    readonly Func<string?>? _blocker;
    readonly Action<ClientInfo> _switched;
    ClientInfo _current;
    bool _reverting;

    public ComputerPicker(ClientInfo current, Action<ClientInfo> switched, Func<string?>? blocker = null)
    {
        _current = current;
        _switched = switched;
        _blocker = blocker;
        MinWidth = 170;
        MaxWidth = 280;
        VerticalAlignment = VerticalAlignment.Center;
        ToolTip = "Computer — change which computer this window shows. The window loads the one you choose.";
        ItemsSource = Choices(current);
        SelectedIndex = ItemsSource.Cast<Choice>().ToList().FindIndex(choice => choice.Client == current);
        SelectionChanged += (_, _) => OnChosen();
    }

    /// <summary>The computers to offer: the Host's list by name, with the window's own computer always among them.</summary>
    public static List<ClientInfo> Offered(ClientInfo current, IEnumerable<ClientInfo> all) =>
        all.Concat([current]).Distinct().OrderBy(client => client.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    static List<Choice> Choices(ClientInfo current) => Offered(current, Computers()).Select(client => new Choice(client)).ToList();

    /// <summary>"DELL-WIN11", or "DELL-WIN11 (offline)" while the Host knows the computer does not answer.</summary>
    public static string Label(ClientInfo client) =>
        client.Level.Length == 0 && client.Status.Contains("ffline", StringComparison.OrdinalIgnoreCase)
            ? Loc.F($"{client.Name} (offline)")
            : client.Name;

    /// <summary>The line a window shows after it has switched, so it is plain which computer is now shown.</summary>
    public static string NowShowing(ClientInfo client) => Loc.F($"Now showing {client.Name}");

    void OnChosen()
    {
        if (_reverting || SelectedItem is not Choice chosen || chosen.Client == _current) return;
        if (_blocker?.Invoke() is { Length: > 0 } reason)
        {
            _reverting = true;
            SelectedIndex = ItemsSource.Cast<Choice>().ToList().FindIndex(choice => choice.Client == _current);
            _reverting = false;
            Tell(Window.GetWindow(this), reason);
            return;
        }
        _current = chosen.Client;
        _switched(chosen.Client);
    }

    /// <summary>The label and the picker for the first row of a window.</summary>
    public static FrameworkElement[] ForRow(ComputerPicker picker)
    {
        picker.Margin = new Thickness(0, 0, 6, 0);
        return [Ui.Label("_Computer:", picker, new Thickness(0, 0, 6, 0)), picker];
    }
}
