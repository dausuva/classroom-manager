// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Shared;
using System.Text.Json.Nodes;

namespace ClassroomManager.Host;

/// <summary>Whether a Client build chosen for "Update client" needs the .NET runtime on the student PC.</summary>
public sealed record ClientBuildRuntime(bool UsesSharedRuntime, int MajorVersion, string DisplayName, bool Guessed)
{
    /// <summary>A build that includes .NET is far larger than this; one that does not is a few megabytes.</summary>
    const long SharedRuntimeSizeLimit = 20L * 1024 * 1024;

    /// <summary>
    /// Reads RUNTIME.json, which every publish folder has beside the executable. A file picked from
    /// somewhere else has no description, so its size decides: guessing "needs the runtime" for a
    /// small file is the safe direction, because the consequence is only a question to the user.
    /// </summary>
    public static ClientBuildRuntime Describe(string exePath)
    {
        var marker = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "", "RUNTIME.json");
        if (File.Exists(marker))
        {
            try
            {
                var info = JsonNode.Parse(File.ReadAllText(marker))!.AsObject();
                var shared = info["mode"]?.GetValue<string>() == "shared-runtime";
                var major = info["majorVersion"]?.GetValue<int>() ?? Environment.Version.Major;
                var name = info["displayName"]?.GetValue<string>() ?? $".NET {major} Desktop Runtime";
                return new ClientBuildRuntime(shared, major, name, Guessed: false);
            }
            catch (Exception) { /* unreadable description: fall through to the size check */ }
        }
        var small = new FileInfo(exePath).Length < SharedRuntimeSizeLimit;
        return new ClientBuildRuntime(small, Environment.Version.Major, $".NET {Environment.Version.Major} Desktop Runtime", Guessed: true);
    }
}

public enum RuntimeReadiness
{
    /// <summary>The computer reported a new enough .NET Desktop Runtime.</summary>
    Ready,
    /// <summary>The computer reported that it has no suitable runtime.</summary>
    Missing,
    /// <summary>
    /// The computer's Client is too old to report its runtime. Such a Client also lacks the start-up
    /// check, so it would install a build it cannot run and then be unreachable.
    /// </summary>
    Unknown,
}

public static class ClientUpdateRuntime
{
    /// <summary><paramref name="reported"/> is null when the Client did not say, empty when it has no runtime.</summary>
    public static RuntimeReadiness Classify(string? reported, int requiredMajor)
    {
        if (reported is null) return RuntimeReadiness.Unknown;
        // The apps roll forward to a newer major version, so anything at or above the required one will do.
        return Version.TryParse(reported, out var version) && version.Major >= requiredMajor
            ? RuntimeReadiness.Ready
            : RuntimeReadiness.Missing;
    }

    /// <summary>Explains which computers cannot take a shared-runtime build yet, and what to do about each kind.</summary>
    public static string Explain(ClientBuildRuntime build, IReadOnlyList<string> missing, IReadOnlyList<string> unknown, int ready)
    {
        static string Names(IReadOnlyList<string> names) =>
            string.Join(", ", names.Take(8)) + (names.Count > 8 ? $" + {names.Count - 8} more" : "");

        var lines = new List<string>
        {
            $"This build uses the {build.DisplayName} installed on each computer" +
            (build.Guessed ? " (judging by its size; there is no RUNTIME.json beside it)." : "."),
            "",
        };
        if (missing.Count > 0)
        {
            lines.Add($"No suitable runtime on {missing.Count}: {Names(missing)}");
            lines.Add("Install the runtime there first (Run command with deploy\\tools\\Install-DotNetRuntime-Winget.ps1, or setup from the kit).");
            lines.Add("");
        }
        if (unknown.Count > 0)
        {
            lines.Add($"Client too old to report its runtime on {unknown.Count}: {Names(unknown)}");
            lines.Add("These Clients would install a build they cannot start and become unreachable. Update them first with the build that includes .NET.");
            lines.Add("");
        }
        lines.Add(ready == 0
            ? "No selected computer can take this build, so nothing will be updated."
            : $"Update only the {ready} computer(s) that are ready?");
        return string.Join("\n", lines);
    }
}

/// <summary>Whether a Client build (x64 or x86) fits the Client it is going to replace.</summary>
public static class ClientUpdateArchitecture
{
    /// <summary>
    /// <paramref name="reported"/> is what the Client said about itself ("x86" or "x64"); empty means it did not say: a Client from before
    /// the 32-bit build, which is x64. A build whose architecture cannot be read is not judged here (the Client's own start-up check decides).
    /// </summary>
    public static bool Mismatch(string build, string reported)
    {
        var client = reported.Length == 0 ? PeArchitecture.X64 : reported;
        return build != PeArchitecture.Unknown && build != client;
    }

    public static string ExplainOne(string build, string reported) =>
        $"this build is for {Describe(build)} and this computer runs the {Describe(reported.Length == 0 ? PeArchitecture.X64 : reported)} client; push the build for {Describe(reported.Length == 0 ? PeArchitecture.X64 : reported)}";

    public static string Explain(string build, IReadOnlyList<string> names, int fitting)
    {
        var list = string.Join(", ", names.Take(8)) + (names.Count > 8 ? $" + {names.Count - 8} more" : "");
        return string.Join("\n",
            $"This build is for {Describe(build)}, which does not fit {names.Count} of the selected computers: {list}.",
            "A 64-bit build cannot start on 32-bit Windows, and a 32-bit build on 64-bit Windows would see the wrong registry and files. Push the matching build to each kind of computer.",
            "",
            fitting == 0 ? "No selected computer can take this build, so nothing will be updated." : $"Update only the {fitting} computer(s) it fits?");
    }

    static string Describe(string architecture) => architecture switch
    {
        PeArchitecture.X86 => "32-bit Windows (x86)",
        PeArchitecture.X64 => "64-bit Windows (x64)",
        _ => architecture,
    };
}
