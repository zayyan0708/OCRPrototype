using OCRPrototype.Models;
using OpenCvSharp;
using Sdcb.PaddleOCR;
using System.Text.RegularExpressions;

namespace OCRPrototype.Services;

public sealed class EgyptianIdOcrService : IEgyptianIdOcrService
{
    private const int NormalizedOcrWidth = 1200;

    private readonly PaddleOcrEngine _engine;
    private readonly ILogger<EgyptianIdOcrService> _logger;

    private static readonly Regex CardNumberRegex =
        new(
            @"^[A-Z]{1,3}[0-9]{5,10}$",
            RegexOptions.Compiled);

    public EgyptianIdOcrService(
        PaddleOcrEngine engine,
        ILogger<EgyptianIdOcrService> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public async Task<EgyptianIdOcrResult> ExtractAsync(
        Stream imageStream,
        CancellationToken ct = default)
    {
        byte[] bytes =
            await ReadAllBytesAsync(
                imageStream,
                ct);

        using Mat original =
            Cv2.ImDecode(
                bytes,
                ImreadModes.Color);

        if (original.Empty())
        {
            throw new InvalidOperationException(
                "The uploaded file could not be read as an image.");
        }

        _logger.LogInformation(
            "INPUT IMAGE SIZE: {Width} x {Height}",
            original.Width,
            original.Height);

        // ============================================================
        // PASS 1 - ORIGINAL IMAGE
        // ============================================================

        List<OcrRegion> originalRegions =
            await RunOcrAsync(
                original,
                ct);

        _logger.LogInformation(
            "ORIGINAL PASS: {Count} OCR regions.",
            originalRegions.Count);

        LogRegions(
            originalRegions,
            "ORIGINAL");

        ParsedPass originalPass =
            ParseRegions(
                originalRegions,
                "ORIGINAL");

        // ============================================================
        // PASS 2 - NORMALIZED TEXT AREA
        //
        // The uploaded canvas size no longer determines field
        // positions. We crop around actual OCR content and resize that
        // useful area to a consistent working scale.
        // ============================================================

        ParsedPass? normalizedPass =
            null;

        using Mat? normalizedImage =
            CreateNormalizedOcrCanvas(
                original,
                originalRegions);

        if (normalizedImage is not null &&
            !normalizedImage.Empty())
        {
            _logger.LogInformation(
                "NORMALIZED IMAGE SIZE: {Width} x {Height}",
                normalizedImage.Width,
                normalizedImage.Height);

            List<OcrRegion> normalizedRegions =
                await RunOcrAsync(
                    normalizedImage,
                    ct);

            _logger.LogInformation(
                "NORMALIZED PASS: {Count} OCR regions.",
                normalizedRegions.Count);

            LogRegions(
                normalizedRegions,
                "NORMALIZED");

            normalizedPass =
                ParseRegions(
                    normalizedRegions,
                    "NORMALIZED");
        }

        // ============================================================
        // CHOOSE BEST PERSONAL-DATA PASS
        // ============================================================

        ParsedPass personalSource =
            ChooseBestPersonalPass(
                originalPass,
                normalizedPass);

        // ============================================================
        // NUMERIC FIELDS
        //
        // Prefer original result when already valid.
        // Exactly 14 digits are still required for National ID.
        // ============================================================

        string? nationalId =
            originalPass.NationalId?.Value
            ?? normalizedPass?.NationalId?.Value;

        string? cardNumber =
            originalPass.CardNumber?.Value
            ?? normalizedPass?.CardNumber?.Value;

        var result =
            new EgyptianIdOcrResult
            {
                FirstName =
                    personalSource.FirstName,

                RestOfName =
                    personalSource.RestOfName,

                AddressLine1 =
                    personalSource.AddressLine1,

                AddressLine2 =
                    personalSource.AddressLine2,

                NationalId =
                    nationalId,

                CardNumber =
                    cardNumber
            };

        LogFinalResult(
            result,
            personalSource.PassName);

        return result;
    }

    // ================================================================
    // RUN OCR
    // ================================================================

    private async Task<List<OcrRegion>> RunOcrAsync(
        Mat image,
        CancellationToken ct)
    {
        PaddleOcrResult result =
            await _engine.RunAsync(
                image,
                ct);

        return result.Regions
            .Select(ConvertRegion)
            .Where(region =>
                !string.IsNullOrWhiteSpace(
                    region.Text))
            .OrderBy(region =>
                region.CenterY)
            .ToList();
    }

    // ================================================================
    // NORMALIZED OCR CANVAS
    // ================================================================

    private Mat? CreateNormalizedOcrCanvas(
        Mat source,
        IReadOnlyList<OcrRegion> regions)
    {
        List<OcrRegion> usefulRegions =
            regions
                .Where(region =>
                    region.Score >= 0.15f)
                .ToList();

        if (usefulRegions.Count < 3)
        {
            _logger.LogWarning(
                "Not enough OCR regions to normalize image.");

            return null;
        }

        float minX =
            usefulRegions.Min(r => r.Left);

        float maxX =
            usefulRegions.Max(r => r.Right);

        float minY =
            usefulRegions.Min(r => r.Top);

        float maxY =
            usefulRegions.Max(r => r.Bottom);

        float contentWidth =
            Math.Max(
                1f,
                maxX - minX);

        float contentHeight =
            Math.Max(
                1f,
                maxY - minY);

        /*
         * Fairly generous padding.
         *
         * This is important when the first pass misses a small
         * first-name field near the right edge.
         */
        float paddingX =
            Math.Max(
                25f,
                contentWidth * 0.15f);

        float paddingY =
            Math.Max(
                25f,
                contentHeight * 0.20f);

        int left =
            Math.Max(
                0,
                (int)Math.Floor(
                    minX - paddingX));

        int top =
            Math.Max(
                0,
                (int)Math.Floor(
                    minY - paddingY));

        int right =
            Math.Min(
                source.Width,
                (int)Math.Ceiling(
                    maxX + paddingX));

        int bottom =
            Math.Min(
                source.Height,
                (int)Math.Ceiling(
                    maxY + paddingY));

        int width =
            right - left;

        int height =
            bottom - top;

        if (width < 50 ||
            height < 50)
        {
            return null;
        }

        var cropRectangle =
            new Rect(
                left,
                top,
                width,
                height);

        using Mat crop =
            new Mat(
                source,
                cropRectangle)
            .Clone();

        _logger.LogInformation(
            "OCR CONTENT CROP: X={X}, Y={Y}, W={Width}, H={Height}",
            left,
            top,
            width,
            height);

        double scale =
            (double)NormalizedOcrWidth /
            crop.Width;

        scale =
            Math.Clamp(
                scale,
                0.50,
                3.00);

        int newWidth =
            Math.Max(
                1,
                (int)Math.Round(
                    crop.Width * scale));

        int newHeight =
            Math.Max(
                1,
                (int)Math.Round(
                    crop.Height * scale));

        const int maxDimension =
            2200;

        if (newHeight > maxDimension)
        {
            double correction =
                (double)maxDimension /
                newHeight;

            newWidth =
                Math.Max(
                    1,
                    (int)Math.Round(
                        newWidth *
                        correction));

            newHeight =
                maxDimension;
        }

        var resized =
            new Mat();

        Cv2.Resize(
            crop,
            resized,
            new Size(
                newWidth,
                newHeight),
            0,
            0,
            scale >= 1.0
                ? InterpolationFlags.Cubic
                : InterpolationFlags.Area);

        return resized;
    }

    // ================================================================
    // CONVERT PADDLE REGION
    // ================================================================

    private static OcrRegion ConvertRegion(
        PaddleOcrResultRegion region)
    {
        string raw =
            region.Text?.Trim()
            ?? string.Empty;

        string normalized =
            ArabicTextHelper.NormalizeDigits(
                raw);

        string text =
            ArabicTextHelper.ContainsArabic(
                normalized)
                ? ArabicTextHelper.FixReadingOrder(
                    normalized)
                : normalized;

        return new OcrRegion(
            Text:
                text.Trim(),

            RawText:
                raw,

            Score:
                region.Score,

            CenterX:
                region.Rect.Center.X,

            CenterY:
                region.Rect.Center.Y,

            Width:
                Math.Abs(
                    region.Rect.Size.Width),

            Height:
                Math.Abs(
                    region.Rect.Size.Height));
    }

    // ================================================================
    // PARSE A SINGLE PASS
    // ================================================================

    private ParsedPass ParseRegions(
        IReadOnlyList<OcrRegion> regions,
        string passName)
    {
        if (regions.Count == 0)
        {
            return ParsedPass.Empty(
                passName);
        }

        DetectedValue? nationalId =
            FindNationalId(
                regions);

        DetectedValue? cardNumber =
            FindCardNumber(
                regions);

        /*
         * IMPORTANT:
         *
         * The previous version grouped boxes too aggressively.
         *
         * sample9 then produced things such as:
         *
         * "قلين البلد مركز قلين كفر الشيخ"
         *
         * even though these are two different printed rows.
         */
        List<OcrLine> lines =
            BuildStrictTextLines(
                regions);

        LogLines(
            lines,
            passName);

        List<OcrLine> personalLines =
            FindPersonalArabicLines(
                lines,
                nationalId,
                cardNumber,
                passName);

        AssignPersonalFields(
            personalLines,
            out string? firstName,
            out string? restOfName,
            out string? addressLine1,
            out string? addressLine2);

        float personalConfidence =
            personalLines.Count == 0
                ? 0f
                : personalLines.Average(
                    line =>
                        line.Confidence);

        double personalStructureScore =
            ScorePersonalBlock(
                personalLines);

        return new ParsedPass(
            PassName:
                passName,

            FirstName:
                firstName,

            RestOfName:
                restOfName,

            AddressLine1:
                addressLine1,

            AddressLine2:
                addressLine2,

            NationalId:
                nationalId,

            CardNumber:
                cardNumber,

            PersonalLineCount:
                personalLines.Count,

            PersonalConfidence:
                personalConfidence,

            PersonalStructureScore:
                personalStructureScore);
    }

    // ================================================================
    // STRICT TEXT-LINE GROUPING
    // ================================================================

    private static List<OcrLine> BuildStrictTextLines(
        IReadOnlyList<OcrRegion> regions)
    {
        List<OcrRegion> ordered =
            regions
                .OrderBy(region =>
                    region.CenterY)
                .ThenBy(region =>
                    region.CenterX)
                .ToList();

        var lines =
            new List<OcrLine>();

        foreach (OcrRegion region in ordered)
        {
            OcrLine? bestLine =
                null;

            float bestDistance =
                float.MaxValue;

            foreach (OcrLine line in lines)
            {
                float centerDifference =
                    Math.Abs(
                        line.CenterY -
                        region.CenterY);

                /*
                 * Two boxes are considered the SAME printed row only
                 * when their vertical centers are extremely close.
                 *
                 * We intentionally use the SMALLER text height.
                 *
                 * This prevents a tall rotated Paddle rectangle from
                 * swallowing the next printed line.
                 */
                float referenceHeight =
                    Math.Max(
                        1f,
                        Math.Min(
                            line.MedianRegionHeight,
                            region.Height));

                float allowedDifference =
                    Math.Max(
                        2f,
                        referenceHeight * 0.30f);

                /*
                 * Also require real vertical overlap.
                 */
                float overlapRatio =
                    line.GetVerticalOverlapRatio(
                        region);

                bool samePrintedRow =
                    centerDifference <= allowedDifference &&
                    overlapRatio >= 0.50f;

                if (samePrintedRow &&
                    centerDifference < bestDistance)
                {
                    bestDistance =
                        centerDifference;

                    bestLine =
                        line;
                }
            }

            if (bestLine is null)
            {
                lines.Add(
                    new OcrLine(
                        region));
            }
            else
            {
                bestLine.Add(
                    region);
            }
        }

        return lines
            .OrderBy(line =>
                line.CenterY)
            .ToList();
    }

    // ================================================================
    // FIND THE PERSONAL ARABIC BLOCK
    // ================================================================

    private List<OcrLine> FindPersonalArabicLines(
        IReadOnlyList<OcrLine> lines,
        DetectedValue? nationalId,
        DetectedValue? cardNumber,
        string passName)
    {
        float cutoffY =
            float.MaxValue;

        if (nationalId is not null)
        {
            cutoffY =
                Math.Min(
                    cutoffY,
                    nationalId.CenterY);
        }

        if (cardNumber is not null)
        {
            cutoffY =
                Math.Min(
                    cutoffY,
                    cardNumber.CenterY);
        }

        List<OcrLine> arabicLines =
            lines
                .Where(line =>
                    line.CenterY < cutoffY)

                .Where(line =>
                    CountArabicLetters(
                        line.RightToLeftText) >= 2)

                .Where(line =>
                    ExtractDigits(
                        line.RightToLeftText)
                        .Length < 6)

                .Where(line =>
                    line.Confidence >= 0.15f)

                .OrderBy(line =>
                    line.CenterY)

                .ToList();

        _logger.LogInformation(
            "{Pass}: {Count} candidate Arabic lines.",
            passName,
            arabicLines.Count);

        for (int i = 0;
             i < arabicLines.Count;
             i++)
        {
            OcrLine line =
                arabicLines[i];

            _logger.LogInformation(
                "{Pass} ARABIC CANDIDATE {Index}: {Text} | Words={Words} | Y={Y:F1} | Score={Score:F3}",
                passName,
                i,
                line.RightToLeftText,
                CountWords(
                    line.RightToLeftText),
                line.CenterY,
                line.Confidence);
        }

        if (arabicLines.Count <= 4)
        {
            return arabicLines;
        }

        /*
         * Do NOT blindly TakeLast(4).
         *
         * Evaluate every consecutive group of four Arabic lines and
         * select the group that most resembles:
         *
         *   first name
         *   rest of name
         *   address 1
         *   address 2
         *
         * This is structural, not dependent on specific Arabic words.
         */
        List<OcrLine>? bestBlock =
            null;

        double bestScore =
            double.MinValue;

        for (int start = 0;
             start <= arabicLines.Count - 4;
             start++)
        {
            List<OcrLine> candidate =
                arabicLines
                    .Skip(start)
                    .Take(4)
                    .ToList();

            double score =
                ScorePersonalBlock(
                    candidate);

            /*
             * Personal details normally come later than government
             * headings, so give a SMALL bonus to later blocks.
             *
             * This is intentionally only a bonus, not the main rule.
             */
            score +=
                start * 0.5;

            /*
             * A larger gap immediately BEFORE this four-line block is
             * useful evidence that the header has ended.
             */
            if (start > 0)
            {
                float gapBefore =
                    candidate[0].CenterY -
                    arabicLines[start - 1].CenterY;

                float referenceHeight =
                    Math.Max(
                        1f,
                        candidate[0]
                            .MedianRegionHeight);

                double normalizedGap =
                    gapBefore /
                    referenceHeight;

                score +=
                    Math.Min(
                        normalizedGap,
                        4.0);
            }

            _logger.LogInformation(
                "{Pass}: personal block start={Start}, score={Score:F2}, first={First}",
                passName,
                start,
                score,
                candidate[0].RightToLeftText);

            if (score > bestScore)
            {
                bestScore =
                    score;

                bestBlock =
                    candidate;
            }
        }

        return bestBlock
            ?? arabicLines.TakeLast(4).ToList();
    }

    // ================================================================
    // PERSONAL-BLOCK STRUCTURE SCORE
    // ================================================================

    private static double ScorePersonalBlock(
        IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0)
            return 0;

        double score =
            0;

        score +=
            lines.Average(
                line =>
                    line.Confidence)
            * 5.0;

        /*
         * The first-name row is normally short.
         *
         * This strongly prevents:
         *
         * "بطاقة تحقيق الشخصية"
         *
         * from becoming FirstName.
         */
        int firstWords =
            CountWords(
                lines[0]
                    .RightToLeftText);

        if (firstWords == 1)
        {
            score += 12;
        }
        else if (firstWords == 2)
        {
            score += 8;
        }
        else
        {
            score -= 10;
        }

        /*
         * A person's remaining name is usually more than one word.
         */
        if (lines.Count > 1)
        {
            int secondWords =
                CountWords(
                    lines[1]
                        .RightToLeftText);

            if (secondWords is >= 2 and <= 6)
            {
                score += 5;
            }
        }

        /*
         * Penalize obvious OCR contamination such as:
         *
         * A0 In 8
         *
         * inside Arabic personal fields.
         */
        foreach (OcrLine line in lines)
        {
            string text =
                line.RightToLeftText;

            int arabic =
                CountArabicLetters(
                    text);

            int latin =
                CountLatinLetters(
                    text);

            int digits =
                ExtractDigits(
                    text).Length;

            if (arabic >= 2)
            {
                score += 2;
            }

            score -=
                latin * 0.75;

            score -=
                digits * 0.50;
        }

        /*
         * Personal rows should progress cleanly down the card.
         *
         * Huge irregular internal gaps are less likely to represent
         * one four-field personal block.
         */
        if (lines.Count == 4)
        {
            float gap1 =
                lines[1].CenterY -
                lines[0].CenterY;

            float gap2 =
                lines[2].CenterY -
                lines[1].CenterY;

            float gap3 =
                lines[3].CenterY -
                lines[2].CenterY;

            float averageGap =
                (gap1 + gap2 + gap3)
                / 3f;

            if (averageGap > 0)
            {
                float deviation =
                    (
                        Math.Abs(
                            gap1 - averageGap)
                        +
                        Math.Abs(
                            gap2 - averageGap)
                        +
                        Math.Abs(
                            gap3 - averageGap)
                    )
                    / averageGap;

                score -=
                    deviation;
            }
        }

        return score;
    }

    // ================================================================
    // ASSIGN PERSONAL FIELDS
    // ================================================================

    private static void AssignPersonalFields(
        IReadOnlyList<OcrLine> personalLines,
        out string? firstName,
        out string? restOfName,
        out string? addressLine1,
        out string? addressLine2)
    {
        firstName = null;
        restOfName = null;
        addressLine1 = null;
        addressLine2 = null;

        if (personalLines.Count >= 4)
        {
            List<OcrLine> fields =
                personalLines
                    .Take(4)
                    .ToList();

            firstName =
                CleanValue(
                    fields[0]
                        .RightToLeftText);

            restOfName =
                CleanValue(
                    fields[1]
                        .RightToLeftText);

            addressLine1 =
                CleanValue(
                    fields[2]
                        .RightToLeftText);

            addressLine2 =
                CleanValue(
                    fields[3]
                        .RightToLeftText);

            return;
        }

        /*
         * Three-line fallback.
         *
         * Preserve a short first line as FirstName instead of shifting
         * all values one field to the right.
         */
        if (personalLines.Count == 3)
        {
            string first =
                personalLines[0]
                    .RightToLeftText;

            if (CountWords(first) <= 2)
            {
                firstName =
                    CleanValue(first);

                restOfName =
                    CleanValue(
                        personalLines[1]
                            .RightToLeftText);

                addressLine1 =
                    CleanValue(
                        personalLines[2]
                            .RightToLeftText);
            }
            else
            {
                restOfName =
                    CleanValue(
                        personalLines[0]
                            .RightToLeftText);

                addressLine1 =
                    CleanValue(
                        personalLines[1]
                            .RightToLeftText);

                addressLine2 =
                    CleanValue(
                        personalLines[2]
                            .RightToLeftText);
            }

            return;
        }

        if (personalLines.Count == 2)
        {
            string first =
                personalLines[0]
                    .RightToLeftText;

            if (CountWords(first) <= 2)
            {
                firstName =
                    CleanValue(first);

                restOfName =
                    CleanValue(
                        personalLines[1]
                            .RightToLeftText);
            }
            else
            {
                restOfName =
                    CleanValue(first);

                addressLine1 =
                    CleanValue(
                        personalLines[1]
                            .RightToLeftText);
            }

            return;
        }

        if (personalLines.Count == 1)
        {
            string value =
                personalLines[0]
                    .RightToLeftText;

            if (CountWords(value) <= 2)
            {
                firstName =
                    CleanValue(value);
            }
            else
            {
                restOfName =
                    CleanValue(value);
            }
        }
    }

    // ================================================================
    // NATIONAL ID
    // ================================================================

    private DetectedValue? FindNationalId(
        IReadOnlyList<OcrRegion> regions)
    {
        /*
         * First try a single original Paddle region.
         */
        var direct =
            regions
                .Select(region => new
                {
                    Region =
                        region,

                    Digits =
                        ExtractDigits(
                            region.Text)
                })

                .Where(item =>
                    item.Digits.Length == 14)

                .Where(item =>
                    CountArabicLetters(
                        item.Region.Text) == 0)

                .OrderByDescending(item =>
                    item.Region.Score)

                .FirstOrDefault();

        if (direct is not null)
        {
            return new DetectedValue(
                Value:
                    direct.Digits,

                CenterY:
                    direct.Region.CenterY,

                Confidence:
                    direct.Region.Score);
        }

        /*
         * Fallback if Paddle divided the 14 digits into several boxes.
         */
        List<OcrLine> lines =
            BuildStrictTextLines(
                regions);

        var combined =
            lines
                .Select(line => new
                {
                    Line =
                        line,

                    Digits =
                        ExtractDigits(
                            line.LeftToRightText)
                })

                .Where(item =>
                    item.Digits.Length == 14)

                .Where(item =>
                    CountArabicLetters(
                        item.Line.LeftToRightText) == 0)

                .OrderByDescending(item =>
                    item.Line.CenterY)

                .FirstOrDefault();

        if (combined is null)
            return null;

        return new DetectedValue(
            Value:
                combined.Digits,

            CenterY:
                combined.Line.CenterY,

            Confidence:
                combined.Line.Confidence);
    }

    // ================================================================
    // CARD NUMBER
    // ================================================================

    private DetectedValue? FindCardNumber(
        IReadOnlyList<OcrRegion> regions)
    {
        var direct =
            regions
                .Select(region => new
                {
                    Region =
                        region,

                    Value =
                        SanitizeCardNumber(
                            region.Text)
                })

                .Where(item =>
                    CardNumberRegex.IsMatch(
                        item.Value))

                .OrderByDescending(item =>
                    item.Region.Score)

                .FirstOrDefault();

        if (direct is not null)
        {
            return new DetectedValue(
                Value:
                    direct.Value,

                CenterY:
                    direct.Region.CenterY,

                Confidence:
                    direct.Region.Score);
        }

        List<OcrLine> lines =
            BuildStrictTextLines(
                regions);

        var combined =
            lines
                .Select(line => new
                {
                    Line =
                        line,

                    Value =
                        SanitizeCardNumber(
                            line.LeftToRightText)
                })

                .Where(item =>
                    CardNumberRegex.IsMatch(
                        item.Value))

                .OrderByDescending(item =>
                    item.Line.CenterY)

                .FirstOrDefault();

        if (combined is null)
            return null;

        return new DetectedValue(
            Value:
                combined.Value,

            CenterY:
                combined.Line.CenterY,

            Confidence:
                combined.Line.Confidence);
    }

    // ================================================================
    // CHOOSE BEST PASS
    // ================================================================

    private ParsedPass ChooseBestPersonalPass(
        ParsedPass original,
        ParsedPass? normalized)
    {
        if (normalized is null)
            return original;

        double originalScore =
            GetPassQuality(
                original);

        double normalizedScore =
            GetPassQuality(
                normalized);

        _logger.LogInformation(
            "PASS QUALITY: ORIGINAL={Original:F2}, NORMALIZED={Normalized:F2}",
            originalScore,
            normalizedScore);

        if (normalizedScore >
            originalScore)
        {
            _logger.LogInformation(
                "Using NORMALIZED pass for personal Arabic fields.");

            return normalized;
        }

        _logger.LogInformation(
            "Using ORIGINAL pass for personal Arabic fields.");

        return original;
    }

    private static double GetPassQuality(
        ParsedPass pass)
    {
        double score =
            pass.PersonalStructureScore;

        /*
         * Four distinct fields are preferred.
         */
        if (pass.PersonalLineCount == 4)
        {
            score += 20;
        }
        else
        {
            score +=
                pass.PersonalLineCount * 2;
        }

        score +=
            pass.PersonalConfidence;

        /*
         * A detected short first name is useful evidence that field
         * alignment is correct.
         */
        if (!string.IsNullOrWhiteSpace(
                pass.FirstName))
        {
            int words =
                CountWords(
                    pass.FirstName);

            if (words <= 2)
            {
                score += 5;
            }
        }

        return score;
    }

    // ================================================================
    // TEXT HELPERS
    // ================================================================

    private static int CountArabicLetters(
        string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        int count =
            0;

        foreach (char c in text)
        {
            if (
                c is >= '\u0621' and <= '\u063A' ||
                c is >= '\u0641' and <= '\u064A' ||
                c is >= '\u066E' and <= '\u06D3' ||
                c is >= '\u06FA' and <= '\u06FF')
            {
                count++;
            }
        }

        return count;
    }

    private static int CountLatinLetters(
        string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return text.Count(
            c =>
                c is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z');
    }

    private static string ExtractDigits(
        string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized =
            ArabicTextHelper.NormalizeDigits(
                text);

        return new string(
            normalized
                .Where(
                    char.IsAsciiDigit)
                .ToArray());
    }

    private static string SanitizeCardNumber(
        string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized =
            ArabicTextHelper
                .NormalizeDigits(
                    text)
                .ToUpperInvariant();

        return new string(
            normalized
                .Where(c =>
                    c is >= 'A' and <= 'Z'
                    or >= '0' and <= '9')
                .ToArray());
    }

    private static int CountWords(
        string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return text
            .Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }

    private static string? CleanValue(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Regex.Replace(
            value.Trim(),
            @"\s+",
            " ");
    }

    // ================================================================
    // LOGGING
    // ================================================================

    private void LogRegions(
        IEnumerable<OcrRegion> regions,
        string pass)
    {
        int index =
            0;

        foreach (OcrRegion region in regions)
        {
            _logger.LogInformation(
                "{Pass} REGION {Index}: {Text} | Score={Score:F3} | X={X:F1} | Y={Y:F1} | W={W:F1} | H={H:F1}",
                pass,
                index++,
                region.Text,
                region.Score,
                region.CenterX,
                region.CenterY,
                region.Width,
                region.Height);
        }
    }

    private void LogLines(
        IEnumerable<OcrLine> lines,
        string pass)
    {
        int index =
            0;

        foreach (OcrLine line in lines)
        {
            _logger.LogInformation(
                "{Pass} LINE {Index}: {Text} | Score={Score:F3} | Y={Y:F1}",
                pass,
                index++,
                line.RightToLeftText,
                line.Confidence,
                line.CenterY);
        }
    }

    private void LogFinalResult(
        EgyptianIdOcrResult result,
        string pass)
    {
        _logger.LogInformation(
            "PERSONAL DATA SOURCE: {Pass}",
            pass);

        _logger.LogInformation(
            "FIRST NAME: {Value}",
            result.FirstName);

        _logger.LogInformation(
            "REST OF NAME: {Value}",
            result.RestOfName);

        _logger.LogInformation(
            "ADDRESS 1: {Value}",
            result.AddressLine1);

        _logger.LogInformation(
            "ADDRESS 2: {Value}",
            result.AddressLine2);

        _logger.LogInformation(
            "NATIONAL ID: {Value}",
            result.NationalId);

        _logger.LogInformation(
            "CARD NUMBER: {Value}",
            result.CardNumber);
    }

    // ================================================================
    // STREAM
    // ================================================================

    private static async Task<byte[]>
        ReadAllBytesAsync(
            Stream input,
            CancellationToken ct)
    {
        using var buffer =
            new MemoryStream();

        await input.CopyToAsync(
            buffer,
            ct);

        return buffer.ToArray();
    }

    // ================================================================
    // INTERNAL TYPES
    // ================================================================

    private sealed record OcrRegion(
        string Text,
        string RawText,
        float Score,
        float CenterX,
        float CenterY,
        float Width,
        float Height)
    {
        public float Left =>
            CenterX -
            (Width / 2f);

        public float Right =>
            CenterX +
            (Width / 2f);

        public float Top =>
            CenterY -
            (Height / 2f);

        public float Bottom =>
            CenterY +
            (Height / 2f);
    }

    private sealed class OcrLine
    {
        private readonly List<OcrRegion>
            _regions =
                new();

        public OcrLine(
            OcrRegion firstRegion)
        {
            _regions.Add(
                firstRegion);
        }

        public void Add(
            OcrRegion region)
        {
            _regions.Add(
                region);
        }

        public float CenterY =>
            _regions.Average(
                region =>
                    region.CenterY);

        public float Top =>
            _regions.Min(
                region =>
                    region.Top);

        public float Bottom =>
            _regions.Max(
                region =>
                    region.Bottom);

        public float Height =>
            Math.Max(
                1f,
                Bottom - Top);

        public float MedianRegionHeight
        {
            get
            {
                float[] heights =
                    _regions
                        .Select(region =>
                            Math.Max(
                                1f,
                                region.Height))
                        .OrderBy(value =>
                            value)
                        .ToArray();

                if (heights.Length == 0)
                    return 1f;

                int middle =
                    heights.Length / 2;

                if (heights.Length % 2 == 1)
                    return heights[middle];

                return
                    (
                        heights[middle - 1]
                        +
                        heights[middle]
                    )
                    / 2f;
            }
        }

        public float Confidence =>
            _regions.Average(
                region =>
                    region.Score);

        public float GetVerticalOverlapRatio(
            OcrRegion region)
        {
            float intersectionTop =
                Math.Max(
                    Top,
                    region.Top);

            float intersectionBottom =
                Math.Min(
                    Bottom,
                    region.Bottom);

            float overlap =
                intersectionBottom -
                intersectionTop;

            if (overlap <= 0)
                return 0f;

            float smallerHeight =
                Math.Max(
                    1f,
                    Math.Min(
                        MedianRegionHeight,
                        region.Height));

            return
                overlap /
                smallerHeight;
        }

        public string RightToLeftText =>
            string.Join(
                    " ",
                    _regions
                        .OrderByDescending(
                            region =>
                                region.CenterX)
                        .Select(
                            region =>
                                region.Text))
                .Trim();

        public string LeftToRightText =>
            string.Join(
                    " ",
                    _regions
                        .OrderBy(
                            region =>
                                region.CenterX)
                        .Select(
                            region =>
                                region.Text))
                .Trim();
    }

    private sealed record DetectedValue(
        string Value,
        float CenterY,
        float Confidence);

    private sealed record ParsedPass(
        string PassName,
        string? FirstName,
        string? RestOfName,
        string? AddressLine1,
        string? AddressLine2,
        DetectedValue? NationalId,
        DetectedValue? CardNumber,
        int PersonalLineCount,
        float PersonalConfidence,
        double PersonalStructureScore)
    {
        public static ParsedPass Empty(
            string passName)
        {
            return new ParsedPass(
                passName,
                null,
                null,
                null,
                null,
                null,
                null,
                0,
                0,
                0);
        }
    }
}