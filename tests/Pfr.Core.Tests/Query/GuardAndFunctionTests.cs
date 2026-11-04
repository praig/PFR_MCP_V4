using Pfr.Core.Query;

namespace Pfr.Core.Tests.Query;

public class SqlGuardTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select * from records")]
    [InlineData("  \n SELECT 1  ")]
    [InlineData("WITH t AS (SELECT 1 AS a) SELECT a FROM t")]
    [InlineData("VALUES (1), (2)")]
    [InlineData("EXPLAIN QUERY PLAN SELECT 1")]
    [InlineData("-- a comment first\nSELECT 1")]
    [InlineData("/* a block comment */ SELECT 1")]
    public void Read_only_queries_are_accepted(string sql) => Assert.NotEmpty(SqlGuard.Check(sql));

    [Theory]
    [InlineData("SELECT 'DELETE FROM x; DROP TABLE y' AS text")]
    [InlineData("SELECT \"update\" FROM t")]
    [InlineData("SELECT [insert] FROM t")]
    [InlineData("SELECT * FROM t -- delete everything")]
    [InlineData("SELECT replace(fqdn, 'a', 'b') FROM records")] // REPLACE is a function here, not a statement
    [InlineData("SELECT CASE WHEN a = 1 THEN 'x' ELSE 'y' END FROM t")]
    [InlineData("SELECT release_date, created_by FROM t")] // whole words only
    [InlineData("SELECT 'it''s; fine' AS s")]
    public void Forbidden_words_inside_strings_names_comments_or_longer_words_are_fine(string sql) =>
        Assert.NotEmpty(SqlGuard.Check(sql));

    [Theory]
    [InlineData("INSERT INTO members VALUES (1)", "start with SELECT")]
    [InlineData("DELETE FROM members", "start with SELECT")]
    [InlineData("UPDATE members SET host_name = 'x'", "start with SELECT")]
    [InlineData("DROP TABLE members", "start with SELECT")]
    [InlineData("CREATE TABLE t (a)", "start with SELECT")]
    [InlineData("PRAGMA query_only = OFF", "start with SELECT")]
    [InlineData("ATTACH DATABASE 'x.db' AS x", "start with SELECT")]
    [InlineData("VACUUM", "start with SELECT")]
    [InlineData("WITH t AS (SELECT 1) INSERT INTO members SELECT * FROM t", "INSERT is not allowed")]
    [InlineData("WITH t AS (SELECT 1) DELETE FROM members", "DELETE is not allowed")]
    [InlineData("SELECT load_extension('evil')", "LOAD_EXTENSION is not allowed")]
    [InlineData("EXPLAIN DROP TABLE members", "DROP is not allowed")]
    public void Anything_that_could_change_things_is_refused(string sql, string expected)
    {
        var ex = Assert.Throws<QueryException>(() => SqlGuard.Check(sql));
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1; DELETE FROM members")]
    [InlineData("SELECT 1;\n-- fine\n; SELECT 2")]
    [InlineData("SELECT 1; 2")]
    public void Only_one_statement_is_allowed(string sql)
    {
        var ex = Assert.Throws<QueryException>(() => SqlGuard.Check(sql));
        Assert.Contains("Only one statement", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- just a comment")]
    [InlineData(";")]
    public void Nothing_to_run_is_reported(string sql) =>
        Assert.Contains("Type a query", Assert.Throws<QueryException>(() => SqlGuard.Check(sql)).Message);

    [Fact]
    public void Trailing_semicolons_and_spaces_are_removed()
    {
        Assert.Equal("SELECT 1", SqlGuard.Check("SELECT 1;"));
        Assert.Equal("SELECT 1", SqlGuard.Check("  SELECT 1 ;  \n"));
        Assert.Equal("SELECT 1", SqlGuard.Check("SELECT 1;;"));
    }

    [Fact]
    public void An_unterminated_string_is_left_for_the_database_to_complain_about()
    {
        // The guard only needs to be sure nothing hides after the string; SQLite reports the syntax error.
        Assert.NotEmpty(SqlGuard.Check("SELECT 'unterminated"));
    }
}

public class IpFunctionsTests
{
    [Fact]
    public void Keys_order_addresses_numerically_with_ipv4_before_ipv6()
    {
        var addresses = new[] { "10.0.0.10", "9.255.255.255", "10.0.0.2", "2001:db8::1", "::1", "192.168.0.1" };

        var ordered = addresses.OrderBy(a => IpFunctions.Key(a), StringComparer.Ordinal).ToArray();

        Assert.Equal(["9.255.255.255", "10.0.0.2", "10.0.0.10", "192.168.0.1", "::1", "2001:db8::1"], ordered);
    }

    [Fact]
    public void A_prefix_key_puts_a_network_before_its_subnets_and_ignores_nothing_else()
    {
        Assert.True(string.CompareOrdinal(IpFunctions.Key("10.0.0.0/8"), IpFunctions.Key("10.0.0.0/24")) < 0);
        Assert.True(string.CompareOrdinal(IpFunctions.Key("10.0.0.0/24"), IpFunctions.Key("10.0.1.0/24")) < 0);
        Assert.NotEqual(IpFunctions.Key("10.0.0.0"), IpFunctions.Key("10.0.0.0/32")); // a host vs a /32 are shown differently
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("10.0.0.0/33")]
    [InlineData("")]
    [InlineData(null)]
    public void Keys_of_things_that_are_not_addresses_are_null(string? text) => Assert.Null(IpFunctions.Key(text));

    [Theory]
    [InlineData("10.0.0.0/24", "10.0.0.5", 1)]
    [InlineData("10.0.0.0/24", "10.0.1.5", 0)]
    [InlineData("10.0.0.0/8", "10.200.1.1", 1)]
    [InlineData("10.0.0.0/8", "11.0.0.1", 0)]
    [InlineData("10.0.0.0/24", "10.0.0.0/25", 1)]   // a subnet is inside
    [InlineData("10.0.0.0/24", "10.0.0.0/16", 0)]   // a bigger network is not
    [InlineData("10.0.0.0/24", "10.0.0.0/24", 1)]   // itself
    [InlineData("10.0.0.4/30", "10.0.0.7", 1)]      // bits that don't fall on a byte boundary
    [InlineData("10.0.0.4/30", "10.0.0.8", 0)]
    [InlineData("0.0.0.0/0", "203.0.113.9", 1)]
    [InlineData("10.0.0.5", "10.0.0.5", 1)]         // a single address is a /32
    [InlineData("2001:db8::/32", "2001:db8:1::5", 1)]
    [InlineData("2001:db8::/32", "2001:db9::5", 0)]
    [InlineData("10.0.0.0/8", "2001:db8::1", 0)]    // different families never match
    public void Cidr_contains_checks_prefixes_bit_by_bit(string prefix, string other, int expected) =>
        Assert.Equal(expected, IpFunctions.Contains(prefix, other));

    [Theory]
    [InlineData("junk", "10.0.0.1")]
    [InlineData("10.0.0.0/24", "junk")]
    [InlineData(null, "10.0.0.1")]
    [InlineData("10.0.0.0/24", null)]
    public void Cidr_contains_is_null_when_either_side_is_not_valid(string? prefix, string? other) =>
        Assert.Null(IpFunctions.Contains(prefix, other));

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.1", "10.0.0.10", 1)]
    [InlineData("10.0.0.1", "10.0.0.1", "10.0.0.10", 1)]   // inclusive
    [InlineData("10.0.0.10", "10.0.0.1", "10.0.0.10", 1)]
    [InlineData("10.0.0.11", "10.0.0.1", "10.0.0.10", 0)]
    [InlineData("10.0.0.9", "10.0.0.1", "10.0.0.10", 1)]   // numerically, not as text
    [InlineData("10.0.1.0", "10.0.0.1", "10.0.0.255", 0)]
    [InlineData("2001:db8::5", "2001:db8::1", "2001:db8::ff", 1)]
    [InlineData("2001:db8::5", "10.0.0.1", "10.0.0.10", 0)]
    public void Ip_in_range_is_inclusive_and_numeric(string address, string start, string end, int expected) =>
        Assert.Equal(expected, IpFunctions.InRange(address, start, end));

    [Fact]
    public void Ip_in_range_is_null_when_something_is_not_an_address()
    {
        Assert.Null(IpFunctions.InRange("x", "10.0.0.1", "10.0.0.2"));
        Assert.Null(IpFunctions.InRange("10.0.0.1", null, "10.0.0.2"));
    }
}

public class QueryResultTests
{
    private static QueryResult Sample() => new(
        ["id", "note"],
        [new object?[] { 1L, "plain" }, new object?[] { null, "has, comma and \"quotes\"" }, new object?[] { 2.5, "x" }],
        truncated: false,
        TimeSpan.FromMilliseconds(12));

    [Fact]
    public void Values_are_formatted_for_people()
    {
        Assert.Equal("NULL", QueryResult.Format(null));
        Assert.Equal("42", QueryResult.Format(42L));
        Assert.Equal("2.5", QueryResult.Format(2.5));
        Assert.Equal("0.333333", QueryResult.Format(1.0 / 3));
        Assert.Equal("(blob, 3 bytes)", QueryResult.Format(new byte[] { 1, 2, 3 }));
        Assert.Equal("text", QueryResult.Format("text"));
    }

    [Fact]
    public void Text_is_an_aligned_table_with_a_summary()
    {
        var text = Sample().ToText();

        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("id", lines[0]);
        Assert.Contains("note", lines[0]);
        Assert.StartsWith("--", lines[1]);
        Assert.Contains("NULL", text);
        Assert.EndsWith("3 rows in 12 ms", lines[^1]);
    }

    [Fact]
    public void Csv_quotes_fields_that_need_it_and_leaves_null_empty()
    {
        var lines = Sample().ToCsv().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("id,note", lines[0]);
        Assert.Equal("1,plain", lines[1]);
        Assert.Equal(",\"has, comma and \"\"quotes\"\"\"", lines[2]);
        Assert.Equal("2.5,x", lines[3]);
    }

    [Fact]
    public void Json_is_an_array_of_objects_with_real_types_and_unique_names()
    {
        var result = new QueryResult(["n", "n", "v"], [new object?[] { 1L, "a", null }, new object?[] { 2L, "b", 1.5 }], false, TimeSpan.Zero);

        using var doc = System.Text.Json.JsonDocument.Parse(result.ToJson());

        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].GetProperty("n").GetInt32());
        Assert.Equal("a", rows[0].GetProperty("n_2").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rows[0].GetProperty("v").ValueKind);
        Assert.Equal(1.5, rows[1].GetProperty("v").GetDouble());
    }

    [Fact]
    public void A_cut_off_result_says_so()
    {
        var result = new QueryResult(["a"], [new object?[] { 1L }], truncated: true, TimeSpan.FromMilliseconds(5));
        Assert.Contains("more were cut off", result.Summary());
    }
}
