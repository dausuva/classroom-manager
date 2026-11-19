// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Matches persisted computers without treating a DHCP address or duplicated hostname as identity.</summary>
public static class ComputerIdentity
{
    public static string NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = new string(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        return normalized.Length == 12 ? normalized : "";
    }

    /// <summary>
    /// A MAC address as Windows prints it (ipconfig /all): six pairs of capital hex digits joined by hyphens,
    /// "AA-BB-CC-DD-EE-FF". Empty when the value is not a MAC address. Only for showing; the stored and
    /// compared form stays <see cref="NormalizeMac"/>.
    /// </summary>
    public static string FormatMac(string? value)
    {
        var digits = NormalizeMac(value);
        return digits.Length == 0 ? "" : string.Join('-', Enumerable.Range(0, 6).Select(i => digits.Substring(i * 2, 2)));
    }

    public static bool StableMatch(string? leftClientId, string? leftMac, string? rightClientId, string? rightMac)
    {
        // Prefer hardware identity when both sides have it. This prevents a client.json copied in a
        // machine image (and therefore a duplicated ClientId) from merging distinct computers.
        var leftNormalizedMac = NormalizeMac(leftMac);
        var rightNormalizedMac = NormalizeMac(rightMac);
        if (leftNormalizedMac.Length > 0 && rightNormalizedMac.Length > 0)
            return leftNormalizedMac.Equals(rightNormalizedMac, StringComparison.OrdinalIgnoreCase);

        var leftId = leftClientId?.Trim() ?? "";
        var rightId = rightClientId?.Trim() ?? "";
        return leftId.Length > 0 && rightId.Length > 0 && leftId.Equals(rightId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An address is a safe legacy fallback only while one side has no stable identity. If both sides
    /// are identified but disagree, DHCP has probably assigned the old address to another computer.
    /// </summary>
    public static bool SafeAddressMatch(ClientInfo computer, string address, string? clientId, string? mac)
    {
        if (!computer.Address.Equals(address, StringComparison.OrdinalIgnoreCase)) return false;
        if (StableMatch(computer.ClientId, computer.Mac, clientId, mac)) return true;
        return !HasStableIdentity(computer.ClientId, computer.Mac) || !HasStableIdentity(clientId, mac);
    }

    public static bool HasStableIdentity(string? clientId, string? mac) =>
        !string.IsNullOrWhiteSpace(clientId) || NormalizeMac(mac).Length > 0;
}
