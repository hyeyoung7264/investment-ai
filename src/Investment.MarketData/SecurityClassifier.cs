using System.Text.RegularExpressions;
using Investment.Domain.Market;

namespace Investment.MarketData;

/// <summary>
/// Rule-based instrument classification from name/code/sector. Used to exclude non-operating
/// vehicles (SPACs, REITs, funds) and preferred shares from the default research universe.
/// </summary>
public static partial class SecurityClassifier
{
    public static SecurityKind Classify(string ticker, string name, string? sector, string? products)
    {
        if (ticker.Length == 6 && ticker[5] != '0') return SecurityKind.Preferred;
        if (name.Contains("스팩", StringComparison.Ordinal) || name.Contains("SPAC", StringComparison.OrdinalIgnoreCase) ||
            (products?.Contains("기업인수합병", StringComparison.Ordinal) ?? false) ||
            (products?.Trim() == "합병"))
            return SecurityKind.Spac;
        // REIT names end with 리츠 (substring match would catch 메리츠금융지주, 블리츠웨이…)
        if (ReitName().IsMatch(name) || name.Contains("REIT", StringComparison.OrdinalIgnoreCase))
            return SecurityKind.Reit;
        // listed funds: ship investment companies and infrastructure funds. Sector alone is not enough:
        // VC firms and trust companies share the 집합투자업 sector but are operating companies.
        if (name.Contains("선박투자", StringComparison.Ordinal) ||
            (name.EndsWith("인프라", StringComparison.Ordinal) && (sector?.Contains("집합투자업", StringComparison.Ordinal) ?? false)))
            return SecurityKind.Fund;
        return SecurityKind.Common;
    }

    [GeneratedRegex(@"리츠(\d+호)?$")]
    private static partial Regex ReitName();
}
