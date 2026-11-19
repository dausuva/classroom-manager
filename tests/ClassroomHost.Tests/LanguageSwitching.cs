// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// The program's language is one global setting. A test that changes it must not run beside any other
/// test: one that expects English would find Lithuanian, and the other way round. Tests in this collection
/// run alone, one after another, after the parallel ones have finished.
/// </summary>
[CollectionDefinition(nameof(LanguageSwitching), DisableParallelization = true)]
public sealed class LanguageSwitching;
