// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

/// <summary>
/// Opening a website on a computer (<see cref="Cmd.WebsiteOpen"/>), with the options added in protocol 1.15:
/// a window that opens maximized, and a browser chosen by the teacher. A Client older than that ignores the
/// two fields and opens the address in the default browser as before, so the Host asks
/// <see cref="Supported"/> first and says which computers cannot honour the choice.
/// </summary>
public static class WebsiteLaunch
{
    public const string RequiredVersion = "1.15";

    // The request.
    public const string Url = "url";
    public const string Browser = "browser";       // a BrowserCatalog id; empty or missing means the default browser
    public const string Maximized = "maximized";   // true: ask for a maximized window

    // The answer. Both are only there when the request used an option.
    public const string OpenedWith = "browser";    // the id of the browser that was started, "" for the default browser
    public const string FellBack = "fallback";     // the chosen browser is not on this computer: the default browser opened instead
    public const string WasMaximized = "maximized"; // a window of the browser was really maximized (false: asked for, not confirmed)

    /// <summary>True for a Client that reports a protocol version with these options.</summary>
    public static bool Supported(string? clientProtocolVersion) => Protocol.IsAtLeast(clientProtocolVersion, RequiredVersion);

    /// <summary>The arguments of one request; the options only go to a Client that understands them.</summary>
    public static JsonObject Args(string url, string browserId, bool maximized, bool clientSupportsOptions)
    {
        var args = new JsonObject { [Url] = url };
        if (!clientSupportsOptions) return args;
        if (browserId.Length > 0) args[Browser] = browserId;
        args[Maximized] = maximized;
        return args;
    }

    /// <summary>What to start: the program and its arguments, or the default browser through the shell.</summary>
    public sealed record Plan(string Program, string Arguments, string BrowserId, bool UsesDefaultBrowser, bool FellBack);

    /// <summary>
    /// Chooses what to start for an address. No browser chosen, or one that is not installed, means the default
    /// browser through Windows (a chosen browser that is missing is reported as <see cref="Plan.FellBack"/>, not
    /// as an error: the teacher still wants the page shown). A chosen browser is started on its own program
    /// file, in a new window, with its own switch for a maximized one.
    /// </summary>
    public static Plan Choose(string url, string browserId, bool maximized, IEnumerable<BrowserCatalog.Installed> installed)
    {
        browserId = browserId.Trim();
        if (browserId.Length == 0) return new(url, "", "", true, false);
        var chosen = installed.FirstOrDefault(browser => browser.Id.Equals(browserId, StringComparison.OrdinalIgnoreCase) && browser.Path.Length > 0);
        if (chosen is null) return new(url, "", "", true, true);

        var quoted = "\"" + url.Replace("\"", "%22") + "\"";
        var arguments = BrowserCatalog.Find(chosen.Id)?.Id == "firefox"
            ? "-new-window " + quoted
            : "--new-window " + (maximized ? "--start-maximized " : "") + quoted;
        return new(chosen.Path, arguments, chosen.Id, false, false);
    }
}
