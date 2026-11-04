using Pfr.Core.Nios.Model;
using Pfr.Core.Nios.OneDb;

namespace Pfr.Core.Nios.Backup;

/// <summary>
/// Reads administrators: accounts, groups, roles, the permissions and interface access granted to them, and the
/// order in which authentication methods are tried.
/// <para>
/// Only names, flags and relationships are read. Passwords, password hashes, certificate details and keys are
/// dropped by <see cref="SensitiveData"/> before they get here. Object types that exist only to hold settings
/// (per-group password and lockout policy, LOM users, authentication server definitions, SSH key lists) are not
/// read at all.
/// </para>
/// <para>
/// How NIOS stores it (checked against NIOS 9.0 backups): an account is <c>one.admin</c>; it is tied to its group by
/// <c>one.admin_group_link</c> (the group is written as ".name"); groups get roles through
/// <c>one.admin_group_role_link</c>; a permission is a <c>one.hier_rule</c> whose entity is a group or a role and
/// whose data is "type$key" for the resource; interface access (GUI, CLI, API...) is <c>one.action_rule</c>.
/// </para>
/// </summary>
internal static class AdminParser
{
    private const string UserType = "one.admin";
    private const string GroupType = "one.admin_group";
    private const string RoleType = "one.role";
    private const string UserGroupLinkType = "one.admin_group_link";
    private const string GroupRoleLinkType = "one.admin_group_role_link";
    private const string PermissionType = "one.hier_rule";
    private const string AccessType = "one.action_rule";
    private const string AuthMethodType = "one.admin_auth_method";

    private const string TypePrefix = ".com.infoblox.";
    private const string AccessPrefix = "access_";

    public static IEnumerable<string> RequiredTypes =>
    [
        UserType, GroupType, RoleType, UserGroupLinkType, GroupRoleLinkType, PermissionType, AccessType, AuthMethodType,
    ];

    public static void Apply(GridInventory inv, IReadOnlyDictionary<string, List<OneDbObject>> byType)
    {
        var groups = new Dictionary<string, AdminGroup>(StringComparer.Ordinal);
        foreach (var o in byType[GroupType])
        {
            if (o.Get("name") is not { Length: > 0 } name) continue;
            var group = new AdminGroup
            {
                Name = name,
                Comment = NullIfEmpty(o.Get("comment")),
                IsSuperuser = o.GetBool("superuser"),
                Disabled = o.GetBool("disabled"),
                UiModel = NullIfEmpty(o.Get("ui_model")),
                RestrictedUserAccess = o.GetBool("enable_restricted_user_access"),
            };
            if (groups.TryAdd(name, group)) inv.AdminGroups.Add(group);
        }

        foreach (var o in byType[RoleType])
        {
            if (o.Get("name") is not { Length: > 0 } name) continue;
            inv.AdminRoles.Add(new AdminRole
            {
                Name = name,
                Comment = NullIfEmpty(o.Get("comment")),
                IsPredefined = o.GetBool("pre_defined"),
                Disabled = o.GetBool("disabled"),
            });
        }

        // Which group each account belongs to.
        var groupOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var link in byType[UserGroupLinkType])
        {
            if (link.Get("admin") is { Length: > 0 } admin && link.Get("admin_group") is { Length: > 0 } group)
                groupOf[admin] = GroupName(group);
        }

        foreach (var o in byType[UserType])
        {
            if (o.Get("name") is not { Length: > 0 } name) continue;
            groupOf.TryGetValue(name, out var groupName);
            inv.AdminUsers.Add(new AdminUser
            {
                Name = name,
                GroupName = groupName,
                IsSuperuser = groupName is not null && groups.TryGetValue(groupName, out var g) && g.IsSuperuser,
                AuthType = NullIfEmpty(o.Get("auth_type")),
                Status = NullIfEmpty(o.Get("status")),
                Disabled = o.GetBool("disabled"),
                IsInternal = o.GetBool("internal_user"),
                UsesSshKeys = o.GetBool("use_ssh_keys"),
                LastLogin = LicenseParser.FromEpoch(o.Get("last_login")),
                TimeZone = NullIfEmpty(o.Get("time_zone")),
                Comment = NullIfEmpty(o.Get("comment")),
            });
        }

        foreach (var o in byType[GroupRoleLinkType])
        {
            if (o.Get("admin_group") is not { Length: > 0 } group || o.Get("role") is not { Length: > 0 } role) continue;
            inv.AdminGroupRoles.Add(new AdminGroupRole
            {
                GroupName = GroupName(group),
                RoleName = role,
                Position = o.GetInt("position") ?? 0,
            });
        }

        foreach (var o in byType[PermissionType])
        {
            if (!TryOwner(o.Get("entity"), out var ownerType, out var ownerName)) continue;
            var (resourceType, resourceKey) = SplitReference(o.GetOrEmpty("data"));
            if (resourceType.Length == 0) continue;
            inv.AdminPermissions.Add(new AdminPermission
            {
                OwnerType = ownerType,
                OwnerName = ownerName,
                ResourceType = resourceType,
                ResourceKey = resourceKey,
                SubType = NullIfEmpty(o.Get("sub_type")),
                CanRead = o.GetBool("allow_read"),
                CanModify = o.GetBool("allow_modify"),
                CanDelete = o.GetBool("allow_delete"),
                CanCreate = o.GetBool("allow_create"),
                Unmanaged = o.GetBool("unmanaged_flag"),
            });
        }

        foreach (var o in byType[AccessType])
        {
            if (!TryOwner(o.Get("entity"), out var ownerType, out var ownerName)) continue;
            if (o.Get("action") is not { Length: > 0 } action) continue;
            inv.AdminAccessRights.Add(new AdminAccess
            {
                OwnerType = ownerType,
                OwnerName = ownerName,
                Interface = action.StartsWith(AccessPrefix, StringComparison.Ordinal) ? action[AccessPrefix.Length..] : action,
                Allowed = o.GetBool("allow_flag"),
            });
        }

        foreach (var o in byType[AuthMethodType])
        {
            inv.AdminAuthMethods.Add(new AdminAuthMethod
            {
                Position = o.GetInt("position") ?? 0,
                Type = o.GetOrEmpty("auth_method_type"),
                Name = NullIfEmpty(o.Get("auth_method_name")),
            });
        }

        Sort(inv);
    }

    /// <summary>
    /// Splits a reference such as ".com.infoblox.dns.zone$._default.example.com" into its type ("dns.zone") and key
    /// ("._default.example.com"). A value with no "$" is all type.
    /// </summary>
    internal static (string Type, string Key) SplitReference(string value)
    {
        var dollar = value.IndexOf('$');
        var type = dollar < 0 ? value : value[..dollar];
        var key = dollar < 0 ? "" : value[(dollar + 1)..];
        if (type.StartsWith(TypePrefix, StringComparison.Ordinal)) type = type[TypePrefix.Length..];
        return (type, key);
    }

    /// <summary>Who a permission or access rule belongs to: an admin group or a role. Anything else is not for us.</summary>
    private static bool TryOwner(string? entity, out string ownerType, out string ownerName)
    {
        ownerType = ownerName = "";
        if (string.IsNullOrEmpty(entity)) return false;

        var (type, key) = SplitReference(entity);
        if (key.Length == 0) return false;
        switch (type)
        {
            case GroupType:
                ownerType = AdminPermission.GroupOwner;
                ownerName = GroupName(key);
                return true;
            case RoleType:
                ownerType = AdminPermission.RoleOwner;
                ownerName = key;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Group references are written with a leading dot (".helpdesk").</summary>
    private static string GroupName(string reference) => reference.StartsWith('.') ? reference[1..] : reference;

    private static void Sort(GridInventory inv)
    {
        static int Name(string a, string b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

        inv.AdminGroups.Sort((a, b) => Name(a.Name, b.Name));
        inv.AdminUsers.Sort((a, b) => Name(a.Name, b.Name));
        inv.AdminRoles.Sort((a, b) => Name(a.Name, b.Name));
        inv.AdminGroupRoles.Sort((a, b) =>
        {
            var c = Name(a.GroupName, b.GroupName);
            if (c != 0) return c;
            c = a.Position.CompareTo(b.Position);
            return c != 0 ? c : Name(a.RoleName, b.RoleName);
        });
        inv.AdminPermissions.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.OwnerType, b.OwnerType);
            if (c != 0) return c;
            c = Name(a.OwnerName, b.OwnerName);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.ResourceType, b.ResourceType);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.ResourceKey, b.ResourceKey);
            return c != 0 ? c : string.CompareOrdinal(a.SubType, b.SubType);
        });
        inv.AdminAccessRights.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.OwnerType, b.OwnerType);
            if (c != 0) return c;
            c = Name(a.OwnerName, b.OwnerName);
            return c != 0 ? c : string.CompareOrdinal(a.Interface, b.Interface);
        });
        inv.AdminAuthMethods.Sort((a, b) => a.Position.CompareTo(b.Position));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
