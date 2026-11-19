// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-174: the drop-down that changes which computer a single-computer window shows.</summary>
[Collection("LanguageSwitching")]
public sealed class ComputerPickerTests
{
    static ClientInfo Pc(string name, string status = "Online", string level = "ok") => new() { ComputerName = name, Status = status, Level = level };

    [Fact]
    public void The_offered_computers_are_the_hosts_list_by_name_with_the_windows_own_among_them()
    {
        var own = Pc("Zed");
        var list = new[] { Pc("beta"), Pc("Alpha") };

        var offered = ComputerPicker.Offered(own, list);

        Assert.Equal(["Alpha", "beta", "Zed"], offered.Select(c => c.Name));
        Assert.Single(ComputerPicker.Offered(list[0], list), c => c == list[0]);   // listed once
    }

    [Fact]
    public void An_offline_computer_says_so_and_an_online_one_shows_only_its_name()
    {
        Assert.Equal("A", ComputerPicker.Label(Pc("A")));
        Assert.Equal("B (offline)", ComputerPicker.Label(Pc("B", "Offline", "")));
    }

    [Fact]
    public void The_notice_names_the_computer()
    {
        var pc = Pc("DELL-WIN11");

        Assert.Equal("Now showing DELL-WIN11", ComputerPicker.NowShowing(pc));
    }

    [Fact]
    public void Choosing_another_computer_tells_the_window_once() => OnSta(() =>
    {
        var a = Pc("A"); var b = Pc("B");
        var old = ComputerPicker.Computers;
        ComputerPicker.Computers = () => [a, b];
        try
        {
            var told = new List<ClientInfo>();
            var picker = new ComputerPicker(a, told.Add);

            picker.SelectedIndex = 1;
            picker.SelectedIndex = 1;   // the same again is nothing new

            Assert.Equal([b], told);
        }
        finally { ComputerPicker.Computers = old; }
    });

    [Fact]
    public void Work_under_way_keeps_the_computer_and_says_why() => OnSta(() =>
    {
        var a = Pc("A"); var b = Pc("B");
        var old = ComputerPicker.Computers;
        var oldTell = ComputerPicker.Tell;
        ComputerPicker.Computers = () => [a, b];
        var reasons = new List<string>();
        ComputerPicker.Tell = (_, reason) => reasons.Add(reason);
        try
        {
            var told = new List<ClientInfo>();
            var busy = true;
            var picker = new ComputerPicker(a, told.Add, () => busy ? "Wait for the installation to finish." : null);

            picker.SelectedIndex = 1;

            Assert.Empty(told);
            Assert.Equal(0, picker.SelectedIndex);   // sprang back to A
            Assert.Equal(["Wait for the installation to finish."], reasons);

            busy = false;
            picker.SelectedIndex = 1;
            Assert.Equal([b], told);
        }
        finally { ComputerPicker.Computers = old; ComputerPicker.Tell = oldTell; }
    });

    static void OnSta(Action test)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() => { try { test(); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }
}
