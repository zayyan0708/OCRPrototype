using System.Text;
using System.Text.RegularExpressions;

namespace OCRPrototype.Services;

public static class ArabicTextHelper
{
    private static readonly Regex ArabicRegex =
        new(@"[\u0600-\u06FF]", RegexOptions.Compiled);

    public static bool ContainsArabic(string text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
               ArabicRegex.IsMatch(text);
    }

    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = NormalizeDigits(text.Trim());

        if (ContainsArabic(text))
            text = FixReadingOrder(text);

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static string NormalizeDigits(string text)
    {
        var result = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            if (c >= '\u0660' && c <= '\u0669')
            {
                result.Append((char)(c - '\u0660' + '0'));
            }
            else if (c >= '\u06F0' && c <= '\u06F9')
            {
                result.Append((char)(c - '\u06F0' + '0'));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static string FixReadingOrder(string text)
    {
        var parts = new List<string>();
        var ltrPart = new StringBuilder();

        foreach (char c in text)
        {
            if (IsLtrCharacter(c))
            {
                ltrPart.Append(c);
            }
            else
            {
                if (ltrPart.Length > 0)
                {
                    parts.Add(ltrPart.ToString());
                    ltrPart.Clear();
                }

                parts.Add(c.ToString());
            }
        }

        if (ltrPart.Length > 0)
            parts.Add(ltrPart.ToString());

        parts.Reverse();

        return string.Concat(parts);
    }

    private static bool IsLtrCharacter(char c)
    {
        return char.IsDigit(c) ||
               (c >= 'a' && c <= 'z') ||
               (c >= 'A' && c <= 'Z') ||
               c == '/' ||
               c == '-' ||
               c == '.' ||
               c == ':';
    }
}