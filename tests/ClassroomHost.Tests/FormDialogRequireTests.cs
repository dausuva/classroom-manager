// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-166: a dialog that needs an answer stays open until it has one.</summary>
public sealed class FormDialogRequireTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(" \t  ")]
    public void A_blank_required_field_is_a_problem(string entered) => OnSta(() =>
    {
        var dialog = new FormDialog("Send message", null)
            .Text("text", "Message", entered)
            .Require("text", "Enter the message to send.");

        Assert.Equal("Enter the message to send.", dialog.FirstProblem());
    });

    [Fact]
    public void A_filled_in_field_is_no_problem() => OnSta(() =>
    {
        var dialog = new FormDialog("Send message", null)
            .Text("text", "Message", "  Please look at the teacher.  ")
            .Require("text", "Enter the message to send.");

        Assert.Null(dialog.FirstProblem());
    });

    [Fact]
    public void The_first_blank_field_in_order_is_the_one_reported() => OnSta(() =>
    {
        var dialog = new FormDialog("Assign", null)
            .Text("room", "Room", "A1")
            .Text("title", "Title", "")
            .Text("text", "Message", "")
            .Require("room", "Enter a room name.")
            .Require("title", "Enter a title.")
            .Require("text", "Enter the message.");

        Assert.Equal("Enter a title.", dialog.FirstProblem());
    });

    [Fact]
    public void A_dialog_with_nothing_required_never_complains() => OnSta(() =>
    {
        var dialog = new FormDialog("Send message", null).Text("secs", "Seconds", "");

        Assert.Null(dialog.FirstProblem());
    });

    static void OnSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
