using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;

namespace OCRPrototype.Services;

public sealed class PaddleOcrEngine : IDisposable
{
    private readonly PaddleOcrAll _ocr;

    private readonly SemaphoreSlim _lock = new(1, 1);

    private PaddleOcrEngine(PaddleOcrAll ocr)
    {
        _ocr = ocr;
    }

    public static async Task<PaddleOcrEngine> CreateAsync()
    {
        FullOcrModel model = await OnlineFullModels.ArabicV5.DownloadAsync();

        var ocr = new PaddleOcrAll(
            model,
            PaddleDevice.Onnx())
        {
            AllowRotateDetection = false,
            Enable180Classification = false
        };

        return new PaddleOcrEngine(ocr);
    }

    public async Task<PaddleOcrResult> RunAsync(
        Mat image,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);

        try
        {
            return _ocr.Run(image);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _ocr.Dispose();
        _lock.Dispose();
    }
}