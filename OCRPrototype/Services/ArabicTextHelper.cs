using System.Text;
using System.Text.RegularExpressions;

namespace OCRPrototype.Services;

public static class ArabicTextHelper
{
    private static readonly Regex ArabicChar =
        new(@"[\u0600-\u06FF]", RegexOptions.Compiled);

    public static bool ContainsArabic(string text) =>
        ArabicChar.IsMatch(text);

    /// <summary>
    /// Converts PaddleOCR Arabic recognition from its visual/LTR order
    /// into logical RTL Arabic order.
    ///
    /// This follows PaddleOCR's official pred_reverse behaviour.
    /// Latin text, numbers and common punctuation are kept together.
    /// </summary>
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

        return string.Concat(parts);
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

    public static string NormalizeDigits(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            sb.Append(
                c is >= '\u0660' and <= '\u0669'
                    ? (char)(c - '\u0660' + '0')
                    : c);
        }

        return sb.ToString();
    }
}