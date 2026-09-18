using Microsoft.AspNetCore.Http;

namespace OCRPrototype.Models;

public class OcrUploadViewModel
{
    public IFormFile? IdImage { get; set; }

    public EgyptianIdOcrResult? Result { get; set; }
}