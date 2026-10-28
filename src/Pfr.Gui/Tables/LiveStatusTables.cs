using Pfr.Core.Wapi;

namespace Pfr.Gui.Tables;

/// <summary>The tree shown for a grid's live status: member, then its nodes (for HA pairs), then services.</summary>
public static class LiveStatusTables
{
    public static TreeTableDefinition Members(LiveGridStatus status)
    {
        var columns = new List<TableColumn>
        {
            new("Member / node / service", Width: 330),
            new("HA", Width: 140),
            new("NIOS version", Width: 150),
            new("Hotfixes", Width: 150),
            new("Status", ColumnKind.Status, 90),
            new("Detail"),
        };

        var roots = new List<TreeRow>();
        foreach (var member in status.Members)
        {
            var row = new TreeRow(
            [
                member.HostName.Length > 0 ? member.HostName : "(unnamed member)",
                member.HaSummary,
                member.Version ?? "",
                string.Join(", ", member.Hotfixes),
                HealthText(member.Health),
                member.HaProblem ?? "",
            ], isGroup: true);

            if (member.Nodes.Count == 1)
            {
                AddServices(row, member.Nodes[0]);
            }
            else
            {
                foreach (var node in member.Nodes)
                {
                    var nodeRow = row.Add(new TreeRow(
                    [
                        $"Node {node.Position}",
                        node.HaStatus,
                        node.Model,
                        "",
                        HealthText(node.Health),
                        "",
                    ], isGroup: true));
                    AddServices(nodeRow, node);
                }
            }

            // Open the members that need attention.
            row.IsExpanded = member.Health is Health.Warning or Health.Failed;
            foreach (var node in row.Children.Where(c => c.Cells[4] is "Warning" or "Failed")) node.IsExpanded = true;
            roots.Add(row);
        }

        return new TreeTableDefinition("members and services", columns, roots, "services");
    }

    private static void AddServices(TreeRow parent, LiveNode node)
    {
        foreach (var service in node.Services)
        {
            parent.Add(new TreeRow(
            [
                service.Name,
                "",
                "",
                "",
                service.State.ToString(),
                service.Description,
            ]));
        }
    }

    public static string HealthText(Health health) => LiveStatusReport.HealthText(health);
}
