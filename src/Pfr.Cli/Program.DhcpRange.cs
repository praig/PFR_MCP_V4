using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;
using Pfr.Core.Reports;

namespace Pfr.Cli;

// The DHCP range report: every range with its lease time and one line per option set on it.
public static partial class Program
{
    private const string DhcpRangeUsage =
        "Usage: pfr dhcp-range-report (<backup file> | --client <name> --grid <name>) --out <file.xlsx|file.csv|file.json>\n" +
        "                             [--only-with-options] [--no-ipv6] [--no-inherited] [--excel-safe]\n" +
        "  One line per option that applies to a DHCP range: network/CIDR/view, start address, end address, default lease (seconds),\n" +
        "  option number, option value, and where the option is set (the range, its network, a container above it, or the grid).\n" +
        "  --no-inherited lists only the options set on the range itself. A range with no options has one line with the option\n" +
        "  columns empty, unless --only-with-options. The name's ending chooses the format (.xlsx is an Excel workbook; none means .xlsx).";

    private static int RunDhcpRangeReport(Args cli)
    {
        if (cli.Value("--out") is not { } outPath) return Fail(DhcpRangeUsage, 2);

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

        var result = DhcpRangeReport.Run(inventory, new DhcpRangeReportOptions
        {
            IncludeRangesWithoutOptions = !cli.Has("--only-with-options"),
            IncludeIpv6 = !cli.Has("--no-ipv6"),
            IncludeInheritedOptions = !cli.Has("--no-inherited"),
        });

        if (Path.GetExtension(outPath).Length == 0) outPath += ".xlsx";
        var format = ResultExport.Write(result.ToResult(), outPath, spreadsheetSafe: cli.Has("--excel-safe"), title: DhcpRangeReport.Title);

        Console.Error.WriteLine(
            $"Wrote {result.Rows.Count:N0} lines ({result.RangeCount:N0} ranges, {result.OptionCount:N0} options, {result.InheritedOptionCount:N0} of them inherited) to {Path.GetFullPath(outPath)} as {ResultExport.Describe(format)}.");
        if (!result.OptionsRead)
            Console.Error.WriteLine("Note: this snapshot was saved before DHCP lease times and options were read, so those columns are empty. Import the backup again.");
        if (result.UnmatchedOptions > 0)
            Console.Error.WriteLine($"Note: {result.UnmatchedOptions:N0} option(s) in the backup belong to a range that could not be identified and are not listed.");
        return 0;
    }
}
