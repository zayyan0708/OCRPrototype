using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;

namespace OCRPrototype.Services;

public sealed class PaddleOcrEngine : IDisposable
{
    private readonly PaddleOcrAll _ocr;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PaddleOcrEngine(PaddleOcrAll ocr)
    {
        _ocr = ocr;
    }

    public static async Task<PaddleOcrEngine> CreateAsync()
    {
        FullOcrModel model =
            await OnlineFullModels.ArabicV5.DownloadAsync();

        var ocr = new PaddleOcrAll(
            model,
            PaddleDevice.Onnx())
        {
            // This was working better with your Egyptian ID samples.
            AllowRotateDetection = false,
            Enable180Classification = false
        };

        return new PaddleOcrEngine(ocr);
    }

    public async Task<PaddleOcrResult> RunAsync(
        Mat image,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);

        try
        {
            return _ocr.Run(image);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _ocr.Dispose();
        _gate.Dispose();
    }
}