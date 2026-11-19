// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

public enum HostEmptyStateReason
{
    None,
    NoSavedComputers,
    NoNetworkMatch,
    NoRoomMatch,
}

/// <summary>
/// A synchronous projection of the visible roster and effective action targets.
/// Membership is copied; ClientInfo objects remain the existing live presentation objects.
/// The caller captures collection-view input on its owning thread. No UI or settings are accessed here.
/// </summary>
public sealed class HostSelectionSnapshot
{
    public IReadOnlyList<ClientInfo> Targets { get; }
    public int VisibleCount { get; }
    public bool IsEmpty => VisibleCount == 0;
    public HostEmptyStateReason EmptyReason { get; }

    HostSelectionSnapshot(List<ClientInfo> targets, int visibleCount, HostEmptyStateReason emptyReason)
    {
        Targets = targets.AsReadOnly();
        VisibleCount = visibleCount;
        EmptyReason = emptyReason;
    }

    public static HostSelectionSnapshot Capture(IEnumerable<ClientInfo> visible, int savedCount, bool hasNetworkMatch,
        IReadOnlyList<ClientInfo>? targetOverride = null)
    {
        var shown = visible.ToList();
        var reason = shown.Count > 0 ? HostEmptyStateReason.None
            : savedCount == 0 ? HostEmptyStateReason.NoSavedComputers
            : !hasNetworkMatch ? HostEmptyStateReason.NoNetworkMatch
            : HostEmptyStateReason.NoRoomMatch;
        return new(SelectTargets(shown, targetOverride), shown.Count, reason);
    }

    /// <summary>
    /// An explicit override (even empty) wins. Return a fresh list, never a deferred query;
    /// single-computer actions capture their temporary override before their first await.
    /// </summary>
    public static List<ClientInfo> SelectTargets(IEnumerable<ClientInfo> visible, IReadOnlyList<ClientInfo>? targetOverride = null) =>
        targetOverride is not null ? [.. targetOverride] : visible.Where(client => client.IsSelected).ToList();
}
