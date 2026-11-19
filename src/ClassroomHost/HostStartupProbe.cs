// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Reflection;

namespace ClassroomManager.Host;

/// <summary>Machine setup composition check with synthetic settings; never loads a teacher profile.</summary>
internal static class HostStartupProbe
{
    internal static int Run(string expectedVersion)
    {
        var actual = typeof(App).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!Version.TryParse(expectedVersion, out var expected) || expected.Revision < 0 ||
            !Version.TryParse(actual, out var built) || expected != built) return 2;
        var suspended = HostSettings.SavesSuspended;
        var quiet = UiHooks.QuietStart;
        var messageBox = UiHooks.MessageBox;
        try
        {
            HostSettings.SavesSuspended = true;
            UiHooks.QuietStart = true;
            UiHooks.MessageBox = (_, _, _) => throw new InvalidOperationException();
            TableSorting.Initialize();
            var window = new MainWindow(new HostSettings
            {
                RunSchedules = false, RunSchedulesWhenClosed = false, AutoRefreshEnabled = false,
                CheckForUpdates = false, ActivityLogEnabled = false
            });
            window.Close();
            return 0;
        }
        catch { return 1; } // Exit status only; never dump settings or raw composition errors.
        finally
        {
            HostSettings.SavesSuspended = suspended;
            UiHooks.QuietStart = quiet;
            UiHooks.MessageBox = messageBox;
        }
    }
}
