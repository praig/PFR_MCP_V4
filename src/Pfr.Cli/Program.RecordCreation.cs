using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;
using Pfr.Core.Reports;

namespace Pfr.Cli;

// The Record creation report: each A record and host with its comment, creation date and one extensible attribute.
public static partial class Program
{
    private const string RecordCreationUsage =
        "Usage: pfr record-creation-report (<backup file> | --client <name> --grid <name>) --out <file.xlsx|file.csv|file.json>\n" +
        "                                  [--attribute <name>] [--no-a] [--no-hosts] [--include-ipam-only] [--excel-safe]\n" +
        "  One line per A record and per address of a host: record type (A or Host), FQDN, IP address, comment, creation date (UTC)\n" +
        "  and the value of an extensible attribute (--attribute; Mnemonic by default). --no-a and --no-hosts leave those out;\n" +
        "  --include-ipam-only adds hosts that are not in DNS. The name's ending chooses the format (.xlsx is an Excel workbook;\n" +
        "  none means .xlsx).";

    private static int RunRecordCreationReport(Args cli)
    {
        if (cli.Value("--out") is not { } outPath) return Fail(RecordCreationUsage, 2);

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

        var result = RecordCreationReport.Run(inventory, new RecordCreationOptions
        {
            Attribute = cli.Value("--attribute") ?? RecordCreationReport.DefaultAttribute,
            IncludeARecords = !cli.Has("--no-a"),
            IncludeHosts = !cli.Has("--no-hosts"),
            IncludeIpamOnlyHosts = cli.Has("--include-ipam-only"),
        });

        if (Path.GetExtension(outPath).Length == 0) outPath += ".xlsx";
        var format = ResultExport.Write(result.ToResult(), outPath, spreadsheetSafe: cli.Has("--excel-safe"), title: RecordCreationReport.Title);

        Console.Error.WriteLine(
            $"Wrote {result.Rows.Count:N0} lines ({result.Count("A"):N0} A record, {result.Count("Host"):N0} host; {result.WithAttribute:N0} with a {result.Attribute} value; " +
            $"{result.WithoutCreationDate:N0} with no creation date) to {Path.GetFullPath(outPath)} as {ResultExport.Describe(format)}.");
        if (!result.AttributeFound)
        {
            Console.Error.WriteLine($"Note: no A record or host listed has an extensible attribute called \"{result.Attribute}\" set." +
                (result.AttributesSeen.Count > 0 ? $" Attributes set on them: {string.Join(", ", result.AttributesSeen)}." : " None of them has any."));
        }
        if (!result.RecordDetailsRead)
            Console.Error.WriteLine("Note: this snapshot was saved before comments, creation dates and extensible attributes were read, so those columns are empty. Import the backup again.");
        return 0;
    }
}
