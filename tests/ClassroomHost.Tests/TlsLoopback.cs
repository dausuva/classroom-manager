// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Xunit;

namespace ClassroomHost.Tests;

// These tests assert the production six-second handshake outcome, not behavior under process/RSA stress.
// Keep their strict certificate/key assertions while excluding unrelated heavy fixtures in this assembly.
[CollectionDefinition(nameof(TlsLoopback), DisableParallelization = true)]
public sealed class TlsLoopback;
