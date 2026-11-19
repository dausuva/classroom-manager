// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class BulkOperationReviewTests
{
    [Fact]
    public void Summary_keeps_target_configuration_guidance_and_warning_together()
    {
        var review = new BulkOperationReview(
            "Review installation",
            "Target: 2 selected computers\nRoom: Lab A",
            [new("Installer", "install\\setup.exe"), new("Parameters", "(none)")],
            "Progress appears in Operations.",
            "The installer may require interaction.",
            "Install anyway");

        var summary = review.FormatSummary();

        Assert.Contains("Target: 2 selected computers", summary);
        Assert.Contains("Installer: install\\setup.exe", summary);
        Assert.Contains("Note: Progress appears in Operations.", summary);
        Assert.Contains("Warning: The installer may require interaction.", summary);
    }
}
