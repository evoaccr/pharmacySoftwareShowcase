using System.Globalization;
using System.Text;

namespace pharmacyApp.Modules.Invoices;

internal sealed record LegacyInvoiceMergeParty(
    string Name,
    string IdentityNo,
    string Gln);

internal sealed record LegacyInvoiceMergeCandidate(
    int SourceFaturaId,
    string InvoiceUuid,
    bool IsSaved,
    DateTimeOffset DatabaseEntryAt,
    DateOnly? DueDate,
    decimal? TotalCost,
    decimal? DiscountAmount,
    decimal? KdvAmount,
    LegacyInvoiceMergeParty Sender,
    LegacyInvoiceMergeParty Receiver);

internal static class LegacyInvoiceMergeMatcher
{
    public static LegacyInvoiceMergeCandidate? Select(
        IEnumerable<LegacyInvoiceMergeCandidate> candidates,
        LegacyInvoiceMergeParty sender,
        LegacyInvoiceMergeParty receiver,
        DateOnly? dueDate,
        decimal? totalCost,
        decimal? discountAmount,
        decimal? kdvAmount)
    {
        var ranked = candidates
            .Where(candidate => PartyMatches(candidate.Sender, sender)
                                && PartyMatches(candidate.Receiver, receiver))
            .Select(candidate => new RankedCandidate(candidate, Score(
                candidate, dueDate, totalCost, discountAmount, kdvAmount)))
            .ToArray();
        if (ranked.Length == 0) return null;

        var bestScore = ranked.Max(candidate => candidate.Score);
        var best = ranked.Where(candidate => candidate.Score == bestScore)
            .OrderBy(candidate => candidate.Candidate.SourceFaturaId)
            .ThenBy(candidate => candidate.Candidate.InvoiceUuid, StringComparer.Ordinal)
            .ToArray();
        if (best.Length == 1) return best[0].Candidate;

        // Equal legacy headers can be duplicate rows from the previous application. A stable
        // one-to-one merge is safe only when the state being preserved is identical for every tie.
        return best.All(candidate => candidate.Candidate.IsSaved == best[0].Candidate.IsSaved)
            ? best[0].Candidate
            : null;
    }

    private static int Score(
        LegacyInvoiceMergeCandidate candidate,
        DateOnly? dueDate,
        decimal? totalCost,
        decimal? discountAmount,
        decimal? kdvAmount)
    {
        var score = 0;
        if (dueDate is not null && candidate.DueDate == dueDate) score++;
        if (MoneyEquals(candidate.TotalCost, totalCost)) score++;
        if (MoneyEquals(candidate.DiscountAmount, discountAmount)) score++;
        if (MoneyEquals(candidate.KdvAmount, kdvAmount)) score++;
        return score;
    }

    private static bool MoneyEquals(decimal? left, decimal? right) =>
        left is not null && right is not null
        && decimal.Round(left.Value, 2, MidpointRounding.AwayFromZero)
        == decimal.Round(right.Value, 2, MidpointRounding.AwayFromZero);

    private static bool PartyMatches(LegacyInvoiceMergeParty left, LegacyInvoiceMergeParty right)
    {
        var leftIdentifiers = Identifiers(left);
        var rightIdentifiers = Identifiers(right);
        if (leftIdentifiers.Count > 0 && rightIdentifiers.Count > 0)
            return leftIdentifiers.Overlaps(rightIdentifiers);
        return NormalizeName(left.Name) == NormalizeName(right.Name)
               && NormalizeName(left.Name).Length > 0;
    }

    private static HashSet<string> Identifiers(LegacyInvoiceMergeParty party) =>
        new[] { Digits(party.IdentityNo), Digits(party.Gln) }
            .Where(value => value.Length is 10 or 11 or 13)
            .ToHashSet(StringComparer.Ordinal);

    private static string NormalizeName(string value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToUpperInvariant(character));
                pendingSpace = false;
            }
            else pendingSpace = true;
        }
        return builder.ToString();
    }

    private static string Digits(string value) =>
        new((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    private sealed record RankedCandidate(LegacyInvoiceMergeCandidate Candidate, int Score);
}
