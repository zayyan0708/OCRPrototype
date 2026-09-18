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

    private static readonly Size NormalizedSize = new(1000, 630);

    // Coordinates are based on the normalized 1000 x 630 card.
    private static readonly Rect PhotoRegion =
        new(15, 35, 300, 370);

    private static readonly Rect PersonalTextRegion =
        new(470, 145, 515, 330);

    private static readonly Rect NationalIdRegion =
        new(480, 470, 505, 110);

    private static readonly Rect DobRegion =
        new(0, 430, 450, 125);

    private static readonly Rect CardIdRegion =
        new(0, 555, 450, 75);

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
        byte[] bytes = await ReadAllBytesAsync(imageStream, ct);

        using Mat decoded = Cv2.ImDecode(bytes, ImreadModes.Color);

        if (decoded.Empty())
        {
            return new EgyptianIdOcrResult
            {
                IsSuccess = false,
                ErrorMessage = "The uploaded file could not be read as an image."
            };
        }

        using Mat oriented = EnsureLandscape(decoded);

        ImageQualityInfo quality = CheckImageQuality(oriented);

        if (!quality.IsReadable)
        {
            return new EgyptianIdOcrResult
            {
                IsSuccess = false,
                Quality = quality,
                ErrorMessage = quality.Message
            };
        }

        using Mat card = NormalizeCard(oriented);

        // -------------------------
        // Personal information
        // -------------------------

        using Mat personalCrop = Crop(card, PersonalTextRegion);

        List<string> personalLines =
            await ReadLinesAsync(personalCrop, true, ct);

        personalLines = personalLines
            .Where(ArabicTextHelper.ContainsArabic)
            .Where(line => !IsCardHeader(line))
            .ToList();

        string? firstName =
            personalLines.Count > 0 ? personalLines[0] : null;

        string? fullName =
            personalLines.Count > 1 ? personalLines[1] : null;

        string? address = personalLines.Count > 2
            ? string.Join(" ", personalLines.Skip(2))
            : null;

        // -------------------------
        // National ID
        // -------------------------

        using Mat nationalIdCrop = Crop(card, NationalIdRegion);

        List<string> nationalIdLines =
            await ReadLinesAsync(nationalIdCrop, false, ct);

        string? nationalId =
            ExtractNationalId(nationalIdLines);

        // -------------------------
        // Card / factory number
        // -------------------------

        using Mat cardIdCrop = Crop(card, CardIdRegion);

        List<string> cardIdLines =
            await ReadLinesAsync(cardIdCrop, false, ct);

        string? cardId =
            ExtractCardId(cardIdLines);

        // -------------------------
        // DOB
        // -------------------------

        string? dateOfBirth =
            GetDateOfBirthFromNationalId(nationalId);

        // Only perform another OCR pass if DOB
        // could not be obtained from the national ID.
        if (dateOfBirth is null)
        {
            using Mat dobCrop = Crop(card, DobRegion);

            List<string> dobLines =
                await ReadLinesAsync(dobCrop, false, ct);

            dateOfBirth = ExtractPrintedDate(dobLines);
        }

        // -------------------------
        // Photo
        // -------------------------

        string photo = ExtractPhoto(card);

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

        result.Warning = BuildWarning(result);

        return result;
    }

    private static Mat EnsureLandscape(Mat image)
    {
        if (image.Cols >= image.Rows)
            return image.Clone();

        var rotated = new Mat();

        Cv2.Rotate(
            image,
            rotated,
            RotateFlags.Rotate90Clockwise);

        return rotated;
    }

    private static ImageQualityInfo CheckImageQuality(Mat image)
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

        double brightness = mean.Val0;
        double contrast = standardDeviation.Val0;

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

        double ratio =
            image.Cols / (double)image.Rows;

        string? message = null;

        if (image.Cols < 420 || image.Rows < 250)
        {
            message = "The image resolution is too small.";
        }
        else if (ratio < 1.45 || ratio > 1.80)
        {
            message = "The full ID card is not clearly visible.";
        }
        else if (brightness < 35)
        {
            message = "The image is too dark.";
        }
        else if (brightness > 235)
        {
            message = "The image is overexposed.";
        }
        else if (contrast < 18)
        {
            message = "The image has too little contrast.";
        }
        else if (sharpness < 30)
        {
            message = "The image is too blurry.";
        }

        return new ImageQualityInfo
        {
            IsReadable = message is null,
            Sharpness = Math.Round(sharpness, 2),
            Brightness = Math.Round(brightness, 2),
            Contrast = Math.Round(contrast, 2),
            Message = message
        };
    }

    private static Mat NormalizeCard(Mat image)
    {
        var resized = new Mat();

        Cv2.Resize(
            image,
            resized,
            NormalizedSize,
            0,
            0,
            InterpolationFlags.Cubic);

        return resized;
    }

    private static Mat Crop(Mat image, Rect region)
    {
        using Mat roi = new(image, region);

        return roi.Clone();
    }

    private async Task<List<string>> ReadLinesAsync(
        Mat crop,
        bool rightToLeft,
        CancellationToken ct)
    {
        using Mat prepared = PrepareForOcr(crop);

        PaddleOcrResult result =
            await _engine.RunAsync(prepared, ct);

        var pieces = result.Regions
            .Select(region => new OcrPiece(
                ArabicTextHelper.Clean(region.Text),
                region.Rect.Center.X,
                region.Rect.Center.Y))
            .Where(piece => !string.IsNullOrWhiteSpace(piece.Text))
            .OrderBy(piece => piece.Y)
            .ToList();

        if (pieces.Count == 0)
            return new List<string>();

        double lineTolerance =
            Math.Max(10, prepared.Rows * 0.06);

        var rows = new List<List<OcrPiece>>();

        foreach (OcrPiece piece in pieces)
        {
            List<OcrPiece>? matchingRow = null;

            foreach (List<OcrPiece> row in rows)
            {
                double rowY = row.Average(x => x.Y);

                if (Math.Abs(rowY - piece.Y) <= lineTolerance)
                {
                    matchingRow = row;
                    break;
                }
            }

            if (matchingRow is null)
            {
                matchingRow = new List<OcrPiece>();
                rows.Add(matchingRow);
            }

            matchingRow.Add(piece);
        }

        var lines = new List<string>();

        foreach (List<OcrPiece> row in
                 rows.OrderBy(r => r.Average(x => x.Y)))
        {
            IEnumerable<OcrPiece> ordered;

            if (rightToLeft)
                ordered = row.OrderByDescending(x => x.X);
            else
                ordered = row.OrderBy(x => x.X);

            string line = string.Join(
                " ",
                ordered.Select(x => x.Text));

            line = Regex.Replace(
                line,
                @"\s+",
                " ").Trim();

            if (line.Length > 0)
                lines.Add(line);
        }

        return lines;
    }

    private static Mat PrepareForOcr(Mat source)
    {
        using var gray = new Mat();

        Cv2.CvtColor(
            source,
            gray,
            ColorConversionCodes.BGR2GRAY);

        using var clahe =
            Cv2.CreateCLAHE(
                2.0,
                new Size(8, 8));

        var enhanced = new Mat();

        clahe.Apply(
            gray,
            enhanced);

        var output = new Mat();

        Cv2.CvtColor(
            enhanced,
            output,
            ColorConversionCodes.GRAY2BGR);

        enhanced.Dispose();

        return output;
    }

    private static bool IsCardHeader(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        bool republicHeader =
            text.Contains("جمهورية") &&
            text.Contains("العربية");

        bool cardHeader =
            text.Contains("بطاقة") &&
            (text.Contains("الشخصية") ||
             text.Contains("قومي"));

        return republicHeader || cardHeader;
    }

    private static string? ExtractNationalId(
        IEnumerable<string> lines)
    {
        string value = string.Concat(lines);

        value = ArabicTextHelper.NormalizeDigits(value);

        // Common OCR mistakes inside a number-only region.
        value = value
            .Replace('O', '0')
            .Replace('o', '0')
            .Replace('I', '1')
            .Replace('l', '1')
            .Replace('|', '1');

        string digits = new(
            value
                .Where(c => c >= '0' && c <= '9')
                .ToArray());

        if (digits.Length < 14)
            return null;

        // If OCR accidentally included extra digits,
        // find the most plausible 14-digit ID.
        for (int i = 0; i <= digits.Length - 14; i++)
        {
            string candidate =
                digits.Substring(i, 14);

            if (IsValidNationalId(candidate))
                return candidate;
        }

        return null;
    }

    private static bool IsValidNationalId(string id)
    {
        if (id.Length != 14)
            return false;

        if (id[0] != '2' && id[0] != '3')
            return false;

        return GetDateOfBirthFromNationalId(id) is not null;
    }

    private static string? GetDateOfBirthFromNationalId(
        string? nationalId)
    {
        if (string.IsNullOrWhiteSpace(nationalId) ||
            nationalId.Length != 14)
        {
            return null;
        }

        int baseYear = nationalId[0] switch
        {
            '2' => 1900,
            '3' => 2000,
            _ => 0
        };

        if (baseYear == 0)
            return null;

        if (!int.TryParse(
                nationalId.Substring(1, 2),
                out int shortYear) ||
            !int.TryParse(
                nationalId.Substring(3, 2),
                out int month) ||
            !int.TryParse(
                nationalId.Substring(5, 2),
                out int day))
        {
            return null;
        }

        try
        {
            var date = new DateTime(
                baseYear + shortYear,
                month,
                day);

            return date.ToString("yyyy-MM-dd");
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractPrintedDate(
        IEnumerable<string> lines)
    {
        string value = string.Join(
            " ",
            lines);

        value =
            ArabicTextHelper.NormalizeDigits(value);

        Match match = Regex.Match(
            value,
            @"\d{1,4}[\/\-.]\d{1,2}[\/\-.]\d{1,4}");

        if (!match.Success)
            return null;

        string[] formats =
        {
            "d/M/yyyy",
            "dd/MM/yyyy",
            "d-M-yyyy",
            "dd-MM-yyyy",
            "yyyy/M/d",
            "yyyy/MM/dd",
            "yyyy-M-d",
            "yyyy-MM-dd"
        };

        if (DateTime.TryParseExact(
                match.Value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime date))
        {
            return date.ToString("yyyy-MM-dd");
        }

        return null;
    }

    private static string? ExtractCardId(
        IEnumerable<string> lines)
    {
        string value = string.Join(
                "",
                lines)
            .ToUpperInvariant();

        value = Regex.Replace(
            value,
            @"\s+",
            "");

        // Example: QW5613784
        Match alphaNumeric = Regex.Match(
            value,
            @"[A-Z]{1,3}\d{6,10}");

        if (alphaNumeric.Success)
            return alphaNumeric.Value;

        // Some older cards can contain a numeric/slash form.
        Match slashNumber = Regex.Match(
            value,
            @"\d{2,5}/\d{3,8}");

        if (slashNumber.Success)
            return slashNumber.Value;

        Match fallback = Regex.Match(
            value,
            @"[A-Z0-9/-]{6,15}");

        return fallback.Success
            ? fallback.Value
            : null;
    }

    private static string ExtractPhoto(Mat card)
    {
        using Mat photo =
            Crop(card, PhotoRegion);

        Cv2.ImEncode(
            ".jpg",
            photo,
            out byte[] photoBytes);

        return Convert.ToBase64String(photoBytes);
    }

    private static string? BuildWarning(
        EgyptianIdOcrResult result)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(result.FirstName))
            missing.Add("firstName");

        if (string.IsNullOrWhiteSpace(result.FullName))
            missing.Add("fullName");

        if (string.IsNullOrWhiteSpace(result.Address))
            missing.Add("address");

        if (string.IsNullOrWhiteSpace(result.NationalId))
            missing.Add("nationalId");

        if (string.IsNullOrWhiteSpace(result.CardId))
            missing.Add("cardId");

        if (missing.Count == 0)
            return null;

        return "OCR completed, but these fields could not be read confidently: "
               + string.Join(", ", missing);
    }

    private static async Task<byte[]> ReadAllBytesAsync(
        Stream input,
        CancellationToken ct)
    {
        using var memory = new MemoryStream();

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