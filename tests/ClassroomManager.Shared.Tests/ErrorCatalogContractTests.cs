// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>
/// T-134: every error has a stable six-hex-digit id, shown at the end of its message and explained in docs/ERROR-CODES.md. Run the tests
/// once with CLASSROOM_UPDATE_ERROR_CODES=1 after adding an error: that writes the reference page and adds the new id to tests/error-ids.txt.
/// These tests are in the shared library's project because the catalogue is in the shared library: the mutation test (docs/QUALITY.md) runs this project.
/// </summary>
public sealed class ErrorCatalogContractTests
{
    static string Root => RepositoryFiles.Root();
    static string IdsFile => Path.Combine(Root, "tests", "error-ids.txt");
    static string PageFile => Path.Combine(Root, "docs", "ERROR-CODES.md");
    static bool Update => Environment.GetEnvironmentVariable("CLASSROOM_UPDATE_ERROR_CODES") == "1";

    [Fact]
    public void Every_id_is_six_hex_digits_unique_and_starts_with_a_known_category()
    {
        Assert.NotEmpty(ErrorCatalog.All);
        Assert.All(ErrorCatalog.All, error =>
        {
            Assert.Matches("^[0-9A-F]{6}$", error.Id);
            Assert.True(ErrorCatalog.Categories.ContainsKey(error.Category), $"{error.Id}: unknown category {error.Category}");
            Assert.NotEqual("0000", error.Id[2..]);
            Assert.False(string.IsNullOrWhiteSpace(error.Title) || string.IsNullOrWhiteSpace(error.Cause) || string.IsNullOrWhiteSpace(error.Action), error.Id);
        });
        Assert.Equal(ErrorCatalog.All.Count, ErrorCatalog.All.Select(error => error.Id).Distinct().Count());
        Assert.All(ErrorCatalog.Categories.Keys, key => Assert.Matches("^[0-9A-F]{2}$", key));
    }

    [Fact]
    public void An_id_once_issued_is_never_removed_or_given_to_another_error()
    {
        // tests/error-ids.txt: "<id> <title when it was issued>". An error that no longer happens is marked Retired in the catalogue, never deleted.
        var issued = File.Exists(IdsFile)
            ? File.ReadAllLines(IdsFile).Where(line => line.Length > 0 && !line.StartsWith('#')).ToDictionary(line => line[..6], line => line[7..])
            : new Dictionary<string, string>();
        var missing = issued.Keys.Where(id => ErrorCatalog.Find(id) is null).ToList();
        Assert.True(missing.Count == 0, "Issued ids gone from the catalogue (mark them Retired instead): " + string.Join(", ", missing));

        var added = ErrorCatalog.All.Where(error => !issued.ContainsKey(error.Id)).ToList();
        if (added.Count > 0 && Update)
        {
            var lines = issued.Select(pair => $"{pair.Key} {pair.Value}").Concat(added.Select(error => $"{error.Id} {error.Title}")).OrderBy(line => line, StringComparer.Ordinal);
            File.WriteAllText(IdsFile, "# Every error id ever issued (T-134), with the title it was issued for. Never remove a line; see ErrorCatalogContractTests.\n" + string.Join("\n", lines) + "\n");
            return;
        }
        Assert.True(added.Count == 0, "New error ids: run the tests once with CLASSROOM_UPDATE_ERROR_CODES=1 to record them: " + string.Join(", ", added.Select(error => error.Id)));
    }

    static string Page()
    {
        var page = new StringBuilder();
        page.Append("""
            # Error codes

            Every error the program reports ends with its code, for example "Authentication failed (wrong shared key?). (error code 020001)".
            The code stays the same when the wording changes, so it can be searched for here or read out to support. The first two digits are the
            category, the last four the error in it (all hexadecimal).

            This page is written by a test (`ErrorCatalogContractTests`) from `src/Shared/ErrorCatalog.cs`; change the catalogue and run the tests with
            `CLASSROOM_UPDATE_ERROR_CODES=1`, never this file.

            """.Replace("\r\n", "\n"));
        foreach (var (key, name) in ErrorCatalog.Categories)
        {
            var errors = ErrorCatalog.All.Where(error => error.Category == key).ToList();
            if (errors.Count == 0) continue;
            page.Append($"\n## {key} {name}\n\n| Code | Error | Why it happens | What to do |\n| --- | --- | --- | --- |\n");
            foreach (var error in errors)
                page.Append($"| `{error.Id}` | {error.Title}{(error.Retired ? " (retired)" : "")} | {error.Cause} | {error.Action} |\n");
        }
        return page.ToString();
    }

    [Fact]
    public void The_reference_page_is_written_from_the_catalogue()
    {
        var expected = Page();
        if (Update) { File.WriteAllText(PageFile, expected); return; }
        Assert.True(File.Exists(PageFile), "docs/ERROR-CODES.md is missing: run the tests with CLASSROOM_UPDATE_ERROR_CODES=1.");
        Assert.True(File.ReadAllText(PageFile).Replace("\r\n", "\n") == expected, "docs/ERROR-CODES.md is out of date: run the tests with CLASSROOM_UPDATE_ERROR_CODES=1.");
    }
}
