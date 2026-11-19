// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>
/// Golden lists: what the Host offers by default in Recent files, and the error categories. Changing either is a product decision and shows up here as a changed line
/// in review; a silent edit of one entry (the mutation test emptied entries one at a time) fails.
/// </summary>
public sealed class DefaultGroupsAndCategoriesTests
{
    [Fact]
    public void The_default_file_type_groups_hold_exactly_these_extensions()
    {
        Assert.Equal([".doc", ".docx", ".docm", ".odt", ".rtf", ".txt", ".md", ".pdf", ".xls", ".xlsx", ".xlsm", ".ods", ".csv", ".ppt", ".pptx", ".odp", ".pub", ".vsdx"],
            FileTypes.Defaults[0].Extensions);
        Assert.Equal([".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".svg", ".ico", ".psd", ".xcf", ".kra"], FileTypes.Defaults[1].Extensions);
        Assert.Equal([".cs", ".py", ".java", ".js", ".ts", ".jsx", ".tsx", ".html", ".htm", ".css", ".scss", ".json", ".xml", ".yaml", ".yml", ".c", ".cpp", ".cc", ".h", ".hpp", ".go",
            ".rs", ".rb", ".php", ".sql", ".sh", ".ps1", ".bat", ".cmd", ".ipynb", ".kt", ".swift", ".lua", ".pas", ".dart", ".vb", ".sb3", ".ino"], FileTypes.Defaults[2].Extensions);
    }

    [Fact]
    public void The_error_categories_are_these_eleven_named_the_way_the_reference_page_names_them()
    {
        Assert.Equal(
        [
            ("00", "Unexpected"), ("01", "Connection"), ("02", "Keys"), ("03", "Certificates and TLS"), ("04", "Permissions and limits"), ("05", "Versions and features"),
            ("06", "Files"), ("07", "Client update and setup"), ("08", "Settings of this PC"), ("09", "Addresses and discovery"), ("0A", "Refused by the computer"),
        ], ErrorCatalog.Categories.Select(pair => (pair.Key, pair.Value)).OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void An_error_is_found_by_its_id_in_any_case_and_an_unknown_or_missing_id_finds_none()
    {
        var known = ErrorCatalog.All[0];
        Assert.Same(known, ErrorCatalog.Find(known.Id));
        Assert.Same(known, ErrorCatalog.Find(known.Id.ToLowerInvariant()));
        Assert.Null(ErrorCatalog.Find("FFFFFF"));
        Assert.Null(ErrorCatalog.Find(""));
        Assert.Null(ErrorCatalog.Find(null));
    }
}
