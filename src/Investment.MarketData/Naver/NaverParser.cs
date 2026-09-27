using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Investment.MarketData.Naver;

public sealed record RawDailyBar(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume, bool IsHalted);

/// <summary>
/// Parser for api.stock.naver.com chart JSON. Prices are split-adjusted, volume is raw.
/// Halted sessions come as O=H=L=0 (close carried); they are normalized to O=H=L=C and flagged.
/// </summary>
public static class NaverParser
{
    private sealed class Row
    {
        [JsonPropertyName("localDate")] public string LocalDate { get; set; } = "";
        [JsonPropertyName("openPrice")] public decimal OpenPrice { get; set; }
        [JsonPropertyName("highPrice")] public decimal HighPrice { get; set; }
        [JsonPropertyName("lowPrice")] public decimal LowPrice { get; set; }
        [JsonPropertyName("closePrice")] public decimal ClosePrice { get; set; }
        [JsonPropertyName("accumulatedTradingVolume")] public long Volume { get; set; }
    }

    public static IReadOnlyList<RawDailyBar> ParseDaily(string json)
    {
        var rows = JsonSerializer.Deserialize<List<Row>>(json) ?? [];
        var result = new List<RawDailyBar>(rows.Count);
        DateOnly? previous = null;
        foreach (var r in rows)
        {
            var date = DateOnly.ParseExact(r.LocalDate, "yyyyMMdd", CultureInfo.InvariantCulture);
            if (previous is { } p && date <= p)
                throw new FormatException($"Naver rows not strictly ascending at {date:yyyy-MM-dd}");
            previous = date;

            if (r.ClosePrice <= 0) continue; // no valid price at all: skip the row entirely

            var halted = r.Volume == 0 || (r.OpenPrice == 0 && r.HighPrice == 0 && r.LowPrice == 0);
            result.Add(halted
                ? new RawDailyBar(date, r.ClosePrice, r.ClosePrice, r.ClosePrice, r.ClosePrice, r.Volume, true)
                : new RawDailyBar(date, r.OpenPrice, r.HighPrice, r.LowPrice, r.ClosePrice, r.Volume, false));
        }
        return result;
    }
}
