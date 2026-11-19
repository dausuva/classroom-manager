// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ClassroomManager.Host;

/// <summary>Temporarily moves a suffix of top-bar commands, without changing their identities or saved layout.</summary>
public sealed class ToolbarOverflow(Panel top, Panel overflow, Func<ButtonBase, string> label)
{
    List<UIElement> _order = top.Children.Cast<UIElement>().ToList();
    public bool FocusMovedOut { get; private set; }

    public void Capture()
    {
        if (overflow.Children.Count != 0) throw new InvalidOperationException(nameof(Restore));
        _order = top.Children.Cast<UIElement>().ToList();
    }

    public bool Restore()
    {
        FocusMovedOut = false;
        return Place(_order, []);
    }

    public bool Reflow(double width)
    {
        FocusMovedOut = false;
        if (!double.IsFinite(width) || width <= 0) return false;
        foreach (var element in _order.Where(element => element.Visibility != Visibility.Collapsed))
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Keep field/label pairs on the bar; commands use the remaining space in the user's priority order.
        var remaining = Math.Max(0, width - _order.Where(element => element is not ButtonBase && element.Visibility != Visibility.Collapsed)
            .Sum(element => element.DesiredSize.Width));
        var moved = new HashSet<UIElement>();
        bool fits = true;
        foreach (var button in _order.OfType<ButtonBase>().Where(button => button.Visibility != Visibility.Collapsed))
        {
            if (fits && button.DesiredSize.Width <= remaining + 0.5) remaining -= button.DesiredSize.Width;
            else { fits = false; moved.Add(button); }
        }
        FocusMovedOut = moved.Any(element => top.Children.Contains(element) && element.IsKeyboardFocusWithin);
        return Place(_order.Where(element => !moved.Contains(element)).ToList(),
            moved.Cast<ButtonBase>().OrderBy(label, StringComparer.CurrentCultureIgnoreCase).Cast<UIElement>().ToList());
    }

    bool Place(IReadOnlyList<UIElement> wantedTop, IReadOnlyList<UIElement> wantedOverflow)
    {
        if (top.Children.Cast<UIElement>().SequenceEqual(wantedTop) && overflow.Children.Cast<UIElement>().SequenceEqual(wantedOverflow)) return false;
        // Only detach moved elements. A room picker or a focused control that stays on the bar keeps its state/focus.
        foreach (var element in top.Children.Cast<UIElement>().Where(element => !wantedTop.Contains(element)).ToArray()) top.Children.Remove(element);
        foreach (var element in overflow.Children.Cast<UIElement>().Where(element => !wantedOverflow.Contains(element)).ToArray()) overflow.Children.Remove(element);
        static void Insert(Panel panel, IReadOnlyList<UIElement> order)
        {
            for (int index = 0; index < order.Count; index++)
            {
                if (index < panel.Children.Count && ReferenceEquals(panel.Children[index], order[index])) continue;
                panel.Children.Remove(order[index]);
                panel.Children.Insert(index, order[index]);
            }
        }
        Insert(top, wantedTop);
        Insert(overflow, wantedOverflow);
        return true;
    }
}
