using System.ComponentModel.DataAnnotations;

namespace OCRPrototype.Services;

public sealed class OcrOptions
{
    [Range(1, 8)] public int Workers { get; set; } = 2;
    [Range(0, 32)] public int QueueCapacity { get; set; } = 4;
    [Range(1024, 20_000_000)] public int MaxUploadBytes { get; set; } = 8_000_000;
    [Range(100_000, 30_000_000)] public int MaxPixels { get; set; } = 16_000_000;
    [Range(800, 2000)] public int NormalizedWidth { get; set; } = 1280;
    [Range(0, 1000)] public double MinBlurVariance { get; set; } = 12;
    [Range(0, 100)] public double MinContrast { get; set; } = 12;
    [Range(0, 100)] public double MinTextConfidence { get; set; } = 45;
    [Range(0, 100)] public double MinIdConfidence { get; set; } = 55;
    [Range(4, 60)] public int MaxTextRegions { get; set; } = 24;
    public string TessdataPath { get; set; } = "tessdata";
    public string FaceModelPath { get; set; } = "models/haarcascade_frontalface_default.xml";
    public string ArtifactPath { get; set; } = "App_Data/portraits";
    [Range(1, 168)] public int ArtifactRetentionHours { get; set; } = 24;
}
