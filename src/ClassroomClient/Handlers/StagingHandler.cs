// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Inventories and cleans one Host's opaque staging namespace without crossing into another.</summary>
sealed class StagingHandler
{
    static readonly Regex NamespacePattern = new(
        "^[a-f0-9]{8}-[a-f0-9]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public Response List(JsonObject args)
    {
        try
        {
            var root = ResolveRoot(args.Str("namespace"));
            Directory.CreateDirectory(root);
            var files = new JsonArray();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                var relative = Path.GetRelativePath(root, file.FullName);
                files.Add(new JsonObject
                {
                    ["relativePath"] = relative,
                    ["name"] = file.Name,
                    ["size"] = file.Length,
                    ["modifiedUtc"] = file.LastWriteTimeUtc.ToString("O"),
                    ["purpose"] = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "other",
                });
            }
            return Response.Success(new JsonObject { ["files"] = files });
        }
        catch (Exception ex) { return Response.Fail("Could not list staged uploads: " + ex.Message); }
    }

    public Response Delete(JsonObject args)
    {
        try
        {
            var root = ResolveRoot(args.Str("namespace"));
            var path = ResolveRelative(root, args.Str("relativePath"));
            if (!File.Exists(path)) return Response.Fail("Staged file not found.");
            File.Delete(path);
            RemoveEmptyParents(Path.GetDirectoryName(path), root);
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail("Could not delete staged upload: " + ex.Message); }
    }

    static string ResolveRoot(string value)
    {
        if (!NamespacePattern.IsMatch(value)) throw new UnauthorizedAccessException("Invalid staging namespace.");
        var hostsRoot = Path.GetFullPath(Path.Combine(ClientConfig.StagingDir, "Hosts"));
        var root = Path.GetFullPath(Path.Combine(hostsRoot, value));
        if (!root.StartsWith(hostsRoot + "\\", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Invalid staging namespace.");
        return root;
    }

    static string ResolveRelative(string root, string relative)
    {
        relative = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (relative.Length == 0 || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("Invalid relative staging path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The path leaves this Host's staging namespace.");
        return path;
    }

    static void RemoveEmptyParents(string? directory, string root)
    {
        while (directory is not null && !directory.Equals(root, StringComparison.OrdinalIgnoreCase) &&
               directory.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any()) return;
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }
}
