using System.Text;
using System.Text.RegularExpressions;

namespace OCRPrototype.Services;

public static class ArabicTextHelper
{
    private static readonly Regex ArabicChar =
        new(
            @"[\u0600-\u06FF]",
            RegexOptions.Compiled);

    public static bool ContainsArabic(string text)
    {
        return !string.IsNullOrWhiteSpace(text)
               && ArabicChar.IsMatch(text);
    }

    public static string FixReadingOrder(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = text.Trim();

        if (!ContainsArabic(text))
            return text;

        var parts = new List<string>();
        var current = new StringBuilder();

        foreach (char c in text)
        {
            if (IsLtrGroupCharacter(c))
            {
                current.Append(c);
            }
            else
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                parts.Add(c.ToString());
            }
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        parts.Reverse();

        return string.Concat(parts).Trim();
    }

    public static string NormalizeDigits(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            // Arabic-Indic:
            // ٠١٢٣٤٥٦٧٨٩
            if (c is >= '\u0660' and <= '\u0669')
            {
                sb.Append(
                    (char)('0' + (c - '\u0660')));
            }

            // Eastern Arabic / Persian:
            // ۰۱۲۳۴۵۶۷۸۹
            else if (c is >= '\u06F0' and <= '\u06F9')
            {
                sb.Append(
                    (char)('0' + (c - '\u06F0')));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static bool IsLtrGroupCharacter(char c)
    {
        return
            (c >= 'a' && c <= 'z') ||
            (c >= 'A' && c <= 'Z') ||
            (c >= '0' && c <= '9') ||
            c == ' ' ||
            c == ':' ||
            c == '*' ||
            c == '.' ||
            c == '/' ||
            c == '%' ||
            c == '+' ||
            c == '-';
    }
}