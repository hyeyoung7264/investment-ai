using System.Security.Cryptography;
using System.Text;

namespace Investment.Domain.Market;

/// <summary>One universe reconstitution: members chosen with data up to <see cref="SelectionDate"/>, effective from <see cref="EffectiveFrom"/>.</summary>
public sealed record UniverseSnapshot(DateOnly SelectionDate, DateOnly EffectiveFrom, IReadOnlyList<string> Members);

/// <summary>
/// Universe membership through time. Membership on day d is the latest snapshot with EffectiveFrom ≤ d,
/// and every snapshot's SelectionDate is strictly before its EffectiveFrom (no same-day look-ahead).
/// </summary>
public sealed class PointInTimeUniverse
{
    private readonly List<UniverseSnapshot> _snapshots;

    public PointInTimeUniverse(IEnumerable<UniverseSnapshot> snapshots)
    {
        _snapshots = snapshots.OrderBy(s => s.EffectiveFrom).ToList();
        foreach (var s in _snapshots)
            if (s.SelectionDate >= s.EffectiveFrom)
                throw new ArgumentException($"snapshot selected on {s.SelectionDate} cannot be effective from {s.EffectiveFrom}");
    }

    public IReadOnlyList<UniverseSnapshot> Snapshots => _snapshots;

    public IReadOnlyList<string> MembersOn(DateOnly date)
    {
        UniverseSnapshot? current = null;
        foreach (var s in _snapshots)
        {
            if (s.EffectiveFrom <= date) current = s;
            else break;
        }
        return current?.Members ?? [];
    }

    public IReadOnlySet<string> AllMembers() => _snapshots.SelectMany(s => s.Members).ToHashSet(StringComparer.Ordinal);

    public string Hash()
    {
        var sb = new StringBuilder();
        foreach (var s in _snapshots)
            sb.Append(s.SelectionDate.ToString("yyyyMMdd")).Append('>').Append(s.EffectiveFrom.ToString("yyyyMMdd")).Append(':')
              .AppendJoin(',', s.Members.Order(StringComparer.Ordinal)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
