using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.OneDb;

namespace Pfr.Core.Tests.Nios;

public class OneDbReaderTests
{
    [Fact]
    public void Reads_only_allowlisted_types()
    {
        var reader = new OneDbReader(["one.cluster", "dns.view"]);
        var objects = reader.Read(OneDbFixture.XmlStream()).ToList();

        Assert.All(objects, o => Assert.Contains(o.Type, new[] { "one.cluster", "dns.view" }));
        Assert.Single(objects, o => o.Type == "one.cluster");
        Assert.Equal(4, objects.Count(o => o.Type == "dns.view")); // raw objects, before view filtering
        Assert.Equal("9.0.8-12345-abc", reader.DatabaseVersion);
    }

    [Fact]
    public void Never_returns_secret_values_even_for_allowed_types()
    {
        // Ask for every type in the fixture, including the private key and admin objects.
        var reader = new OneDbReader(["one.private_key", "one.admin", "one.virtual_node", "dns.view",
            "dns.zone_ext_secondary_server", "dns.dhcp_failover_association"]);
        var all = reader.Read(OneDbFixture.XmlStream()).ToList();

        var values = all.SelectMany(o => o.Properties.Values).ToList();
        Assert.DoesNotContain(values, v => v.Contains("SUPERSECRET"));

        var names = all.SelectMany(o => o.Properties.Keys).ToHashSet();
        Assert.DoesNotContain("password", names);
        Assert.DoesNotContain("proxy_auth_key", names);
        Assert.DoesNotContain("csp_join_token", names);
        Assert.DoesNotContain("ddns_update_key", names);
        Assert.DoesNotContain("tsig_key", names);
        Assert.DoesNotContain("shared_secret", names);

        // Non-secret neighbours survive.
        Assert.Contains("tsig_key_alg", names);
        Assert.Contains("host_name", names);
    }

    [Theory]
    [InlineData("password", "x", true)]
    [InlineData("privacy_password", "x", true)]
    [InlineData("ddns_update_key", "x", true)]
    [InlineData("license_string", "x", true)]
    [InlineData("anything", "{0}_{aes}_abc", true)]
    [InlineData("snmp_community", "public", true)]
    [InlineData("tsig_key_name", "k1", false)]
    [InlineData("host_name", "gm.lab.test", false)]
    [InlineData("mgm_private", "true", false)]
    public void Sensitive_field_detection(string name, string value, bool expected) =>
        Assert.Equal(expected, SensitiveData.IsSensitive(name, value));

    [Fact]
    public void Parser_allowlist_excludes_secret_object_types()
    {
        Assert.DoesNotContain(BackupParser.AllowedTypes, t =>
            t.Contains("private_key") || t.Contains("certificate") || t.Contains("admin"));
    }

    [Fact]
    public void License_keys_are_never_read()
    {
        var reader = new OneDbReader(["one.product_license"]);
        var licenses = reader.Read(OneDbFixture.XmlStream()).ToList();

        Assert.NotEmpty(licenses);
        Assert.All(licenses, l =>
        {
            Assert.False(l.Properties.ContainsKey("license_string"));
            Assert.NotNull(l.Get("license_type"));
        });
        Assert.DoesNotContain(licenses.SelectMany(l => l.Properties.Values), v => v.Contains("LICENSEKEY"));
    }
}
