using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Pfr.Core.Tests.Nios;

/// <summary>
/// A small synthetic onedb.xml shaped like a real NIOS 9 backup.
/// Includes secret fields and a private key so tests can prove they are dropped.
/// </summary>
internal static class OneDbFixture
{
    // Values are XML-escaped as in real backups (e.g. TXT quotes become &quot;).
    private static string P(string name, string value) =>
        $"<PROPERTY NAME=\"{name}\" VALUE=\"{System.Security.SecurityElement.Escape(value)}\"/>";

    private static string O(string type, params (string Name, string Value)[] props) =>
        "<OBJECT>" + P("__type", ".com.infoblox." + type) +
        string.Concat(props.Select(p => P(p.Name, p.Value))) + "</OBJECT>";

    public const string SecretValue = "{0}_{aes}_SUPERSECRET";

    public static readonly string Xml = string.Join("\n",
    [
        "<DATABASE NAME=\"onedb\" VERSION=\"9.0.8-12345-abc\" MD5=\"x\">",
        O("one.cluster", ("name", "Lab Grid"), ("token_usage_delay", "10")),
        O("one.private_key", ("data", "-----BEGIN PRIVATE KEY-----")),
        O("one.admin", ("name", "admin"), ("password", SecretValue)),

        // Grid Master (oid 0), GMC (oid 7), HA member (oid 16, two nodes)
        O("one.virtual_node", ("virtual_oid", "0"), ("host_name", "gm.lab.test"), ("virtual_ip", "10.0.0.1"),
            ("is_master", "true"), ("is_potential_master", "true"), ("ha_enabled", "0"),
            ("member_type", "VNIOS"), ("upgrade_group", "Grid Master"),
            ("proxy_auth_key", SecretValue), ("csp_join_token", SecretValue)),
        O("one.virtual_node", ("virtual_oid", "7"), ("host_name", "gmc.lab.test"), ("virtual_ip", "10.0.0.2"),
            ("is_master", "false"), ("is_potential_master", "true"), ("ha_enabled", "0")),
        O("one.virtual_node", ("virtual_oid", "16"), ("host_name", "ns1.lab.test"), ("virtual_ip", "10.0.0.3"),
            ("is_master", "false"), ("is_potential_master", "false"), ("ha_enabled", "1")),
        O("one.physical_node", ("physical_oid", "0"), ("virtual_node", "0"), ("position", "0"), ("hwtype", "IB-V926"),
            ("serial_number", "SN-GM"), ("public_ip_address", "10.0.0.1")),
        O("one.physical_node", ("physical_oid", "10"), ("virtual_node", "7"), ("position", "0"), ("hwtype", "IB-V926")),
        O("one.physical_node", ("physical_oid", "21"), ("virtual_node", "16"), ("position", "1"), ("hwtype", "IB-1415"),
            ("serial_number", "SN-B")),
        O("one.physical_node", ("physical_oid", "20"), ("virtual_node", "16"), ("position", "0"), ("hwtype", "IB-1415"),
            ("serial_number", "SN-A")),

        // Licenses (per physical node). license_string must never be read.
        O("one.product_license", ("pnode", "0"), ("license_type", "dns"), ("license_string", "LICENSEKEY-GM")),
        O("one.product_license", ("pnode", "0"), ("license_type", "dtc"), ("license_string", "LICENSEKEY-GM2"),
            ("license_kind", "STATIC"), ("expiry_date", "4102444800")), // 2100-01-01
        O("one.product_license", ("pnode", "10"), ("license_type", "sw_tp"), ("license_string", "LICENSEKEY-GMC2"),
            ("license_kind", "STATIC"), ("expiry_date", "1577836800")), // 2020-01-01: expired
        O("one.license_grid_wide", ("license_type", "rpt_sub"), ("license_limit", "0"), ("cluster", "0")),
        O("one.vnode_license", ("parent", "0"), ("next_expiry", "4102444800"), ("valid_licenses", "dns dtc rpt_sub")),
        O("one.product_license", ("pnode", "10"), ("license_type", "dns"), ("license_string", "LICENSEKEY-GMC")),
        O("one.product_license", ("pnode", "20"), ("license_type", "dtc"), ("license_string", "LICENSEKEY-A")),
        O("one.product_license", ("pnode", "21"), ("license_type", "dtc"), ("license_string", "LICENSEKEY-B")),

        // Services. GM: DNS+DHCP+NTP (+DTC via license). GMC: DNS+TFTP, no DTC license. ns1: DHCP only + reporting.
        O("dns.member_dns_properties", ("virtual_node", "0"), ("service_enabled", "true")),
        O("dns.member_dns_properties", ("virtual_node", "7"), ("service_enabled", "true")),
        O("dns.member_dns_properties", ("virtual_node", "16"), ("service_enabled", "false")),
        O("dns.member_dhcp_properties", ("virtual_node", "0"), ("service_enabled", "true")),
        O("dns.member_dhcp_properties", ("virtual_node", "16"), ("service_enabled", "true")),
        O("one.vnode_time", ("virtual_node", "0"), ("ntp_service_enabled", "true")),
        O("one.vnode_time", ("virtual_node", "7"), ("ntp_service_enabled", "false")),
        O("one.member_tftp_properties", ("virtual_node", "7"), ("tftp_enabled", "true"), ("http_enabled", "false"),
            ("ftp_enabled", "false")),
        O("atp.member_atp_restart", ("parent", "16"), ("service_enabled", "false")),
        O("reporting.member_reporting_properties", ("parent", "0"), ("enable", "true")),
        O("reporting.indexer_vnode", ("parent", "0"), ("vnode", "16")),

        O("dns.network_view", ("id", "0"), ("name", "default")),

        // Views are top-level "zones" of type View plus a dns.view settings object.
        O("dns.zone", ("zone_type", "View"), ("zone", ".."), ("name", "_default"), ("fqdn", "_default")),
        O("dns.zone", ("zone_type", "View"), ("zone", ".."), ("name", "1"), ("fqdn", "1")),
        O("dns.view", ("zone", "._default"), ("displayname", "Internal"), ("network_view", "0"),
            ("recursion_enabled", "true"), ("ddns_update_key", SecretValue), ("tsig_key_alg", "HMAC-SHA256")),
        O("dns.view", ("zone", ".1"), ("displayname", "External"), ("network_view", "0"),
            ("recursion_enabled", "false")),
        // Internal roots that also carry dns.view objects but are not real views.
        O("dns.zone", ("zone_type", "View"), ("zone", ".."), ("name", "idns_root"), ("fqdn", "idns_root")),
        O("dns.view", ("zone", ".idns_root"), ("displayname", " "), ("network_view", "0")),
        O("dns.zone", ("zone_type", "NonDNSHost"), ("zone", ".."), ("name", "non_DNS_host_root"),
            ("fqdn", "non_DNS_host_root")),
        O("dns.view", ("zone", ".non_DNS_host_root"), ("displayname", " "), ("network_view", "0")),

        // Zones
        O("dns.zone", ("zone_type", "Authoritative"), ("zone", "._default"), ("name", "test.lab"),
            ("fqdn", "lab.test"), ("primary_type", "Grid"), ("is_reverse_zone", "0"), ("disabled", "false")),
        O("dns.zone", ("zone_type", "Authoritative"), ("zone", "._default"), ("name", "arpa.in-addr.10.0.0"),
            ("fqdn", "0.0.10.in-addr.arpa"), ("primary_type", "Grid"), ("is_reverse_zone", "1")),
        O("dns.zone", ("zone_type", "Delegated"), ("zone", "._default.test.lab"), ("name", "aws"),
            ("fqdn", "aws.lab.test")),
        O("dns.zone", ("zone_type", "Authoritative"), ("zone", ".1"), ("name", "test.lab"),
            ("fqdn", "lab.test"), ("primary_type", "Grid")),
        O("dns.zone", ("zone_type", "Dummy"), ("zone", "._default"), ("name", "arpa.in-addr.127.0.0"),
            ("fqdn", "0.0.127.in-addr.arpa"), ("is_reverse_zone", "1")),
        O("dns.zone", ("zone_type", "ResponsePolicy"), ("zone", ".."), ("name", "rpz_root"), ("fqdn", "rpz_root")),

        O("dns.zone_grid_primary", ("zone", "._default.test.lab"), ("grid_member", "0")),
        O("dns.zone_cluster_secondary_server", ("zone", "._default.test.lab"), ("member_address", "16")),
        O("dns.zone_ext_secondary_server", ("zone", ".1.test.lab"), ("ext_sec_name", "ns2.isp.test"),
            ("ext_sec_address", "192.0.2.53"), ("tsig_key", SecretValue)),
        O("dns.zone_delegated_server", ("zone", "._default.test.lab.aws"), ("ds_name", "ns-1.awsdns.test"),
            ("address", "255.255.255.255")),

        // Records
        O("dns.bind_a", ("zone", "._default.test.lab"), ("name", "www"), ("address", "10.0.0.10")),
        O("dns.bind_a", ("zone", "._default.test.lab"), ("name", ""), ("address", "10.0.0.11")),
        O("dns.bind_aaaa", ("zone", ".1.test.lab"), ("name", "www"), ("address", "2001:db8::10")),
        O("dns.bind_cname", ("zone", "._default.test.lab"), ("name", "web"), ("canonical_name", "www.lab.test")),
        O("dns.bind_cname", ("zone", "._default.test.lab"), ("name", "old"), ("canonical_name", "gone.lab.test"),
            ("disabled", "true")),
        O("dns.bind_cname", ("zone", "._default.blacklist"), ("name", "com.bad"), ("canonical_name", "")),
        O("dns.bind_ptr", ("zone", "._default.arpa.in-addr.10.0.0"), ("name", "10"), ("dname", "www.lab.test")),
        O("dns.host", ("zone", "._default.test.lab"), ("name", "server1")),
        // Host: two IPv4 (one linked to the 10.0.0 reverse zone) and one IPv6 address, plus an alias.
        O("dns.host_address", ("host", "._default.test.lab.server1"), ("address", "10.0.0.20"), ("is_ipv4", "true"),
            ("reverse_zone", "._default.arpa.in-addr.10.0.0")),
        O("dns.host_address", ("host", "._default.test.lab.server1"), ("address", "10.0.0.21"), ("is_ipv4", "true")),
        O("dns.host_address", ("host", "._default.test.lab.server1"), ("address", "2001:db8::20"), ("is_ipv4", "false")),
        O("dns.host_alias", ("zone", "._default.test.lab"), ("alias", "files"), ("host", "._default.test.lab.server1")),
        // A non-DNS host (IPAM only) must not produce records.
        O("dns.host", ("zone", ".non_DNS_host_root"), ("name", "printer")),
        O("dns.host_address", ("host", ".non_DNS_host_root.printer"), ("address", "10.0.0.30"), ("is_ipv4", "true")),

        // Other record types.
        O("dns.bind_mx", ("zone", "._default.test.lab"), ("name", ""), ("priority", "10"), ("mx", "mail.lab.test")),
        O("dns.bind_srv", ("zone", "._default.test.lab"), ("name", "_sip._tcp"), ("priority", "10"), ("weight", "20"),
            ("port", "5060"), ("target", "sip.lab.test")),
        O("dns.bind_txt", ("zone", "._default.test.lab"), ("name", ""), ("txt_string", "\"v=spf1 -all\"")),
        O("dns.bind_naptr", ("zone", "._default.test.lab"), ("name", "voip"), ("order", "90"), ("preference", "50"),
            ("flag", "S"), ("services", "SIP+D2T"), ("regexp", ""), ("replacement", "_sip._tcp.lab.test")),
        O("dns.bind_caa", ("zone", "._default.test.lab"), ("name", ""), ("ca_flag", "0"), ("ca_tag", "issue"),
            ("ca_value", "letsencrypt.org"), ("ca_value_hash", "abc123")),
        O("dns.bind_ns", ("zone", "._default.test.lab"), ("name", ""), ("dname", "gm.lab.test"), ("auto_created", "true")),

        // IPAM
        O("dns.network_container", ("address", "10.0.0.0"), ("cidr", "8"), ("network_view", "0"), ("is_ipv4", "true"),
            ("comment", "Lab")),
        O("dns.network", ("address", "10.0.0.0"), ("cidr", "24"), ("network_view", "0"), ("is_ipv4", "true"),
            ("comment", "Infrastructure")),
        O("dns.network", ("address", "2001:db8::"), ("cidr", "64"), ("network_view", "0"), ("is_ipv4", "false")),

        // DHCP
        O("dns.dhcp_failover_association", ("name", "gm-gmc"), ("primary_server_vnode", "0"),
            ("secondary_server_vnode", "7"), ("ms_server", "."), ("shared_secret", SecretValue)),
        O("dns.dhcp_failover_association", ("name", "dc1-dc2"), ("ms_server", "12"),
            ("ms_failover_address", "192.0.2.32"), ("ms_failover_partner", "192.0.2.31"),
            ("ms_failover_mode", "LOADBALANCE")),
        O("dns.dhcp_failover_association", ("name", "dc1-dc2"), ("ms_server", "13"),
            ("ms_failover_address", "192.0.2.31"), ("ms_failover_partner", "192.0.2.32"),
            ("ms_failover_mode", "LOADBALANCE")),
        O("dns.dhcp_range", ("network", "10.0.0.0/24/0"), ("start_address", "10.0.0.100"),
            ("end_address", "10.0.0.199"), ("network_view", "0"), ("server_association_type", "Failover"),
            ("failover_association", "gm-gmc."), ("member", "0"), ("ms_server", ".")),
        O("dns.dhcp_range", ("network", "10.0.0.0/24/0"), ("start_address", "10.0.0.50"),
            ("end_address", "10.0.0.59"), ("network_view", "0"), ("server_association_type", "Member"),
            ("member", "16"), ("ms_server", ".")),
        O("dns.dhcp_range", ("network", "2001:db8::/64/0"), ("start_address", "2001:db8::1000"),
            ("end_address", "2001:db8::1fff"), ("network_view", "0"), ("server_association_type", "None"),
            ("member", "0"), ("ms_server", ".")),

        "</DATABASE>",
    ]);

    public static MemoryStream XmlStream() => new(Encoding.UTF8.GetBytes(Xml));

    /// <summary>Builds a .bak-style gzip tar with onedb.xml after another entry, like NIOS does.</summary>
    public static MemoryStream GzipTarBackup(string oneDbEntryName = "./onedb.xml")
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var other = new PaxTarEntry(TarEntryType.RegularFile, "./active_ip.txt")
            {
                DataStream = new MemoryStream("10.0.0.1"u8.ToArray()),
            };
            tar.WriteEntry(other);

            var onedb = new PaxTarEntry(TarEntryType.RegularFile, oneDbEntryName)
            {
                DataStream = XmlStream(),
            };
            tar.WriteEntry(onedb);
        }

        output.Position = 0;
        return output;
    }
}
