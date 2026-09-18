namespace OCRPrototype.Models;

public sealed class OcrUploadViewModel
{
    public IFormFile? IdImage { get; set; }
    public OcrExtractionResult? Result { get; set; }
}
