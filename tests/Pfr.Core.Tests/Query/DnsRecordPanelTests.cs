using Pfr.Core.Nios.Model;
using Pfr.Core.Reports;

namespace Pfr.Core.Tests.Query;

public class DnsRecordPanelTests
{
    private static DnsRecord Record(string name, DnsRecordType type = DnsRecordType.A, string value = "10.0.0.1", Ttl? ttl = null,
        string? host = null, bool disabled = false) => new()
    {
        Type = type, Name = name, Fqdn = name.Length == 0 ? "lab.test" : $"{name}.lab.test", Value = value, ZoneKey = "z", ViewKey = "v",
        Ttl = ttl ?? new Ttl(300, TtlSource.Record), HostName = host, Disabled = disabled,
    };

    [Fact]
    public void The_columns_are_those_of_the_inventory_panel()
    {
        Assert.Equal(["Name", "Type", "Value", "TTL", "TTL from", "Host", "FQDN", "Disabled"], DnsRecordPanel.Columns);
    }

    [Fact]
    public void A_record_is_a_row_of_those_cells()
    {
        var cells = DnsRecordPanel.Cells(Record("web1", DnsRecordType.A, "10.0.0.20", new Ttl(28800, TtlSource.Record), host: "web1.lab.test", disabled: true));

        Assert.Equal(["web1", "A", "10.0.0.20", "28800 (8h)", "Record", "Yes", "web1.lab.test", "Yes"], cells);
        Assert.Equal(DnsRecordPanel.Columns.Count, cells.Count);
    }

    [Fact]
    public void The_zones_own_name_is_shown_as_an_at_sign_and_empty_cells_are_empty()
    {
        var cells = DnsRecordPanel.Cells(Record("", DnsRecordType.NS, "ns1.lab.test."));

        Assert.Equal(["@", "NS", "ns1.lab.test.", "300 (5m)", "Record", "", "lab.test", ""], cells);
    }

    [Theory]
    [InlineData(TtlSource.Record, "Record")]
    [InlineData(TtlSource.Host, "Host object")]
    [InlineData(TtlSource.Zone, "Inherited: zone")]
    [InlineData(TtlSource.Grid, "Inherited: grid")]
    public void Where_the_ttl_comes_from_is_worded_so_inherited_values_are_obvious(TtlSource source, string expected)
    {
        Assert.Equal(expected, DnsRecordPanel.TtlFrom(new Ttl(3600, source)));
        Assert.Equal(expected, DnsRecordPanel.Cells(Record("x", ttl: new Ttl(3600, source)))[4]);
    }

    [Fact]
    public void A_record_with_no_known_ttl_shows_neither_a_ttl_nor_where_it_comes_from()
    {
        var cells = DnsRecordPanel.Cells(Record("x", ttl: new Ttl(null, TtlSource.Grid)));

        Assert.Equal(("", ""), (cells[3], cells[4]));
    }

    [Fact]
    public void The_order_is_the_zones_own_records_first_then_by_name_ignoring_case_then_by_type()
    {
        var ordered = DnsRecordPanel.Order(
        [
            Record("www", DnsRecordType.CNAME), Record("Web1", DnsRecordType.AAAA), Record("web1", DnsRecordType.A),
            Record("app"), Record("", DnsRecordType.TXT), Record("", DnsRecordType.NS),
        ]).Select(r => $"{(r.Name.Length == 0 ? "@" : r.Name)}/{r.Type}").ToList();

        // Types are in the order of the enum (A, AAAA, CNAME, PTR, MX, SRV, TXT, NAPTR, CAA, NS), not alphabetical.
        Assert.Equal(["@/TXT", "@/NS", "app/A", "web1/A", "Web1/AAAA", "www/CNAME"], ordered);
    }

    [Fact]
    public void Records_that_are_equal_keep_the_order_they_came_in()
    {
        var first = Record("rr", value: "10.0.0.1");
        var second = Record("rr", value: "10.0.0.2");

        Assert.Equal([first, second], DnsRecordPanel.Order([first, second]));
        Assert.Equal([second, first], DnsRecordPanel.Order([second, first]));
    }
}
