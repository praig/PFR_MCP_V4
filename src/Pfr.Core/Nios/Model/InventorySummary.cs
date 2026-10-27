using System.Text;

namespace Pfr.Core.Nios.Model;

/// <summary>Plain-text inventory summary shared by the CLI and (later) reports.</summary>
public static class InventorySummary
{
    public static string Render(GridInventory inv)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Grid:          {inv.GridName ?? "(unknown)"}");
        sb.AppendLine($"NIOS version:  {inv.NiosVersion ?? "(unknown)"}");
        sb.AppendLine();

        sb.AppendLine($"Members ({inv.Members.Count})");
        var rows = inv.Members.Select(m =>
        {
            var node = m.Nodes.FirstOrDefault();
            return new[]
            {
                m.HostName,
                m.Role,
                string.Join(", ", m.Services),
                m.Ipv4Address ?? "",
                node?.HardwareType ?? "",
                m.HaEnabled ? $"HA ({m.Nodes.Count} nodes)" : "",
                m.UpgradeGroup ?? "",
            };
        }).ToList();
        AppendTable(sb, ["Host name", "Role", "Services", "IPv4", "Model", "HA", "Upgrade group"], rows);
        sb.AppendLine();

        sb.AppendLine($"DNS views ({inv.DnsViews.Count})");
        foreach (var v in inv.DnsViews.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            var zoneCount = inv.Zones.Count(z => z.ViewKey == v.Key);
            var flags = new List<string>();
            if (v.RecursionEnabled) flags.Add("recursion");
            if (v.Disabled) flags.Add("disabled");
            sb.AppendLine($"  {v.Name,-30} {zoneCount,5} zones  {string.Join(", ", flags)}");
        }
        sb.AppendLine();

        sb.AppendLine($"Zones ({inv.Zones.Count})");
        foreach (var g in inv.Zones.GroupBy(z => z.ZoneType).OrderBy(g => g.Key))
        {
            var reverse = g.Count(z => z.IsReverse);
            sb.AppendLine($"  {g.Key,-16} {g.Count(),5}   ({g.Count() - reverse} forward, {reverse} reverse)");
        }
        sb.AppendLine();

        var nets = inv.Networks;
        sb.AppendLine($"IPAM ({inv.NetworkViews.Count} network views)");
        sb.AppendLine($"  Containers      {nets.Count(n => n.IsContainer),5}   ({nets.Count(n => n.IsContainer && !n.IsIpv6)} IPv4, {nets.Count(n => n.IsContainer && n.IsIpv6)} IPv6)");
        sb.AppendLine($"  Networks        {nets.Count(n => !n.IsContainer),5}   ({nets.Count(n => !n.IsContainer && !n.IsIpv6)} IPv4, {nets.Count(n => !n.IsContainer && n.IsIpv6)} IPv6)");
        sb.AppendLine();

        sb.AppendLine($"DHCP ranges ({inv.DhcpRanges.Count})");
        foreach (var g in inv.DhcpRanges.GroupBy(r => r.AssociationType).OrderBy(g => g.Key))
        {
            sb.AppendLine($"  {(g.Key.Length > 0 ? g.Key : "(none)"),-16} {g.Count(),5}");
        }
        sb.AppendLine($"Failover associations ({inv.FailoverAssociations.Count})");
        foreach (var f in inv.FailoverAssociations)
        {
            var peers = f.IsMicrosoft
                ? $"Microsoft DHCP  {f.MsServerAddress ?? "?"} <-> {f.MsPartnerAddress ?? "?"}"
                  + (f.MsMode is null ? "" : $"  ({f.MsMode.ToLowerInvariant()})")
                : $"{inv.FindMember(f.PrimaryOid)?.HostName ?? "?"} / {inv.FindMember(f.SecondaryOid)?.HostName ?? "?"}";
            sb.AppendLine($"  {f.Name,-20} {peers}");
        }
        sb.AppendLine();

        sb.AppendLine($"DNS records ({inv.Records.Count})");
        foreach (var g in inv.Records.GroupBy(r => r.Type).OrderBy(g => g.Key))
        {
            sb.AppendLine($"  {g.Key,-16} {g.Count(),5}");
        }

        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, string[] headers, List<string[]> rows)
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        sb.AppendLine("  " + string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        sb.AppendLine("  " + string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var r in rows)
        {
            sb.AppendLine("  " + string.Join("  ", r.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
        }
    }
}
