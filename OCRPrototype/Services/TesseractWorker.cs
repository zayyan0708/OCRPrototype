using OpenCvSharp;
using Tesseract;
using CvRect = OpenCvSharp.Rect;

namespace OCRPrototype.Services;

public sealed record RecognizedLine(CvRect Bounds, string Text, double Confidence);

// One instance is exclusively leased for an entire card, never shared across native calls.
public sealed class TesseractWorker : IDisposable
{
    private readonly TesseractEngine _arabic;
    private readonly TesseractEngine _latin;
    public CascadeClassifier Faces { get; }

    public TesseractWorker(string dataPath, string facePath)
    {
        _arabic = new TesseractEngine(dataPath, "ara", EngineMode.LstmOnly);
        try
        {
            _latin = new TesseractEngine(dataPath, "eng", EngineMode.LstmOnly);
            try
            {
                Faces = new CascadeClassifier(facePath);
                if (Faces.Empty()) throw new InvalidOperationException("The face detector model could not be loaded.");
                _arabic.SetVariable("user_defined_dpi", 300);
                _arabic.SetVariable("preserve_interword_spaces", 1);
                _latin.SetVariable("user_defined_dpi", 300);
                _latin.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
            }
            catch { _latin.Dispose(); throw; }
        }
        catch { _arabic.Dispose(); throw; }
    }

    public RecognizedLine Read(Mat image, CvRect roi, bool latin = false)
    {
        using var crop = new Mat(image, roi);
        using var bordered = new Mat();
        Cv2.CopyMakeBorder(crop, bordered, 10, 10, 10, 10, BorderTypes.Constant, Scalar.White);
        // Native Tesseract/Leptonica requires an owned Pix. This is a deliberate bounded copy.
        Cv2.ImEncode(".png", bordered, out byte[] bytes);
        using var pix = Pix.LoadFromMemory(bytes);
        using var page = (latin ? _latin : _arabic).Process(pix, PageSegMode.SingleLine);
        return new(roi, ArabicTextHelper.Normalize(page.GetText()), page.GetMeanConfidence() * 100);
    }

    public void Dispose() { Faces.Dispose(); _latin.Dispose(); _arabic.Dispose(); }
}
