// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.RegularExpressions;

namespace ClassroomManager.Modules;

/// <summary>What a descriptor may say beyond its id: the icon and the scheduled actions it adds.</summary>
public static class ModuleDescriptorRules
{
    static readonly Regex ActionCode = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Problems(ModuleDescriptor descriptor)
    {
        var problems = new List<string>(ModuleIcon.Problems(descriptor.Icon));
        problems.AddRange(ModuleManifest.PortProblems(descriptor.Ports));
        problems.AddRange(ModuleManifest.SinkProblems(descriptor.Sinks, descriptor.Ports));
        problems.AddRange(ModuleManifest.UploadProblems(descriptor.Uploads, descriptor.Ports));
        problems.AddRange(ModuleManifest.SystemCommandProblems(descriptor.SystemCommands, descriptor.ClientCapabilities));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in descriptor.ScheduledActions)
        {
            if (action is null || !ActionCode.IsMatch(action.Code ?? "")) problems.Add($"\"{action?.Code}\" is not a valid action code (lower-case letters, digits and hyphens).");
            else if (!seen.Add(action.Code!)) problems.Add($"The action \"{action.Code}\" is listed twice.");
            if (action is not null && (string.IsNullOrWhiteSpace(action.Label) || action.Label.Length > 120)) problems.Add($"The action \"{action.Code}\" needs a label of 1 to 120 characters.");
        }
        return problems;
    }
}
