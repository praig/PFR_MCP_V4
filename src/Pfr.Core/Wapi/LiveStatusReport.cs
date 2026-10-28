using System.Text;

namespace Pfr.Core.Wapi;

/// <summary>Plain-text rendering of a <see cref="LiveGridStatus"/>, for the CLI.</summary>
public static class LiveStatusReport
{
    public static string Render(LiveGridStatus status, string? label = null)
    {
        var text = new StringBuilder();
        var gridName = status.GridName is { Length: > 0 } name ? $"grid \"{name}\"" : "grid";
        var heading = label is { Length: > 0 } ? $"{label}: " : "";
        text.AppendLine($"{heading}{gridName} · WAPI v{status.ApiVersion} · collected {status.CollectedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        text.AppendLine($"Overall: {HealthText(status.Health)} · {Count(status.Members.Count, "member")} · NIOS {status.VersionSummary}");
        text.AppendLine();

        if (status.Members.Count > 0)
        {
            var rows = status.Members
                .Select(m => new[]
                {
                    m.HostName,
                    m.HaSummary,
                    m.Version ?? "?",
                    m.Hotfixes.Count == 0 ? "-" : string.Join(", ", m.Hotfixes),
                    HealthText(m.Health),
                })
                .ToList();
            var headers = new[] { "Member", "HA", "NIOS version", "Hotfixes", "Status" };
            var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();

            text.AppendLine(Line(headers, widths));
            foreach (var row in rows) text.AppendLine(Line(row, widths));
        }
        else
        {
            text.AppendLine("The grid reported no members.");
        }

        if (status.Problems.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Problems");
            foreach (var problem in status.Problems) text.AppendLine($"  {problem}");
        }

        if (status.Notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Notes");
            foreach (var note in status.Notes) text.AppendLine($"  {note}");
        }
        return text.ToString();
    }

    public static string HealthText(Health health) => health switch
    {
        Health.Ok => "OK",
        Health.Warning => "Warning",
        Health.Failed => "Failed",
        _ => "Unknown",
    };

    private static string Line(string[] cells, int[] widths) =>
        string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
