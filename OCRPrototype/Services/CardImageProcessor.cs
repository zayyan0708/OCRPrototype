using OCRPrototype.Models;
using OpenCvSharp;

namespace OCRPrototype.Services;

public sealed class PreparedCard(Mat color, Mat gray, Mat binary) : IDisposable
{
    public Mat Color { get; } = color;
    public Mat Gray { get; } = gray;
    public Mat Binary { get; } = binary;
    public void Dispose() { Binary.Dispose(); Gray.Dispose(); Color.Dispose(); }
}

public sealed class CardImageProcessor(OcrOptions options)
{
    public ImageQuality Measure(Mat color, ImageHeader header)
    {
        using var gray = new Mat();
        Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
        using var probe = new Mat();
        double scale = Math.Min(1, 800d / gray.Width);
        Cv2.Resize(gray, probe, new Size(), scale, scale, InterpolationFlags.Area);
        Cv2.MeanStdDev(probe, out Scalar mean, out Scalar std);
        using var lap = new Mat();
        Cv2.Laplacian(probe, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out Scalar lapStd);
        double blur = lapStd.Val0 * lapStd.Val0;
        // Estimated sampling density for a tightly cropped ID-1 card; metadata is informational.
        double dpi = Math.Min(color.Width / (85.60 / 25.4), color.Height / (53.98 / 25.4));
        string? rejection = color.Width < 400 || color.Height < 250 ? "Card resolution is too low (minimum 400 by 250 pixels)."
            : mean.Val0 < 25 || mean.Val0 > 240 ? "Image is too dark or overexposed."
            : std.Val0 < options.MinContrast ? "Image contrast is too low."
            : blur < options.MinBlurVariance ? "Image is too blurred. Retake it with the card in focus." : null;
        double score = 100 * (.45 * Math.Clamp(blur / 150, 0, 1)
            + .30 * Math.Clamp(std.Val0 / 55, 0, 1)
            + .25 * Math.Clamp(dpi / 300, 0, 1));
        return new(blur, mean.Val0, std.Val0, dpi, header.Dpi, Math.Round(score, 1), rejection);
    }

    public PreparedCard Prepare(Mat source)
    {
        var color = new Mat(); var gray = new Mat(); var binary = new Mat();
        try
        {
            // Contract: upright front, card only. Canonical physical card ratio corrects stretched scans.
            Cv2.Resize(source, color, new Size(options.NormalizedWidth,
                (int)Math.Round(options.NormalizedWidth * 53.98 / 85.60)), 0, 0, InterpolationFlags.Cubic);
            Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
            using var edges = new Mat();
            Cv2.Canny(gray, edges, 60, 160);
            var angles = Cv2.HoughLinesP(edges, 1, Math.PI / 180, 60,
                    options.NormalizedWidth * .10, options.NormalizedWidth * .02)
                .Select(l => Math.Atan2(l.P2.Y - l.P1.Y, l.P2.X - l.P1.X) * 180 / Math.PI)
                .Where(a => Math.Abs(a) <= 12).Order().ToArray();
            if (angles.Length >= 4)
            {
                double angle = angles[angles.Length / 2];
                using var transform = Cv2.GetRotationMatrix2D(new Point2f(color.Width / 2f, color.Height / 2f), angle, 1);
                using var rotated = new Mat();
                Cv2.WarpAffine(color, rotated, transform, color.Size(), InterpolationFlags.Cubic,
                    BorderTypes.Constant, Scalar.White);
                rotated.CopyTo(color);
                Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
            }
            // Mild bilateral smoothing retains Arabic dots and marks; no erosion of the OCR image.
            using var denoised = new Mat();
            Cv2.BilateralFilter(gray, denoised, 5, 25, 25);
            using var clahe = Cv2.CreateCLAHE(2, new Size(8, 8));
            clahe.Apply(denoised, gray);
            Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC,
                ThresholdTypes.Binary, 41, 13);
            return new(color, gray, binary);
        }
        catch { color.Dispose(); gray.Dispose(); binary.Dispose(); throw; }
    }
}
