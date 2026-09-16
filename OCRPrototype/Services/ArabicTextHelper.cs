using System.Text;
using System.Text.RegularExpressions;

namespace OCRPrototype.Services;

/// <summary>
/// PaddleOCR (like most OCR engines) detects and reads text boxes in a plain
/// left-to-right order. For Latin text that's fine. For Arabic, a line that
/// visually reads right-to-left comes back with its words in reverse order.
/// The characters inside each word are still correct - it's just the word
/// sequence that's flipped. Reversing the word order fixes it back up.
///
/// PaddleOCR/PaddleSharp doesn't do this for you, so it has to happen here.
/// </summary>
public static class ArabicTextHelper
{
    private static readonly Regex ArabicChar = new(@"[\u0600-\u06FF]", RegexOptions.Compiled);

    public static bool ContainsArabic(string text) => ArabicChar.IsMatch(text);

    public static string FixReadingOrder(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        if (!ContainsArabic(text))
            return text.Trim();

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Array.Reverse(words);
        return string.Join(' ', words);
    }

    /// <summary>
    /// Egyptian ID cards sometimes print numbers using Arabic-Indic digits
    /// (٠١٢٣٤٥٦٧٨٩) instead of the Western ones, and the recognizer just
    /// passes through whatever glyph it saw. Normalizing here means the
    /// regexes further down the pipeline don't have to care which one showed up.
    /// </summary>
    public static string NormalizeDigits(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c is >= '\u0660' and <= '\u0669' ? (char)(c - '\u0660' + '0') : c);
        return sb.ToString();
    }
}
