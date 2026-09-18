using System.Text;
using System.Text.RegularExpressions;

namespace OCRPrototype.Services;

public static class ArabicTextHelper
{
    private static readonly Regex ArabicRegex = new(@"[\u0600-\u06FF]", RegexOptions.Compiled);

    public static bool ContainsArabic(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) && ArabicRegex.IsMatch(text);
    }

    public static string CleanArabic(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string cleaned = text.Trim();

        if (ContainsArabic(cleaned))
            cleaned = FixReadingOrder(cleaned);

        cleaned = Regex.Replace(
            cleaned,
            @"\s+",
            " ");

        cleaned = Regex.Replace(
            cleaned,
            @"(?<=[\u0600-\u06FF])\s+[0٠]\s+(?=[\u0600-\u06FF])",
            " - ");

        return cleaned.Trim();
    }

    public static string CleanBasic(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string cleaned = NormalizeDigits(text.Trim());

        cleaned = Regex.Replace(
            cleaned,
            @"\s+",
            " ");

        return cleaned.Trim();
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
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var parts = new List<string>();

        var current = new StringBuilder();

        foreach (char c in text)
        {
            if (IsLtrCharacter(c))
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
        {
            parts.Add(current.ToString());
        }

        parts.Reverse();

        return string.Concat(parts).Trim();
    }

    private static bool IsLtrCharacter(char c)
    {
        return
            (c >= 'a' && c <= 'z') ||
            (c >= 'A' && c <= 'Z') ||
            (c >= '0' && c <= '9') ||
            c == ' ' ||
            c == ':' ||
            c == '/' ||
            c == '\\' ||
            c == '-' ||
            c == '.' ||
            c == '+' ||
            c == '%' ||
            c == '*';
    }
}