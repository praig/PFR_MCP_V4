using Pfr.Core.Query;

namespace Pfr.Core.Tests.Query;

public class QueryBuilderTests
{
    private static ColumnAddResult Add(string sql, string table, string column) => QueryBuilder.AddColumn(sql, table, column);

    // ---- Starting a query ----

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void An_empty_editor_gets_a_new_query(string sql)
    {
        var result = Add(sql, "members", "host_name");

        Assert.Equal("SELECT host_name\nFROM members", result.Sql);
        Assert.Equal(ColumnAddOutcome.Started, result.Outcome);
    }

    [Fact]
    public void Names_are_matched_without_regard_to_case_and_written_as_the_schema_spells_them()
    {
        Assert.Equal("SELECT host_name\nFROM members", Add("", "MEMBERS", "HOST_NAME").Sql);
    }

    [Fact]
    public void An_unknown_table_or_column_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Add("", "nope", "x"));
        Assert.Throws<ArgumentException>(() => Add("", "members", "nope"));
    }

    // ---- The SELECT list ----

    [Fact]
    public void A_column_is_added_to_the_end_of_the_select_list()
    {
        var result = Add("SELECT host_name FROM members", "members", "role");

        Assert.Equal("SELECT host_name, role FROM members", result.Sql);
        Assert.Equal(ColumnAddOutcome.Added, result.Outcome);
        Assert.Equal("Added role to the SELECT list.", result.Message);
    }

    [Theory]
    [InlineData("SELECT host_name FROM members")]
    [InlineData("SELECT HOST_NAME FROM members")]
    [InlineData("SELECT members.host_name FROM members")]
    [InlineData("SELECT m.host_name FROM members m")]
    [InlineData("SELECT m.host_name FROM members AS m")]
    [InlineData("SELECT \"host_name\" FROM members")]
    [InlineData("SELECT host_name AS h FROM members")]
    [InlineData("SELECT host_name h FROM members")]
    [InlineData("SELECT oid, host_name FROM members")]
    [InlineData("SELECT m.* FROM members m")]
    public void A_column_already_selected_is_left_alone(string sql)
    {
        var result = Add(sql, "members", "host_name");

        Assert.Equal(sql, result.Sql);
        Assert.Equal(ColumnAddOutcome.AlreadyThere, result.Outcome);
    }

    [Fact]
    public void A_lone_star_is_replaced_by_the_column()
    {
        var result = Add("SELECT * FROM members LIMIT 100", "members", "host_name");

        Assert.Equal("SELECT host_name FROM members LIMIT 100", result.Sql);
        Assert.Equal(ColumnAddOutcome.ReplacedStar, result.Outcome);
    }

    [Fact]
    public void DISTINCT_is_kept()
    {
        Assert.Equal("SELECT DISTINCT type, fqdn FROM records", Add("SELECT DISTINCT type FROM records", "records", "fqdn").Sql);
        Assert.Equal(ColumnAddOutcome.AlreadyThere, Add("SELECT DISTINCT type FROM records", "records", "type").Outcome);
    }

    [Fact]
    public void Keywords_may_be_in_any_case()
    {
        Assert.Equal("select host_name, role from members", Add("select host_name from members", "members", "role").Sql);
    }

    [Fact]
    public void Other_clauses_are_left_exactly_as_they_were()
    {
        var result = Add("SELECT host_name FROM members WHERE role = 'Member' ORDER BY host_name", "members", "role");

        Assert.Equal("SELECT host_name, role FROM members WHERE role = 'Member' ORDER BY host_name", result.Sql);
    }

    [Fact]
    public void A_trailing_semicolon_stays_at_the_end()
    {
        Assert.Equal("SELECT host_name, role FROM members;", Add("SELECT host_name FROM members;", "members", "role").Sql);
    }

    [Fact]
    public void Comments_and_strings_are_not_mistaken_for_sql()
    {
        Assert.Equal("SELECT host_name, role -- from here\nFROM members",
            Add("SELECT host_name -- from here\nFROM members", "members", "role").Sql);
        Assert.Equal("SELECT 'FROM x' AS s, role FROM members",
            Add("SELECT 'FROM x' AS s FROM members", "members", "role").Sql);
    }

    [Fact]
    public void A_subquery_in_the_select_list_does_not_confuse_it()
    {
        var result = Add("SELECT (SELECT COUNT(*) FROM nodes) AS n FROM members", "members", "role");

        Assert.Equal("SELECT (SELECT COUNT(*) FROM nodes) AS n, role FROM members", result.Sql);
    }

    // ---- The FROM clause ----

    [Fact]
    public void A_table_that_can_be_joined_is_joined_on_its_key()
    {
        var result = Add("SELECT host_name FROM members", "member_services", "service");

        // The column already selected is qualified, since a bare name could now be ambiguous.
        Assert.Equal(
            "SELECT members.host_name, member_services.service FROM members\nJOIN member_services ON member_services.member_oid = members.oid",
            result.Sql);
        Assert.Equal(ColumnAddOutcome.Added, result.Outcome);
        Assert.Contains("joined on member_oid", result.Message);
    }

    [Fact]
    public void A_join_uses_the_alias_the_existing_table_was_given()
    {
        var result = Add("SELECT m.host_name FROM members m", "nodes", "serial_number");

        Assert.Equal("SELECT m.host_name, nodes.serial_number FROM members m\nJOIN nodes ON nodes.member_oid = m.oid", result.Sql);
    }

    [Fact]
    public void The_join_goes_before_the_where_clause()
    {
        var result = Add("SELECT host_name FROM members WHERE role = 'Member' ORDER BY host_name", "nodes", "serial_number");

        Assert.Equal(
            "SELECT members.host_name, nodes.serial_number FROM members\nJOIN nodes ON nodes.member_oid = members.oid WHERE role = 'Member' ORDER BY host_name",
            result.Sql);
    }

    [Fact]
    public void A_table_with_no_known_key_is_added_with_a_comma_and_the_user_is_told()
    {
        var result = Add("SELECT host_name FROM members", "grid", "grid_name");

        Assert.Equal("SELECT members.host_name, grid.grid_name FROM members, grid", result.Sql);
        Assert.Contains("no known key", result.Message);
    }

    [Fact]
    public void A_query_with_no_from_gets_one()
    {
        var result = Add("SELECT 1", "members", "oid");

        Assert.Equal("SELECT 1, oid\nFROM members", result.Sql);
    }

    [Fact]
    public void A_star_over_another_table_is_replaced_and_the_table_joined()
    {
        var result = Add("SELECT * FROM members", "nodes", "serial_number");

        Assert.Equal("SELECT nodes.serial_number FROM members\nJOIN nodes ON nodes.member_oid = members.oid", result.Sql);
        Assert.Equal(ColumnAddOutcome.ReplacedStar, result.Outcome);
    }

    [Fact]
    public void Expressions_and_aliases_in_the_select_list_survive_a_join()
    {
        Assert.Equal(
            "SELECT COUNT(*), nodes.hardware_type FROM members\nJOIN nodes ON nodes.member_oid = members.oid",
            Add("SELECT COUNT(*) FROM members", "nodes", "hardware_type").Sql);
        Assert.Equal(
            "SELECT members.host_name AS h, nodes.position FROM members\nJOIN nodes ON nodes.member_oid = members.oid",
            Add("SELECT host_name AS h FROM members", "nodes", "position").Sql);
    }

    // ---- Tables already in the query ----

    [Fact]
    public void A_column_of_a_joined_table_is_qualified_with_its_alias()
    {
        const string sql = "SELECT m.host_name FROM members m JOIN member_services s ON s.member_oid = m.oid";

        Assert.Equal(
            "SELECT m.host_name, s.service FROM members m JOIN member_services s ON s.member_oid = m.oid",
            Add(sql, "member_services", "service").Sql);
    }

    [Fact]
    public void A_third_table_is_joined_to_one_already_there()
    {
        const string sql = "SELECT m.host_name FROM members m JOIN member_services s ON s.member_oid = m.oid";

        Assert.Equal(
            "SELECT m.host_name, nodes.serial_number FROM members m JOIN member_services s ON s.member_oid = m.oid\nJOIN nodes ON nodes.member_oid = m.oid",
            Add(sql, "nodes", "serial_number").Sql);
    }

    [Fact]
    public void A_star_over_two_tables_is_replaced_by_a_qualified_column()
    {
        Assert.Equal("SELECT members.role FROM members, nodes", Add("SELECT * FROM members, nodes", "members", "role").Sql);
    }

    [Fact]
    public void A_bare_name_that_two_tables_share_does_not_count_as_already_selected()
    {
        const string sql = "SELECT ipv4 FROM members JOIN nodes ON nodes.member_oid = members.oid";

        var result = Add(sql, "nodes", "ipv4");

        Assert.Equal(ColumnAddOutcome.Added, result.Outcome);
        Assert.Equal(sql.Replace("SELECT ipv4", "SELECT ipv4, nodes.ipv4"), result.Sql);
    }

    [Fact]
    public void Admin_tables_are_joined_on_group_and_role_names()
    {
        Assert.Equal(
            "SELECT admin_users.name, admin_groups.is_superuser FROM admin_users\nJOIN admin_groups ON admin_users.group_name = admin_groups.name",
            Add("SELECT name FROM admin_users", "admin_groups", "is_superuser").Sql);
        Assert.Equal(
            "SELECT admin_group_roles.group_name, admin_roles.is_predefined FROM admin_group_roles\nJOIN admin_roles ON admin_group_roles.role_name = admin_roles.name",
            Add("SELECT group_name FROM admin_group_roles", "admin_roles", "is_predefined").Sql);
    }

    // ---- Queries too involved to edit ----

    [Theory]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("SELECT host_name FROM members UNION SELECT host_name FROM members")]
    [InlineData("SELECT host_name FROM (SELECT * FROM members) m")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("EXPLAIN SELECT 1")]
    public void An_involved_query_is_not_edited_and_the_caller_is_given_text_to_insert(string sql)
    {
        var result = Add(sql, "members", "role");

        Assert.Equal(sql, result.Sql);
        Assert.Equal(ColumnAddOutcome.NotEditable, result.Outcome);
        Assert.Equal("members.role", result.Fallback);
    }
}
