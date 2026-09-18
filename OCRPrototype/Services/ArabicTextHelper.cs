using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OCRPrototype.Services;

public static partial class ArabicTextHelper
{
    // Tesseract returns logical Unicode. Reversing Arabic corrupts numbers and combining marks.
    public static string Normalize(string text)
    {
        var b = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormC))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            b.Append(c switch
            {
                >= '\u0660' and <= '\u0669' => (char)('0' + c - '\u0660'),
                >= '\u06f0' and <= '\u06f9' => (char)('0' + c - '\u06f0'),
                _ => c
            });
        }
        return Whitespace().Replace(b.ToString(), " ").Trim();
    }

    // Used only for matching labels. Returned names retain their diacritics.
    public static string MatchKey(string text) => string.Concat(Normalize(text)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && c != '\u0640'))
        .Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ة', 'ه');

    public static bool IsHeader(string text)
    {
        string key = MatchKey(text);
        return key.Contains("جمهوري") || key.Contains("بطاقه") || key.Contains("تحقيق الشخص")
            || key.Contains("الرقم القوم") || key.Contains("تاريخ الميلاد") || key.Contains("رقم المصنع");
    }

    public static bool HasArabicLetters(string text) => text.Any(c => c is >= '\u0621' and <= '\u064a' && char.IsLetter(c));
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
