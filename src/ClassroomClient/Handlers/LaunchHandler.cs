// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Starts explicitly requested websites and programs in the signed-in user's session.</summary>
sealed class LaunchHandler
{
    /// <summary>The browser asked for, "" for the default one. A Host older than protocol 1.15 sends none, which must keep working.</summary>
    public static string BrowserId(JsonObject args) => args.Str(WebsiteLaunch.Browser, "").Trim();

    /// <summary>
    /// Opens an address. With no option it goes to the default browser through Windows, as it always did. The
    /// options of protocol 1.15 are a browser chosen by id and a maximized window; a chosen browser that is not
    /// installed falls back to the default browser, which the answer says.
    /// </summary>
    public async Task<Response> OpenWebsiteAsync(JsonObject args, CancellationToken ct)
    {
        var target = args.Str(WebsiteLaunch.Url).Trim();
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return Response.Fail("Enter a complete http:// or https:// website address.");

        var browserId = BrowserId(args);
        var maximized = args.Bool(WebsiteLaunch.Maximized) ?? false;
        if (browserId.Length == 0 && !maximized)
            return Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }, "website");

        var installed = browserId.Length == 0 ? [] : BrowserHandler.FindInstalled(includeUser: true);
        var plan = WebsiteLaunch.Choose(uri.AbsoluteUri, browserId, maximized, installed);
        if (plan.UsesDefaultBrowser)
        {
            // The default browser is started by Windows, so it is only asked for a maximized window; whether it
            // obeys is up to the browser, and the answer does not claim it did.
            var shell = new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
            if (maximized) shell.WindowStyle = ProcessWindowStyle.Maximized;
            var opened = Start(shell, "website");
            return !opened.Ok ? opened : Response.Success(new JsonObject
            {
                [WebsiteLaunch.OpenedWith] = "", [WebsiteLaunch.FellBack] = plan.FellBack, [WebsiteLaunch.WasMaximized] = false,
            });
        }

        var processName = Path.GetFileNameWithoutExtension(plan.Program);
        var before = maximized ? WindowMaximizer.Windows(processName) : [];
        var started = Start(new ProcessStartInfo(plan.Program, plan.Arguments) { UseShellExecute = false }, "website");
        if (!started.Ok) return started;
        var confirmed = maximized && await WindowMaximizer.MaximizeNewAsync(processName, before, TimeSpan.FromSeconds(5), ct);
        return Response.Success(new JsonObject
        {
            [WebsiteLaunch.OpenedWith] = plan.BrowserId, [WebsiteLaunch.FellBack] = false, [WebsiteLaunch.WasMaximized] = confirmed,
        });
    }

    public Response OpenProgram(JsonObject args)
    {
        var target = Environment.ExpandEnvironmentVariables(args.Str("program").Trim());
        var arguments = args.Str("arguments");
        if (target.Length == 0) return Response.Fail("Enter a program path or executable name.");
        if (target.Length > 2048 || arguments.Length > 8192) return Response.Fail("The program path or arguments are too long.");

        return Start(new ProcessStartInfo(target)
        {
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = ExistingWorkingDirectory(target),
        }, "program");
    }

    static string ExistingWorkingDirectory(string target)
    {
        try
        {
            var directory = Path.GetDirectoryName(target);
            return directory is not null && Directory.Exists(directory) ? directory : "";
        }
        catch { return ""; }
    }

    static Response Start(ProcessStartInfo startInfo, string kind)
    {
        try
        {
            // Shell activation can legitimately return no process handle when an existing browser
            // or application receives the request. No exception means Windows accepted the launch.
            using var process = Process.Start(startInfo);
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail($"Could not open the {kind}: {ex.Message}"); }
    }
}
