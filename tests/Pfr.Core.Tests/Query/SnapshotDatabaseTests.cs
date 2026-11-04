using Pfr.Core.Nios;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;
using Pfr.Core.Tests.Nios;

namespace Pfr.Core.Tests.Query;

public sealed class SnapshotDatabaseTests : IDisposable
{
    private readonly GridInventory _inventory = BackupParser.Parse(OneDbFixture.XmlStream());
    private readonly SnapshotDatabase _db;

    public SnapshotDatabaseTests() => _db = SnapshotDatabase.Load(_inventory);

    public void Dispose() => _db.Dispose();

    private long Scalar(string sql) => Assert.IsType<long>(Assert.Single(Assert.Single(_db.Execute(sql).Rows)));

    // ---- Loading ------------------------------------------------------------------

    [Fact]
    public void Every_collection_of_the_snapshot_becomes_a_table_with_the_same_number_of_rows()
    {
        var counts = _db.Tables.ToDictionary(t => t.Name, t => t.RowCount);

        Assert.Equal(1, counts["grid"]);
        Assert.Equal(_inventory.Members.Count, counts["members"]);
        Assert.Equal(_inventory.Members.Sum(m => m.Nodes.Count), counts["nodes"]);
        Assert.Equal(_inventory.Members.Sum(m => m.Services.Count), counts["member_services"]);
        Assert.Equal(_inventory.Licenses.Count, counts["licenses"]);
        Assert.Equal(_inventory.NetworkViews.Count, counts["network_views"]);
        Assert.Equal(_inventory.DnsViews.Count, counts["dns_views"]);
        Assert.Equal(_inventory.Zones.Count, counts["zones"]);
        Assert.Equal(_inventory.Networks.Count, counts["networks"]);
        Assert.Equal(_inventory.DhcpRanges.Count, counts["dhcp_ranges"]);
        Assert.Equal(_inventory.FailoverAssociations.Count, counts["failover"]);
        Assert.Equal(_inventory.Records.Count, counts["records"]);
        Assert.Equal(_inventory.Hosts.Sum(h => h.Addresses.Count), counts["hosts"]);
        Assert.Equal(_inventory.AdminUsers.Count, counts["admin_users"]);
        Assert.Equal(_inventory.AdminGroups.Count, counts["admin_groups"]);
        Assert.Equal(_inventory.AdminRoles.Count, counts["admin_roles"]);
        Assert.Equal(_inventory.AdminGroupRoles.Count, counts["admin_group_roles"]);
        Assert.Equal(_inventory.AdminPermissions.Count, counts["admin_permissions"]);
        Assert.Equal(_inventory.AdminAccessRights.Count, counts["admin_access"]);
        Assert.Equal(_inventory.AdminAuthMethods.Count, counts["admin_auth_methods"]);

        // And a count taken through SQL agrees with the number loaded.
        Assert.Equal(_inventory.Records.Count, Scalar("SELECT COUNT(*) FROM records"));
        Assert.Equal(_inventory.Members.Count, Scalar("SELECT COUNT(*) FROM members"));
    }

    [Fact]
    public void The_tables_can_be_described_without_loading_anything()
    {
        var described = SnapshotDatabase.Describe();

        Assert.Equal(_db.Tables.Select(t => t.Name), described.Select(t => t.Name));
        Assert.All(described, t => Assert.Equal(0, t.RowCount));
        Assert.All(described, t => Assert.NotEmpty(t.Description));
        Assert.All(described.SelectMany(t => t.Columns), c => Assert.NotEmpty(c.Description));
    }

    [Fact]
    public void Described_columns_are_the_columns_the_database_really_has()
    {
        foreach (var table in _db.Tables)
        {
            var result = _db.Execute($"SELECT * FROM {table.Name} LIMIT 0");
            Assert.Equal(table.Columns.Select(c => c.Name), result.Columns);
        }
    }

    [Fact]
    public void Values_arrive_with_the_right_types_and_nulls()
    {
        var gm = _db.Execute("SELECT oid, host_name, is_grid_master, role, ipv6 FROM members WHERE is_grid_master = 1").Rows.Single();

        Assert.IsType<long>(gm[0]);
        Assert.Equal("gm.lab.test", gm[1]);
        Assert.Equal(1L, gm[2]);
        Assert.Equal("Grid Master", gm[3]);
        Assert.Null(gm[4]);
    }

    [Fact]
    public void Tables_join_through_their_ids_and_keys()
    {
        // Every service row belongs to a member.
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM member_services s LEFT JOIN members m ON m.oid = s.member_oid WHERE m.oid IS NULL"));
        // Joining records to their zone keeps every record whose zone is known.
        var joined = Scalar("SELECT COUNT(*) FROM records r JOIN zones z ON z.zone_key = r.zone_key");
        Assert.Equal(_inventory.Records.Count(r => _inventory.Zones.Any(z => z.Key == r.ZoneKey)), joined);
    }

    [Fact]
    public void Ha_members_have_a_row_per_node_and_their_names_are_carried_along()
    {
        var rows = _db.Execute("SELECT member_host_name, position FROM nodes WHERE member_host_name = 'ns1.lab.test' ORDER BY position").Rows;
        Assert.Equal(2, rows.Count);
        Assert.Equal([0L, 1L], rows.Select(r => (long)r[1]!));
    }

    // ---- Administrators ------------------------------------------------------------------

    [Fact]
    public void Administrators_join_to_their_groups_roles_and_permissions()
    {
        // Superusers are the accounts whose group is a superuser group.
        var superusers = _db.Execute(
            "SELECT u.name FROM admin_users u JOIN admin_groups g ON g.name = u.group_name WHERE g.is_superuser = 1 ORDER BY u.name").Rows;
        Assert.Equal(["$SYSTEM-CLOUD$", "admin"], superusers.Select(r => (string)r[0]!));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM admin_users WHERE is_superuser = 1"));

        // The count columns agree with the rows they summarise.
        Assert.Equal(2L, Scalar("SELECT user_count FROM admin_groups WHERE name = 'admin-group'"));
        Assert.Equal(2L, Scalar("SELECT role_count FROM admin_groups WHERE name = 'helpdesk'"));
        Assert.Equal(2L, Scalar("SELECT group_count FROM admin_roles WHERE name = 'readonly'"));

        // A group gets the permissions of its roles: helpdesk has DNS Admin (2 rules on the zone) and readonly (1).
        Assert.Equal(3L, Scalar(
            "SELECT COUNT(*) FROM admin_group_roles gr JOIN admin_permissions p ON p.owner_type = 'role' AND p.owner_name = gr.role_name "
            + "WHERE gr.group_name = 'helpdesk' AND p.resource_type = 'dns.zone'"));

        // Dates are text like the other tables'.
        var login = _db.Execute("SELECT last_login FROM admin_users WHERE name = 'admin'").Rows.Single()[0];
        Assert.Equal("2026-02-10", login);
    }

    [Fact]
    public void Permissions_join_to_the_zones_and_networks_they_are_about()
    {
        var zone = _db.Execute(
            "SELECT DISTINCT z.fqdn FROM admin_permissions p JOIN zones z ON p.resource_type = 'dns.zone' AND z.zone_key = p.resource_key").Rows;
        Assert.Equal("lab.test", Assert.Single(zone)[0]);

        var network = _db.Execute(
            "SELECT DISTINCT n.prefix FROM admin_permissions p JOIN networks n ON p.resource_type = 'dns.network_container' AND n.network_key = p.resource_key").Rows;
        Assert.Equal("10.0.0.0/8", Assert.Single(network)[0]);
    }

    [Fact]
    public void Accounts_with_no_group_and_rules_for_other_things_do_not_break_the_joins()
    {
        // "orphan" has no group: it appears with a NULL group and is not a superuser.
        var orphan = _db.Execute("SELECT group_name, is_superuser FROM admin_users WHERE name = 'orphan'").Rows.Single();
        Assert.Null(orphan[0]);
        Assert.Equal(0L, orphan[1]);

        // Interface access is per group and can be joined to accounts.
        var api = _db.Execute(
            "SELECT u.name FROM admin_users u JOIN admin_access a ON a.owner_type = 'group' AND a.owner_name = u.group_name "
            + "WHERE a.interface = 'papi' AND a.allowed = 1 ORDER BY u.name").Rows;
        Assert.Equal(["$SYSTEM-CLOUD$", "admin"], api.Select(r => (string)r[0]!));
    }

    // ---- Hosts ----------------------------------------------------------------------------

    [Fact]
    public void Hosts_are_one_row_per_address_and_include_the_ones_with_no_dns_records()
    {
        Assert.Equal(5L, Scalar("SELECT COUNT(*) FROM hosts"));

        var ipamOnly = _db.Execute("SELECT host_name, address, configure_for_dhcp, mac_address FROM hosts WHERE is_dns_host = 0 ORDER BY host_name").Rows;
        Assert.Equal(2, ipamOnly.Count);
        Assert.Equal("printer", ipamOnly[0][0]);
        Assert.Equal("router", ipamOnly[1][0]);
        Assert.Equal(1L, ipamOnly[1][2]);
        Assert.Equal("00:11:22:33:44:55", ipamOnly[1][3]);

        // A DNS host with three addresses is three rows, with its TTL and where it came from.
        var server = _db.Execute("SELECT address, ttl, ttl_source, aliases FROM hosts WHERE host_name = 'server1.lab.test' ORDER BY ip_key(address)").Rows;
        Assert.Equal(["10.0.0.20", "10.0.0.21", "2001:db8::20"], server.Select(r => (string)r[0]!));
        Assert.All(server, r => Assert.Equal(600L, r[1]));
        Assert.All(server, r => Assert.Equal("Host", r[2]));
        Assert.All(server, r => Assert.Equal("files.lab.test", r[3]));
    }

    [Fact]
    public void Host_addresses_can_be_matched_to_the_networks_that_contain_them()
    {
        var inNetwork = _db.Execute(
            "SELECT h.address FROM hosts h JOIN networks n ON n.network_view_id = h.network_view_id AND cidr_contains(n.prefix, h.address) = 1 " +
            "WHERE n.is_container = 0 ORDER BY ip_key(h.address)").Rows;

        // 10.0.0.0/24 holds the four IPv4 addresses, and 2001:db8::/64 the IPv6 one.
        Assert.Equal(["10.0.0.20", "10.0.0.21", "10.0.0.30", "10.0.0.31", "2001:db8::20"], inNetwork.Select(r => (string)r[0]!));
    }

    // ---- IP helpers through SQL -------------------------------------------------------

    [Fact]
    public void Ip_key_orders_addresses_numerically_in_sql()
    {
        var result = _db.Execute("WITH t(a) AS (VALUES ('10.0.0.10'), ('10.0.0.2'), ('9.0.0.1'), ('2001:db8::1')) SELECT a FROM t ORDER BY ip_key(a)");

        Assert.Equal(["9.0.0.1", "10.0.0.2", "10.0.0.10", "2001:db8::1"], result.Rows.Select(r => (string)r[0]!));
    }

    [Fact]
    public void Cidr_contains_finds_the_same_records_as_checking_each_address_in_code()
    {
        Assert.True(IpPrefix.TryParse("10.0.0.0", 24, out var prefix));
        var expected = _inventory.Records.Count(r =>
            r.Type == DnsRecordType.A && System.Net.IPAddress.TryParse(r.Value, out var ip) && prefix.Covers(ip));

        var actual = Scalar("SELECT COUNT(*) FROM records WHERE type = 'A' AND cidr_contains('10.0.0.0/24', value) = 1");

        Assert.Equal(expected, actual);
        Assert.True(actual > 0, "the fixture has A records in 10.0.0.0/24");
    }

    [Fact]
    public void Ip_in_range_works_on_dhcp_ranges()
    {
        var inRange = Scalar("SELECT COUNT(*) FROM dhcp_ranges WHERE ip_in_range('10.0.0.150', start_address, end_address) = 1");

        var expected = _inventory.DhcpRanges.Count(r =>
            !r.IsIpv6 && IpSort.Compare("10.0.0.150", r.StartAddress) >= 0 && IpSort.Compare("10.0.0.150", r.EndAddress) <= 0);
        Assert.Equal(expected, inRange);
    }

    // ---- The example queries -------------------------------------------------------------

    [Fact]
    public void Every_example_query_runs()
    {
        foreach (var example in ExampleQueries.All)
        {
            var ex = Record.Exception(() => _db.Execute(example.Sql));
            Assert.True(ex is null, $"Example \"{example.Title}\" failed: {ex?.Message}");
        }
    }

    [Fact]
    public void Example_titles_are_unique_and_filled_in()
    {
        Assert.Equal(ExampleQueries.All.Count, ExampleQueries.All.Select(e => e.Title).Distinct().Count());
        Assert.All(ExampleQueries.All, e => Assert.NotEmpty(e.Sql));
    }

    // ---- Read-only ----------------------------------------------------------------------

    [Theory]
    [InlineData("DELETE FROM members")]
    [InlineData("UPDATE members SET host_name = 'x'")]
    [InlineData("INSERT INTO members (oid) VALUES (99)")]
    [InlineData("DROP TABLE records")]
    [InlineData("CREATE TABLE mine (a)")]
    [InlineData("PRAGMA query_only = OFF")]
    [InlineData("ATTACH DATABASE ':memory:' AS other")]
    [InlineData("WITH t AS (SELECT 1) DELETE FROM members")]
    [InlineData("SELECT 1; DELETE FROM members")]
    public void Nothing_that_changes_data_runs_and_the_data_is_untouched(string sql)
    {
        Assert.Throws<QueryException>(() => _db.Execute(sql));

        Assert.Equal(_inventory.Members.Count, Scalar("SELECT COUNT(*) FROM members"));
        Assert.Equal(_inventory.Records.Count, Scalar("SELECT COUNT(*) FROM records"));
    }

    [Fact]
    public void Even_if_a_write_got_past_the_guard_the_database_itself_is_read_only()
    {
        // Straight to SQLite, bypassing SqlGuard: the database refuses to change.
        var inner = typeof(SnapshotDatabase).GetField("_connection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)inner.GetValue(_db)!;
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM members";

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(_inventory.Members.Count, Scalar("SELECT COUNT(*) FROM members"));
    }

    // ---- Limits and errors ----------------------------------------------------------------

    [Fact]
    public void A_row_limit_cuts_the_result_short_and_says_so()
    {
        var result = _db.Execute("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 100) SELECT i FROM n", maxRows: 10);

        Assert.Equal(10, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.Contains("cut off", result.Summary());

        var all = _db.Execute("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 100) SELECT i FROM n", maxRows: 100);
        Assert.Equal(100, all.Rows.Count);
        Assert.False(all.Truncated);
    }

    [Fact]
    public async Task A_query_that_runs_too_long_is_stopped()
    {
        const string forever = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT COUNT(*) FROM n";

        var ex = await Assert.ThrowsAsync<QueryException>(() =>
            Task.Run(() => _db.Execute(forever, timeout: TimeSpan.FromMilliseconds(300))).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains("longer than", ex.Message);

        // The database is still usable afterwards.
        Assert.Equal(_inventory.Members.Count, Scalar("SELECT COUNT(*) FROM members"));
    }

    [Theory]
    [InlineData("SELECT * FROM no_such_table", "no such table")]
    [InlineData("SELECT no_such_column FROM members", "no such column")]
    [InlineData("SELEC 1", "start with SELECT")]
    [InlineData("SELECT FROM members", "syntax error")]
    public void Mistakes_are_explained_without_database_noise(string sql, string expected)
    {
        var ex = Assert.Throws<QueryException>(() => _db.Execute(sql));

        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("SQLite Error", ex.Message);
    }

    [Fact]
    public void A_disposed_database_refuses_queries()
    {
        var other = SnapshotDatabase.Load(_inventory);
        other.Dispose();
        other.Dispose(); // harmless

        Assert.Throws<ObjectDisposedException>(() => other.Execute("SELECT 1"));
    }

    [Fact]
    public void An_empty_snapshot_still_gives_every_table()
    {
        using var empty = SnapshotDatabase.Load(new GridInventory());

        Assert.Equal(_db.Tables.Count, empty.Tables.Count);
        Assert.Equal(0, Assert.IsType<long>(empty.Execute("SELECT COUNT(*) FROM records").Rows[0][0]));
    }
}
