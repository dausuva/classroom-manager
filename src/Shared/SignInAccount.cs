// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>
/// The account a teacher types for a remote sign-in, split the way Windows' automatic sign-in wants it.
/// An empty domain means "an account of that computer"; each Client fills in its own computer name.
/// </summary>
public sealed record SignInAccount(string Domain, string User)
{
    public bool IsLocal => Domain.Length == 0;

    public string Display => IsLocal ? User : User.Contains('@') ? User : $@"{Domain}\{User}";

    /// <summary>
    /// Accepts <c>user</c>, <c>.\user</c>, <c>DOMAIN\user</c>, and <c>user@domain</c>. Returns null for
    /// anything else, including names with characters Windows does not allow in an account name.
    /// </summary>
    public static SignInAccount? Parse(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return null;

        var domain = "";
        var user = value;
        var slash = value.IndexOf('\\');
        if (slash >= 0)
        {
            domain = value[..slash].Trim();
            user = value[(slash + 1)..].Trim();
            if (domain == ".") domain = "";
            else if (domain.Length == 0 || !ValidPart(domain, allowAt: false)) return null;
        }
        else if (value.Contains('@'))
        {
            // A user principal name is passed whole; Windows resolves its domain itself.
            var parts = value.Split('@');
            if (parts.Length != 2 || !ValidPart(parts[0], allowAt: false) || !ValidPart(parts[1], allowAt: false)) return null;
            return new SignInAccount(parts[1], value);
        }
        return ValidPart(user, allowAt: false) ? new SignInAccount(domain, user) : null;
    }

    static bool ValidPart(string part, bool allowAt) =>
        part.Length is > 0 and <= 104 &&
        part.IndexOfAny(['"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>']) < 0 &&
        (allowAt || !part.Contains('@')) &&
        !part.Any(char.IsControl);
}
