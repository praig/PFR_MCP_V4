using System.Text;
using System.Text.Json;
using Pfr.Core.Nios;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;
using Pfr.Core.Tests.Nios;

namespace Pfr.Core.Tests.Query;

public class QueryParametersTests
{
    private static Dictionary<string, string> Values(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    // ---- Finding parameters ---------------------------------------------------------

    [Fact]
    public void Parameters_are_found_once_each_in_order_with_their_prefix()
    {
        const string sql = "SELECT :b, @a, :b, $c FROM t WHERE x = :b AND y = @a";

        Assert.Equal([":b", "@a", "$c"], QueryParameters.FindTokens(sql));
        Assert.Equal(["b", "a", "c"], QueryParameters.FindNames(sql));
    }

    [Fact]
    public void The_same_name_with_different_prefixes_is_one_parameter_with_several_tokens()
    {
        Assert.Equal([":a", "@a"], QueryParameters.FindTokens("SELECT :a, @a"));
        Assert.Equal(["a"], QueryParameters.FindNames("SELECT :a, @a"));
    }

    [Theory]
    [InlineData("SELECT ':a' FROM t")]
    [InlineData("SELECT 'it''s :a' FROM t")]
    [InlineData("SELECT \":a\" FROM t")]
    [InlineData("SELECT [:a] FROM t")]
    [InlineData("SELECT `:a` FROM t")]
    [InlineData("SELECT 1 -- :a")]
    [InlineData("/* :a */ SELECT 1")]
    [InlineData("SELECT a$b FROM t")]            // $ inside a name
    [InlineData("SELECT '10:30' AS time")]
    [InlineData("SELECT 1 AS x:")]               // a colon with no name after it
    [InlineData("SELECT 1 WHERE 2 > 1")]
    public void Things_that_only_look_like_parameters_are_not(string sql) => Assert.Empty(QueryParameters.FindTokens(sql));

    [Fact]
    public void A_positional_question_mark_is_refused_but_one_in_a_string_is_fine()
    {
        var ex = Assert.Throws<QueryException>(() => QueryParameters.FindTokens("SELECT * FROM t WHERE a = ?"));
        Assert.Contains(":name", ex.Message);

        Assert.Empty(QueryParameters.FindTokens("SELECT 'why?' AS q -- really?"));
    }

    [Theory]
    [InlineData("network", true)]
    [InlineData("_x1", true)]
    [InlineData("A", true)]
    [InlineData("", false)]
    [InlineData("1abc", false)]
    [InlineData("a-b", false)]
    [InlineData("a b", false)]
    [InlineData("é", false)]
    public void Parameter_names_are_plain_identifiers(string name, bool valid) => Assert.Equal(valid, QueryParameters.IsValidName(name));

    [Fact]
    public void A_name_can_be_at_most_fifty_characters()
    {
        Assert.True(QueryParameters.IsValidName(new string('a', QueryParameters.MaxNameLength)));
        Assert.False(QueryParameters.IsValidName(new string('a', QueryParameters.MaxNameLength + 1)));
    }

    // ---- Turning typed values into bound values ----------------------------------------

    [Fact]
    public void Text_numbers_and_choices_are_bound_as_what_they_are()
    {
        var definitions = new List<ReportParameter>
        {
            new() { Name = "zone", Label = "Zone" },
            new() { Name = "days", Type = ParameterType.Number },
            new() { Name = "ratio", Type = ParameterType.Number },
            new() { Name = "kind", Type = ParameterType.Choice, Options = ["A", "AAAA", "CNAME"] },
        };

        var bound = QueryParameters.Bind(definitions, Values(("zone", "  lab.test "), ("days", "30"), ("ratio", "0.5"), ("kind", "aaaa")));

        Assert.Equal("lab.test", bound["zone"]);   // trimmed
        Assert.Equal(30L, bound["days"]);          // a whole number is an integer
        Assert.Equal(0.5, bound["ratio"]);
        Assert.Equal("AAAA", bound["kind"]);       // the option as written, whatever the case typed
    }

    [Fact]
    public void An_empty_optional_value_is_null_and_an_empty_required_one_is_an_error()
    {
        var definitions = new List<ReportParameter>
        {
            new() { Name = "needed", Label = "Needed one" },
            new() { Name = "spare", Required = false },
        };

        var ex = Assert.Throws<QueryException>(() => QueryParameters.Bind(definitions, Values(("needed", "  "))));
        Assert.Contains("Needed one", ex.Message);

        var bound = QueryParameters.Bind(definitions, Values(("needed", "x")));
        Assert.Null(bound["spare"]);
        Assert.True(bound.ContainsKey("spare"));
    }

    [Fact]
    public void A_number_must_be_a_number_and_a_choice_must_be_an_option()
    {
        var definitions = new List<ReportParameter>
        {
            new() { Name = "days", Label = "Days", Type = ParameterType.Number },
            new() { Name = "kind", Label = "Kind", Type = ParameterType.Choice, Options = ["A", "AAAA"] },
        };

        Assert.Contains("must be a number", Assert.Throws<QueryException>(() =>
            QueryParameters.Bind(definitions, Values(("days", "soon"), ("kind", "A")))).Message);

        var ex = Assert.Throws<QueryException>(() => QueryParameters.Bind(definitions, Values(("days", "1"), ("kind", "MX"))));
        Assert.Contains("must be one of: A, AAAA", ex.Message);
    }

    [Fact]
    public void Values_for_parameters_that_do_not_exist_are_refused_and_the_real_names_listed()
    {
        var definitions = new List<ReportParameter> { new() { Name = "zone" }, new() { Name = "kind" } };

        var ex = Assert.Throws<QueryException>(() => QueryParameters.Bind(definitions, Values(("zon", "x"))));
        Assert.Contains("“zon”", ex.Message);
        Assert.Contains("zone, kind", ex.Message);

        Assert.Contains("takes none", Assert.Throws<QueryException>(() =>
            QueryParameters.Bind([], Values(("x", "1")))).Message);
    }

    [Fact]
    public void Resolve_starts_from_the_defaults_and_applies_what_was_supplied()
    {
        var definitions = new List<ReportParameter>
        {
            new() { Name = "network", Default = "10.0.0.0/8" },
            new() { Name = "kind", Type = ParameterType.Choice, Options = ["A", "AAAA"], Default = "A" },
        };

        Assert.Equal("10.0.0.0/8", QueryParameters.Resolve(definitions, Values())["network"]);

        var bound = QueryParameters.Resolve(definitions, Values(("kind", "AAAA")));
        Assert.Equal(("10.0.0.0/8", "AAAA"), ((string)bound["network"]!, (string)bound["kind"]!));

        Assert.Throws<QueryException>(() => QueryParameters.Resolve(definitions, Values(("nope", "1"))));
    }

    [Fact]
    public void A_required_parameter_with_no_default_must_be_supplied()
    {
        var definitions = new List<ReportParameter> { new() { Name = "zone", Label = "Zone" } };

        Assert.Throws<QueryException>(() => QueryParameters.Resolve(definitions, Values()));
        Assert.Equal("lab.test", QueryParameters.Resolve(definitions, Values(("zone", "lab.test")))["zone"]);
    }

    [Fact]
    public void A_parameter_typed_into_a_query_is_optional_text()
    {
        var adHoc = QueryParameters.AdHoc("x");
        Assert.Equal(("x", false, ParameterType.Text), (adHoc.Name, adHoc.Required, adHoc.Type));
    }
}

public sealed class ParameterQueryTests : IDisposable
{
    private readonly GridInventory _inventory = BackupParser.Parse(OneDbFixture.XmlStream());
    private readonly SnapshotDatabase _db;

    public ParameterQueryTests() => _db = SnapshotDatabase.Load(_inventory);

    public void Dispose() => _db.Dispose();

    private static Dictionary<string, object?> P(params (string Name, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void A_text_parameter_filters_rows()
    {
        var result = _db.Execute("SELECT host_name FROM members WHERE host_name = :host", parameters: P(("host", "gm.lab.test")));
        Assert.Equal("gm.lab.test", Assert.Single(result.Rows)[0]);
    }

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("x'; DELETE FROM members; --")]
    [InlineData("\" OR \"\"=\"")]
    [InlineData("gm.lab.test' UNION SELECT 1 --")]
    public void A_value_that_looks_like_sql_is_only_ever_data(string value)
    {
        var result = _db.Execute("SELECT host_name FROM members WHERE host_name = :host", parameters: P(("host", value)));

        Assert.Empty(result.Rows);
        Assert.Equal(_inventory.Members.Count, Assert.IsType<long>(_db.Execute("SELECT COUNT(*) FROM members").Rows[0][0]));
    }

    [Fact]
    public void Numbers_are_bound_as_numbers()
    {
        var limited = _db.Execute("SELECT oid FROM members ORDER BY oid LIMIT :n", parameters: P(("n", 2L)));
        Assert.Equal(2, limited.Rows.Count);

        var comparison = _db.Execute("SELECT COUNT(*) FROM members WHERE oid > :min", parameters: P(("min", 7L)));
        Assert.Equal((long)_inventory.Members.Count(m => m.Oid > 7), comparison.Rows[0][0]);
    }

    [Fact]
    public void A_network_parameter_gives_the_same_answer_as_the_literal_would()
    {
        Assert.True(IpPrefix.TryParse("10.0.0.0", 24, out var prefix));
        var expected = _inventory.Records.Count(r => r.Type == DnsRecordType.A && System.Net.IPAddress.TryParse(r.Value, out var ip) && prefix.Covers(ip));

        var result = _db.Execute("SELECT COUNT(*) FROM records WHERE type = :type AND cidr_contains(:network, value)",
            parameters: P(("type", "A"), ("network", "10.0.0.0/24")));

        Assert.Equal((long)expected, result.Rows[0][0]);
        Assert.True(expected > 0);
    }

    [Fact]
    public void Null_is_a_value_and_lets_a_query_make_a_parameter_optional()
    {
        var all = _db.Execute("SELECT COUNT(*) FROM members WHERE (:host IS NULL OR host_name = :host)", parameters: P(("host", null)));
        Assert.Equal((long)_inventory.Members.Count, all.Rows[0][0]);

        var one = _db.Execute("SELECT COUNT(*) FROM members WHERE (:host IS NULL OR host_name = :host)", parameters: P(("host", "gm.lab.test")));
        Assert.Equal(1L, one.Rows[0][0]);
    }

    [Fact]
    public void The_same_name_with_different_prefixes_gets_the_same_value()
    {
        var row = Assert.Single(_db.Execute("SELECT :a, @a, $a", parameters: P(("a", "v"))).Rows);
        Assert.Equal(["v", "v", "v"], row.Select(c => (string)c!));
    }

    [Fact]
    public void A_parameter_without_a_value_is_an_error_that_names_it()
    {
        var ex = Assert.Throws<QueryException>(() => _db.Execute("SELECT :zone"));
        Assert.Contains(":zone", ex.Message);

        Assert.Throws<QueryException>(() => _db.Execute("SELECT :zone", parameters: P(("other", "x"))));
    }

    [Fact]
    public void Extra_values_and_parameter_looking_text_inside_strings_do_no_harm()
    {
        var result = _db.Execute("SELECT ':nope' AS s", parameters: P(("unused", "x")));
        Assert.Equal(":nope", Assert.Single(result.Rows)[0]);
    }

    [Fact]
    public void A_positional_placeholder_is_refused_with_advice()
    {
        var ex = Assert.Throws<QueryException>(() => _db.Execute("SELECT * FROM members WHERE oid = ?"));
        Assert.Contains(":name", ex.Message);
    }
}

public sealed class ReportTemplateParameterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pfr-tpl-params-{Guid.NewGuid():N}");
    private readonly ReportTemplateStore _store;

    public ReportTemplateParameterTests() => _store = new ReportTemplateStore(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static ReportTemplate New(string sql, params ReportParameter[] parameters) =>
        new() { Name = "With parameters", Sql = sql, Parameters = [.. parameters] };

    private const string TwoParameterSql = "SELECT fqdn, value FROM records WHERE type = :type AND cidr_contains(:network, value)";

    private static ReportParameter[] TwoParameters() =>
    [
        new() { Name = "type", Label = "Record type", Type = ParameterType.Choice, Options = ["A", "AAAA"], Default = "A", Description = "Which records." },
        new() { Name = "network", Label = "Network", Default = "10.0.0.0/8" },
    ];

    [Fact]
    public void Parameters_round_trip_through_the_file()
    {
        var saved = _store.Save(New(TwoParameterSql, TwoParameters()));

        var loaded = Assert.Single(_store.List());
        Assert.Equal(saved.Id, loaded.Id);
        var type = loaded.Parameters[0];
        Assert.Equal(("type", "Record type", "Which records.", ParameterType.Choice, "A", true), (type.Name, type.Label, type.Description, type.Type, type.Default, type.Required));
        Assert.Equal(["A", "AAAA"], type.Options);
        var network = loaded.Parameters[1];
        Assert.Equal(("network", ParameterType.Text, "10.0.0.0/8"), (network.Name, network.Type, network.Default));
    }

    [Fact]
    public void The_file_lists_the_parameters_readably_before_the_sql()
    {
        var spare = new ReportParameter { Name = "limit", Type = ParameterType.Number, Required = false, Default = "50" };
        _store.Save(New("SELECT * FROM records LIMIT :limit", spare));

        var text = File.ReadAllText(Directory.GetFiles(_dir, "*.json").Single());

        Assert.Contains("\"parameters\": [", text);
        Assert.Contains("\"name\": \"limit\"", text);
        Assert.Contains("\"type\": \"number\"", text);
        Assert.Contains("\"default\": \"50\"", text);
        Assert.Contains("\"required\": false", text);
        Assert.True(text.IndexOf("\"parameters\"", StringComparison.Ordinal) < text.IndexOf("\"sql\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_is_trimmed_and_options_are_cleaned_up()
    {
        var parameter = new ReportParameter
        {
            Name = " kind ", Label = "  Kind ", Type = ParameterType.Choice, Default = " a ",
            Options = [" A", "a", "", "AAAA ", "A"],
        };

        var saved = _store.Save(New("SELECT :kind", parameter));

        var p = Assert.Single(saved.Parameters);
        Assert.Equal(("kind", "Kind", "a"), (p.Name, p.Label, p.Default));
        Assert.Equal(["A", "AAAA"], p.Options); // blanks and repeats (ignoring case) dropped
    }

    [Fact]
    public void Every_name_in_the_sql_must_be_declared()
    {
        var ex = Assert.Throws<ArgumentException>(() => _store.Save(New("SELECT :a, :b", new ReportParameter { Name = "a" })));
        Assert.Contains(":b", ex.Message);
        Assert.Contains("no parameter named", ex.Message);
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []);
    }

    [Fact]
    public void Every_declared_parameter_must_be_used()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _store.Save(New("SELECT :a", new ReportParameter { Name = "a" }, new ReportParameter { Name = "extra" })));
        Assert.Contains("“extra”", ex.Message);
        Assert.Contains("not used", ex.Message);
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("a-b")]
    [InlineData("a b")]
    [InlineData("")]
    public void Names_must_be_plain_identifiers(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => _store.Save(New("SELECT 1", new ReportParameter { Name = name })));
        Assert.Contains("not a valid parameter name", ex.Message);
    }

    [Fact]
    public void A_parameter_cannot_be_declared_twice()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _store.Save(New("SELECT :a", new ReportParameter { Name = "a" }, new ReportParameter { Name = "a" })));
        Assert.Contains("declared twice", ex.Message);
    }

    [Fact]
    public void A_choice_needs_options_and_its_default_must_be_one_of_them()
    {
        var none = new ReportParameter { Name = "k", Type = ParameterType.Choice };
        Assert.Contains("at least one option", Assert.Throws<ArgumentException>(() => _store.Save(New("SELECT :k", none))).Message);

        var wrong = new ReportParameter { Name = "k", Type = ParameterType.Choice, Options = ["A", "AAAA"], Default = "MX" };
        Assert.Contains("must be one of its options", Assert.Throws<ArgumentException>(() => _store.Save(New("SELECT :k", wrong))).Message);
    }

    [Fact]
    public void A_numbers_default_must_be_a_number()
    {
        var bad = new ReportParameter { Name = "n", Type = ParameterType.Number, Default = "lots" };
        Assert.Contains("must be a number", Assert.Throws<ArgumentException>(() => _store.Save(New("SELECT :n", bad))).Message);

        _store.Save(New("SELECT :n", new ReportParameter { Name = "n", Type = ParameterType.Number, Default = "2.5" }));
    }

    [Fact]
    public void There_is_a_limit_on_how_many_parameters_a_report_can_have()
    {
        var names = Enumerable.Range(0, ReportTemplateStore.MaxParameters + 1).Select(i => $"p{i}").ToList();
        var sql = "SELECT " + string.Join(", ", names.Select(n => ":" + n));

        var ex = Assert.Throws<ArgumentException>(() => _store.Save(New(sql, names.Select(n => new ReportParameter { Name = n }).ToArray())));
        Assert.Contains("at most", ex.Message);
    }

    [Fact]
    public void A_positional_placeholder_cannot_be_saved()
    {
        var ex = Assert.Throws<QueryException>(() => _store.Save(New("SELECT * FROM members WHERE oid = ?")));
        Assert.Contains(":name", ex.Message);
    }

    [Fact]
    public void Editing_keeps_the_parameters_unless_they_are_changed()
    {
        var saved = _store.Save(New(TwoParameterSql, TwoParameters()));

        var edit = _store.List().Single();
        edit.Description = "now described";
        var again = _store.Save(edit);
        Assert.Equal(2, again.Parameters.Count);

        edit = _store.List().Single();
        edit.Sql = "SELECT fqdn FROM records WHERE cidr_contains(:network, value)";
        edit.Parameters.RemoveAll(p => p.Name == "type");
        var changed = _store.Save(edit);

        Assert.Equal(saved.Id, changed.Id);
        Assert.Equal(["network"], Assert.Single(_store.List()).Parameters.Select(p => p.Name));
    }

    [Fact]
    public void A_hand_written_file_can_use_plain_numbers_any_case_and_leave_things_out()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "handmade.json"), """
            {
              "name": "Handmade",
              "parameters": [
                { "name": "days", "type": "NUMBER", "default": 30, "required": false },
                { "name": "who" }
              ],
              "sql": "SELECT * FROM licenses WHERE :days > 0 OR :who IS NULL"
            }
            """);

        var template = Assert.Single(_store.List());

        var days = template.Parameters[0];
        Assert.Equal((ParameterType.Number, "30", false), (days.Type, days.Default, days.Required));
        var who = template.Parameters[1];
        Assert.Equal((ParameterType.Text, "", true, "who"), (who.Type, who.Default, who.Required, who.DisplayLabel));
    }

    [Fact]
    public void Files_with_inconsistent_or_unknown_parameters_are_skipped_and_explained()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "undeclared.json"), """{"name": "Undeclared", "sql": "SELECT :x"}""");
        File.WriteAllText(Path.Combine(_dir, "unused.json"), """{"name": "Unused", "parameters": [{"name": "x"}], "sql": "SELECT 1"}""");
        File.WriteAllText(Path.Combine(_dir, "badtype.json"), """{"name": "Bad type", "parameters": [{"name": "x", "type": "date"}], "sql": "SELECT :x"}""");
        File.WriteAllText(Path.Combine(_dir, "notlist.json"), """{"name": "Not a list", "parameters": {"name": "x"}, "sql": "SELECT :x"}""");
        File.WriteAllText(Path.Combine(_dir, "question.json"), """{"name": "Question", "sql": "SELECT ?"}""");

        var problems = new List<string>();
        Assert.Empty(_store.List(problems));

        Assert.Equal(5, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("undeclared.json:", StringComparison.Ordinal) && p.Contains("no parameter named"));
        Assert.Contains(problems, p => p.StartsWith("unused.json:", StringComparison.Ordinal) && p.Contains("not used"));
        Assert.Contains(problems, p => p.StartsWith("badtype.json:", StringComparison.Ordinal) && p.Contains("unknown parameter type"));
        Assert.Contains(problems, p => p.StartsWith("notlist.json:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("question.json:", StringComparison.Ordinal) && p.Contains(":name"));
    }

    [Fact]
    public void Parameters_travel_with_a_shared_file()
    {
        _store.Save(New(TwoParameterSql, TwoParameters()));

        var colleague = new ReportTemplateStore(_dir).List().Single();

        Assert.Equal(["type", "network"], colleague.Parameters.Select(p => p.Name));
    }
}

public sealed class ResultExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pfr-export-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static QueryResult Sample() => new(
        ["name", "value", "n"],
        [
            new object?[] { "plain", "10.0.0.1", 1L },
            new object?[] { "formula", "=1+1", -5L },
            new object?[] { "plus", "+cmd", 2.5 },
            new object?[] { "minus", "-all", null },
            new object?[] { "at", "@sum(A1)", 3L },
            new object?[] { "tab", "\tx", 4L },
            new object?[] { "ünïcode", "é, \"quoted\"", 5L },
        ],
        truncated: false,
        TimeSpan.FromMilliseconds(3));

    [Fact]
    public void Spreadsheet_safe_csv_defuses_cells_that_would_run_as_formulas()
    {
        var lines = Sample().ToCsv(spreadsheetSafe: true).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("name,value,n", lines[0]);                 // the header is left alone
        Assert.Equal("plain,10.0.0.1,1", lines[1]);
        Assert.Equal("formula,'=1+1,-5", lines[2]);             // text gets a leading apostrophe; the number -5 does not
        Assert.Equal("plus,'+cmd,2.5", lines[3]);
        Assert.Equal("minus,'-all,", lines[4]);                 // and NULL stays an empty field
        Assert.Equal("at,'@sum(A1),3", lines[5]);
        Assert.StartsWith("tab,'\tx,4", lines[6]);
        Assert.Equal("ünïcode,\"é, \"\"quoted\"\"\",5", lines[7]);
    }

    [Fact]
    public void Plain_csv_leaves_everything_as_it_is()
    {
        var lines = Sample().ToCsv().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("formula,=1+1,-5", lines[2]);
        Assert.Equal("minus,-all,", lines[4]);
    }

    [Fact]
    public void A_csv_file_starts_with_a_byte_order_mark_so_excel_reads_it_as_utf8()
    {
        var path = Path.Combine(_dir, "out.csv");

        var format = ResultExport.Write(Sample(), path, spreadsheetSafe: true);

        Assert.Equal(ExportFormat.Csv, format);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("name,value,n", text);
        Assert.Contains("ünïcode", text);
        Assert.Contains("formula,'=1+1,-5", text);
    }

    [Fact]
    public void A_json_file_has_no_byte_order_mark_and_real_types()
    {
        var path = Path.Combine(_dir, "out.JSON"); // the extension is matched ignoring case

        var format = ResultExport.Write(Sample(), path, spreadsheetSafe: true);

        Assert.Equal(ExportFormat.Json, format);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'[', bytes[0]);
        using var doc = JsonDocument.Parse(bytes);
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(7, rows.Count);
        Assert.Equal(-5, rows[1].GetProperty("n").GetInt32());
        Assert.Equal(JsonValueKind.Null, rows[3].GetProperty("n").ValueKind);
        Assert.Equal("=1+1", rows[1].GetProperty("value").GetString()); // JSON is data: nothing is added to it
    }

    [Theory]
    [InlineData("results.csv", ExportFormat.Csv)]
    [InlineData("RESULTS.CSV", ExportFormat.Csv)]
    [InlineData("results.json", ExportFormat.Json)]
    [InlineData("results.Json", ExportFormat.Json)]
    [InlineData("results.txt", ExportFormat.Csv)]
    [InlineData("results", ExportFormat.Csv)]
    public void The_extension_chooses_the_format_and_anything_unknown_is_csv(string name, ExportFormat expected) =>
        Assert.Equal(expected, ResultExport.FormatFor(name));

    [Fact]
    public void Missing_folders_are_created_existing_files_replaced_and_no_temporary_file_is_left()
    {
        var path = Path.Combine(_dir, "deep", "er", "out.csv");

        ResultExport.Write(Sample(), path, spreadsheetSafe: false);
        var first = File.ReadAllText(path);
        ResultExport.Write(new QueryResult(["only"], [new object?[] { "one" }], false, TimeSpan.Zero), path, spreadsheetSafe: false);

        Assert.NotEqual(first, File.ReadAllText(path));
        Assert.Equal(["out.csv"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(f => Path.GetFileName(f)!));
    }

    [Fact]
    public void An_empty_result_still_gets_its_header()
    {
        var path = Path.Combine(_dir, "empty.csv");

        ResultExport.Write(new QueryResult(["a", "b"], [], false, TimeSpan.Zero), path, spreadsheetSafe: true);

        Assert.Equal("a,b", Encoding.UTF8.GetString(File.ReadAllBytes(path)[3..]).Trim());
    }
}
