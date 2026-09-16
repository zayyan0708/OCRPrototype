using Microsoft.Extensions.Logging;
using OCRPrototype.Models;
using OpenCvSharp;
using Sdcb.PaddleOCR;

namespace OCRPrototype.Services;

public class EgyptianIdOcrService : IEgyptianIdOcrService
{
    private readonly PaddleOcrEngine _engine;
    private readonly ILogger<EgyptianIdOcrService> _logger;

    // Fixed boilerplate that's printed on every card ("Arab Republic of
    // Egypt" / "National ID Card") - it's not personal data, so it gets
    // filtered out rather than showing up as a line in the result.
    private static readonly string[] Boilerplate =
    {
        "جمهورية مصر العربية",
        "جمهوريه مصر العربيه",
        "بطاقة تحقيق الشخصية",
        "بطاقه تحقيق الشخصيه",
    };

    public EgyptianIdOcrService(PaddleOcrEngine engine, ILogger<EgyptianIdOcrService> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public async Task<EgyptianIdOcrResult> ExtractAsync(Stream imageStream, CancellationToken ct = default)
    {
        byte[] bytes = await ReadAllBytesAsync(imageStream, ct);

        using Mat mat = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (mat.Empty())
            throw new InvalidOperationException("The uploaded file couldn't be read as an image.");

        PaddleOcrResult ocrResult = await _engine.RunAsync(mat, ct);
        _logger.LogInformation("OCR found {Count} text regions", ocrResult.Regions.Count());

        List<string> lines = ocrResult.Regions
            // Cards are laid out as stacked horizontal lines, so sorting by
            // the vertical center of each box reconstructs reading order
            // top-to-bottom without needing anything fancier.
            .OrderBy(region => region.Rect.Center.Y)
            .Select(region => ArabicTextHelper.NormalizeDigits(region.Text.Trim()))
            .Where(text => text.Length > 0)
            .Where(text => !Boilerplate.Any(phrase => text.Contains(phrase)))
            .Select(FixDirection)
            .ToList();

        return new EgyptianIdOcrResult { IdInformations = lines };
    }

    private static string FixDirection(string line) =>
        ArabicTextHelper.ContainsArabic(line) ? ArabicTextHelper.FixReadingOrder(line) : line;

    private static async Task<byte[]> ReadAllBytesAsync(Stream input, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
