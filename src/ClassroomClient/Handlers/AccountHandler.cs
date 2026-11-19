// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.DirectoryServices.AccountManagement;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Local (SAM) user accounts on this machine: list, create, reset a password, enable/disable,
/// delete. System-level only - local account management needs admin rights and has nothing to do
/// with whichever student is signed in, so it would never make sense to run it as their token.
/// </summary>
sealed class AccountHandler
{
    static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Administrator", "Guest", "DefaultAccount", "WDAGUtilityAccount",
    };

    public Response List()
    {
        try
        {
            var arr = new JsonArray();
            using var ctx = new PrincipalContext(ContextType.Machine);
            using var admins = GroupPrincipal.FindByIdentity(ctx, "Administrators");
            var adminNames = admins?.GetMembers(false)
                .Select(m => m.SamAccountName)
                .Where(n => n is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var template = new UserPrincipal(ctx);
            using var searcher = new PrincipalSearcher(template);
            foreach (var found in searcher.FindAll())
            {
                using var user = found as UserPrincipal;
                if (user?.SamAccountName is null) continue;
                arr.Add(new JsonObject
                {
                    ["name"] = user.SamAccountName,
                    ["displayName"] = user.DisplayName ?? "",
                    ["enabled"] = user.Enabled ?? true,
                    ["locked"] = SafeIsLockedOut(user),
                    ["admin"] = adminNames.Contains(user.SamAccountName),
                    ["builtIn"] = IsBuiltIn(user),
                });
            }
            return Response.Success(new JsonObject { ["accounts"] = arr });
        }
        catch (Exception ex) { return Response.Fail($"Could not list local accounts: {ex.Message}"); }
    }

    static bool SafeIsLockedOut(UserPrincipal user)
    {
        try { return user.IsAccountLockedOut(); }
        catch { return false; }
    }

    static bool IsBuiltIn(UserPrincipal user)
    {
        var value = user.Sid?.Value;
        if (value is null) return ProtectedNames.Contains(user.SamAccountName ?? "");
        var dash = value.LastIndexOf('-');
        return dash >= 0 && int.TryParse(value[(dash + 1)..], out var rid) && rid is 500 or 501 or 503 or 504;
    }

    public Response Create(JsonObject a)
    {
        var name = a.Str("name").Trim();
        var password = a.Str("password");
        bool admin = a.Bool("admin") ?? false;
        bool mustChangePassword = a.Bool("mustChangePassword") ?? true;

        if (name.Length == 0) return Response.Fail("Account name cannot be empty.");

        try
        {
            using var ctx = new PrincipalContext(ContextType.Machine);
            if (UserPrincipal.FindByIdentity(ctx, name) is not null)
                return Response.Fail($"An account named '{name}' already exists.");

            using var user = new UserPrincipal(ctx) { Name = name, Enabled = true };
            user.SetPassword(password);
            if (mustChangePassword) user.ExpirePasswordNow();
            user.Save();

            if (admin)
            {
                using var admins = GroupPrincipal.FindByIdentity(ctx, "Administrators");
                if (admins is not null)
                {
                    admins.Members.Add(user);
                    admins.Save();
                }
            }

            Log.Write($"account.create: created '{name}' (admin={admin}).");
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail($"Could not create the account: {ex.Message}"); }
    }

    public Response SetPassword(JsonObject a)
    {
        var name = a.Str("name");
        var password = a.Str("password");
        bool mustChangePassword = a.Bool("mustChangePassword") ?? true;

        try
        {
            using var ctx = new PrincipalContext(ContextType.Machine);
            using var user = UserPrincipal.FindByIdentity(ctx, name);
            if (user is null) return Response.Fail($"No local account named '{name}'.");

            user.SetPassword(password);
            if (mustChangePassword) user.ExpirePasswordNow();
            user.UnlockAccount();
            user.Save();

            Log.Write($"account.setpassword: reset the password for '{name}'.");
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail($"Could not reset the password: {ex.Message}"); }
    }

    public Response SetEnabled(JsonObject a, bool enabled)
    {
        var name = a.Str("name");
        try
        {
            using var ctx = new PrincipalContext(ContextType.Machine);
            using var user = UserPrincipal.FindByIdentity(ctx, name);
            if (user is null) return Response.Fail($"No local account named '{name}'.");
            user.Enabled = enabled;
            user.Save();

            Log.Write($"account.{(enabled ? "enable" : "disable")}: '{name}'.");
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail($"Could not {(enabled ? "enable" : "disable")} the account: {ex.Message}"); }
    }

    public Response Delete(JsonObject a)
    {
        var name = a.Str("name");
        try
        {
            using var ctx = new PrincipalContext(ContextType.Machine);
            using var user = UserPrincipal.FindByIdentity(ctx, name);
            if (user is null) return Response.Fail($"No local account named '{name}'.");
            if (IsBuiltIn(user)) return Response.Fail($"The built-in '{name}' account can be disabled but cannot be deleted.");

            user.Delete();
            Log.Write($"account.delete: removed '{name}'.");
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail($"Could not delete the account: {ex.Message}"); }
    }

    public Response IdentityList()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent().Name;
            var account = identity.Contains('\\') ? identity[(identity.IndexOf('\\') + 1)..] : identity;
            var credentials = new JsonArray();
            var output = RunCmdKey("/list");
            foreach (var line in output.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("Target:", StringComparison.OrdinalIgnoreCase)) continue;
                var target = trimmed["Target:".Length..].Trim();
                if (target.Length > 0) credentials.Add(new JsonObject { ["target"] = target });
            }
            var microsoft = identity.StartsWith("MicrosoftAccount\\", StringComparison.OrdinalIgnoreCase) ||
                            account.Contains('@') &&
                            (account.Contains("outlook", StringComparison.OrdinalIgnoreCase) || account.Contains("hotmail", StringComparison.OrdinalIgnoreCase) || account.Contains("live.com", StringComparison.OrdinalIgnoreCase));
            return Response.Success(new JsonObject
            {
                ["user"] = account,
                ["identity"] = identity,
                ["microsoftAccountConnected"] = microsoft,
                ["credentials"] = credentials,
                ["guidance"] = "Windows does not expose a supported silent command to detach a personal Microsoft account from a local profile. Use Open Windows account settings for that action; selected stored app credentials can be removed here.",
            });
        }
        catch (Exception ex) { return Response.Fail("Could not inspect the signed-in account: " + ex.Message); }
    }

    public Response IdentityRemove(JsonObject a)
    {
        var target = a.Str("target").Trim();
        if (target.Length == 0 || target.Contains('"') || target.Contains('\n') || target.Contains('\r'))
            return Response.Fail("Credential target is invalid.");
        var result = RunCmdKey($"/delete:{target}");
        if (result.ExitCode != 0) return Response.Fail(result.Output.Trim().Length > 0 ? result.Output.Trim() : "Windows could not remove the stored app credential.");
        Log.Write($"account.identity.remove: removed credential target '{target}'.");
        return Response.Success();
    }

    public Response OpenIdentitySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:emailandaccounts") { UseShellExecute = true });
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail("Could not open Windows account settings: " + ex.Message); }
    }

    static (int ExitCode, string Output) RunCmdKey(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmdkey.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start cmdkey.exe.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        return (process.ExitCode, output);
    }
}
