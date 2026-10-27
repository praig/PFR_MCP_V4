using Pfr.Core.Nios.Model;
using Pfr.Core.Nios.OneDb;

namespace Pfr.Core.Nios.Backup;

/// <summary>
/// Reads installed licenses: per-node licenses, grid-wide licenses, and each
/// member's next expiry. License keys are never read (see <see cref="SensitiveData"/>).
/// </summary>
internal static class LicenseParser
{
    private const string NodeLicenseType = "one.product_license";
    private const string GridWideType = "one.license_grid_wide";
    private const string MemberSummaryType = "one.vnode_license";

    public static IEnumerable<string> RequiredTypes => [NodeLicenseType, GridWideType, MemberSummaryType];

    public static void Apply(GridInventory inv, IReadOnlyDictionary<string, List<OneDbObject>> byType)
    {
        // Node licenses belong to physical nodes; map them up to members.
        var nodeById = new Dictionary<int, (GridMember Member, GridNode Node)>();
        foreach (var m in inv.Members)
        {
            foreach (var n in m.Nodes)
            {
                if (n.PhysicalOid is { } p) nodeById[p] = (m, n);
            }
        }

        foreach (var lic in byType[NodeLicenseType])
        {
            var type = lic.Get("license_type");
            if (string.IsNullOrEmpty(type) || lic.GetInt("pnode") is not { } pnode
                || !nodeById.TryGetValue(pnode, out var owner))
            {
                continue;
            }

            inv.Licenses.Add(new GridLicense
            {
                Type = type,
                MemberOid = owner.Member.Oid,
                NodePosition = owner.Node.Position,
                NodeSerial = owner.Node.SerialNumber,
                Kind = Normalize(lic.Get("license_kind")),
                Expires = FromEpoch(lic.Get("expiry_date")),
            });

            if (!owner.Member.Licenses.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                owner.Member.Licenses.Add(type);
            }
        }

        foreach (var lic in byType[GridWideType])
        {
            if (lic.Get("license_type") is not { Length: > 0 } type) continue;
            inv.Licenses.Add(new GridLicense
            {
                Type = type,
                IsGridWide = true,
                Kind = "Grid-wide",
                Expires = FromEpoch(lic.Get("expiry_date")),
            });
        }

        // Per-member summary: the earliest expiry across everything that applies to it.
        foreach (var summary in byType[MemberSummaryType])
        {
            if (inv.FindMember(summary.GetInt("parent")) is { } member)
            {
                member.NextLicenseExpiry = FromEpoch(summary.Get("next_expiry"));
            }
        }

        // NIOS's summary may be missing or stale; a member's own license dates count too.
        foreach (var m in inv.Members)
        {
            var own = inv.Licenses.Where(l => l.MemberOid == m.Oid && l.Expires is not null).Select(l => l.Expires);
            m.NextLicenseExpiry = own.Append(m.NextLicenseExpiry).Where(e => e is not null).Min();
            m.Licenses.Sort(StringComparer.OrdinalIgnoreCase);
        }
        inv.Licenses.Sort((a, b) =>
        {
            var c = b.IsGridWide.CompareTo(a.IsGridWide); // grid-wide first
            if (c != 0) return c;
            c = string.Compare(inv.FindMember(a.MemberOid)?.HostName, inv.FindMember(b.MemberOid)?.HostName,
                StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = Nullable.Compare(a.NodePosition, b.NodePosition);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>NIOS stores expiry as Unix seconds; 0 or blank means permanent.</summary>
    internal static DateTimeOffset? FromEpoch(string? value) =>
        long.TryParse(value, out var s) && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    private static string? Normalize(string? kind) => kind switch
    {
        null or "" => null,
        _ => char.ToUpperInvariant(kind[0]) + kind[1..].ToLowerInvariant(),
    };
}
