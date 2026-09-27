using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Investment.Domain.Market;

namespace Investment.MarketData.Kind;

public sealed record ListedCompany(string Ticker, string Name, MarketType Market, string? Sector, string? Products, DateOnly? ListedDate);

public sealed record DelistedCompany(string Ticker, string Name, MarketType Market, DateOnly DelistedDate, string Reason)
{
    /// <summary>
    /// KIND's delisting list also contains market transfers (KONEX→KOSDAQ, KOSDAQ→KOSPI),
    /// which are not delistings of the tradable share.
    /// </summary>
    public bool IsMarketTransfer =>
        Reason.Contains("이전상장", StringComparison.Ordinal) ||
        Reason.Contains("유가증권시장 상장", StringComparison.Ordinal) ||
        Reason.Contains("코스닥시장 상장", StringComparison.Ordinal);
}

/// <summary>Parsers for KIND (kind.krx.co.kr) HTML responses. Pure functions — unit tested with fixtures.</summary>
public static partial class KindParser
{
    [GeneratedRegex(@"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<td[^>]*>(.*?)</td>", RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"companysummary_open\('([0-9A-Z]{5})'\)")]
    private static partial Regex CompanyCodeRegex();

    [GeneratedRegex(@"alt='(유가증권|코스닥|코넥스)'")]
    private static partial Regex MarketAltRegex();

    [GeneratedRegex(@"title='([^']*)'")]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"전체\s*<em>(\d+)</em>")]
    private static partial Regex TotalRegex();

    public static IReadOnlyList<ListedCompany> ParseListed(string html)
    {
        var result = new List<ListedCompany>();
        foreach (Match row in RowRegex().Matches(html))
        {
            var cells = Cells(row.Groups[1].Value);
            if (cells.Count < 6) continue; // header row uses <th>
            var ticker = cells[2].Trim();
            if (ticker.Length != 6) continue;
            result.Add(new ListedCompany(
                ticker,
                cells[0],
                ParseMarket(cells[1]),
                NullIfEmpty(cells[3]),
                NullIfEmpty(cells[4]),
                DateOnly.TryParseExact(cells[5], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null));
        }
        return result;
    }

    public static IReadOnlyList<DelistedCompany> ParseDelisted(string html)
    {
        var result = new List<DelistedCompany>();
        foreach (Match row in RowRegex().Matches(html))
        {
            var raw = CellRegex().Matches(row.Groups[1].Value).Select(m => m.Groups[1].Value).ToList();
            if (raw.Count < 4) continue;
            var code = CompanyCodeRegex().Match(raw[1]);
            if (!code.Success) continue;
            var market = MarketAltRegex().Match(raw[1]);
            var title = TitleRegex().Match(raw[1]);
            var name = title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value).Trim() : Clean(raw[1]);
            if (!DateOnly.TryParseExact(Clean(raw[2]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            result.Add(new DelistedCompany(
                // KIND uses the 5-character company code; the common share ticker appends '0'.
                code.Groups[1].Value + "0",
                name,
                market.Success ? ParseMarket(market.Groups[1].Value) : MarketType.Unknown,
                date,
                Clean(raw[3])));
        }
        return result;
    }

    public static int? ParseTotalCount(string html)
    {
        var m = TotalRegex().Match(html);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    public static MarketType ParseMarket(string text)
    {
        var t = Clean(text);
        if (t.StartsWith("유가", StringComparison.Ordinal)) return MarketType.Kospi;
        if (t.StartsWith("코스닥", StringComparison.Ordinal)) return MarketType.Kosdaq;
        if (t.StartsWith("코넥스", StringComparison.Ordinal)) return MarketType.Konex;
        return MarketType.Unknown;
    }

    private static List<string> Cells(string rowHtml) =>
        CellRegex().Matches(rowHtml).Select(m => Clean(m.Groups[1].Value)).ToList();

    private static string Clean(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(TagRegex().Replace(html, " ")), @"\s+", " ").Trim();

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
