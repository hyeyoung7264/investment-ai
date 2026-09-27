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

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ct = cts.Token;
        var sub = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        var opts = CliOptions.Parse(args.Skip(sub is null ? 1 : 2));
        try
        {
            switch (args[0])
            {
                case "migrate":
                    await using (var db = Database.Create(opts.Get("db")))
                        await db.Database.MigrateAsync();
                    Console.WriteLine("database migrated");
                    return 0;
                case "ingest":
                    return await MarketDataCommands.IngestAsync(sub ?? "all", opts, ct);
                case "quality":
                    return await MarketDataCommands.QualityAsync(opts, ct);
                case "backtest":
                    return await ResearchCommands.BacktestAsync(opts, ct);
                case "rerun":
                    return await ResearchCommands.RerunAsync(opts, ct);
                case "runs":
                    return await ResearchCommands.ListRunsAsync(opts, ct);
                case "walkforward":
                    return await StudyCommands.WalkForwardAsync(opts, ct);
                case "robustness":
                    return await StudyCommands.RobustnessAsync(opts, ct);
                case "regime":
                    return await StudyCommands.RegimeAsync(opts, ct);
                case "evaluations":
                    return await StudyCommands.EvaluationsAsync(opts, ct);
                case "failures":
                    return await StudyCommands.FailuresAsync(opts, ct);
                case "agent" when sub == "cycle":
                    return await AgentCommands.CycleAsync(opts, ct);
                case "paper":
                    return await PaperCommands.RunAsync(sub, opts, ct);
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
              ingest [securities|indices|prices|all] [--from 2015-01-01] [--to today] [--tickers a,b] [--concurrency 4]
                                            collect KIND security master (incl. delisted) and Naver daily prices
              quality [--from] [--to] [--out reports/data-quality.json]
                                            data integrity report
              backtest --strategy <id|all> [--from 2017-01-01] [--to today] [--params json] [--top 100]
                       [--capital 1e8] [--commission 0.00015] [--slippage 0.001] [--impact 0.1]
                       [--max-positions 10] [--max-weight 0.1] [--max-sector 0.3] [--max-daily-loss 0.03]
                       [--max-drawdown 0.25] [--stop-loss 0.10|none] [--max-participation 0.05] [--label text]
              rerun --run <id>              re-execute a stored run and verify data/result hashes
              runs [--limit 20]             list recent runs (net metrics)
              walkforward --strategy <id> --hypothesis "text" [--params json | --grid '[json,...]']
                       [--from 2017-01-01] [--to today] [--train 3] [--validation 1] [--oos 1] [--markets Kospi]
                       + backtest options          rolling train/validation/OOS study, then Promotion Gate
              robustness --strategy <id> --params json [--neighbors '[{"EntryZ":-3},...]'] [--cost-stress 2]
                       [--universe-sizes 50,200]   diagnostic walk-forwards around one parameter set (no status changes)
              regime [--index KOSPI]        classify and store daily market regimes
              evaluations [--limit 30]      gate decisions and current strategy statuses
              failures                      rejected experiments (never deleted)
              agent cycle [--max-studies 3] [--dry-run] [--to date] + backtest options
                                            research loop: observe evidence, propose (rules / Claude if ANTHROPIC_API_KEY),
                                            skip duplicates, run walk-forward + gate, write reports/research/cycle-*.md
              paper start --strategy <id> [--version-id guid] [--name] [--start date] + backtest options
                                            open a forward paper session (VALIDATED versions only, no backfill)
              paper daily [--ingest]        (optionally refresh data, then) advance all active paper sessions
              paper status                  equity, positions, next-open orders, recent paper trades
            NOTE: there is no live trading and no broker connection in this system.

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
