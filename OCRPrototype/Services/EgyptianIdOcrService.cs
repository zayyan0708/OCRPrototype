using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using OCRPrototype.Models;
using OpenCvSharp;

namespace OCRPrototype.Services;

public sealed class EgyptianIdOcrService(
    TesseractPool pool, IOptions<OcrOptions> configured, PortraitStore portraits,
    TimeProvider clock, ILogger<EgyptianIdOcrService> logger) : IEgyptianIdOcrService
{
    private readonly OcrOptions _options = configured.Value;

    public async Task<PipelineResult> ExtractAsync(Stream imageStream, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!pool.TryAdmit()) return Fail(OcrFailure.Busy, "OCR is busy. Retry shortly.");
        byte[]? buffer = null;
        TesseractWorker? worker = null;
        long start = Stopwatch.GetTimestamp();
        try
        {
            // Admission occurs before allocation; memory is bounded even during request bursts.
            buffer = ArrayPool<byte>.Shared.Rent(_options.MaxUploadBytes + 1);
            int count = 0;
            while (count <= _options.MaxUploadBytes)
            {
                int read = await imageStream.ReadAsync(buffer.AsMemory(count, _options.MaxUploadBytes + 1 - count), ct);
                if (read == 0) break;
                count += read;
            }
            if (count == 0 || count > _options.MaxUploadBytes)
                return Fail(OcrFailure.InvalidImage, "The image is empty or exceeds the upload limit.");
            if (!ImageHeader.TryRead(buffer.AsSpan(0, count), out var header)
                || (long)header.Width * header.Height > _options.MaxPixels)
                return Fail(OcrFailure.InvalidImage, "Use a valid JPEG or PNG within the pixel limit.");
            double ratio = header.Width / (double)header.Height;
            if (ratio is < .9 or > 2.0)
                return Fail(OcrFailure.InvalidImage, "Upload the upright front of the card, tightly cropped to its edges.");

            worker = await pool.RentAsync(ct);
            var leased = worker;
            var input = buffer.AsMemory(0, count);
            // CPU-bound native work is synchronous. Task.Run is bounded by exclusive engine leases.
            // Never abandon this task on cancellation: the engine and input must remain owned until it returns.
            var processed = await Task.Run(() => Process(input, header, leased, ct), ct);
            ct.ThrowIfCancellationRequested();
            var result = processed.Result;
            if (processed.Portrait is { Length: > 0 } photo)
            {
                string path = await portraits.SaveAsync(photo, ct);
                result = result with { Result = result.Result with { Artifacts = new(path) } };
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OpenCVException or OpenCvSharpException)
        {
            return Fail(OcrFailure.InvalidImage, "The image could not be decoded or processed.");
        }
        catch (Exception ex) when (ex is Tesseract.TesseractException or IOException or UnauthorizedAccessException)
        {
            logger.LogError("OCR processing failed ({ErrorType}).", ex.GetType().Name);
            return Fail(OcrFailure.Unavailable, "OCR processing is temporarily unavailable.");
        }
        finally
        {
            if (worker is not null) pool.Return(worker);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            pool.ReleaseAdmission();
            logger.LogInformation("Front OCR completed in {ElapsedMs:F0} ms.", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    private (PipelineResult Result, byte[]? Portrait) Process(ReadOnlyMemory<byte> input,
        ImageHeader header, TesseractWorker worker, CancellationToken ct)
    {
        using var source = Cv2.ImDecode(input.Span, ImreadModes.Color);
        if (source.Empty()) return (Fail(OcrFailure.InvalidImage, "Image decoding failed."), null);
        if ((long)source.Width * source.Height > _options.MaxPixels)
            return (Fail(OcrFailure.InvalidImage, "Decoded image exceeds the pixel limit."), null);
        var processor = new CardImageProcessor(_options);
        var quality = processor.Measure(source, header);
        if (quality.Rejection is not null) return (Fail(OcrFailure.Unreadable, quality.Rejection, quality.Score), null);
        ct.ThrowIfCancellationRequested();
        using var card = processor.Prepare(source);
        var regions = new DynamicRegionLocator().Locate(card, worker.Faces, _options.MaxTextRegions);
        var recognized = new List<RecognizedLine>();
        foreach (var region in regions.TextLines)
        {
            ct.ThrowIfCancellationRequested();
            var line = worker.Read(card.Gray, region);
            if (line.Confidence < _options.MinTextConfidence)
            {
                ct.ThrowIfCancellationRequested();
                var alternate = worker.Read(card.Binary, region);
                if (alternate.Confidence > line.Confidence) line = alternate;
            }
            if (!string.IsNullOrWhiteSpace(line.Text)) recognized.Add(line);
            // The lower field group holds Latin serials and numerals. Bounds still come from contours.
            if (region.Y > (regions.Photo?.Bottom ?? card.Gray.Height / 2)
                || line.Text.Any(char.IsDigit))
            {
                ct.ThrowIfCancellationRequested();
                var code = worker.Read(card.Gray, region, latin: true);
                if (!string.IsNullOrWhiteSpace(code.Text)) recognized.Add(code);
            }
        }
        var parsed = new FrontCardParser(_options).Parse(recognized, regions.Photo, clock.GetUtcNow().UtcDateTime);
        byte[]? portrait = null;
        if (regions.Photo is { } box)
        {
            using var crop = new Mat(card.Color, box);
            Cv2.ImEncode(".jpg", crop, out portrait);
        }
        var extraction = new OcrExtractionResult(parsed.Complete, quality.Score, parsed.Data,
            new ExtractedArtifacts(null), parsed.Error);
        return (new(extraction, parsed.Complete ? OcrFailure.None : OcrFailure.Incomplete), portrait);
    }

    public static PipelineResult Fail(OcrFailure failure, string message, double score = 0) =>
        new(new(false, score, null, null, message), failure);
}
