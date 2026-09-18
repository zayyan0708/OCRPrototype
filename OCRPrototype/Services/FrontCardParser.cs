using System.Globalization;
using System.Text.RegularExpressions;
using OCRPrototype.Models;
using OpenCvSharp;

namespace OCRPrototype.Services;

public sealed record ParsedFront(ExtractedCardData Data, bool Complete, string? Error);

public sealed partial class FrontCardParser(OcrOptions options)
{
    public ParsedFront Parse(IReadOnlyList<RecognizedLine> lines, Rect? photo, DateTime today)
    {
        var ids = lines.Where(l => l.Confidence >= options.MinIdConfidence)
            .Select(l => (Line: l, Digits: CompactNumber(l.Text)))
            .Where(x => TryBirthDate(x.Digits, today, out _)).ToArray();
        string[] distinctIds = ids.Select(x => x.Digits).Distinct().ToArray();
        string? nationalId = distinctIds.Length == 1 ? distinctIds[0] : null;
        DateTime? dob = nationalId is not null && TryBirthDate(nationalId, today, out var date) ? date : null;
        var serials = lines.Where(l => l.Confidence >= options.MinIdConfidence)
            .Select(l => string.Concat(l.Text.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant())
            .Where(s => CardNumber().IsMatch(s)).Distinct().ToArray();
        string? cardId = serials.Length == 1 ? serials[0] : null;
        var printedDates = lines.Where(l => l.Confidence >= options.MinIdConfidence)
            .Select(l => TryPrintedDate(l.Text, today, out var printed) ? (DateTime?)printed : null)
            .OfType<DateTime>().Distinct().ToArray();
        bool dateConflict = printedDates.Length > 1
            || (printedDates.Length == 1 && dob is not null && printedDates[0] != dob.Value);
        if (dateConflict) dob = null;
        else if (dob is null && printedDates.Length == 1) dob = printedDates[0];

        // Label, portrait and national-number anchors identify the central field block.
        // Missing anchors or an unexpected row count yield partial data rather than shifted fields.
        var headers = lines.Where(l => ArabicTextHelper.IsHeader(l.Text)
            && ArabicTextHelper.MatchKey(l.Text).Contains("بطاقه")).ToArray();
        int top = headers.Length > 0 ? headers.Max(l => l.Bounds.Bottom) : -1;
        int bottom = nationalId is not null ? ids.Where(x => x.Digits == nationalId).Min(x => x.Line.Bounds.Y) : int.MaxValue;
        var candidates = lines.Where(l => l.Confidence >= options.MinTextConfidence
                && ArabicTextHelper.HasArabicLetters(l.Text) && !ArabicTextHelper.IsHeader(l.Text)
                && l.Bounds.Y >= top && l.Bounds.Bottom < bottom
                && l.Text.Count(c => c is >= '\u0621' and <= '\u064a' && char.IsLetter(c)) >= 3
                && (photo is null || l.Bounds.X > photo.Value.X + photo.Value.Width / 2))
            .OrderBy(l => l.Bounds.Y).ToList();
        // Multiple preprocessing passes can recognize the same row. Keep the most confident
        // complete alternative; never append duplicate rows or shift fields around a rejected ID.
        var unique = new List<RecognizedLine>();
        foreach (var candidate in candidates.OrderByDescending(l => l.Confidence))
        {
            if (unique.Any(l => SameRow(l.Bounds, candidate.Bounds))) continue;
            unique.Add(candidate);
        }
        // Personal Arabic rows share a right edge. Security artwork elsewhere must not
        // inflate the row count and suppress the real text block.
        candidates = unique.Select(anchor => unique.Where(l =>
                Math.Abs(l.Bounds.Right - anchor.Bounds.Right) <= Math.Max(l.Bounds.Height, anchor.Bounds.Height) * 1.5)
            .OrderBy(l => l.Bounds.Y).ToList())
            .Where(group => group.Count is 3 or 4)
            .OrderByDescending(group => group.Sum(l => l.Confidence)).FirstOrDefault() ?? [];
        string? name = null, address = null;
        if (top >= 0 && candidates.Count is 3 or 4)
        {
            name = string.Join(" ", candidates.Take(2).Select(l => l.Text));
            address = string.Join("، ", candidates.Skip(2).Select(l => l.Text));
        }
        var data = new ExtractedCardData(name, address, nationalId, cardId, dob);
        bool complete = name is not null && address is not null && nationalId is not null && cardId is not null && dob is not null && !dateConflict;
        var missing = new List<string>();
        if (name is null) missing.Add("FullName (layout or recognition uncertain)");
        if (address is null) missing.Add("Address (layout or recognition uncertain)");
        if (nationalId is null) missing.Add("NationalId (not recognized, invalid structure, or conflicting readings)");
        if (cardId is null) missing.Add("CardId (not recognized or conflicting readings)");
        if (dob is null) missing.Add("DateOfBirth (no valid source or conflicting dates)");
        return new(data, complete, complete ? null : "Partial extraction. Review: " + string.Join("; ", missing) + ".");
    }

    private static bool SameRow(Rect a, Rect b) =>
        Math.Abs(a.Y + a.Height / 2d - b.Y - b.Height / 2d) < Math.Max(a.Height, b.Height) * .6
        && Math.Min(a.Right, b.Right) > Math.Max(a.X, b.X);

    public static string CompactNumber(string text)
    {
        string normalized = ArabicTextHelper.Normalize(text);
        if (normalized.Any(c => !char.IsAsciiDigit(c) && !char.IsWhiteSpace(c) )) return "";
        return string.Concat(normalized.Where(char.IsAsciiDigit));
    }

    public static bool TryBirthDate(string id, DateTime today, out DateTime date)
    {
        date = default;
        if (id.Length != 14 || !id.All(char.IsAsciiDigit) || id[0] is not ('2' or '3')) return false;
        int year = (id[0] == '2' ? 1900 : 2000) + int.Parse(id.AsSpan(1, 2), CultureInfo.InvariantCulture);
        int month = int.Parse(id.AsSpan(3, 2), CultureInfo.InvariantCulture);
        int day = int.Parse(id.AsSpan(5, 2), CultureInfo.InvariantCulture);
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        // Structural plausibility only; this is not registry or checksum verification.
        string governorate = id.Substring(7, 2);
        if (governorate is not ("01" or "02" or "03" or "04" or "11" or "12" or "13" or "14"
            or "15" or "16" or "17" or "18" or "19" or "21" or "22" or "23" or "24" or "25"
            or "26" or "27" or "28" or "29" or "31" or "32" or "33" or "34" or "35" or "88")) return false;
        date = new DateTime(year, month, day);
        return date <= today.Date;
    }

    public static bool TryPrintedDate(string text, DateTime today, out DateTime date)
    {
        date = default;
        string value = string.Concat(ArabicTextHelper.Normalize(text).Where(c => !char.IsWhiteSpace(c)));
        // Keep separators: concatenating dates into a 14-digit candidate would invent an ID.
        if (!PrintedDate().IsMatch(value)) return false;
        return DateTime.TryParseExact(value, ["yyyy/M/d", "d/M/yyyy", "yyyy-M-d", "d-M-yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            && date.Year >= 1900 && date <= today.Date;
    }

    [GeneratedRegex(@"\A(?:[0-9]{4}[-/][0-9]{1,2}[-/][0-9]{1,2}|[0-9]{1,2}[-/][0-9]{1,2}[-/][0-9]{4})\z")]
    private static partial Regex PrintedDate();

    [GeneratedRegex(@"\A[A-Z]{2}[0-9]{7}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CardNumber();
}
