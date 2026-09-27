using System.Globalization;
using System.Text.Json;
using Investment.Domain.Market;
using Investment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Investment.MarketData.Splits;

public sealed record RawQuote(DateOnly Date, decimal Close, long Volume);

public sealed record SplitCandidate(string Ticker, DateOnly PreDate, DateOnly EventDate, double VolumeShift);

public sealed record SplitVerdict(double PriceFactor, bool IsEvent, bool SourceVolumeAdjusted, double VolumeCorrection);

/// <summary>
/// Finds share-count events and whether the price source adjusted volume for them. The chart source adjusts
/// prices for all events but volume only for some (reverse splits yes, forward splits no — e.g. Samsung 2018,
/// NAVER 2018, Kakao 2021 pre-split volume is raw), which understates pre-split trading value by the split ratio.
/// </summary>
public static class SplitVerifier
{
    /// <summary>First session after a halt with a persistent volume regime shift (median of 40 sessions).</summary>
    public static IEnumerable<SplitCandidate> Candidates(string ticker, IReadOnlyList<Bar> bars, int window = 40, double shift = 2.0)
    {
        for (var i = 1; i < bars.Count; i++)
        {
            if (!bars[i - 1].IsHalted || bars[i].IsHalted) continue;
            var pre = i - 1;
            while (pre >= 0 && bars[pre].IsHalted) pre--;
            if (pre < 0) continue;
            var before = Median(bars, Math.Max(0, pre - window + 1), pre + 1);
            var after = Median(bars, i, Math.Min(bars.Count, i + window));
            if (before <= 0 || after <= 0) continue;
            var ratio = after / before;
            if (ratio >= shift || ratio <= 1 / shift)
                yield return new SplitCandidate(ticker, bars[pre].Date, bars[i].Date, ratio);
        }
    }

    /// <summary>
    /// Compares adjusted (chart) and raw quotes on the last session before and the first session after the event.
    /// </summary>
    public static SplitVerdict Evaluate(Bar adjustedPre, RawQuote rawPre, Bar adjustedPost, RawQuote rawPost)
    {
        if (rawPre.Close <= 0 || rawPost.Close <= 0 || adjustedPre.Close <= 0 || adjustedPost.Close <= 0)
            return new SplitVerdict(1, false, true, 1);
        var ratioPre = adjustedPre.Close / (double)rawPre.Close;
        var ratioPost = adjustedPost.Close / (double)rawPost.Close;
        var factor = ratioPre / ratioPost;
        if (Math.Abs(Math.Log(factor)) < Math.Log(1.02))
            return new SplitVerdict(factor, false, true, 1);

        // if the chart volume equals the raw volume, the source did not adjust volume for this event
        var chartVol = Math.Max(1, adjustedPre.Volume);
        var rawVol = Math.Max(1, rawPre.Volume);
        var distUnadjusted = Math.Abs(Math.Log(chartVol / (double)rawVol));
        var distAdjusted = Math.Abs(Math.Log(chartVol / (rawVol / factor)));
        var adjusted = distAdjusted <= distUnadjusted;
        return new SplitVerdict(factor, true, adjusted, adjusted ? 1 : 1 / factor);
    }

    /// <summary>
    /// Multiplies pre-event volume by the correction of every later event, so that
    /// adjusted close × corrected volume approximates raw trading value throughout.
    /// </summary>
    public static List<Bar> ApplyVolumeCorrections(IReadOnlyList<Bar> bars, IReadOnlyList<SplitEvent> events)
    {
        var relevant = events.Where(e => Math.Abs(e.VolumeCorrection - 1) > 1e-9).OrderBy(e => e.EventDate).ToList();
        if (relevant.Count == 0) return bars as List<Bar> ?? bars.ToList();
        var result = new List<Bar>(bars.Count);
        foreach (var b in bars)
        {
            var factor = 1.0;
            foreach (var e in relevant)
                if (b.Date < e.EventDate) factor *= e.VolumeCorrection;
            result.Add(factor == 1.0 ? b : b with { Volume = (long)Math.Round(b.Volume * factor) });
        }
        return result;
    }

    private static double Median(IReadOnlyList<Bar> bars, int from, int to)
    {
        var xs = new List<double>();
        for (var i = from; i < to; i++) if (!bars[i].IsHalted) xs.Add(bars[i].Volume);
        if (xs.Count == 0) return 0;
        xs.Sort();
        return xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) / 2;
    }
}

/// <summary>Naver mobile quote pages: raw (unadjusted) prices, 60 sessions per page, newest first. Listed tickers only.</summary>
public sealed class NaverRawQuoteSource(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? Http.CreateClient();
    public const int PageSize = 60;

    public Task<IReadOnlyList<RawQuote>> GetPageAsync(string ticker, int page, CancellationToken ct) =>
        Http.RetryAsync<IReadOnlyList<RawQuote>>(async () =>
        {
            var json = await _http.GetStringAsync($"https://m.stock.naver.com/api/stock/{Uri.EscapeDataString(ticker)}/price?pageSize={PageSize}&page={page}", ct);
            return Parse(json);
        }, ct);

    public static IReadOnlyList<RawQuote> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<RawQuote>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var d = DateOnly.ParseExact(e.GetProperty("localTradedAt").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var close = decimal.Parse(e.GetProperty("closePrice").GetString()!.Replace(",", ""), CultureInfo.InvariantCulture);
            var vol = e.GetProperty("accumulatedTradingVolume").GetInt64();
            list.Add(new RawQuote(d, close, vol));
        }
        return list;
    }
}

public sealed class SplitVerificationService(Func<InvestmentDbContext> dbFactory, MarketDataStore store, NaverRawQuoteSource raw, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    public async Task<(int Candidates, int Events, int VolumeCorrections, int Unverifiable)> RunAsync(DateOnly from, int concurrency, CancellationToken ct)
    {
        HashSet<string> listed;
        await using (var db = dbFactory())
            listed = (await db.Securities.AsNoTracking()
                .Where(s => s.DelistedDate == null && s.Kind == SecurityKind.Common && (s.Market == MarketType.Kospi || s.Market == MarketType.Kosdaq))
                .Select(s => s.Ticker).ToListAsync(ct)).ToHashSet();

        var work = new List<(SplitCandidate Candidate, List<Bar> Bars)>();
        await foreach (var (ticker, bars) in store.StreamBarsByTickerAsync(from, DateOnly.FromDateTime(DateTime.Today), listed, ct))
            foreach (var c in SplitVerifier.Candidates(ticker, bars))
                work.Add((c, bars));
        _log($"split candidates: {work.Count} in {work.Select(w => w.Candidate.Ticker).Distinct().Count()} tickers");

        var results = new System.Collections.Concurrent.ConcurrentBag<SplitEvent>();
        int unverifiable = 0, done = 0;
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct }, async (w, token) =>
        {
            var (c, bars) = w;
            var pre = bars.First(b => b.Date == c.PreDate);
            var post = bars.First(b => b.Date == c.EventDate);
            var rawPre = await FindAsync(c.Ticker, bars, c.PreDate, token);
            var rawPost = await FindAsync(c.Ticker, bars, c.EventDate, token);
            if (rawPre is null || rawPost is null) { Interlocked.Increment(ref unverifiable); return; }
            var v = SplitVerifier.Evaluate(pre, rawPre, post, rawPost);
            if (v.IsEvent)
                results.Add(new SplitEvent
                {
                    Ticker = c.Ticker, EventDate = c.EventDate, PriceFactor = v.PriceFactor,
                    SourceVolumeAdjusted = v.SourceVolumeAdjusted, VolumeCorrection = v.VolumeCorrection,
                    CheckedAt = DateTimeOffset.UtcNow,
                    Notes = $"pre {c.PreDate:yyyy-MM-dd} adj={pre.Close} raw={rawPre.Close} vol chart={pre.Volume} raw={rawPre.Volume}; post adj={post.Close} raw={rawPost.Close}",
                });
            if (Interlocked.Increment(ref done) % 200 == 0) _log($"verified {done}/{work.Count}");
        });

        await using (var db = dbFactory())
        {
            await db.SplitEvents.ExecuteDeleteAsync(ct);
            db.SplitEvents.AddRange(results.GroupBy(e => (e.Ticker, e.EventDate)).Select(g => g.First()));
            await db.SaveChangesAsync(ct);
        }
        return (work.Count, results.Count, results.Count(e => !e.SourceVolumeAdjusted), unverifiable);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), Task<IReadOnlyList<RawQuote>>> _pages = new();

    private async Task<RawQuote?> FindAsync(string ticker, List<Bar> bars, DateOnly date, CancellationToken ct)
    {
        // pages are newest-first; sessions after `date` in our own data give the offset
        var newer = bars.Count(b => b.Date > date);
        var page = newer / NaverRawQuoteSource.PageSize + 1;
        foreach (var p in new[] { page, page + 1, page - 1 })
        {
            if (p < 1) continue;
            var quotes = await _pages.GetOrAdd((ticker, p), k => raw.GetPageAsync(k.Item1, k.Item2, ct));
            var q = quotes.FirstOrDefault(x => x.Date == date);
            if (q is not null) return q;
        }
        return null;
    }
}
