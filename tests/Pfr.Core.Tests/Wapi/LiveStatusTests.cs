using System.Text.Json;
using Pfr.Core.Clients;
using Pfr.Core.Wapi;

namespace Pfr.Core.Tests.Wapi;

public sealed class LiveStatusTests : IDisposable
{
    private readonly FakeWapiServer _server = new();

    public void Dispose() => _server.Dispose();

    private WapiClient Wapi() => WapiClient.ForReadOnly(new GridConnection
    {
        Address = "127.0.0.1",
        Port = _server.Port,
        CertificateFingerprint = _server.Fingerprint,
        ReadOnly = new Credential { Username = _server.User, Password = _server.Password },
    });

    private static List<JsonElement> Elements(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();

    private static LiveGridStatus Build(string members, string upgrades = "[]", params string[] notes) =>
        LiveStatusCollector.Build("Lab Grid", "2.12", new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero),
            Elements(members), Elements(upgrades), notes);

    // ---- Collecting from a grid ----------------------------------------------------

    [Fact]
    public async Task Members_nodes_services_ha_versions_and_hotfixes_are_read()
    {
        using var wapi = Wapi();

        var status = await LiveStatusCollector.CollectAsync(wapi);

        Assert.Equal("Lab Grid", status.GridName);
        Assert.Equal("2.12", status.ApiVersion);
        Assert.Equal(["gm.lab.test", "ns1.lab.test"], status.Members.Select(m => m.HostName));
        Assert.Empty(status.Notes);

        var gm = status.Members[0];
        Assert.False(gm.HaEnabled);
        Assert.Equal("-", gm.HaSummary);
        Assert.Equal("9.0.8-12345", gm.Version);
        Assert.Equal(["Hotfix-9.0.8-1 (Applied)"], gm.Hotfixes);
        Assert.Equal(Health.Ok, gm.Health); // a service that is switched off (inactive) is not a problem
        Assert.Equal(["DHCP", "DNS (authoritative)", "NTP"], gm.Nodes.Single().Services.Select(s => s.Name));

        var ns1 = status.Members[1];
        Assert.True(ns1.HaEnabled);
        Assert.Equal("Active / Passive", ns1.HaSummary);
        Assert.Null(ns1.HaProblem);
        Assert.Empty(ns1.Hotfixes);
        Assert.Equal(Health.Failed, ns1.Health);
        Assert.Equal(Health.Ok, ns1.Nodes[0].Health);
        Assert.Equal(Health.Failed, ns1.Nodes[1].Health);

        // On the failing node, problems come first.
        Assert.Equal(["DHCP", "Swap usage", "DNS (authoritative)"], ns1.Nodes[1].Services.Select(s => s.Name));
        Assert.Equal(ServiceState.Failed, ns1.Nodes[1].Services[0].State);

        Assert.Equal(Health.Failed, status.Health);
        Assert.Equal("9.0.8-12345", status.VersionSummary);
        Assert.Equal(
        [
            "ns1.lab.test (node 2, Passive): DHCP Failed - The DHCP service failed to start",
            "ns1.lab.test (node 2, Passive): Swap usage Warning - Swap usage is 62%",
        ], status.Problems);
    }

    [Fact]
    public async Task Collecting_only_reads()
    {
        using var wapi = Wapi();
        await LiveStatusCollector.CollectAsync(wapi);
        Assert.NotEmpty(_server.Requests);
        Assert.All(_server.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task A_field_the_grid_does_not_offer_is_not_requested_and_is_noted()
    {
        _server.SchemaFields["member"] = ["host_name", "node_info"]; // no enable_ha
        using var wapi = Wapi();

        var status = await LiveStatusCollector.CollectAsync(wapi);

        var memberRead = _server.Requests.Single(r => r.PathAndQuery.Contains("/member?_return_fields", StringComparison.Ordinal));
        Assert.DoesNotContain("enable_ha", memberRead.PathAndQuery);
        Assert.Contains(status.Notes, n => n.Contains("member", StringComparison.Ordinal) && n.Contains("enable_ha", StringComparison.Ordinal));
        Assert.True(status.Members.Single(m => m.HostName == "ns1.lab.test").HaEnabled); // two nodes still mean HA
    }

    [Fact]
    public async Task Versions_that_cannot_be_read_become_a_note_and_the_rest_still_works()
    {
        _server.UpgradeStatusError = 400;
        using var wapi = Wapi();

        var status = await LiveStatusCollector.CollectAsync(wapi);

        Assert.Equal(2, status.Members.Count);
        Assert.All(status.Members, m => Assert.Null(m.Version));
        Assert.Equal("unknown", status.VersionSummary);
        var note = Assert.Single(status.Notes);
        Assert.StartsWith("upgradestatus:", note);
        Assert.Contains("type search field is required", note);
    }

    [Fact]
    public async Task Members_are_essential_so_their_failure_is_an_error()
    {
        _server.Extra = request =>
            request.Method == "GET" && request.PathAndQuery.Contains("/member?_return_fields", StringComparison.Ordinal)
                ? FakeResponse.Json(403, """{"Error":"AdmConProtoError: Operation not permitted","text":"Not permitted to read members"}""")
                : null;
        using var wapi = Wapi();

        var ex = await Assert.ThrowsAsync<WapiRequestException>(() => LiveStatusCollector.CollectAsync(wapi));
        Assert.Contains("Not permitted to read members", ex.Message);
    }

    [Fact]
    public async Task A_schema_that_cannot_be_read_does_not_stop_the_status()
    {
        _server.Extra = request =>
            request.PathAndQuery.Contains("_schema", StringComparison.Ordinal) && request.PathAndQuery.Contains("/member?", StringComparison.Ordinal)
                ? FakeResponse.Json(404, "{}")
                : null;
        using var wapi = Wapi();

        var status = await LiveStatusCollector.CollectAsync(wapi);

        Assert.Equal(2, status.Members.Count);
    }

    // ---- Raw reads ------------------------------------------------------------------------

    [Fact]
    public async Task A_raw_read_returns_what_the_grid_answered_as_indented_json()
    {
        using var wapi = Wapi();

        var json = await wapi.GetRawAsync("member", returnFields: "host_name,node_info");

        Assert.Contains("gm.lab.test", json);
        Assert.Contains(Environment.NewLine, json); // indented
        Assert.Contains(_server.Requests, r => r.PathAndQuery.Contains("_return_fields=host_name%2Cnode_info", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_objects_schema_fields_can_be_listed()
    {
        using var wapi = Wapi();

        var fields = await wapi.GetSchemaFieldsAsync("member");

        Assert.Contains("node_info", fields);
        Assert.DoesNotContain("not_a_field", fields);
    }

    [Theory]
    [InlineData("../grid")]
    [InlineData("member?x=1")]
    [InlineData("")]
    [InlineData("a b")]
    public async Task An_object_type_cannot_add_to_the_path_or_query(string objectType)
    {
        using var wapi = Wapi();
        await Assert.ThrowsAsync<ArgumentException>(() => wapi.GetRawAsync(objectType));
        Assert.Empty(_server.Requests.Where(r => r.PathAndQuery.Contains("x=1", StringComparison.Ordinal)));
    }

    // ---- Reading the grid's answer (no network) -----------------------------------------

    [Theory]
    [InlineData("WORKING", ServiceState.Working, Health.Ok)]
    [InlineData("INACTIVE", ServiceState.Inactive, Health.Ok)]
    [InlineData("WARNING", ServiceState.Warning, Health.Warning)]
    [InlineData("FAILED", ServiceState.Failed, Health.Failed)]
    [InlineData("OFFLINE", ServiceState.Offline, Health.Failed)]
    [InlineData("SOMETHING_NEW", ServiceState.Unknown, Health.Unknown)]
    public void Service_states_map_to_a_health(string status, ServiceState state, Health health)
    {
        var result = Build($$"""[{"host_name":"m","node_info":[{"service_status":[{"service":"DHCPD","status":"{{status}}","description":"d"}]}]}]""");
        var service = result.Members.Single().Nodes.Single().Services.Single();
        Assert.Equal(state, service.State);
        Assert.Equal(health, service.Health);
    }

    [Fact]
    public void An_ha_pair_without_exactly_one_active_node_is_a_problem()
    {
        var both = Build("""[{"host_name":"ha","enable_ha":true,"node_info":[{"ha_status":"ACTIVE"},{"ha_status":"ACTIVE"}]}]""");
        var member = both.Members.Single();
        Assert.Equal("HA pair has 2 active nodes (expected 1)", member.HaProblem);
        Assert.Equal(Health.Warning, member.Health);
        Assert.Contains("ha: HA pair has 2 active nodes (expected 1)", both.Problems);

        var none = Build("""[{"host_name":"ha","enable_ha":true,"node_info":[{"ha_status":"PASSIVE"},{"ha_status":"PASSIVE"}]}]""");
        Assert.Equal("HA pair has 0 active nodes (expected 1)", none.Members.Single().HaProblem);

        var healthy = Build("""[{"host_name":"ha","enable_ha":true,"node_info":[{"ha_status":"ACTIVE"},{"ha_status":"PASSIVE"}]}]""");
        Assert.Null(healthy.Members.Single().HaProblem);

        // When the grid says nothing about HA states there is nothing to judge.
        var silent = Build("""[{"host_name":"ha","enable_ha":true,"node_info":[{},{}]}]""");
        Assert.Null(silent.Members.Single().HaProblem);
    }

    [Fact]
    public void Versions_are_matched_to_members_by_name_and_mixed_versions_are_shown()
    {
        var status = Build(
            """[{"host_name":"GM.lab.test"},{"host_name":"ns2.lab.test"},{"host_name":"ns3.lab.test"}]""",
            """
            [
              {"member":"gm.lab.test","current_version":"9.0.8-2","hotfixes":["HF-1","HF-2"]},
              {"member":"ns2.lab.test","current_version":"9.0.7-1","hotfixes":[{"version":"HF-7"}]}
            ]
            """);

        var gm = status.Members.Single(m => m.HostName == "GM.lab.test"); // names match ignoring case
        Assert.Equal("9.0.8-2", gm.Version);
        Assert.Equal(["HF-1", "HF-2"], gm.Hotfixes);
        Assert.Equal(["HF-7"], status.Members.Single(m => m.HostName == "ns2.lab.test").Hotfixes);
        Assert.Null(status.Members.Single(m => m.HostName == "ns3.lab.test").Version);
        Assert.Equal("mixed: 9.0.7-1, 9.0.8-2", status.VersionSummary);
    }

    [Fact]
    public void Unexpected_shapes_are_tolerated_rather_than_failing()
    {
        var status = Build("""[{"host_name":"odd","node_info":"not a list"},{"node_info":[{"service_status":[{"status":"WORKING"}]}]},{"host_name":"x","node_info":[null]}]""");

        Assert.Equal(3, status.Members.Count);
        Assert.All(status.Members, m => Assert.NotNull(m.Hotfixes));
        Assert.Equal(Health.Unknown, status.Members.Single(m => m.HostName == "odd").Health);
    }

    [Theory]
    [InlineData("AUTH_NAMED", "DNS (authoritative)")]
    [InlineData("DHCPD", "DHCP")]
    [InlineData("NTP_SYNC", "NTP")]
    [InlineData("SOME_NEW_THING", "Some new thing")]
    public void Service_codes_get_readable_names(string code, string expected) =>
        Assert.Equal(expected, LiveStatusCollector.ServiceName(code));

    // ---- The text report ---------------------------------------------------------------------

    [Fact]
    public void The_report_lists_members_problems_and_notes()
    {
        var status = Build(
            """
            [
              {"host_name":"gm.lab.test","node_info":[{"ha_status":"NOT_CONFIGURED","service_status":[{"service":"DHCPD","status":"WORKING"}]}]},
              {"host_name":"ns1.lab.test","enable_ha":true,"node_info":[
                {"ha_status":"ACTIVE","service_status":[{"service":"DHCPD","status":"WORKING"}]},
                {"ha_status":"PASSIVE","service_status":[{"service":"DHCPD","status":"FAILED","description":"did not start"}]}]}
            ]
            """,
            """[{"member":"gm.lab.test","current_version":"9.0.8-2","hotfixes":[{"version":"HF-1","status_text":"Applied"}]}]""",
            "upgradestatus: example note");

        var text = LiveStatusReport.Render(status, "Acme › Production");

        Assert.Contains("Acme › Production: grid \"Lab Grid\" · WAPI v2.12", text);
        Assert.Contains("Overall: Failed · 2 members · NIOS 9.0.8-2", text);
        Assert.Contains("Member", text);
        Assert.Contains("Active / Passive", text);
        Assert.Contains("HF-1 (Applied)", text);
        Assert.Contains("Problems", text);
        Assert.Contains("ns1.lab.test (node 2, Passive): DHCP Failed - did not start", text);
        Assert.Contains("Notes", text);
        Assert.Contains("upgradestatus: example note", text);
    }

    [Fact]
    public void A_grid_that_reports_no_members_says_so()
    {
        var text = LiveStatusReport.Render(Build("[]"));
        Assert.Contains("The grid reported no members.", text);
        Assert.Contains("Overall: Unknown", text);
    }
}
