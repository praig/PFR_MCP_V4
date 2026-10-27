using System.Text.Json;
using System.Text.Json.Serialization;
using Pfr.Core;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;

namespace Pfr.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "version" or "--version" => Version(),
                "inspect" => Inspect(args[1..]),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Version()
    {
        Console.WriteLine(AppInfo.Banner);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run 'pfr help' for usage.");
        return 2;
    }

    private static int Inspect(string[] args)
    {
        var json = args.Contains("--json");
        var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (paths.Count != 1)
        {
            Console.Error.WriteLine("Usage: pfr inspect <backup.bak | onedb.xml> [--json]");
            return 2;
        }

        var path = paths[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        var inventory = BackupParser.ParseFile(path);

        if (json)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() },
            };
            Console.WriteLine(JsonSerializer.Serialize(inventory, options));
        }
        else
        {
            Console.Write(InventorySummary.Render(inventory));
        }

        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(AppInfo.Banner);
        Console.WriteLine();
        Console.WriteLine("Usage: pfr <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  inspect <file> [--json]   Summarize a NIOS grid backup (.bak) or onedb.xml");
        Console.WriteLine("  help                      Show this help");
        Console.WriteLine("  version                   Show version");
        Console.WriteLine();
        Console.WriteLine("Planned modules:");
        foreach (var (_, title, summary) in ModuleCatalog.All)
        {
            Console.WriteLine($"  {title,-16}{summary}");
        }
    }
}
