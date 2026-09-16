using Microsoft.AspNetCore.Http;

namespace OCRPrototype.Models;

public class OcrUploadViewModel
{
    public IFormFile? IdImage { get; set; }

    // Only populated after a successful POST, so the view can render it.
    public EgyptianIdOcrResult? Result { get; set; }
}
