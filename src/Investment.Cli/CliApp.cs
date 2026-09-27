using Investment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Investment.Cli;

public static class CliApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var opts = CliOptions.Parse(args.Skip(1));
        try
        {
            switch (args[0])
            {
                case "migrate":
                    await using (var db = Database.Create(opts.Get("db")))
                        await db.Database.MigrateAsync();
                    Console.WriteLine("database migrated");
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown command: {args[0]}");
                    PrintHelp();
                    return 2;
            }
        }
        catch (CliUsageException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            investment-cli <command> [--option value ...]

              migrate                       apply EF Core migrations

            common options: --db <connection string>  (default: INVESTMENT_DB_CONNECTION or local dev cluster)
            """);
    }
}

public sealed class CliUsageException(string message) : Exception(message);

public sealed class CliOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(IEnumerable<string> args)
    {
        var o = new CliOptions();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--")) throw new CliUsageException($"unexpected argument: {list[i]}");
            var key = list[i][2..];
            if (i + 1 < list.Count && !list[i + 1].StartsWith("--")) o._values[key] = list[++i];
            else o._values[key] = "true";
        }
        return o;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public string Require(string key) => Get(key) ?? throw new CliUsageException($"missing --{key}");

    public int GetInt(string key, int fallback) => Get(key) is { } v ? int.Parse(v) : fallback;

    public double GetDouble(string key, double fallback) => Get(key) is { } v ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    public DateOnly GetDate(string key, DateOnly fallback) => Get(key) is { } v ? DateOnly.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    public bool Has(string key) => _values.ContainsKey(key);
}
