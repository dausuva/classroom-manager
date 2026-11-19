// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// A guard for T-110 (credentials must not reach logs): no log call in the Client, Host or Shared sources puts a value named like a secret into a message.
/// The runtime protection (the log providers that strip known secrets) is tested elsewhere; this fails when someone writes
/// <c>Log.Write($"... {psk} ...")</c> in the first place. A key may appear only as its fingerprint (<c>SharedKey.Fingerprint(key)</c>).
/// </summary>
public sealed class LogSecretGuardTests
{
    static readonly Regex LogCall = new(@"\b(?:Log|log|ActivityLog|activityLog|_log)\w*\.(?:Write|Append|Add)\w*\(", RegexOptions.Compiled);
    static readonly Regex Hole = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
    static readonly Regex SecretName = new(@"psk|password|passphrase|pkcs8|privatekey|sharedkey|secret", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void No_log_message_interpolates_a_value_named_like_a_secret_unless_it_is_a_fingerprint()
    {
        var root = StringExtractor.RepositoryRoot();
        var offenders = new List<string>();
        foreach (var folder in new[] { "src/ClassroomClient", "src/ClassroomHost", "src/Shared", "src/Modules" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!LogCall.IsMatch(lines[i])) continue;
                foreach (Match hole in Hole.Matches(lines[i]))
                {
                    var expression = hole.Groups[1].Value;
                    if (SecretName.IsMatch(expression) && !expression.Contains("Fingerprint(", StringComparison.Ordinal))
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {{{expression}}}");
                }
            }
        }
        Assert.True(offenders.Count == 0, "A log message names a secret-like value (log only a fingerprint):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_guard_does_find_a_secret_in_a_log_call()
    {
        var bad = "Log.Write($\"key is {settings.Psk}\");";
        var good = "Log.Write($\"key {SharedKey.Fingerprint(settings.Psk)}\");";
        Assert.True(LogCall.IsMatch(bad) && Hole.Matches(bad).Any(h => SecretName.IsMatch(h.Groups[1].Value) && !h.Groups[1].Value.Contains("Fingerprint(")));
        Assert.DoesNotContain(Hole.Matches(good), h => SecretName.IsMatch(h.Groups[1].Value) && !h.Groups[1].Value.Contains("Fingerprint(", StringComparison.Ordinal));
    }
}
