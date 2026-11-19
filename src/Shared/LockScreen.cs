// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>
/// Names used by the lock and sign-in screen picture commands. signin.image.default answers with
/// <see cref="Home"/> true on Windows Home, which ignores the machine-wide picture: there the picture
/// belongs to each user, and lockscreen.reset puts the signed-in user's back to Windows spotlight.
/// </summary>
public static class LockScreen
{
    public const string Home = "home";
    public const string Restore = "restore";
    public const string Spotlight = "spotlight";
    public const string Restored = "restored";
}
