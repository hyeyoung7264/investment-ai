using Investment.Domain.Market;
using Npgsql;
using NpgsqlTypes;

namespace Investment.Persistence;

public sealed record UpsertResult(long Inserted, long Updated);

/// <summary>
/// High-volume price I/O with raw Npgsql (binary COPY + ON CONFLICT). EF Core is used for everything else.
/// </summary>
public sealed class MarketDataStore(string connectionString)
{
    public async Task<UpsertResult> UpsertDailyPricesAsync(IReadOnlyCollection<DailyPrice> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return new UpsertResult(0, 0);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "CREATE TEMP TABLE tmp_daily_prices (LIKE daily_prices INCLUDING DEFAULTS) ON COMMIT DROP", conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var w = await conn.BeginBinaryImportAsync(
            "COPY tmp_daily_prices (ticker, date, open, high, low, close, volume, trading_value_estimate, is_halted, source, ingested_at) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var r in rows)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(r.Ticker, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(r.Date, NpgsqlDbType.Date, ct);
                await w.WriteAsync(r.Open, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.High, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.Low, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.Close, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.Volume, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(r.TradingValueEstimate, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.IsHalted, NpgsqlDbType.Boolean, ct);
                await w.WriteAsync(r.Source, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(r.IngestedAt, NpgsqlDbType.TimestampTz, ct);
            }
            await w.CompleteAsync(ct);
        }

        // Only rows whose values actually changed are rewritten; (xmax = 0) distinguishes inserts from updates.
        const string merge = """
            WITH up AS (
              INSERT INTO daily_prices AS d SELECT * FROM tmp_daily_prices
              ON CONFLICT (ticker, date) DO UPDATE SET
                open = EXCLUDED.open, high = EXCLUDED.high, low = EXCLUDED.low, close = EXCLUDED.close,
                volume = EXCLUDED.volume, trading_value_estimate = EXCLUDED.trading_value_estimate,
                is_halted = EXCLUDED.is_halted, source = EXCLUDED.source, ingested_at = EXCLUDED.ingested_at
              WHERE (d.open, d.high, d.low, d.close, d.volume, d.is_halted)
                    IS DISTINCT FROM (EXCLUDED.open, EXCLUDED.high, EXCLUDED.low, EXCLUDED.close, EXCLUDED.volume, EXCLUDED.is_halted)
              RETURNING (xmax = 0) AS inserted)
            SELECT count(*) FILTER (WHERE inserted), count(*) FILTER (WHERE NOT inserted) FROM up
            """;
        UpsertResult result;
        await using (var cmd = new NpgsqlCommand(merge, conn, tx))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            await rd.ReadAsync(ct);
            result = new UpsertResult(rd.GetInt64(0), rd.GetInt64(1));
        }
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task UpsertKrxDailyAsync(IReadOnlyCollection<KrxDaily> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("CREATE TEMP TABLE tmp_krx (LIKE krx_daily INCLUDING DEFAULTS) ON COMMIT DROP", conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);
        await using (var w = await conn.BeginBinaryImportAsync(
            "COPY tmp_krx (ticker, date, market, open, high, low, close, change_from_previous, volume, trading_value, market_cap, listed_shares) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var r in rows)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(r.Ticker, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(r.Date, NpgsqlDbType.Date, ct);
                await w.WriteAsync(r.Market, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(r.Open, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.High, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.Low, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.Close, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.ChangeFromPrevious, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.Volume, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(r.TradingValue, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.MarketCap, NpgsqlDbType.Numeric, ct);
                await w.WriteAsync(r.ListedShares, NpgsqlDbType.Bigint, ct);
            }
            await w.CompleteAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO krx_daily (ticker, date, market, open, high, low, close, change_from_previous, volume, trading_value, market_cap, listed_shares)
            SELECT ticker, date, market, open, high, low, close, change_from_previous, volume, trading_value, market_cap, listed_shares FROM tmp_krx
            ON CONFLICT (ticker, date) DO UPDATE SET market = EXCLUDED.market, open = EXCLUDED.open, high = EXCLUDED.high,
              low = EXCLUDED.low, close = EXCLUDED.close, change_from_previous = EXCLUDED.change_from_previous, volume = EXCLUDED.volume, trading_value = EXCLUDED.trading_value,
              market_cap = EXCLUDED.market_cap, listed_shares = EXCLUDED.listed_shares
            """, conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Removes stored rows for a ticker that the source no longer reports inside [from, to].</summary>
    public async Task<int> DeleteMissingAsync(string ticker, DateOnly from, DateOnly to, IReadOnlyCollection<DateOnly> keep, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM daily_prices WHERE ticker = @t AND date BETWEEN @f AND @to AND NOT (date = ANY(@keep))", conn);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("f", from);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("keep", keep.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertIndexPricesAsync(IReadOnlyCollection<IndexPrice> rows, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var r in rows)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO index_prices (index_code, date, open, high, low, close, volume, source, ingested_at)
                VALUES (@c, @d, @o, @h, @l, @cl, @v, @s, @i)
                ON CONFLICT (index_code, date) DO UPDATE SET open = EXCLUDED.open, high = EXCLUDED.high, low = EXCLUDED.low,
                  close = EXCLUDED.close, volume = EXCLUDED.volume, source = EXCLUDED.source, ingested_at = EXCLUDED.ingested_at
                """, conn, tx);
            cmd.Parameters.AddWithValue("c", r.IndexCode);
            cmd.Parameters.AddWithValue("d", r.Date);
            cmd.Parameters.AddWithValue("o", r.Open);
            cmd.Parameters.AddWithValue("h", r.High);
            cmd.Parameters.AddWithValue("l", r.Low);
            cmd.Parameters.AddWithValue("cl", r.Close);
            cmd.Parameters.AddWithValue("v", r.Volume);
            cmd.Parameters.AddWithValue("s", r.Source);
            cmd.Parameters.AddWithValue("i", r.IngestedAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Streams bars grouped by ticker (ascending ticker, then date) for [from, to].</summary>
    public async IAsyncEnumerable<(string Ticker, List<Bar> Bars)> StreamBarsByTickerAsync(
        DateOnly from, DateOnly to, IReadOnlyCollection<string>? tickers = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var sql = "SELECT ticker, date, open, high, low, close, volume, is_halted FROM daily_prices WHERE date BETWEEN @f AND @t"
                  + (tickers is null ? "" : " AND ticker = ANY(@tk)")
                  + " ORDER BY ticker, date";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("f", from);
        cmd.Parameters.AddWithValue("t", to);
        if (tickers is not null) cmd.Parameters.AddWithValue("tk", tickers.ToArray());
        await using var rd = await cmd.ExecuteReaderAsync(ct);

        string? current = null;
        var bars = new List<Bar>();
        while (await rd.ReadAsync(ct))
        {
            var t = rd.GetString(0);
            if (current is not null && t != current)
            {
                yield return (current, bars);
                bars = new List<Bar>();
            }
            current = t;
            bars.Add(new Bar(
                rd.GetFieldValue<DateOnly>(1),
                (double)rd.GetDecimal(2), (double)rd.GetDecimal(3), (double)rd.GetDecimal(4), (double)rd.GetDecimal(5),
                rd.GetInt64(6), rd.GetBoolean(7)));
        }
        if (current is not null) yield return (current, bars);
    }

    /// <summary>Streams official KRX records grouped by ticker (ascending ticker, then date).</summary>
    public async IAsyncEnumerable<(string Ticker, List<KrxDaily> Rows)> StreamKrxByTickerAsync(
        DateOnly from, DateOnly to, IReadOnlyCollection<string>? tickers = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var sql = "SELECT ticker, date, market, open, high, low, close, change_from_previous, volume, trading_value, market_cap, listed_shares FROM krx_daily WHERE date BETWEEN @f AND @t"
                  + (tickers is null ? "" : " AND ticker = ANY(@tk)") + " ORDER BY ticker, date";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("f", from);
        cmd.Parameters.AddWithValue("t", to);
        if (tickers is not null) cmd.Parameters.AddWithValue("tk", tickers.ToArray());
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        string? current = null;
        var rows = new List<KrxDaily>();
        while (await rd.ReadAsync(ct))
        {
            var t = rd.GetString(0);
            if (current is not null && t != current) { yield return (current, rows); rows = []; }
            current = t;
            rows.Add(new KrxDaily
            {
                Ticker = t, Date = rd.GetFieldValue<DateOnly>(1), Market = rd.GetString(2), Open = rd.GetDecimal(3), High = rd.GetDecimal(4),
                Low = rd.GetDecimal(5), Close = rd.GetDecimal(6), ChangeFromPrevious = rd.GetDecimal(7), Volume = rd.GetInt64(8),
                TradingValue = rd.GetDecimal(9), MarketCap = rd.GetDecimal(10), ListedShares = rd.GetInt64(11),
            });
        }
        if (current is not null) yield return (current, rows);
    }

    public async Task<List<Bar>> LoadIndexAsync(string indexCode, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT date, open, high, low, close, volume FROM index_prices WHERE index_code = @c AND date BETWEEN @f AND @t ORDER BY date", conn);
        cmd.Parameters.AddWithValue("c", indexCode);
        cmd.Parameters.AddWithValue("f", from);
        cmd.Parameters.AddWithValue("t", to);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Bar>();
        while (await rd.ReadAsync(ct))
            list.Add(new Bar(rd.GetFieldValue<DateOnly>(0), (double)rd.GetDecimal(1), (double)rd.GetDecimal(2),
                (double)rd.GetDecimal(3), (double)rd.GetDecimal(4), rd.GetInt64(5), false));
        return list;
    }
}
