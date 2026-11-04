using System.Text.Json;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Nios.OneDb;
using Pfr.Core.Storage;

namespace Pfr.Core.Tests.Nios;

public class AdminParserTests
{
    private static readonly GridInventory Inventory = BackupParser.Parse(OneDbFixture.XmlStream());

    // ---- Groups, roles, accounts ----

    [Fact]
    public void Groups_are_read_with_their_flags_and_sorted_by_name()
    {
        Assert.Equal(["admin-group", "helpdesk", "readonly"], Inventory.AdminGroups.Select(g => g.Name));

        var admins = Inventory.AdminGroups[0];
        Assert.True(admins.IsSuperuser);
        Assert.False(admins.Disabled);
        Assert.Equal("Superusers", admins.Comment);
        Assert.Equal("FULL_UI", admins.UiModel);

        var helpdesk = Inventory.AdminGroups[1];
        Assert.False(helpdesk.IsSuperuser);
        Assert.True(helpdesk.RestrictedUserAccess);

        Assert.True(Inventory.AdminGroups[2].Disabled);
    }

    [Fact]
    public void Roles_are_read()
    {
        Assert.Equal(["DNS Admin", "readonly"], Inventory.AdminRoles.Select(r => r.Name));
        Assert.True(Inventory.AdminRoles[0].IsPredefined);
        Assert.False(Inventory.AdminRoles[1].IsPredefined);
        Assert.Equal("View only", Inventory.AdminRoles[1].Comment);
    }

    [Fact]
    public void Accounts_are_tied_to_their_group_and_inherit_its_superuser_flag()
    {
        Assert.Equal(["$SYSTEM-CLOUD$", "admin", "jsmith", "olduser", "orphan"], Inventory.AdminUsers.Select(u => u.Name));
        AdminUser User(string name) => Inventory.AdminUsers.Single(u => u.Name == name);

        var admin = User("admin");
        Assert.Equal("admin-group", admin.GroupName); // the backup writes ".admin-group"
        Assert.True(admin.IsSuperuser);
        Assert.Equal("LOCAL", admin.AuthType);
        Assert.Equal("ACTIVE", admin.Status);
        Assert.Equal("US/Eastern", admin.TimeZone);
        Assert.Equal("Built-in", admin.Comment);
        Assert.Equal("2026-02-10", admin.LastLogin?.ToString("yyyy-MM-dd"));

        var jsmith = User("jsmith");
        Assert.Equal("helpdesk", jsmith.GroupName);
        Assert.False(jsmith.IsSuperuser);
        Assert.Equal("REMOTE", jsmith.AuthType);
        Assert.True(jsmith.UsesSshKeys);
        Assert.Null(jsmith.LastLogin);

        Assert.True(User("olduser").Disabled);
        Assert.True(User("$SYSTEM-CLOUD$").IsInternal);

        var orphan = User("orphan");
        Assert.Null(orphan.GroupName);
        Assert.False(orphan.IsSuperuser);
    }

    [Fact]
    public void Roles_given_to_groups_are_listed_in_order()
    {
        Assert.Equal(
            [("helpdesk", "DNS Admin", 0), ("helpdesk", "readonly", 1), ("readonly", "readonly", 0)],
            Inventory.AdminGroupRoles.Select(l => (l.GroupName, l.RoleName, l.Position)));
    }

    // ---- Permissions ----

    [Fact]
    public void Permissions_say_who_has_what_on_which_resource()
    {
        // Six rules belong to a group or a role; the one for something else is ignored.
        Assert.Equal(6, Inventory.AdminPermissions.Count);
        Assert.DoesNotContain(Inventory.AdminPermissions, p => p.OwnerName == "x");

        var onNetwork = Inventory.AdminPermissions
            .Where(p => p.OwnerType == "group" && p.OwnerName == "helpdesk").OrderBy(p => p.SubType).ToList();
        Assert.Equal(2, onNetwork.Count);
        Assert.All(onNetwork, p =>
        {
            Assert.Equal("dns.network_container", p.ResourceType); // the ".com.infoblox." prefix is gone
            Assert.Equal("10.0.0.0/8/0", p.ResourceKey);           // same form as networks.network_key
        });

        var readOnly = onNetwork[0];
        Assert.Null(readOnly.SubType);
        Assert.Equal("Read-only", readOnly.Level);
        Assert.False(readOnly.Unmanaged);

        // Values written in capitals count too.
        var readWrite = onNetwork[1];
        Assert.Equal("dns.host_address;is_ipv4=true", readWrite.SubType);
        Assert.Equal("Read/Write", readWrite.Level);
        Assert.False(readWrite.Unmanaged);
    }

    [Fact]
    public void Roles_have_permissions_too_and_the_levels_are_named()
    {
        var zonePermissions = Inventory.AdminPermissions
            .Where(p => p.OwnerType == "role" && p.ResourceType == "dns.zone").ToList();
        Assert.All(zonePermissions, p => Assert.Equal("._default.test.lab", p.ResourceKey)); // same form as zones.zone_key

        Assert.Equal("Read/Write", zonePermissions.Single(p => p.OwnerName == "DNS Admin" && p.SubType is null).Level);
        Assert.Equal("Deny", zonePermissions.Single(p => p.OwnerName == "readonly").Level);

        // Read, change and create but not delete: not one of the three named levels.
        var custom = zonePermissions.Single(p => p.SubType == "dns.bind_a");
        Assert.Equal("Custom", custom.Level);
        Assert.True(custom.CanModify);
        Assert.False(custom.CanDelete);
    }

    [Fact]
    public void Rules_NIOS_adds_itself_are_marked_unmanaged()
    {
        var cluster = Inventory.AdminPermissions.Single(p => p.ResourceType == "one.cluster");

        Assert.Equal("admin-group", cluster.OwnerName);
        Assert.Equal("0", cluster.ResourceKey);
        Assert.True(cluster.Unmanaged);
        Assert.Equal(1, Inventory.AdminPermissions.Count(p => p.Unmanaged));
    }

    // ---- Interface access and authentication ----

    [Fact]
    public void Interface_access_is_listed_without_the_access_prefix()
    {
        Assert.Equal(
            [("admin-group", "cloud_api", false), ("admin-group", "papi", true), ("helpdesk", "gui", true), ("helpdesk", "papi", false)],
            Inventory.AdminAccessRights.Select(a => (a.OwnerName, a.Interface, a.Allowed)));
        Assert.All(Inventory.AdminAccessRights, a => Assert.Equal("group", a.OwnerType));
    }

    [Fact]
    public void Authentication_methods_are_in_the_order_they_are_tried()
    {
        Assert.Equal(
            [(0, "Local", "Local Admin"), (1, "TACACS+", "tacacs+")],
            Inventory.AdminAuthMethods.Select(m => (m.Position, m.Type, m.Name)));
    }

    // ---- Secrets ----

    [Fact]
    public void Nothing_secret_reaches_the_inventory_or_what_is_stored_of_it()
    {
        var json = JsonSerializer.Serialize(Inventory, StoreJson.Options);

        Assert.DoesNotContain("SUPERSECRET", json);
        Assert.DoesNotContain("CERT-SERIAL", json); // certificate details are dropped by name
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LICENSEKEY", json);
    }

    [Fact]
    public void Types_that_only_hold_secrets_or_settings_are_never_read()
    {
        var objects = new OneDbReader(BackupParser.AllowedTypes).Read(OneDbFixture.XmlStream()).ToList();

        Assert.DoesNotContain(objects, o => o.Type is "one.lom_user" or "one.admin_group_password_setting");
        Assert.All(objects.Where(o => o.Type == "one.admin"), o =>
        {
            Assert.False(o.Properties.ContainsKey("password"));
            Assert.False(o.Properties.ContainsKey("client_certificate_serial_number"));
        });
    }

    // ---- Stored snapshots ----

    [Fact]
    public void Administrators_survive_being_stored_and_loaded()
    {
        var json = JsonSerializer.Serialize(Inventory, StoreJson.Options);
        var loaded = JsonSerializer.Deserialize<GridInventory>(json, StoreJson.Options)!;

        Assert.Equal(Inventory.AdminGroups.Count, loaded.AdminGroups.Count);
        Assert.Equal(Inventory.AdminUsers.Count, loaded.AdminUsers.Count);
        Assert.Equal(Inventory.AdminRoles.Count, loaded.AdminRoles.Count);
        Assert.Equal(Inventory.AdminGroupRoles.Count, loaded.AdminGroupRoles.Count);
        Assert.Equal(Inventory.AdminPermissions.Count, loaded.AdminPermissions.Count);
        Assert.Equal(Inventory.AdminAccessRights.Count, loaded.AdminAccessRights.Count);
        Assert.Equal(Inventory.AdminAuthMethods.Count, loaded.AdminAuthMethods.Count);

        var before = Inventory.AdminPermissions.Single(p => p.SubType == "dns.bind_a");
        var after = loaded.AdminPermissions.Single(p => p.SubType == "dns.bind_a");
        Assert.Equal(before.Level, after.Level);
        Assert.Equal(before.ResourceKey, after.ResourceKey);
        Assert.Equal(Inventory.AdminUsers.Single(u => u.Name == "admin").LastLogin, loaded.AdminUsers.Single(u => u.Name == "admin").LastLogin);
    }

    [Fact]
    public void A_snapshot_stored_before_administrators_were_read_still_loads_with_none()
    {
        const string old = """{"GridName":"Old Grid","NiosVersion":"9.0.0","Members":[]}""";

        var loaded = JsonSerializer.Deserialize<GridInventory>(old, StoreJson.Options)!;

        Assert.Equal("Old Grid", loaded.GridName);
        Assert.Empty(loaded.AdminUsers);
        Assert.Empty(loaded.AdminPermissions);
    }

    [Fact]
    public void A_backup_with_no_administrators_gives_empty_lists()
    {
        const string xml = "<DATABASE NAME=\"onedb\" VERSION=\"9.0.8-1-a\">\n"
            + "<OBJECT><PROPERTY NAME=\"__type\" VALUE=\".com.infoblox.one.cluster\"/><PROPERTY NAME=\"name\" VALUE=\"G\"/></OBJECT>\n"
            + "</DATABASE>";

        var inventory = BackupParser.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)));

        Assert.Empty(inventory.AdminUsers);
        Assert.Empty(inventory.AdminGroups);
        Assert.Empty(inventory.AdminPermissions);
    }
}
