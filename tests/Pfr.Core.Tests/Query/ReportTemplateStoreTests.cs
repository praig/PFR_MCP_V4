using Pfr.Core.Query;

namespace Pfr.Core.Tests.Query;

public sealed class ReportTemplateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pfr-templates-{Guid.NewGuid():N}");
    private readonly ReportTemplateStore _store;

    public ReportTemplateStoreTests() => _store = new ReportTemplateStore(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static ReportTemplate New(string name, string sql = "SELECT 1", string description = "") =>
        new() { Name = name, Sql = sql, Description = description };

    private string[] Files() =>
        Directory.Exists(_dir) ? Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).Order().ToArray() : [];

    // ---- Saving and reading back --------------------------------------------------

    [Fact]
    public void A_saved_template_is_a_json_file_named_after_it_and_reads_back()
    {
        var saved = _store.Save(New("Networks with no DHCP range", "SELECT n.prefix\nFROM networks n\nWHERE n.is_container = 0", "Leaf networks nobody serves."));

        Assert.Equal("networks-with-no-dhcp-range", saved.Id);
        Assert.Equal(["networks-with-no-dhcp-range.json"], Files());

        var loaded = Assert.Single(_store.List());
        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal("Networks with no DHCP range", loaded.Name);
        Assert.Equal("Leaf networks nobody serves.", loaded.Description);
        Assert.Equal("SELECT n.prefix\nFROM networks n\nWHERE n.is_container = 0", loaded.Sql);
    }

    [Fact]
    public void The_file_is_readable_text_with_the_sql_as_lines()
    {
        _store.Save(New("A records", "SELECT fqdn, value\nFROM records\nWHERE type = 'A' AND value <> '' AND note = \"é\""));

        var text = File.ReadAllText(Path.Combine(_dir, "a-records.json"));

        Assert.Contains("\"format\": 1", text);
        Assert.Contains("\"name\": \"A records\"", text);
        Assert.Contains("\"sql\": [", text);
        Assert.Contains("\"SELECT fqdn, value\"", text);
        Assert.Contains("WHERE type = 'A' AND value <> ''", text); // quotes and < > are not turned into \u0027 and friends
        Assert.Contains("é", text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void Windows_line_endings_are_stored_as_lines_and_come_back_as_newlines()
    {
        _store.Save(New("Crlf", "SELECT 1\r\nFROM members\r\nWHERE 1 = 1"));
        Assert.Equal("SELECT 1\nFROM members\nWHERE 1 = 1", Assert.Single(_store.List()).Sql);
    }

    [Fact]
    public void A_trailing_semicolon_and_surrounding_space_are_dropped_but_comments_are_kept()
    {
        var saved = _store.Save(New("Commented", "  -- why this exists\nSELECT 1 ;  "));
        Assert.Equal("-- why this exists\nSELECT 1", saved.Sql);
    }

    [Fact]
    public void Templates_are_listed_by_name_ignoring_case()
    {
        foreach (var name in new[] { "zones", "Alpha", "members", "Beta" }) _store.Save(New(name));
        Assert.Equal(["Alpha", "Beta", "members", "zones"], _store.List().Select(t => t.Name));
    }

    [Fact]
    public void Nothing_is_created_until_something_is_saved()
    {
        Assert.Empty(_store.List());
        Assert.False(Directory.Exists(_dir));

        _store.Save(New("First"));
        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void Templates_can_be_found_by_name_or_id_ignoring_case()
    {
        var saved = _store.Save(New("Zones With No Secondary"));

        Assert.Equal(saved.Id, _store.Find("zones with no secondary")?.Id);
        Assert.Equal(saved.Id, _store.Find("  ZONES-WITH-NO-SECONDARY ")?.Id);
        Assert.Null(_store.Find("nothing like it"));
    }

    // ---- Names ---------------------------------------------------------------------

    [Fact]
    public void A_name_is_required_and_not_too_long()
    {
        Assert.Throws<ArgumentException>(() => _store.Save(New("   ")));
        Assert.Throws<ArgumentException>(() => _store.Save(New(new string('x', ReportTemplateStore.MaxNameLength + 1))));
        _store.Save(New(new string('x', ReportTemplateStore.MaxNameLength)));
        Assert.Single(_store.List());
    }

    [Fact]
    public void The_names_of_built_in_reports_are_reserved_whatever_the_case()
    {
        Assert.Contains("Explore data", ReportTemplateStore.ReservedNames);
        Assert.Equal(3, ReportTemplateStore.ReservedNames.Count);

        foreach (var reserved in ReportTemplateStore.ReservedNames)
        {
            foreach (var name in new[] { reserved, reserved.ToUpperInvariant(), "  " + reserved.ToLowerInvariant() + " " })
            {
                var ex = Assert.Throws<ArgumentException>(() => _store.Save(New(name)));
                Assert.Contains("built-in report", ex.Message);
            }
        }
        Assert.Empty(Files());
    }

    [Fact]
    public void Names_are_unique_ignoring_case()
    {
        _store.Save(New("Zones"));

        var ex = Assert.Throws<ArgumentException>(() => _store.Save(New("  ZONES ")));
        Assert.Contains("already exists", ex.Message);
        Assert.Single(Files());
    }

    [Fact]
    public void Different_names_with_the_same_file_name_get_a_number()
    {
        var a = _store.Save(New("A B"));
        var b = _store.Save(New("a-b"));
        var c = _store.Save(New("A  B!"));

        Assert.Equal(["a-b", "a-b-2", "a-b-3"], new[] { a.Id, b.Id, c.Id });
        Assert.Equal(3, _store.List().Count);
    }

    [Theory]
    [InlineData("Networks with no DHCP range", "networks-with-no-dhcp-range")]
    [InlineData("  Zones / views (2026)!  ", "zones-views-2026")]
    [InlineData("!!!", "report")]
    [InlineData("", "report")]
    [InlineData("ÆØ unicode", "unicode")]
    public void Slugs_are_safe_file_names(string name, string expected) => Assert.Equal(expected, ReportTemplateStore.Slug(name));

    [Fact]
    public void A_very_long_name_makes_a_short_file_name()
    {
        var slug = ReportTemplateStore.Slug(string.Join(" ", Enumerable.Repeat("word", 40)));
        Assert.True(slug.Length <= 60);
        Assert.False(slug.EndsWith('-'));
    }

    // ---- SQL ------------------------------------------------------------------------

    [Theory]
    [InlineData("DELETE FROM members")]
    [InlineData("SELECT 1; DROP TABLE records")]
    [InlineData("PRAGMA query_only = OFF")]
    [InlineData("WITH t AS (SELECT 1) INSERT INTO members SELECT * FROM t")]
    public void A_query_that_is_not_read_only_cannot_be_saved(string sql)
    {
        Assert.Throws<QueryException>(() => _store.Save(New("Sneaky", sql)));
        Assert.Empty(Files());
    }

    [Fact]
    public void Empty_sql_cannot_be_saved()
    {
        Assert.Throws<QueryException>(() => _store.Save(New("Empty", "   ")));
        Assert.Empty(Files());
    }

    // ---- Editing ----------------------------------------------------------------------

    [Fact]
    public void Editing_a_template_keeps_its_file_and_creation_time()
    {
        var first = _store.Save(New("Zones", "SELECT 1", "old"));
        Thread.Sleep(20);

        var edit = _store.List().Single();
        edit.Sql = "SELECT fqdn FROM zones";
        edit.Description = "new description";
        var saved = _store.Save(edit);

        Assert.Equal(first.Id, saved.Id);
        Assert.Equal(first.CreatedUtc, saved.CreatedUtc, TimeSpan.FromSeconds(1));
        Assert.True(saved.UpdatedUtc > first.UpdatedUtc);
        var loaded = Assert.Single(_store.List());
        Assert.Equal("SELECT fqdn FROM zones", loaded.Sql);
        Assert.Equal("new description", loaded.Description);
        Assert.Single(Files());
    }

    [Fact]
    public void Renaming_moves_the_file_and_keeps_everything_else()
    {
        var first = _store.Save(New("Old name", "SELECT 1", "kept"));

        var edit = _store.List().Single();
        edit.Name = "New name";
        var saved = _store.Save(edit);

        Assert.Equal("new-name", saved.Id);
        Assert.Equal(["new-name.json"], Files());
        var loaded = Assert.Single(_store.List());
        Assert.Equal(("New name", "kept", "SELECT 1"), (loaded.Name, loaded.Description, loaded.Sql));
        Assert.Equal(first.CreatedUtc, loaded.CreatedUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Changing_only_the_case_of_a_name_keeps_the_same_file()
    {
        _store.Save(New("my report"));

        var edit = _store.List().Single();
        edit.Name = "My Report";
        var saved = _store.Save(edit);

        Assert.Equal("my-report", saved.Id);
        Assert.Equal(["my-report.json"], Files());
        Assert.Equal("My Report", Assert.Single(_store.List()).Name);
    }

    [Fact]
    public void A_rename_cannot_take_another_templates_name()
    {
        _store.Save(New("One"));
        _store.Save(New("Two"));

        var two = _store.Find("Two")!;
        two.Name = "one";

        Assert.Throws<ArgumentException>(() => _store.Save(two));
        Assert.Equal(["One", "Two"], _store.List().Select(t => t.Name));
    }

    [Fact]
    public void Saving_the_same_template_as_a_new_one_makes_a_copy()
    {
        var original = _store.Save(New("Zones", "SELECT 1"));

        var copy = _store.Save(new ReportTemplate { Name = "Zones copy", Sql = original.Sql, Description = "a copy" });

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(["Zones", "Zones copy"], _store.List().Select(t => t.Name));
    }

    // ---- Deleting ---------------------------------------------------------------------

    [Fact]
    public void Deleting_removes_only_that_file_and_is_harmless_when_repeated()
    {
        var one = _store.Save(New("One"));
        _store.Save(New("Two"));

        _store.Delete(one.Id);
        _store.Delete(one.Id);

        Assert.Equal(["Two"], _store.List().Select(t => t.Name));
        Assert.Equal(["two.json"], Files());
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void A_template_id_cannot_reach_outside_the_folder(string id)
    {
        Assert.Throws<ArgumentException>(() => _store.Delete(id));
    }

    [Fact]
    public void Deleting_cannot_touch_files_outside_the_folder()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"pfr-outside-{Guid.NewGuid():N}.json");
        File.WriteAllText(outside, "{}");
        try
        {
            _store.Save(New("Inside"));
            Assert.Throws<ArgumentException>(() => _store.Delete("../" + Path.GetFileNameWithoutExtension(outside)));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // ---- Files from other people --------------------------------------------------------

    [Fact]
    public void A_hand_written_file_is_accepted_with_a_single_sql_string_comments_and_no_dates()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "handmade.json"), """
            {
              // written in a text editor
              "name": "Handmade",
              "sql": "SELECT host_name FROM members ORDER BY host_name",
            }
            """);

        var template = Assert.Single(_store.List());

        Assert.Equal("handmade", template.Id);
        Assert.Equal("Handmade", template.Name);
        Assert.Equal("", template.Description);
        Assert.Equal("SELECT host_name FROM members ORDER BY host_name", template.Sql);
    }

    [Fact]
    public void Unusable_files_are_skipped_and_described_and_the_good_ones_still_load()
    {
        Directory.CreateDirectory(_dir);
        _store.Save(New("Good"));
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, "no-name.json"), """{"sql": "SELECT 1"}""");
        File.WriteAllText(Path.Combine(_dir, "no-sql.json"), """{"name": "No SQL"}""");
        File.WriteAllText(Path.Combine(_dir, "array.json"), "[1, 2]");
        File.WriteAllText(Path.Combine(_dir, "writes.json"), """{"name": "Writes", "sql": "DELETE FROM members"}""");
        File.WriteAllText(Path.Combine(_dir, "newer.json"), """{"format": 99, "name": "Newer", "sql": "SELECT 1"}""");

        var problems = new List<string>();
        var templates = _store.List(problems);

        Assert.Equal(["Good"], templates.Select(t => t.Name));
        Assert.Equal(6, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("broken.json:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("no-name.json:", StringComparison.Ordinal) && p.Contains("no name"));
        Assert.Contains(problems, p => p.StartsWith("no-sql.json:", StringComparison.Ordinal) && p.Contains("no SQL"));
        Assert.Contains(problems, p => p.StartsWith("writes.json:", StringComparison.Ordinal) && p.Contains("read-only"));
        Assert.Contains(problems, p => p.StartsWith("newer.json:", StringComparison.Ordinal) && p.Contains("newer version"));
    }

    [Fact]
    public void Temporary_and_hidden_files_and_other_file_types_are_ignored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "half.json.tmp"), "{ partly written");
        File.WriteAllText(Path.Combine(_dir, ".hidden.json"), """{"name": "Hidden", "sql": "SELECT 1"}""");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a template");

        var problems = new List<string>();
        Assert.Empty(_store.List(problems));
        Assert.Empty(problems);
    }

    [Fact]
    public void A_template_saved_by_one_store_is_found_by_another_pointing_at_the_same_folder()
    {
        _store.Save(New("Shared", "SELECT 1", "passed on to a colleague"));

        var colleague = new ReportTemplateStore(_dir);

        Assert.Equal("passed on to a colleague", Assert.Single(colleague.List()).Description);
    }
}
