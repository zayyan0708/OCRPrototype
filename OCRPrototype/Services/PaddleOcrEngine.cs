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

    private PaddleOcrEngine(PaddleOcrAll ocr) => _ocr = ocr;

    // Downloads the model files on first run and caches them locally
    // afterwards - needs network access the first time the app starts.
    // NOTE: same caveat as before - check "OnlineFullModels." in the IDE
    // for the exact Arabic member name if this doesn't compile as-is.
    public static async Task<PaddleOcrEngine> CreateAsync()
    {
        FullOcrModel model = await OnlineFullModels.ArabicV4.DownloadAsync();

        var ocr = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
        {
            AllowRotateDetection = true,
            Enable180Classification = true,
        };
        return new PaddleOcrEngine(ocr);
    }

    public async Task<PaddleOcrResult> RunAsync(Mat image, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return _ocr.Run(image); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _ocr.Dispose();
        _gate.Dispose();
    }
}