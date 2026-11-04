using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;
using Pfr.Core.Reports;

namespace Pfr.Cli;

// The Scavenge report: each A record with its creator, name, IP address and last-queried date.
public static partial class Program
{
    private const string ScavengeUsage =
        "Usage: pfr scavenge-report (<backup file> | --client <name> --grid <name>) --out <file.xlsx|file.csv|file.json>\n" +
        "                           [--creator DYNAMIC,STATIC,SYSTEM] [--type A,CNAME,PTR] [--excel-safe]\n" +
        "  One line per A, CNAME or PTR record: creator (STATIC, DYNAMIC or SYSTEM), FQDN, IP address (an A record's, or the one a PTR's\n" +
        "  name stands for; empty for a CNAME), last queried (UTC date; empty when NIOS has no time for the record) and record type.\n" +
        "  --creator and --type limit it to those creators and types.\n" +
        "  The name's ending chooses the format (.xlsx is an Excel workbook; none means .xlsx).";

    private static int RunScavengeReport(Args cli)
    {
        if (cli.Value("--out") is not { } outPath) return Fail(ScavengeUsage, 2);

        HashSet<string>? creators = null;
        if (cli.Value("--creator") is { } list)
        {
            creators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!ScavengeReport.CreatorNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"\"{name}\" is not a creator. Use {string.Join(", ", ScavengeReport.CreatorNames)}.");
                creators.Add(name);
            }
        }

        HashSet<DnsRecordType>? types = null;
        if (cli.Value("--type") is { } typeList)
        {
            types = [];
            foreach (var name in typeList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<DnsRecordType>(name, ignoreCase: true, out var type) || !ScavengeReport.RecordTypes.Contains(type))
                    throw new ArgumentException($"\"{name}\" is not a type this report covers. Use {string.Join(", ", ScavengeReport.RecordTypes)}.");
                types.Add(type);
            }
        }

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

        var result = ScavengeReport.Run(inventory, new ScavengeReportOptions { Creators = creators, Types = types });

        if (Path.GetExtension(outPath).Length == 0) outPath += ".xlsx";
        var format = ResultExport.Write(result.ToResult(), outPath, spreadsheetSafe: cli.Has("--excel-safe"), title: ScavengeReport.Title);

        Console.Error.WriteLine(
            $"Wrote {result.Rows.Count:N0} records ({result.Count(DnsRecordType.A):N0} A, {result.Count(DnsRecordType.CNAME):N0} CNAME, {result.Count(DnsRecordType.PTR):N0} PTR; " +
            $"{result.Count("DYNAMIC"):N0} dynamic, {result.Count("STATIC"):N0} static, " +
            $"{result.Count("SYSTEM"):N0} system; {result.NeverQueried:N0} with no last-queried date) to {Path.GetFullPath(outPath)} as {ResultExport.Describe(format)}.");
        if (!result.RecordOriginsRead)
            Console.Error.WriteLine("Note: this snapshot was saved before record creators and last-queried dates were read, so those columns are empty. Import the backup again.");
        return 0;
    }
}
