// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Reflection;
using Xunit;

namespace ClassroomHost.Tests;

static class FrameworkReferenceAssertions
{
    public static void AssertOnly(Assembly assembly) =>
        Assert.All(assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name is "netstandard" or "ClassroomManager.Modules.Contract" || reference.Name?.StartsWith("System.", StringComparison.Ordinal) == true,
                $"Unexpected non-framework reference {reference.FullName}"));
}
