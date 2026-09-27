using Investment.Domain.Market;

namespace Investment.MarketData.Dart;

/// <summary>Maps DART report names to event types. Corrections ([기재정정] etc.) are flagged, not new events.</summary>
public static class DisclosureClassifier
{
    private static readonly (string Keyword, DisclosureEvent Event)[] Rules =
    [
        ("자기주식취득신탁계약체결", DisclosureEvent.BuybackTrust),
        ("자기주식취득결정", DisclosureEvent.Buyback),
        ("자기주식처분결정", DisclosureEvent.TreasuryDisposal),
        // order matters: "유무상증자결정" contains "무상증자결정"
        ("유무상증자결정", DisclosureEvent.RightsOffering),
        ("무상증자결정", DisclosureEvent.BonusIssue),
        ("유상증자결정", DisclosureEvent.RightsOffering),
        ("전환사채권발행결정", DisclosureEvent.ConvertibleBond),
        ("신주인수권부사채권발행결정", DisclosureEvent.BondWithWarrant),
        ("교환사채권발행결정", DisclosureEvent.ExchangeableBond),
        ("감자결정", DisclosureEvent.CapitalReduction),
        ("단일판매ㆍ공급계약체결", DisclosureEvent.SupplyContract),
        ("단일판매·공급계약체결", DisclosureEvent.SupplyContract),
        ("영업(잠정)실적", DisclosureEvent.PreliminaryEarnings),
        ("손익구조", DisclosureEvent.EarningsChange),
        ("회사합병결정", DisclosureEvent.Merger),
        ("회사분할결정", DisclosureEvent.Split),
        ("최대주주변경", DisclosureEvent.LargestShareholderChange),
    ];

    public static (DisclosureEvent Event, bool IsCorrection) Classify(string reportName)
    {
        var name = reportName.Replace(" ", "");
        var correction = name.StartsWith('[') && name.Contains("정정]", StringComparison.Ordinal);
        foreach (var (k, e) in Rules)
            if (name.Contains(k, StringComparison.Ordinal)) return (e, correction);
        return (DisclosureEvent.Other, correction);
    }

    /// <summary>Dilutive financing: new shares now or on conversion.</summary>
    public static bool IsDilutive(DisclosureEvent e) =>
        e is DisclosureEvent.RightsOffering or DisclosureEvent.ConvertibleBond or DisclosureEvent.BondWithWarrant;
}
