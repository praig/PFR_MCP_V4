using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Reports;

namespace Pfr.Cli;

// The Row count report: how many DNS records each zone holds, as an Excel workbook.
public static partial class Program
{
    private const string RowCountUsage =
        "Usage: pfr row-count (<backup file> | --client <name> --grid <name>) --out <file.xlsx> [--no-reverse] [--no-empty]\n" +
        "  Writes an Excel workbook: a Home sheet with a row per zone (view, zone, record count, the zone linking to its sheet),\n" +
        "  then a sheet per zone, named view.zone, listing its records. --no-reverse leaves out reverse zones,\n" +
        "  --no-empty leaves out zones with no records.";

    private static int RunRowCount(Args cli)
    {
        if (cli.Value("--out") is not { } outPath) return Fail(RowCountUsage, 2);

        GridInventory inventory;
        if (cli.Positional.Any())
        {
            inventory = BackupParser.ParseFile(cli.Positional.First());
        }
        else
        {
            inventory = LatestInventory(cli, out var workspace, out _, out _);
            workspace.Store.Dispose();
        }

        var result = RowCountReport.Run(inventory, new RowCountOptions
        {
            IncludeReverseZones = !cli.Has("--no-reverse"),
            IncludeEmptyZones = !cli.Has("--no-empty"),
        });

        if (!outPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) outPath += ".xlsx";
        var notes = RowCountReport.WriteFile(outPath, result, inventory.GridName);

        Console.Error.WriteLine(
            $"Wrote {result.Zones.Count + 1:N0} sheets ({result.Zones.Count:N0} zones, {result.TotalRecords:N0} records) to {Path.GetFullPath(outPath)}.");
        foreach (var note in notes) Console.Error.WriteLine($"Note: {note}");
        return 0;
    }
}
