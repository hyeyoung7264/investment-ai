using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Investment.Domain.Market;

/// <summary>
/// Immutable, normalized input for one simulation: calendar, bars, security master, benchmark and
/// point-in-time universe. <see cref="ComputeHash"/> identifies the exact input data for reproducibility.
/// </summary>
public sealed class MarketDataSet
{
    public MarketDataSet(
        IReadOnlyList<DateOnly> calendar,
        IReadOnlyDictionary<string, Bar[]> bars,
        IReadOnlyDictionary<string, Security> securities,
        Bar[] index,
        string indexCode,
        PointInTimeUniverse universe,
        string source,
        IReadOnlyDictionary<string, CorporateEvent[]>? events = null)
    {
        Events = events;
        Calendar = calendar;
        Securities = securities;
        Index = index;
        IndexCode = indexCode;
        Universe = universe;
        Source = source;

        var normalized = new Dictionary<string, Bar[]>(bars.Count, StringComparer.Ordinal);
        var disc = new Dictionary<string, IReadOnlySet<DateOnly>>(StringComparer.Ordinal);
        foreach (var (ticker, series) in bars)
        {
            var arr = series.Select(KrxRules.Normalize).ToArray();
            for (var i = 1; i < arr.Length; i++)
                if (arr[i].Date <= arr[i - 1].Date)
                    throw new ArgumentException($"{ticker}: bars not strictly ascending at {arr[i].Date}");
            normalized[ticker] = arr;
            if (securities.TryGetValue(ticker, out var sec))
            {
                var set = new HashSet<DateOnly>();
                for (var i = 1; i < arr.Length; i++)
                    if (KrxRules.IsDiscontinuity(sec, arr[i - 1], arr[i])) set.Add(arr[i].Date);
                if (set.Count > 0) disc[ticker] = set;
            }
        }
        Bars = normalized;
        Discontinuities = disc;
    }

    public IReadOnlyList<DateOnly> Calendar { get; }
    public IReadOnlyDictionary<string, Bar[]> Bars { get; }
    public IReadOnlyDictionary<string, Security> Securities { get; }
    public Bar[] Index { get; }
    public string IndexCode { get; }
    public PointInTimeUniverse Universe { get; }
    public string Source { get; }

    /// <summary>Corporate events per ticker, ascending by date (null when the strategy does not use events).</summary>
    public IReadOnlyDictionary<string, CorporateEvent[]>? Events { get; }

    /// <summary>Per ticker, dates whose bar is not a tradable continuation of the previous bar.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<DateOnly>> Discontinuities { get; }

    public string ComputeHash()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sb = new StringBuilder();
        void Flush() { sha.AppendData(Encoding.UTF8.GetBytes(sb.ToString())); sb.Clear(); }

        sb.Append("source=").Append(Source).Append(";index=").Append(IndexCode).Append('\n');
        foreach (var b in Index) AppendBar(sb, b);
        Flush();
        foreach (var ticker in Bars.Keys.Order(StringComparer.Ordinal))
        {
            sb.Append('#').Append(ticker);
            if (Securities.TryGetValue(ticker, out var s))
                sb.Append('|').Append(s.Market).Append('|').Append(s.Sector).Append('|').Append(s.DelistedDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            sb.Append('\n');
            foreach (var b in Bars[ticker]) AppendBar(sb, b);
            Flush();
        }
        sb.Append("universe=").Append(Universe.Hash());
        Flush();
        if (Events is not null)
        {
            foreach (var ticker in Events.Keys.Order(StringComparer.Ordinal))
            {
                sb.Append("@").Append(ticker).Append('\n');
                foreach (var e in Events[ticker])
                    sb.Append(e.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append(',').Append(e.Type).Append('\n');
                Flush();
            }
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static void AppendBar(StringBuilder sb, Bar b) =>
        sb.Append(b.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append(',')
          .Append(b.Open.ToString("R", CultureInfo.InvariantCulture)).Append(',')
          .Append(b.High.ToString("R", CultureInfo.InvariantCulture)).Append(',')
          .Append(b.Low.ToString("R", CultureInfo.InvariantCulture)).Append(',')
          .Append(b.Close.ToString("R", CultureInfo.InvariantCulture)).Append(',')
          .Append(b.Volume).Append(',').Append(b.IsHalted ? '1' : '0').Append('\n');
}
