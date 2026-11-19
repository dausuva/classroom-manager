// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class BrowserCatalogTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome")]
    [InlineData(@"""C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe""", "edge")]
    [InlineData(@"C:\Program Files\Mozilla Firefox\FIREFOX.EXE", "firefox")]
    [InlineData(@"C:\Users\pc\AppData\Local\Programs\Opera\launcher.exe", "opera")]
    [InlineData(@"C:\Tools\launcher.exe", null)]
    [InlineData(@"C:\Windows\notepad.exe", null)]
    public void A_browser_is_known_by_its_program(string path, string? id) => Assert.Equal(id, BrowserCatalog.ByExe(path)?.Id);

    [Theory]
    [InlineData("ChromeHTML", "chrome")]
    [InlineData("MSEdgeHTM", "edge")]
    [InlineData("FirefoxURL-308046B0AF4A39CB", "firefox")]
    [InlineData("BraveHTML", "brave")]
    [InlineData("AppXq0fevzme2pys62n3e0fbqa7peapykr8v", null)]
    [InlineData("", null)]
    public void A_browser_is_known_by_its_registration(string progId, string? id) => Assert.Equal(id, BrowserCatalog.ByProgId(progId)?.Id);

    [Fact]
    public void The_associations_file_names_the_browser_for_links_and_pages_and_reads_back()
    {
        var firefox = new BrowserCatalog.Installed("firefox", "Mozilla Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", "131.0",
            "FirefoxURL-308046B0AF4A39CB", "FirefoxHTML-308046B0AF4A39CB", true);
        var xml = BrowserCatalog.AssociationsXml(firefox);

        var lines = XDocument.Parse(xml).Root!.Elements("Association")
            .ToDictionary(element => (string)element.Attribute("Identifier")!, element => (string)element.Attribute("ProgId")!);
        Assert.Equal("FirefoxURL-308046B0AF4A39CB", lines["https"]);
        Assert.Equal("FirefoxURL-308046B0AF4A39CB", lines["http"]);
        Assert.Equal("FirefoxHTML-308046B0AF4A39CB", lines[".html"]);
        Assert.Equal("FirefoxHTML-308046B0AF4A39CB", lines[".htm"]);
        Assert.Equal("firefox", BrowserCatalog.FromAssociationsXml(xml)?.Id);
        Assert.Null(BrowserCatalog.FromAssociationsXml("<DefaultAssociations />"));
    }

    [Fact]
    public void A_browser_without_a_page_registration_opens_pages_with_its_link_one()
    {
        var chrome = new BrowserCatalog.Installed("chrome", "Google Chrome", "", "", "ChromeHTML", "", false);
        Assert.Contains("Identifier=\".html\" ProgId=\"ChromeHTML\"", BrowserCatalog.AssociationsXml(chrome));
    }

    [Fact]
    public void An_answer_survives_the_wire_and_names_the_missing_main_browsers()
    {
        var edge = new BrowserCatalog.Installed("edge", "Microsoft Edge", @"C:\x\msedge.exe", "141.0.3537.71", "MSEdgeHTM", "MSEdgeHTM", true);
        var data = JsonNode.Parse(new JsonObject
        {
            [BrowserCatalog.Browsers] = new JsonArray(edge.ToJson()),
            [BrowserCatalog.Default] = "edge",
            [BrowserCatalog.Managed] = "",
        }.ToJsonString())!.AsObject();

        Assert.Equal(edge, Assert.Single(BrowserCatalog.InstalledFrom(data)));
        Assert.Equal(["Google Chrome", "Mozilla Firefox"], BrowserCatalog.MissingMain(BrowserCatalog.InstalledFrom(data)));
        Assert.Equal("Browsers: Microsoft Edge 141.0.3537.71. Missing: Google Chrome, Mozilla Firefox. Opens web links: Microsoft Edge.",
            BrowserCatalog.Describe(data));
    }

    [Fact]
    public void The_commands_are_new_in_1_12_need_an_admin_key_and_the_list_can_run_at_either_level()
    {
        foreach (var command in new[] { Cmd.BrowserList, Cmd.BrowserDefaultSet })
        {
            Assert.Equal("1.12", Protocol.RequiredVersion(command));
            Assert.Contains(command, Protocol.Capabilities);
            Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(command));
            Assert.False(Cmd.IsUserOnly(command));
        }
    }

    [Fact]
    public void Each_browser_has_its_own_translation_policy()
    {
        Assert.Equal(("Chrome", "TranslateEnabled", 0), Single(BrowserRules.NoTranslateChrome));
        Assert.Equal(("Edge", "TranslateEnabled", 0), Single(BrowserRules.NoTranslateEdge));
        var firefox = Assert.Single(BrowserRules.Settings(BrowserRules.NoTranslateFirefox));
        Assert.Equal("Preferences", firefox.Name);
        var locked = JsonNode.Parse(firefox.Text!)!["browser.translations.enable"]!;
        Assert.False(locked["Value"]!.GetValue<bool>());
        Assert.Equal("locked", locked["Status"]!.GetValue<string>());
        Assert.All(BrowserRules.TranslationPolicies, policy => Assert.Contains(policy, BrowserRules.Policies));

        static (string, string, int) Single(string policy)
        {
            var setting = Assert.Single(BrowserRules.Settings(policy));
            return (setting.Browser, setting.Name, setting.Number);
        }
    }
}
