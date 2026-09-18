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

    /*
     * Every accepted card is converted to this size.
     *
     * After this point all ROI coordinates use exactly
     * the same coordinate system.
     */
    private static readonly Size NormalizedSize =
        new(1000, 630);

    /*
     * These ROIs describe locations on the Egyptian
     * national ID front layout.
     *
     * They are deliberately a little wider than the
     * exact text to tolerate differences between cards.
     */

    private static readonly Rect PhotoRegion =
        new(15, 35, 300, 370);

    private static readonly Rect PersonalTextRegion =
        new(430, 125, 555, 350);

    private static readonly Rect NationalIdRegion =
        new(350, 435, 640, 150);

    private static readonly Rect DobRegion =
        new(0, 420, 380, 140);

    private static readonly Rect CardIdRegion =
        new(0, 535, 470, 95);

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

        /*
         * STEP 1
         * Check actual pixel quality.
         *
         * We intentionally do NOT reject an image because
         * its aspect ratio is square.
         */
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

        /*
         * STEP 2
         * Locate/straighten the card when possible,
         * then make every card exactly 1000 x 630.
         */
        using Mat card =
            NormalizeCard(original);

        /*
         * STEP 3
         * Extract personal-information region.
         */
        using Mat personalCrop =
            SafeCrop(
                card,
                PersonalTextRegion);

        List<string> personalLines =
            await ReadTextLinesAsync(
                personalCrop,
                ct);

        personalLines = personalLines
            .Where(line =>
                !string.IsNullOrWhiteSpace(line))
            .Where(ArabicTextHelper.ContainsArabic)
            .Where(line => !IsCardHeader(line))
            .ToList();

        string? firstName = null;
        string? fullName = null;
        string? address = null;

        if (personalLines.Count >= 1)
            firstName = personalLines[0];

        if (personalLines.Count >= 2)
            fullName = personalLines[1];

        if (personalLines.Count >= 3)
        {
            address = string.Join(
                " ",
                personalLines.Skip(2));
        }

        /*
         * National ID
         */
        using Mat nationalIdCrop =
            SafeCrop(
                card,
                NationalIdRegion);

        List<string> nationalIdLines =
            await ReadNumberLinesAsync(
                nationalIdCrop,
                expectedMinimumDigits: 12,
                ct);

        string? nationalId =
            ExtractNationalId(
                nationalIdLines);

        /*
         * Card/factory ID
         */
        using Mat cardIdCrop =
            SafeCrop(
                card,
                CardIdRegion);

        List<string> cardIdLines =
            await ReadNumberLinesAsync(
                cardIdCrop,
                expectedMinimumDigits: 5,
                ct);

        string? cardId =
            ExtractCardId(
                cardIdLines);

        /*
         * Date of birth.
         *
         * First derive it from the national ID.
         * This is much more reliable than OCRing another
         * tiny field.
         */
        string? dateOfBirth =
            GetDateOfBirthFromNationalId(
                nationalId);

        /*
         * Only OCR the printed DOB if the ID itself
         * did not provide a valid DOB.
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

        /*
         * Portrait does not need OCR.
         * Just crop it from the normalized card.
         */
        string? photo =
            ExtractPhoto(card);

        var result = new EgyptianIdOcrResult
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
            "Egyptian ID OCR completed. " +
            "Name={HasName}, NationalId={HasNationalId}, CardId={HasCardId}",
            !string.IsNullOrWhiteSpace(result.FirstName),
            !string.IsNullOrWhiteSpace(result.NationalId),
            !string.IsNullOrWhiteSpace(result.CardId));

        return result;
    }

    // ==========================================================
    // IMAGE QUALITY
    // ==========================================================

    private static ImageQualityInfo CheckImageQuality(
        Mat image)
    {
        using var gray = new Mat();

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

        using var laplacian = new Mat();

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
         * Do not use aspect ratio here.
         *
         * A user may upload a square phone picture
         * containing a perfectly readable ID card.
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
                Math.Round(sharpness, 2),

            Brightness =
                Math.Round(brightness, 2),

            Contrast =
                Math.Round(contrast, 2),

            Message = message
        };
    }

    // ==========================================================
    // CARD NORMALIZATION
    // ==========================================================

    private static Mat NormalizeCard(
        Mat source)
    {
        using Mat oriented =
            EnsureLandscape(source);

        /*
         * First try to find the actual rectangular ID
         * card inside a larger photograph.
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
         * Some scans/photos contain ONLY the card and
         * therefore have no visible outside border.
         *
         * In that situation simply normalize the whole
         * image instead of rejecting it.
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
         * Square images are deliberately left alone.
         */
        if (image.Cols >= image.Rows)
            return image.Clone();

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
        corners = Array.Empty<Point2f>();

        using var gray = new Mat();
        using var blurred = new Mat();
        using var edges = new Mat();

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
            image.Cols * (double)image.Rows;

        // Important:
        // Use a lambda instead of passing Cv2.ContourArea directly.
        // ContourArea has multiple overloads, which causes CS0411.
        IEnumerable<Point[]> orderedContours =
            contours
                .OrderByDescending(
                    contour => Cv2.ContourArea(contour))
                .Take(15);

        foreach (Point[] contour in orderedContours)
        {
            double area =
                Cv2.ContourArea(contour);

            // Ignore small objects/text/photo regions.
            if (area < imageArea * 0.20)
                continue;

            double perimeter =
                Cv2.ArcLength(
                    contour,
                    true);

            Point[] approx =
                Cv2.ApproxPolyDP(
                    contour,
                    perimeter * 0.02,
                    true);

            // We need a four-corner rectangular object.
            if (approx.Length != 4)
                continue;

            if (!Cv2.IsContourConvex(approx))
                continue;

            Point2f[] ordered =
                OrderCorners(approx);

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
                (topWidth + bottomWidth) / 2.0;

            double height =
                (leftHeight + rightHeight) / 2.0;

            if (width < 1 ||
                height < 1)
            {
                continue;
            }

            double ratio =
                Math.Max(width, height) /
                Math.Min(width, height);

            // Loose range because phone photos may have perspective.
            if (ratio < 1.25 ||
                ratio > 2.10)
            {
                continue;
            }

            corners = ordered;
            return true;
        }

        return false;
    }

    private static Point2f[] OrderCorners(
        Point[] points)
    {
        Point2f[] p =
            points
                .Select(point =>
                    new Point2f(
                        point.X,
                        point.Y))
                .ToArray();

        Point2f topLeft =
            p.OrderBy(point =>
                    point.X + point.Y)
                .First();

        Point2f bottomRight =
            p.OrderByDescending(point =>
                    point.X + point.Y)
                .First();

        Point2f topRight =
            p.OrderByDescending(point =>
                    point.X - point.Y)
                .First();

        Point2f bottomLeft =
            p.OrderBy(point =>
                    point.X - point.Y)
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
            (topWidth + bottomWidth) / 2.0;

        double averageHeight =
            (leftHeight + rightHeight) / 2.0;

        bool cardIsLandscape =
            averageWidth >= averageHeight;

        Size warpSize =
            cardIsLandscape
                ? new Size(1000, 630)
                : new Size(630, 1000);

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

        if (cardIsLandscape)
            return warped;

        /*
         * If the physical card was photographed vertically,
         * rotate the corrected result back to landscape.
         */
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
            a.X - b.X;

        double y =
            a.Y - b.Y;

        return Math.Sqrt(
            x * x +
            y * y);
    }

    // ==========================================================
    // CROPPING
    // ==========================================================

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
            right - x;

        int height =
            bottom - y;

        if (width <= 0 ||
            height <= 0)
        {
            return new Mat();
        }

        var safeRect =
            new Rect(
                x,
                y,
                width,
                height);

        using Mat roi =
            new(
                image,
                safeRect);

        return roi.Clone();
    }

    // ==========================================================
    // OCR PREPROCESSING
    // ==========================================================

    private static Mat PrepareTextForOcr(
        Mat source)
    {
        using var gray =
            new Mat();

        Cv2.CvtColor(
            source,
            gray,
            ColorConversionCodes.BGR2GRAY);

        using var clahe =
            Cv2.CreateCLAHE(
                2.0,
                new Size(8, 8));

        using var enhanced =
            new Mat();

        clahe.Apply(
            gray,
            enhanced);

        var output =
            new Mat();

        Cv2.CvtColor(
            enhanced,
            output,
            ColorConversionCodes.GRAY2BGR);

        return output;
    }

    private static Mat PrepareNumberForOcr(
        Mat source)
    {
        using var gray =
            new Mat();

        Cv2.CvtColor(
            source,
            gray,
            ColorConversionCodes.BGR2GRAY);

        using var clahe =
            Cv2.CreateCLAHE(
                2.5,
                new Size(8, 8));

        using var enhanced =
            new Mat();

        clahe.Apply(
            gray,
            enhanced);

        int width =
            Math.Max(
                enhanced.Cols * 2,
                1);

        int height =
            Math.Max(
                enhanced.Rows * 2,
                1);

        using var enlarged =
            new Mat();

        Cv2.Resize(
            enhanced,
            enlarged,
            new Size(
                width,
                height),
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

    /*
     * Fallback for a difficult number.
     *
     * This is not another OCR model.
     * It is just another OpenCV image representation.
     */
    private static Mat PrepareBinaryNumberForOcr(
        Mat source)
    {
        using var gray =
            new Mat();

        Cv2.CvtColor(
            source,
            gray,
            ColorConversionCodes.BGR2GRAY);

        using var enlarged =
            new Mat();

        Cv2.Resize(
            gray,
            enlarged,
            new Size(
                Math.Max(gray.Cols * 2, 1),
                Math.Max(gray.Rows * 2, 1)),
            0,
            0,
            InterpolationFlags.Cubic);

        using var blurred =
            new Mat();

        Cv2.GaussianBlur(
            enlarged,
            blurred,
            new Size(3, 3),
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

    // ==========================================================
    // OCR READING
    // ==========================================================

    private async Task<List<string>> ReadTextLinesAsync(
        Mat crop,
        CancellationToken ct)
    {
        if (crop.Empty())
            return new List<string>();

        using Mat prepared =
            PrepareTextForOcr(crop);

        return await ReadPreparedLinesAsync(
            prepared,
            rightToLeft: true,
            fixArabicDirection: true,
            ct);
    }

    private async Task<List<string>> ReadNumberLinesAsync(
        Mat crop,
        int expectedMinimumDigits,
        CancellationToken ct)
    {
        if (crop.Empty())
            return new List<string>();

        /*
         * Normal enhanced grayscale pass.
         */
        using Mat prepared =
            PrepareNumberForOcr(crop);

        List<string> firstPass =
            await ReadPreparedLinesAsync(
                prepared,
                rightToLeft: false,
                fixArabicDirection: false,
                ct);

        int firstDigitCount =
            CountDigits(firstPass);

        /*
         * If the normal version already contains enough
         * digits there is no reason to run OCR again.
         */
        if (firstDigitCount >=
            expectedMinimumDigits)
        {
            return firstPass;
        }

        /*
         * Otherwise try a binary representation.
         */
        using Mat binary =
            PrepareBinaryNumberForOcr(crop);

        List<string> secondPass =
            await ReadPreparedLinesAsync(
                binary,
                rightToLeft: false,
                fixArabicDirection: false,
                ct);

        int secondDigitCount =
            CountDigits(secondPass);

        /*
         * Keep whichever result preserved more numeric
         * information.
         */
        return secondDigitCount >
               firstDigitCount
            ? secondPass
            : firstPass;
    }

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

        foreach (var region in result.Regions)
        {
            string text =
                fixArabicDirection
                    ? ArabicTextHelper.CleanArabic(
                        region.Text)
                    : ArabicTextHelper.CleanBasic(
                        region.Text);

            if (string.IsNullOrWhiteSpace(text))
                continue;

            pieces.Add(
                new OcrPiece(
                    text,
                    region.Rect.Center.X,
                    region.Rect.Center.Y));
        }

        if (pieces.Count == 0)
            return new List<string>();

        pieces =
            pieces
                .OrderBy(piece =>
                    piece.Y)
                .ToList();

        /*
         * Paddle can split one printed line into multiple
         * OCR regions.
         *
         * Group regions that have almost the same Y value.
         */
        double lineTolerance =
            Math.Max(
                10,
                prepared.Rows * 0.06);

        var rows =
            new List<List<OcrPiece>>();

        foreach (OcrPiece piece in pieces)
        {
            List<OcrPiece>? matchingRow =
                null;

            foreach (List<OcrPiece> row in rows)
            {
                double rowY =
                    row.Average(
                        item =>
                            item.Y);

                if (Math.Abs(
                        rowY -
                        piece.Y) <=
                    lineTolerance)
                {
                    matchingRow = row;
                    break;
                }
            }

            if (matchingRow is null)
            {
                matchingRow =
                    new List<OcrPiece>();

                rows.Add(
                    matchingRow);
            }

            matchingRow.Add(
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
            IEnumerable<OcrPiece> orderedPieces;

            if (rightToLeft)
            {
                orderedPieces =
                    row.OrderByDescending(
                        piece =>
                            piece.X);
            }
            else
            {
                orderedPieces =
                    row.OrderBy(
                        piece =>
                            piece.X);
            }

            string line =
                string.Join(
                    " ",
                    orderedPieces.Select(
                        piece =>
                            piece.Text));

            line =
                Regex.Replace(
                    line,
                    @"\s+",
                    " ");

            line =
                line.Trim();

            if (line.Length > 0)
                lines.Add(line);
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
                        .NormalizeDigits(line))
            .Count(char.IsDigit);
    }

    // ==========================================================
    // PERSONAL TEXT
    // ==========================================================

    private static bool IsCardHeader(
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        bool republic =
            text.Contains("جمهورية") &&
            text.Contains("العربية");

        bool idCard =
            text.Contains("بطاقة") &&
            (
                text.Contains("الشخصية") ||
                text.Contains("تحقيق")
            );

        return republic ||
               idCard;
    }

    // ==========================================================
    // NATIONAL ID
    // ==========================================================

    private static string? ExtractNationalId(
        IEnumerable<string> lines)
    {
        List<string> candidates =
            new();

        foreach (string line in lines)
        {
            string cleaned =
                PrepareNumericText(line);

            string digits =
                new(
                    cleaned
                        .Where(char.IsDigit)
                        .ToArray());

            if (digits.Length >= 10)
                candidates.Add(digits);
        }

        /*
         * Best case:
         * Paddle found exactly one complete 14-digit ID.
         */
        string? exact =
            candidates
                .FirstOrDefault(
                    candidate =>
                        candidate.Length == 14);

        if (exact is not null)
            return exact;

        /*
         * Sometimes one OCR line contains extra noise.
         * Try every possible 14-digit window and prefer
         * one that represents a structurally valid ID.
         */
        foreach (string candidate in
                 candidates
                     .Where(candidate =>
                         candidate.Length > 14))
        {
            for (int i = 0;
                 i <= candidate.Length - 14;
                 i++)
            {
                string part =
                    candidate.Substring(
                        i,
                        14);

                if (IsValidNationalId(part))
                    return part;
            }
        }

        /*
         * It may be a synthetic/test card whose number does
         * not encode a real date.
         *
         * Do NOT discard the OCR result simply because the
         * validation layer does not approve it.
         */
        foreach (string candidate in
                 candidates
                     .Where(candidate =>
                         candidate.Length > 14))
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
         * Last resort for a long numeric OCR result.
         */
        string? longCandidate =
            candidates
                .Where(candidate =>
                    candidate.Length > 14)
                .OrderByDescending(
                    candidate =>
                        candidate.Length)
                .FirstOrDefault();

        if (longCandidate is not null)
        {
            return longCandidate.Substring(
                0,
                14);
        }

        /*
         * Paddle may miss a digit.
         *
         * Keep the partial OCR value instead of silently
         * changing it to null.
         */
        string? partial =
            candidates
                .Where(candidate =>
                    candidate.Length >= 10 &&
                    candidate.Length < 14)
                .OrderByDescending(
                    candidate =>
                        candidate.Length)
                .FirstOrDefault();

        return partial;
    }

    private static string PrepareNumericText(
        string value)
    {
        value =
            ArabicTextHelper
                .NormalizeDigits(value);

        /*
         * Very conservative OCR corrections.
         *
         * These are safe because this method is only used
         * inside numeric ID regions.
         */
        value = value
            .Replace('O', '0')
            .Replace('o', '0')
            .Replace('I', '1')
            .Replace('l', '1')
            .Replace('|', '1');

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

        return GetDateOfBirthFromNationalId(id)
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
            century = 1900;
        }
        else if (nationalId[0] == '3')
        {
            century = 2000;
        }
        else
        {
            return null;
        }

        if (!int.TryParse(
                nationalId.Substring(
                    1,
                    2),
                out int shortYear))
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
            var date =
                new DateTime(
                    century + shortYear,
                    month,
                    day);

            /*
             * Do not accept a DOB in the future.
             */
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

    // ==========================================================
    // PRINTED DOB FALLBACK
    // ==========================================================

    private static string? ExtractPrintedDate(
        IEnumerable<string> lines)
    {
        string value =
            string.Join(
                " ",
                lines);

        value =
            ArabicTextHelper
                .NormalizeDigits(value);

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

    // ==========================================================
    // CARD ID
    // ==========================================================

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
                .NormalizeDigits(value);

        value =
            Regex.Replace(
                value,
                @"\s+",
                "");

        /*
         * Typical alphanumeric card/factory number.
         * Examples of structure:
         *
         * AB1234567
         * ABC123456
         */
        Match alphaNumeric =
            Regex.Match(
                value,
                @"[A-Z]{1,3}\d{5,10}");

        if (alphaNumeric.Success)
            return alphaNumeric.Value;

        /*
         * Some card formats may contain slash-separated
         * numeric IDs.
         */
        Match slashNumber =
            Regex.Match(
                value,
                @"\d{2,6}/\d{3,10}");

        if (slashNumber.Success)
            return slashNumber.Value;

        /*
         * Conservative fallback.
         */
        Match fallback =
            Regex.Match(
                value,
                @"[A-Z0-9/-]{6,16}");

        return fallback.Success
            ? fallback.Value
            : null;
    }

    // ==========================================================
    // PORTRAIT
    // ==========================================================

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

    // ==========================================================
    // RESULT WARNING
    // ==========================================================

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

    // ==========================================================
    // STREAM
    // ==========================================================

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

    private sealed record OcrPiece(
        string Text,
        double X,
        double Y);
}