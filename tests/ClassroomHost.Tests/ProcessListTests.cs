// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ProcessListTests
{
    [Theory]
    [InlineData("No process with that id (it may have already exited).", true)]
    [InlineData("Process id 4120 now belongs to 'notepad', not 'chrome'. Refresh before ending it.", true)]
    [InlineData("Process id 4120 has been reused by another 'chrome' process. Refresh before ending it.", true)]
    [InlineData("Could not end the process: Access is denied.", false)]
    [InlineData("Could not verify the process start time: Access is denied.", false)]
    public void A_process_that_is_already_gone_counts_as_ended(string answer, bool gone) =>
        Assert.Equal(gone, ProcessKillResults.AlreadyGone(answer));

    sealed record Row(string Name, int Pid);

    [Fact]
    public void A_refresh_keeps_the_sort_and_ended_rows_leave_without_a_resort()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var list = new ListView();
                Ui.ReplaceItemsPreservingSelection(list, new ObservableCollection<Row>([new("b", 2), new("c", 3), new("a", 1)]), row => row.Name);
                CollectionViewSource.GetDefaultView(list.ItemsSource).SortDescriptions.Add(new SortDescription(nameof(Row.Pid), ListSortDirection.Descending));

                // A refresh brings new rows; the teacher's sort stays.
                var fresh = new ObservableCollection<Row>([new("a", 1), new("d", 4), new("b", 2), new("c", 3)]);
                Ui.ReplaceItemsPreservingSelection(list, fresh, row => row.Name);
                Assert.Equal([4, 3, 2, 1], list.Items.Cast<Row>().Select(row => row.Pid));

                // Ending a process takes its row out and leaves the others in the same order.
                Ui.FadeOutAndRemoveAsync(list, [fresh[2]]).GetAwaiter().GetResult();
                Assert.Equal([4, 3, 1], list.Items.Cast<Row>().Select(row => row.Pid));
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
