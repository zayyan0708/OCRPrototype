using System.Globalization;
using System.Text.RegularExpressions;
using OCRPrototype.Models;
using OpenCvSharp;
using Sdcb.PaddleOCR;

namespace OCRPrototype.Services;

public class EgyptianIdOcrService : IEgyptianIdOcrService
{
    private readonly PaddleOcrEngine _engine;
    private readonly ILogger<EgyptianIdOcrService> _logger;

    private static readonly Size NormalizedSize =
        new(1000, 630);

    /*
     * Personal information area only.
     *
     * We deliberately start below the card heading.
     *
     * Expected order:
     *
     * First name
     * Full name
     * Address line 1
     * Address line 2 (optional)
     */
    private static readonly Rect PersonalTextRegion =
        new(330, 145, 670, 325);

    /*
     * Smaller crop used only to recover a first name that the
     * larger personal-information OCR may have missed.
     */
    private static readonly Rect FirstNameFallbackRegion =
        new(400, 145, 600, 135);

    private static readonly Rect NationalIdRegion =
        new(330, 430, 670, 190);

    private static readonly Rect DobRegion =
        new(0, 415, 390, 155);

    private static readonly Rect CardIdRegion =
        new(0, 530, 480, 100);

    private static readonly Rect PhotoRegion =
        new(10, 25, 320, 380);

    /*
     * Contains only Arabic letters.
     *
     * This prevents numeric OCR regions from becoming personal
     * information simply because Arabic digits belong to the
     * broader Arabic Unicode block.
     */
    private static readonly Regex ArabicLetterRegex =
        new(
            @"[\u0621-\u063A\u0641-\u064A]",
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
        byte[] imageBytes =
            await ReadAllBytesAsync(
                imageStream,
                ct);

        using Mat original =
            Cv2.ImDecode(
                imageBytes,
                ImreadModes.Color);

        if (original.Empty())
        {
            return new EgyptianIdOcrResult
            {
                IsSuccess = false,
                ErrorMessage =
                    "The uploaded file could not be read as an image."
            };
        }

        // =====================================================
        // 1. IMAGE QUALITY
        // =====================================================

        ImageQualityInfo quality =
            CheckImageQuality(original);

        if (!quality.IsReadable)
        {
            return new EgyptianIdOcrResult
            {
                IsSuccess = false,
                Quality = quality,
                ErrorMessage = quality.Message
            };
        }

        // =====================================================
        // 2. NORMALIZE CARD
        // =====================================================

        using Mat card =
            NormalizeCard(original);

        // =====================================================
        // 3. PERSONAL INFORMATION
        // =====================================================

        List<string> personalLines;

        using (Mat personalCrop =
               SafeCrop(
                   card,
                   PersonalTextRegion))
        {
            personalLines =
                await ReadArabicLinesAsync(
                    personalCrop,
                    enlarge: false,
                    ct);
        }

        personalLines =
            FilterPersonalLines(
                personalLines);

        /*
         * Independently read the top part of the personal
         * information area.
         *
         * This helps when Paddle sees:
         *
         * Full Name
         * Address 1
         * Address 2
         *
         * but misses the shorter First Name above them.
         */
        List<string> firstNameFallbackLines;

        using (Mat firstNameCrop =
               SafeCrop(
                   card,
                   FirstNameFallbackRegion))
        {
            firstNameFallbackLines =
                await ReadArabicLinesAsync(
                    firstNameCrop,
                    enlarge: true,
                    ct);
        }

        firstNameFallbackLines =
            FilterPersonalLines(
                firstNameFallbackLines);

        MergeFirstNameFallback(
            personalLines,
            firstNameFallbackLines);

        /*
         * Personal data is mapped only according to relative
         * top-to-bottom line order.
         *
         * We do NOT use:
         *
         * - hardcoded Arabic header text
         * - RightX alignment
         * - fixed Y position for FirstName
         * - fixed Y position for FullName
         */

        string? firstName =
            personalLines.Count >= 1
                ? personalLines[0]
                : null;

        string? fullName =
            personalLines.Count >= 2
                ? personalLines[1]
                : null;

        /*
         * The address can consist of one or more lines.
         *
         * Examples:
         *
         * line 1 -> FirstName
         * line 2 -> FullName
         * line 3 -> Address
         *
         * OR
         *
         * line 1 -> FirstName
         * line 2 -> FullName
         * line 3 -> Address line 1
         * line 4 -> Address line 2
         *
         * Everything from line 3 onward is joined.
         */
        string? address =
            personalLines.Count >= 3
                ? string.Join(
                    " ",
                    personalLines.Skip(2))
                : null;

        _logger.LogInformation(
            "PERSONAL LINES: {Lines}",
            string.Join(
                " | ",
                personalLines));

        // =====================================================
        // 4. NATIONAL ID
        // =====================================================

        string? nationalId = null;

        using (Mat nationalIdCrop =
               SafeCrop(
                   card,
                   NationalIdRegion))
        {
            nationalId =
                await ReadNationalIdAsync(
                    nationalIdCrop,
                    ct);
        }

        // =====================================================
        // 5. CARD ID
        // =====================================================

        string? cardId = null;

        using (Mat cardIdCrop =
               SafeCrop(
                   card,
                   CardIdRegion))
        {
            List<string> cardIdLines =
                await ReadNumberLinesAsync(
                    cardIdCrop,
                    expectedMinimumDigits: 5,
                    ct);

            cardId =
                ExtractCardId(
                    cardIdLines);
        }

        // =====================================================
        // 6. DATE OF BIRTH
        // =====================================================

        /*
         * Prefer DOB derived from the National ID.
         */
        string? dateOfBirth =
            GetDateOfBirthFromNationalId(
                nationalId);

        /*
         * If National ID did not provide a valid DOB,
         * OCR the printed date as fallback.
         */
        if (dateOfBirth is null)
        {
            using Mat dobCrop =
                SafeCrop(
                    card,
                    DobRegion);

            List<string> dobLines =
                await ReadNumberLinesAsync(
                    dobCrop,
                    expectedMinimumDigits: 6,
                    ct);

            dateOfBirth =
                ExtractPrintedDate(
                    dobLines);
        }

        // =====================================================
        // 7. PHOTO
        // =====================================================

        string? photo =
            ExtractPhoto(card);

        // =====================================================
        // RESULT
        // =====================================================

        var result =
            new EgyptianIdOcrResult
            {
                IsSuccess = true,
                Quality = quality,

                FirstName = firstName,
                FullName = fullName,
                Address = address,

                NationalId = nationalId,
                CardId = cardId,
                DateOfBirth = dateOfBirth,

                Photo = photo
            };

        result.Warning =
            BuildWarning(result);

        _logger.LogInformation(
            "ID OCR completed. FirstName={FirstName}, FullName={FullName}, Address={Address}, NationalId={NationalId}, CardId={CardId}",
            result.FirstName,
            result.FullName,
            result.Address,
            result.NationalId,
            result.CardId);

        return result;
    }

    // =========================================================
    // PERSONAL INFORMATION
    // =========================================================

    private async Task<List<string>> ReadArabicLinesAsync(
        Mat crop,
        bool enlarge,
        CancellationToken ct)
    {
        if (crop.Empty())
            return new List<string>();

        using Mat prepared =
            PrepareTextForOcr(
                crop,
                enlarge);

        return await ReadPreparedLinesAsync(
            prepared,
            rightToLeft: true,
            fixArabicDirection: true,
            ct);
    }

    private static List<string> FilterPersonalLines(
        IEnumerable<string> lines)
    {
        var result =
            new List<string>();

        foreach (string rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(
                    rawLine))
            {
                continue;
            }

            string line =
                Regex.Replace(
                    rawLine.Trim(),
                    @"\s+",
                    " ");

            /*
             * Personal information must contain at least one
             * actual Arabic letter.
             */
            if (!ContainsArabicLetter(line))
                continue;

            /*
             * Avoid duplicated OCR lines.
             */
            bool duplicate =
                result.Any(
                    existing =>
                        SameText(
                            existing,
                            line));

            if (duplicate)
                continue;

            result.Add(line);
        }

        return result;
    }

    private static bool ContainsArabicLetter(
        string text)
    {
        return
            !string.IsNullOrWhiteSpace(text) &&
            ArabicLetterRegex.IsMatch(text);
    }

    private static void MergeFirstNameFallback(
        List<string> personalLines,
        List<string> fallbackLines)
    {
        if (fallbackLines.Count == 0)
            return;

        string candidate =
            fallbackLines[0];

        if (string.IsNullOrWhiteSpace(
                candidate))
        {
            return;
        }

        /*
         * Main personal OCR found nothing.
         */
        if (personalLines.Count == 0)
        {
            personalLines.Add(
                candidate);

            return;
        }

        /*
         * Main OCR already found this same first name.
         */
        if (SameText(
                candidate,
                personalLines[0]))
        {
            return;
        }

        /*
         * The fallback crop can occasionally include part of
         * the full-name line.
         *
         * Do not insert it if it is already one of the first
         * two main OCR lines.
         */
        bool alreadyPresent =
            personalLines
                .Take(2)
                .Any(
                    line =>
                        SameText(
                            candidate,
                            line));

        if (alreadyPresent)
            return;

        /*
         * The most likely explanation is that the main OCR
         * missed the first-name line.
         */
        personalLines.Insert(
            0,
            candidate);
    }

    private static bool SameText(
        string? first,
        string? second)
    {
        if (string.IsNullOrWhiteSpace(first) ||
            string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        string a =
            NormalizeForComparison(
                first);

        string b =
            NormalizeForComparison(
                second);

        if (a.Length == 0 ||
            b.Length == 0)
        {
            return false;
        }

        if (a == b)
            return true;

        /*
         * The same text can vary slightly between two OCR
         * crops because of spacing or one small character.
         */
        if (a.Length >= 4 &&
            b.Length >= 4)
        {
            if (a.Contains(b) ||
                b.Contains(a))
            {
                double shorter =
                    Math.Min(
                        a.Length,
                        b.Length);

                double longer =
                    Math.Max(
                        a.Length,
                        b.Length);

                /*
                 * Avoid treating a very short name fragment
                 * as identical to a much longer full name.
                 */
                if (shorter / longer >= 0.60)
                    return true;
            }
        }

        return false;
    }

    private static string NormalizeForComparison(
        string value)
    {
        /*
         * Normalize common Arabic forms only for comparison.
         *
         * The actual OCR value returned to the user remains
         * untouched.
         */
        value = value
            .Replace('أ', 'ا')
            .Replace('إ', 'ا')
            .Replace('آ', 'ا')
            .Replace('ٱ', 'ا');

        /*
         * Remove Arabic diacritics for comparison.
         */
        value =
            Regex.Replace(
                value,
                @"[\u064B-\u065F\u0670]",
                "");

        /*
         * Ignore spaces and punctuation during comparison.
         */
        return new string(
            value
                .Where(
                    char.IsLetterOrDigit)
                .ToArray());
    }

    // =========================================================
    // IMAGE QUALITY
    // =========================================================

    private static ImageQualityInfo CheckImageQuality(
        Mat image)
    {
        using var gray =
            new Mat();

        Cv2.CvtColor(
            image,
            gray,
            ColorConversionCodes.BGR2GRAY);

        Cv2.MeanStdDev(
            gray,
            out Scalar mean,
            out Scalar standardDeviation);

        double brightness =
            mean.Val0;

        double contrast =
            standardDeviation.Val0;

        using var laplacian =
            new Mat();

        Cv2.Laplacian(
            gray,
            laplacian,
            MatType.CV_64FC1);

        Cv2.MeanStdDev(
            laplacian,
            out _,
            out Scalar laplacianDeviation);

        double sharpness =
            laplacianDeviation.Val0 *
            laplacianDeviation.Val0;

        string? message = null;

        /*
         * Do not reject based on aspect ratio.
         *
         * A square phone image can contain a perfectly
         * readable ID card.
         */
        if (image.Cols < 320 ||
            image.Rows < 180)
        {
            message =
                "The image resolution is too small.";
        }
        else if (brightness < 30)
        {
            message =
                "The image is too dark.";
        }
        else if (brightness > 245)
        {
            message =
                "The image is overexposed.";
        }
        else if (contrast < 10)
        {
            message =
                "The image has too little contrast.";
        }
        else if (sharpness < 15)
        {
            message =
                "The image is too blurry.";
        }

        return new ImageQualityInfo
        {
            IsReadable =
                message is null,

            Sharpness =
                Math.Round(
                    sharpness,
                    2),

            Brightness =
                Math.Round(
                    brightness,
                    2),

            Contrast =
                Math.Round(
                    contrast,
                    2),

            Message =
                message
        };
    }

    // =========================================================
    // CARD NORMALIZATION
    // =========================================================

    private static Mat NormalizeCard(
        Mat source)
    {
        using Mat oriented =
            EnsureLandscape(
                source);

        /*
         * Try to detect and perspective-correct the physical
         * card first.
         */
        if (TryFindCardCorners(
                oriented,
                out Point2f[] corners))
        {
            return WarpCard(
                oriented,
                corners);
        }

        /*
         * Some scans/photos contain only the card and therefore
         * have no clear outer contour.
         */
        var normalized =
            new Mat();

        Cv2.Resize(
            oriented,
            normalized,
            NormalizedSize,
            0,
            0,
            InterpolationFlags.Cubic);

        return normalized;
    }

    private static Mat EnsureLandscape(
        Mat image)
    {
        /*
         * Landscape and square images stay unchanged.
         */
        if (image.Cols >=
            image.Rows)
        {
            return image.Clone();
        }

        var rotated =
            new Mat();

        Cv2.Rotate(
            image,
            rotated,
            RotateFlags.Rotate90Clockwise);

        return rotated;
    }

    private static bool TryFindCardCorners(
        Mat image,
        out Point2f[] corners)
    {
        corners =
            Array.Empty<Point2f>();

        using var gray =
            new Mat();

        using var blurred =
            new Mat();

        using var edges =
            new Mat();

        Cv2.CvtColor(
            image,
            gray,
            ColorConversionCodes.BGR2GRAY);

        Cv2.GaussianBlur(
            gray,
            blurred,
            new Size(5, 5),
            0);

        Cv2.Canny(
            blurred,
            edges,
            50,
            150);

        using Mat kernel =
            Cv2.GetStructuringElement(
                MorphShapes.Rect,
                new Size(5, 5));

        Cv2.MorphologyEx(
            edges,
            edges,
            MorphTypes.Close,
            kernel);

        Cv2.FindContours(
            edges,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0)
            return false;

        double imageArea =
            image.Cols *
            (double)image.Rows;

        IEnumerable<Point[]> orderedContours =
            contours
                .OrderByDescending(
                    contour =>
                        Cv2.ContourArea(
                            contour))
                .Take(15);

        foreach (Point[] contour in
                 orderedContours)
        {
            double area =
                Cv2.ContourArea(
                    contour);

            /*
             * Ignore small objects such as text regions and
             * the portrait.
             */
            if (area <
                imageArea * 0.20)
            {
                continue;
            }

            double perimeter =
                Cv2.ArcLength(
                    contour,
                    true);

            Point[] approx =
                Cv2.ApproxPolyDP(
                    contour,
                    perimeter * 0.02,
                    true);

            if (approx.Length != 4)
                continue;

            if (!Cv2.IsContourConvex(
                    approx))
            {
                continue;
            }

            Point2f[] ordered =
                OrderCorners(
                    approx);

            double topWidth =
                Distance(
                    ordered[0],
                    ordered[1]);

            double bottomWidth =
                Distance(
                    ordered[3],
                    ordered[2]);

            double leftHeight =
                Distance(
                    ordered[0],
                    ordered[3]);

            double rightHeight =
                Distance(
                    ordered[1],
                    ordered[2]);

            double width =
                (
                    topWidth +
                    bottomWidth
                ) / 2.0;

            double height =
                (
                    leftHeight +
                    rightHeight
                ) / 2.0;

            if (width < 1 ||
                height < 1)
            {
                continue;
            }

            double ratio =
                Math.Max(
                    width,
                    height) /
                Math.Min(
                    width,
                    height);

            /*
             * Keep this intentionally loose because a phone
             * image can introduce perspective distortion.
             */
            if (ratio < 1.25 ||
                ratio > 2.10)
            {
                continue;
            }

            corners =
                ordered;

            return true;
        }

        return false;
    }

    private static Point2f[] OrderCorners(
        Point[] points)
    {
        Point2f[] converted =
            points
                .Select(
                    point =>
                        new Point2f(
                            point.X,
                            point.Y))
                .ToArray();

        Point2f topLeft =
            converted
                .OrderBy(
                    point =>
                        point.X +
                        point.Y)
                .First();

        Point2f bottomRight =
            converted
                .OrderByDescending(
                    point =>
                        point.X +
                        point.Y)
                .First();

        Point2f topRight =
            converted
                .OrderByDescending(
                    point =>
                        point.X -
                        point.Y)
                .First();

        Point2f bottomLeft =
            converted
                .OrderBy(
                    point =>
                        point.X -
                        point.Y)
                .First();

        return new[]
        {
            topLeft,
            topRight,
            bottomRight,
            bottomLeft
        };
    }

    private static Mat WarpCard(
        Mat image,
        Point2f[] corners)
    {
        double topWidth =
            Distance(
                corners[0],
                corners[1]);

        double bottomWidth =
            Distance(
                corners[3],
                corners[2]);

        double leftHeight =
            Distance(
                corners[0],
                corners[3]);

        double rightHeight =
            Distance(
                corners[1],
                corners[2]);

        double averageWidth =
            (
                topWidth +
                bottomWidth
            ) / 2.0;

        double averageHeight =
            (
                leftHeight +
                rightHeight
            ) / 2.0;

        bool landscape =
            averageWidth >=
            averageHeight;

        Size warpSize =
            landscape
                ? new Size(
                    1000,
                    630)
                : new Size(
                    630,
                    1000);

        Point2f[] destination =
        {
            new(0, 0),

            new(
                warpSize.Width - 1,
                0),

            new(
                warpSize.Width - 1,
                warpSize.Height - 1),

            new(
                0,
                warpSize.Height - 1)
        };

        using Mat transform =
            Cv2.GetPerspectiveTransform(
                corners,
                destination);

        var warped =
            new Mat();

        Cv2.WarpPerspective(
            image,
            warped,
            transform,
            warpSize);

        if (landscape)
            return warped;

        var rotated =
            new Mat();

        Cv2.Rotate(
            warped,
            rotated,
            RotateFlags.Rotate90Clockwise);

        warped.Dispose();

        return rotated;
    }

    private static double Distance(
        Point2f a,
        Point2f b)
    {
        double x =
            a.X -
            b.X;

        double y =
            a.Y -
            b.Y;

        return Math.Sqrt(
            x * x +
            y * y);
    }

    // =========================================================
    // CROPPING
    // =========================================================

    private static Mat SafeCrop(
        Mat image,
        Rect requested)
    {
        int x =
            Math.Max(
                requested.X,
                0);

        int y =
            Math.Max(
                requested.Y,
                0);

        int right =
            Math.Min(
                requested.X +
                requested.Width,
                image.Cols);

        int bottom =
            Math.Min(
                requested.Y +
                requested.Height,
                image.Rows);

        int width =
            right -
            x;

        int height =
            bottom -
            y;

        if (width <= 0 ||
            height <= 0)
        {
            return new Mat();
        }

        Rect safe =
            new(
                x,
                y,
                width,
                height);

        using Mat roi =
            new(
                image,
                safe);

        return roi.Clone();
    }

    // =========================================================
    // TEXT PREPROCESSING
    // =========================================================

    private static Mat PrepareTextForOcr(
        Mat source,
        bool enlarge)
    {
        /*
         * Padding helps Paddle detect text near ROI edges.
         */
        using var padded =
            new Mat();

        Cv2.CopyMakeBorder(
            source,
            padded,
            15,
            15,
            20,
            20,
            BorderTypes.Constant,
            new Scalar(
                255,
                255,
                255));

        using var gray =
            new Mat();

        Cv2.CvtColor(
            padded,
            gray,
            ColorConversionCodes.BGR2GRAY);

        using var clahe =
            Cv2.CreateCLAHE(
                2.0,
                new Size(
                    8,
                    8));

        using var enhanced =
            new Mat();

        clahe.Apply(
            gray,
            enhanced);

        Mat working;

        if (enlarge)
        {
            working =
                new Mat();

            Cv2.Resize(
                enhanced,
                working,
                new Size(
                    enhanced.Cols * 2,
                    enhanced.Rows * 2),
                0,
                0,
                InterpolationFlags.Cubic);
        }
        else
        {
            working =
                enhanced.Clone();
        }

        var output =
            new Mat();

        Cv2.CvtColor(
            working,
            output,
            ColorConversionCodes.GRAY2BGR);

        working.Dispose();

        return output;
    }

    // =========================================================
    // NATIONAL ID OCR
    // =========================================================

    private async Task<string?> ReadNationalIdAsync(
        Mat crop,
        CancellationToken ct)
    {
        if (crop.Empty())
            return null;

        string? bestResult =
            null;

        // -----------------------------------------------------
        // PASS 1
        // Contrast enhancement + enlargement
        // -----------------------------------------------------

        using (Mat first =
               PrepareNumberForOcr(
                   crop))
        {
            List<string> lines =
                await ReadPreparedLinesAsync(
                    first,
                    rightToLeft: false,
                    fixArabicDirection: false,
                    ct);

            string? value =
                ExtractNationalId(
                    lines);

            if (value?.Length == 14)
                return value;

            bestResult =
                ChooseBetterNationalId(
                    bestResult,
                    value);
        }

        // -----------------------------------------------------
        // PASS 2
        // Binary image
        // -----------------------------------------------------

        using (Mat second =
               PrepareBinaryNumberForOcr(
                   crop))
        {
            List<string> lines =
                await ReadPreparedLinesAsync(
                    second,
                    rightToLeft: false,
                    fixArabicDirection: false,
                    ct);

            string? value =
                ExtractNationalId(
                    lines);

            if (value?.Length == 14)
                return value;

            bestResult =
                ChooseBetterNationalId(
                    bestResult,
                    value);
        }

        // -----------------------------------------------------
        // PASS 3
        // Simple enlarged image
        // -----------------------------------------------------

        using (Mat third =
               PrepareSimpleNumberForOcr(
                   crop))
        {
            List<string> lines =
                await ReadPreparedLinesAsync(
                    third,
                    rightToLeft: false,
                    fixArabicDirection: false,
                    ct);

            string? value =
                ExtractNationalId(
                    lines);

            if (value?.Length == 14)
                return value;

            bestResult =
                ChooseBetterNationalId(
                    bestResult,
                    value);
        }

        return bestResult;
    }

    private static string? ChooseBetterNationalId(
        string? current,
        string? candidate)
    {
        if (string.IsNullOrWhiteSpace(
                candidate))
        {
            return current;
        }

        if (string.IsNullOrWhiteSpace(
                current))
        {
            return candidate;
        }

        /*
         * Prefer the result containing more digits.
         */
        if (candidate.Length >
            current.Length)
        {
            return candidate;
        }

        /*
         * When both contain 14 digits, prefer the candidate
         * that contains a valid encoded date.
         */
        if (candidate.Length == 14 &&
            current.Length == 14)
        {
            bool candidateValid =
                IsValidNationalId(
                    candidate);

            bool currentValid =
                IsValidNationalId(
                    current);

            if (candidateValid &&
                !currentValid)
            {
                return candidate;
            }
        }

        return current;
    }

    // =========================================================
    // NUMBER PREPROCESSING
    // =========================================================

    private static Mat PrepareNumberForOcr(
        Mat source)
    {
        using var padded =
            AddNumberPadding(
                source);

        using var gray =
            new Mat();

        Cv2.CvtColor(
            padded,
            gray,
            ColorConversionCodes.BGR2GRAY);

        using var clahe =
            Cv2.CreateCLAHE(
                2.5,
                new Size(
                    8,
                    8));

        using var enhanced =
            new Mat();

        clahe.Apply(
            gray,
            enhanced);

        using var enlarged =
            new Mat();

        Cv2.Resize(
            enhanced,
            enlarged,
            new Size(
                enhanced.Cols * 2,
                enhanced.Rows * 2),
            0,
            0,
            InterpolationFlags.Cubic);

        var output =
            new Mat();

        Cv2.CvtColor(
            enlarged,
            output,
            ColorConversionCodes.GRAY2BGR);

        return output;
    }

    private static Mat PrepareSimpleNumberForOcr(
        Mat source)
    {
        using var padded =
            AddNumberPadding(
                source);

        var enlarged =
            new Mat();

        Cv2.Resize(
            padded,
            enlarged,
            new Size(
                padded.Cols * 2,
                padded.Rows * 2),
            0,
            0,
            InterpolationFlags.Cubic);

        return enlarged;
    }

    private static Mat PrepareBinaryNumberForOcr(
        Mat source)
    {
        using var padded =
            AddNumberPadding(
                source);

        using var gray =
            new Mat();

        Cv2.CvtColor(
            padded,
            gray,
            ColorConversionCodes.BGR2GRAY);

        using var enlarged =
            new Mat();

        Cv2.Resize(
            gray,
            enlarged,
            new Size(
                gray.Cols * 2,
                gray.Rows * 2),
            0,
            0,
            InterpolationFlags.Cubic);

        using var blurred =
            new Mat();

        Cv2.GaussianBlur(
            enlarged,
            blurred,
            new Size(
                3,
                3),
            0);

        using var binary =
            new Mat();

        Cv2.AdaptiveThreshold(
            blurred,
            binary,
            255,
            AdaptiveThresholdTypes.GaussianC,
            ThresholdTypes.Binary,
            31,
            10);

        var output =
            new Mat();

        Cv2.CvtColor(
            binary,
            output,
            ColorConversionCodes.GRAY2BGR);

        return output;
    }

    private static Mat AddNumberPadding(
        Mat source)
    {
        var padded =
            new Mat();

        /*
         * Extra right-side padding is useful for the final
         * National ID digits.
         */
        Cv2.CopyMakeBorder(
            source,
            padded,
            20,
            20,
            30,
            50,
            BorderTypes.Constant,
            new Scalar(
                255,
                255,
                255));

        return padded;
    }

    private async Task<List<string>> ReadNumberLinesAsync(
        Mat crop,
        int expectedMinimumDigits,
        CancellationToken ct)
    {
        if (crop.Empty())
            return new List<string>();

        using Mat first =
            PrepareNumberForOcr(
                crop);

        List<string> firstPass =
            await ReadPreparedLinesAsync(
                first,
                rightToLeft: false,
                fixArabicDirection: false,
                ct);

        int firstDigits =
            CountDigits(
                firstPass);

        if (firstDigits >=
            expectedMinimumDigits)
        {
            return firstPass;
        }

        using Mat second =
            PrepareBinaryNumberForOcr(
                crop);

        List<string> secondPass =
            await ReadPreparedLinesAsync(
                second,
                rightToLeft: false,
                fixArabicDirection: false,
                ct);

        int secondDigits =
            CountDigits(
                secondPass);

        return secondDigits >
               firstDigits
            ? secondPass
            : firstPass;
    }

    // =========================================================
    // OCR LINE GROUPING
    // =========================================================

    private async Task<List<string>> ReadPreparedLinesAsync(
        Mat prepared,
        bool rightToLeft,
        bool fixArabicDirection,
        CancellationToken ct)
    {
        PaddleOcrResult result =
            await _engine.RunAsync(
                prepared,
                ct);

        var pieces =
            new List<OcrPiece>();

        foreach (var region in
                 result.Regions)
        {
            string text =
                fixArabicDirection
                    ? ArabicTextHelper.CleanArabic(
                        region.Text)
                    : ArabicTextHelper.CleanBasic(
                        region.Text);

            if (string.IsNullOrWhiteSpace(
                    text))
            {
                continue;
            }

            pieces.Add(
                new OcrPiece(
                    text,
                    region.Rect.Center.X,
                    region.Rect.Center.Y));
        }

        if (pieces.Count == 0)
            return new List<string>();

        /*
         * Sort detected text regions from top to bottom.
         */
        pieces =
            pieces
                .OrderBy(
                    piece =>
                        piece.Y)
                .ToList();

        /*
         * Paddle can split a single printed line into multiple
         * OCR regions.
         *
         * Group regions with approximately the same Y position.
         */
        double lineTolerance =
            Math.Max(
                10,
                prepared.Rows * 0.06);

        var rows =
            new List<List<OcrPiece>>();

        foreach (OcrPiece piece in
                 pieces)
        {
            List<OcrPiece>? rowMatch =
                null;

            foreach (List<OcrPiece> row in
                     rows)
            {
                double averageY =
                    row.Average(
                        item =>
                            item.Y);

                if (Math.Abs(
                        averageY -
                        piece.Y) <=
                    lineTolerance)
                {
                    rowMatch =
                        row;

                    break;
                }
            }

            if (rowMatch is null)
            {
                rowMatch =
                    new List<OcrPiece>();

                rows.Add(
                    rowMatch);
            }

            rowMatch.Add(
                piece);
        }

        var lines =
            new List<string>();

        foreach (List<OcrPiece> row in
                 rows.OrderBy(
                     row =>
                         row.Average(
                             item =>
                                 item.Y)))
        {
            IEnumerable<OcrPiece> ordered;

            if (rightToLeft)
            {
                ordered =
                    row.OrderByDescending(
                        item =>
                            item.X);
            }
            else
            {
                ordered =
                    row.OrderBy(
                        item =>
                            item.X);
            }

            string line =
                string.Join(
                    " ",
                    ordered.Select(
                        item =>
                            item.Text));

            line =
                Regex.Replace(
                    line,
                    @"\s+",
                    " ")
                .Trim();

            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static int CountDigits(
        IEnumerable<string> lines)
    {
        return lines
            .SelectMany(
                line =>
                    ArabicTextHelper
                        .NormalizeDigits(
                            line))
            .Count(
                char.IsDigit);
    }

    // =========================================================
    // NATIONAL ID PARSING
    // =========================================================

    private static string? ExtractNationalId(
        IEnumerable<string> lines)
    {
        var candidates =
            new List<string>();

        /*
         * Check every OCR line independently.
         */
        foreach (string line in
                 lines)
        {
            string value =
                PrepareNumericText(
                    line);

            string digits =
                new(
                    value
                        .Where(
                            char.IsDigit)
                        .ToArray());

            if (digits.Length >= 8)
            {
                candidates.Add(
                    digits);
            }
        }

        /*
         * Paddle can split the National ID into several
         * different OCR regions.
         *
         * Therefore also test all regions joined together.
         */
        string combined =
            PrepareNumericText(
                string.Concat(
                    lines));

        string combinedDigits =
            new(
                combined
                    .Where(
                        char.IsDigit)
                    .ToArray());

        if (combinedDigits.Length >= 8)
        {
            candidates.Add(
                combinedDigits);
        }

        /*
         * Perfect result.
         */
        foreach (string candidate in
                 candidates)
        {
            if (candidate.Length == 14)
                return candidate;
        }

        /*
         * OCR may contain additional numeric noise.
         *
         * Search every possible 14-digit sequence.
         */
        foreach (string candidate in
                 candidates
                     .Where(
                         value =>
                             value.Length > 14))
        {
            for (int i = 0;
                 i <= candidate.Length - 14;
                 i++)
            {
                string part =
                    candidate.Substring(
                        i,
                        14);

                if (IsValidNationalId(
                        part))
                {
                    return part;
                }
            }
        }

        /*
         * Test/synthetic cards may contain a printed number
         * which does not encode a valid DOB.
         *
         * Preserve the visible OCR result rather than
         * discarding it.
         */
        foreach (string candidate in
                 candidates
                     .Where(
                         value =>
                             value.Length > 14))
        {
            for (int i = 0;
                 i <= candidate.Length - 14;
                 i++)
            {
                string part =
                    candidate.Substring(
                        i,
                        14);

                if (part[0] == '2' ||
                    part[0] == '3')
                {
                    return part;
                }
            }
        }

        /*
         * Return the longest useful partial result if Paddle
         * failed to recover all 14 digits.
         */
        return candidates
            .Where(
                value =>
                    value.Length >= 8 &&
                    value.Length < 14)
            .OrderByDescending(
                value =>
                    value.Length)
            .FirstOrDefault();
    }

    private static string PrepareNumericText(
        string text)
    {
        string value =
            ArabicTextHelper
                .NormalizeDigits(
                    text);

        /*
         * Conservative OCR fixes only inside numeric areas.
         */
        value = value
            .Replace(
                'O',
                '0')
            .Replace(
                'o',
                '0')
            .Replace(
                'I',
                '1')
            .Replace(
                'l',
                '1')
            .Replace(
                '|',
                '1');

        return value;
    }

    private static bool IsValidNationalId(
        string id)
    {
        if (id.Length != 14)
            return false;

        if (id[0] != '2' &&
            id[0] != '3')
        {
            return false;
        }

        return
            GetDateOfBirthFromNationalId(
                id)
            is not null;
    }

    private static string? GetDateOfBirthFromNationalId(
        string? nationalId)
    {
        if (string.IsNullOrWhiteSpace(
                nationalId))
        {
            return null;
        }

        if (nationalId.Length != 14)
            return null;

        int century;

        if (nationalId[0] == '2')
        {
            century =
                1900;
        }
        else if (nationalId[0] == '3')
        {
            century =
                2000;
        }
        else
        {
            return null;
        }

        if (!int.TryParse(
                nationalId.Substring(
                    1,
                    2),
                out int year))
        {
            return null;
        }

        if (!int.TryParse(
                nationalId.Substring(
                    3,
                    2),
                out int month))
        {
            return null;
        }

        if (!int.TryParse(
                nationalId.Substring(
                    5,
                    2),
                out int day))
        {
            return null;
        }

        try
        {
            DateTime date =
                new(
                    century +
                    year,
                    month,
                    day);

            if (date.Date >
                DateTime.Today)
            {
                return null;
            }

            return date.ToString(
                "yyyy-MM-dd");
        }
        catch
        {
            return null;
        }
    }

    // =========================================================
    // PRINTED DATE OF BIRTH FALLBACK
    // =========================================================

    private static string? ExtractPrintedDate(
        IEnumerable<string> lines)
    {
        string value =
            string.Join(
                " ",
                lines);

        value =
            ArabicTextHelper
                .NormalizeDigits(
                    value);

        Match match =
            Regex.Match(
                value,
                @"\d{1,4}\s*[\/\-.]\s*\d{1,2}\s*[\/\-.]\s*\d{1,4}");

        if (!match.Success)
            return null;

        string dateText =
            Regex.Replace(
                match.Value,
                @"\s+",
                "");

        string[] formats =
        {
            "d/M/yyyy",
            "dd/M/yyyy",
            "d/MM/yyyy",
            "dd/MM/yyyy",

            "d-M-yyyy",
            "dd-M-yyyy",
            "d-MM-yyyy",
            "dd-MM-yyyy",

            "yyyy/M/d",
            "yyyy/MM/dd",

            "yyyy-M-d",
            "yyyy-MM-dd"
        };

        if (DateTime.TryParseExact(
                dateText,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime date))
        {
            if (date.Date <=
                DateTime.Today)
            {
                return date.ToString(
                    "yyyy-MM-dd");
            }
        }

        return null;
    }

    // =========================================================
    // CARD ID
    // =========================================================

    private static string? ExtractCardId(
        IEnumerable<string> lines)
    {
        string value =
            string.Join(
                "",
                lines)
            .ToUpperInvariant();

        value =
            ArabicTextHelper
                .NormalizeDigits(
                    value);

        value =
            Regex.Replace(
                value,
                @"\s+",
                "");

        /*
         * Common modern format:
         *
         * HW5870214
         * IG6434506
         */
        Match alphaNumeric =
            Regex.Match(
                value,
                @"[A-Z]{1,3}\d{5,10}");

        if (alphaNumeric.Success)
            return alphaNumeric.Value;

        /*
         * Some cards use numeric slash-separated codes.
         */
        Match slash =
            Regex.Match(
                value,
                @"\d{2,6}/\d{3,10}");

        if (slash.Success)
            return slash.Value;

        Match fallback =
            Regex.Match(
                value,
                @"[A-Z0-9/-]{6,16}");

        return fallback.Success
            ? fallback.Value
            : null;
    }

    // =========================================================
    // PHOTO
    // =========================================================

    private static string? ExtractPhoto(
        Mat card)
    {
        using Mat photo =
            SafeCrop(
                card,
                PhotoRegion);

        if (photo.Empty())
            return null;

        bool success =
            Cv2.ImEncode(
                ".jpg",
                photo,
                out byte[] bytes);

        if (!success ||
            bytes.Length == 0)
        {
            return null;
        }

        return Convert.ToBase64String(
            bytes);
    }

    // =========================================================
    // RESULT WARNING
    // =========================================================

    private static string? BuildWarning(
        EgyptianIdOcrResult result)
    {
        var problems =
            new List<string>();

        if (string.IsNullOrWhiteSpace(
                result.FirstName))
        {
            problems.Add(
                "firstName");
        }

        if (string.IsNullOrWhiteSpace(
                result.FullName))
        {
            problems.Add(
                "fullName");
        }

        if (string.IsNullOrWhiteSpace(
                result.Address))
        {
            problems.Add(
                "address");
        }

        if (string.IsNullOrWhiteSpace(
                result.NationalId))
        {
            problems.Add(
                "nationalId");
        }
        else if (result.NationalId.Length != 14)
        {
            problems.Add(
                "nationalId may be incomplete");
        }

        if (string.IsNullOrWhiteSpace(
                result.CardId))
        {
            problems.Add(
                "cardId");
        }

        if (problems.Count == 0)
            return null;

        return
            "OCR completed, but review these fields: " +
            string.Join(
                ", ",
                problems);
    }

    // =========================================================
    // STREAM
    // =========================================================

    private static async Task<byte[]> ReadAllBytesAsync(
        Stream input,
        CancellationToken ct)
    {
        using var memory =
            new MemoryStream();

        await input.CopyToAsync(
            memory,
            ct);

        return memory.ToArray();
    }

    // =========================================================
    // INTERNAL OCR TYPE
    // =========================================================

    private sealed record OcrPiece(
        string Text,
        double X,
        double Y);
}