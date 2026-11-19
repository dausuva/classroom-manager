// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.RegularExpressions;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public class HelpGuideTests
{
    [Theory]
    [InlineData("lt-LT", "guide-lt.html")]
    [InlineData("lt", "guide-lt.html")]
    [InlineData("en-US", "guide-en.html")]
    [InlineData("pl-PL", "guide-en.html")]     // no Polish guide: English
    [InlineData("", "guide-en.html")]
    [InlineData(null, "guide-en.html")]
    public void The_guide_follows_the_language_and_falls_back_to_English(string? language, string file)
    {
        Assert.Equal(file, HelpGuide.FileFor(language));
    }

    [Fact]
    public void The_program_carries_the_whole_guide_and_every_picture_its_pages_show()
    {
        var folder = Directory.CreateTempSubdirectory("cm-help-").FullName;
        try
        {
            var page = HelpGuide.Extract(folder, "lt-LT");

            Assert.Equal(Path.Combine(folder, "guide-lt.html"), page);
            Assert.True(File.Exists(Path.Combine(folder, "guide.css")));
            foreach (var language in HelpGuide.Languages)
            {
                var html = File.ReadAllText(Path.Combine(folder, $"guide-{language}.html"));
                var pictures = Regex.Matches(html, "<img src=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToList();
                Assert.True(pictures.Count >= 6, $"The {language} guide shows only {pictures.Count} pictures.");
                // A picture named in a page but not carried in the program would be a broken image for every teacher.
                Assert.All(pictures, picture =>
                {
                    Assert.StartsWith($"img/{language}/", picture);
                    var file = Path.Combine(folder, picture.Replace('/', Path.DirectorySeparatorChar));
                    Assert.True(File.Exists(file) && new FileInfo(file).Length > 1000, $"{picture} is missing from the program.");
                });
                Assert.Contains($"<html lang=\"{language}\">", html);
            }
            // Written once; a second request leaves the files alone.
            var written = File.GetLastWriteTimeUtc(page);
            HelpGuide.Extract(folder, "en-US");
            Assert.Equal(written, File.GetLastWriteTimeUtc(page));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
