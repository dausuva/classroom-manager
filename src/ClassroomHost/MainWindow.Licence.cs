// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public partial class MainWindow
{
    /// <summary>
    /// The free tier (T-220): an official Host without a licence sends an action to at most three computers online at once; the others are told
    /// they wait for a licence. A source build, or a Host with a licence for enough computers, gets its whole selection back.
    /// </summary>
    List<ClientInfo> LimitToLicence(List<ClientInfo> selection)
    {
        var allowed = LicenceCapacity.Allowed(BuildEdition.HasFreeLimit, LicenceKeys.Trusted.Length > 0, HostLicence.Read(DateOnly.FromDateTime(DateTime.Now)));
        if (allowed is not { } limit) return selection;
        var plan = LicenceCapacity.Plan(_clients.ToList(), selection, limit);
        if (plan.Waiting.Count == 0) return selection;
        MessageBox.Show(this, LicenceCapacity.Explain(limit, plan), "Licence limit", MessageBoxButton.OK, MessageBoxImage.Information);
        return plan.Acting.ToList();
    }
}
