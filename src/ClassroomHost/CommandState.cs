// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Command metadata shared by toolbar buttons, quick actions, shortcuts and search.</summary>
public static class CommandState
{
    public static readonly DependencyProperty IdProperty = DependencyProperty.RegisterAttached(
        nameof(GetId)[3..], typeof(string), typeof(CommandState), new PropertyMetadata(""));
    public static readonly DependencyProperty DisabledReasonProperty = DependencyProperty.RegisterAttached(
        nameof(GetDisabledReason)[3..], typeof(string), typeof(CommandState), new PropertyMetadata(""));
    static readonly DependencyProperty DescriptionProperty = DependencyProperty.RegisterAttached(
        nameof(DescriptionProperty)[..^8], typeof(string), typeof(CommandState), new PropertyMetadata(null));

    public static string GetId(DependencyObject element) => (string)element.GetValue(IdProperty);
    public static void SetId(DependencyObject element, string value) => element.SetValue(IdProperty, value);
    public static string GetDisabledReason(DependencyObject element) => (string)element.GetValue(DisabledReasonProperty);

    public static void Update(ButtonBase button, int selectedCount)
    {
        var id = GetId(button);
        if (id.Length == 0) return;
        if (button.GetValue(DescriptionProperty) is not string description)
        {
            description = button.ToolTip is string text ? UiTranslator.Source(text) : "";
            button.SetValue(DescriptionProperty, description);
        }
        if (MainActionAvailability.Requirement(id) != ActionTargetRequirement.None)
            button.SetCurrentValue(UIElement.IsEnabledProperty, MainActionAvailability.CanExecute(id, selectedCount));
        var reason = button.IsEnabled ? "" : MainActionAvailability.DisabledReason(id, selectedCount);
        if (!button.IsEnabled && reason.Length == 0) reason = "This command is temporarily unavailable.";
        button.SetValue(DisabledReasonProperty, reason);
        var help = Loc.T(description);
        if (reason.Length > 0) help = help.Length == 0 ? Loc.T(reason) : help + "\n" + Loc.T(reason);
        if (help.Length > 0) button.ToolTip = help;
        AutomationProperties.SetHelpText(button, help);
        ToolTipService.SetShowOnDisabled(button, true);
    }
}
