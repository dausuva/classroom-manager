// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>A normal text editor with an explicit UI Automation relationship to its suggestions.</summary>
public sealed class CommandSearchTextBox : TextBox
{
    public ListBox? Suggestions { get; set; }
    public string SuggestionStatus { get; private set; } = "";
    string _announcementKey = "";

    public void UpdateSuggestionStatus(string status, bool announce)
    {
        SuggestionStatus = status;
        var hint = Loc.T(UiTranslator.Source(ToolTip as string ?? ""));
        AutomationProperties.SetHelpText(this, status.Length == 0 ? hint : hint + "\n" + status);
        var key = Text + "\n" + status;
        if (!announce || status.Length == 0) { _announcementKey = ""; return; }
        if (key == _announcementKey) return;
        _announcementKey = key;
        if (AutomationPeer.ListenerExists(AutomationEvents.Notification))
            UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent(
                AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent,
                status, nameof(CommandSearchTextBox));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SearchPeer(this);

    sealed class SearchPeer(CommandSearchTextBox owner) : TextBoxAutomationPeer(owner)
    {
        protected override List<AutomationPeer> GetControlledPeersCore() =>
            owner.Suggestions is { } list && UIElementAutomationPeer.CreatePeerForElement(list) is { } peer ? [peer] : [];
    }
}
