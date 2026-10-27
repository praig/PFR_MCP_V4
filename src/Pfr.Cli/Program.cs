using Pfr.Core;

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

        if (args[0] is "version" or "--version")
        {
            Console.WriteLine(AppInfo.Banner);
            return 0;
        }

        Console.Error.WriteLine($"Unknown command: {args[0]}");
        Console.Error.WriteLine("Run 'pfr help' for usage.");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(AppInfo.Banner);
        Console.WriteLine();
        Console.WriteLine("Usage: pfr <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  help       Show this help");
        Console.WriteLine("  version    Show version");
        Console.WriteLine();
        Console.WriteLine("Planned modules:");
        foreach (var (_, title, summary) in ModuleCatalog.All)
        {
            Console.WriteLine($"  {title,-16}{summary}");
        }
    }
}
