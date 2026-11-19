// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class AccountInventoryComparisonTests
{
    [Fact]
    public void Build_reports_account_coverage_and_state_differences()
    {
        var first = new ClientInfo { Address = "10.0.0.1", Name = "PC-1" };
        var second = new ClientInfo { Address = "10.0.0.2", Name = "PC-2" };
        var third = new ClientInfo { Address = "10.0.0.3", Name = "PC-3" };
        var inventories = new Dictionary<ClientInfo, IReadOnlyList<AccountEntry>>
        {
            [first] = [Account("Student", enabled: true, admin: false), Account("Teacher", enabled: true, admin: true)],
            [second] = [Account("student", enabled: false, admin: true, locked: true), Account("Teacher", enabled: true, admin: true)],
            [third] = [Account("Teacher", enabled: true, admin: true)],
        };

        var compared = AccountInventoryComparison.Build([first, second, third], inventories);

        var student = Assert.Single(compared, item => item.Name.Equals("Student", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("2 / 3 checked", student.Present);
        Assert.Equal("1 / 2", student.Enabled);
        Assert.Equal("1 / 2", student.Administrators);
        Assert.Equal("1 / 2", student.Locked);
        Assert.Equal("PC-3", student.MissingComputers);
        Assert.True(student.HasDifferenceOrAttention);
        Assert.Contains("Missing on 1 checked PC", student.Comparison);
        Assert.Contains("Enabled state differs", student.Comparison);
        Assert.Contains("Administrator membership differs", student.Comparison);
        Assert.Contains("Locked on 1 PC", student.Comparison);

        var teacher = Assert.Single(compared, item => item.Name == "Teacher");
        Assert.False(teacher.HasDifferenceOrAttention);
        Assert.Equal("Same on all checked PCs", teacher.Comparison);
    }

    [Fact]
    public void Build_marks_failed_inventory_as_unavailable_not_missing()
    {
        var online = new ClientInfo { Address = "10.0.0.1", Name = "Online" };
        var offline = new ClientInfo { Address = "10.0.0.2", Name = "Offline" };
        var inventories = new Dictionary<ClientInfo, IReadOnlyList<AccountEntry>>
        {
            [online] = [Account("Student", enabled: true, admin: false)],
        };

        var student = Assert.Single(AccountInventoryComparison.Build([online, offline], inventories));

        Assert.Equal("1 / 1 checked", student.Present);
        Assert.Equal("—", student.MissingComputers);
        Assert.False(student.HasDifferenceOrAttention);
        Assert.Contains("1 unavailable", student.Comparison);
    }

    static AccountEntry Account(string name, bool enabled, bool admin, bool locked = false) => new()
    {
        Name = name,
        DisplayName = name,
        Enabled = enabled,
        Admin = admin,
        Locked = locked,
    };
}
