using Microsoft.AspNetCore.Mvc;
using OCRPrototype.Models;
using OCRPrototype.Services;
using System.Diagnostics;
using System.Diagnostics;   // ADD to the top using block
namespace OCRPrototype.Controllers;

public class OcrController : Controller
{
    private readonly IEgyptianIdOcrService _ocrService;
    private readonly ILogger<OcrController> _logger;

    public OcrController(IEgyptianIdOcrService ocrService, ILogger<OcrController> logger)
    {
        _ocrService = ocrService;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index() => View(new OcrUploadViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(10_000_000)] // 10 MB, plenty for a phone photo of a card
    public async Task<IActionResult> Index(OcrUploadViewModel model, CancellationToken ct)
    {
        if (model.IdImage is null || model.IdImage.Length == 0)
        {
            ModelState.AddModelError(nameof(model.IdImage), "Choose an image of the ID card first.");
            return View(model);
        }

        try
        {
            await using Stream stream = model.IdImage.OpenReadStream();
            model.Result = await _ocrService.ExtractAsync(stream, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR extraction failed for {FileName}", model.IdImage.FileName);
            ModelState.AddModelError(string.Empty, "Couldn't read that image - try a clearer, well-lit photo of the full card.");
        }

        return View(model);
    }

    /// <summary>
    /// Plain JSON endpoint, for calling from JS (see the upload page) or
    /// from another service instead of going through the HTML form.
    /// </summary>
    [HttpPost("api/ocr/id-card")]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> ExtractApi(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest("No file was uploaded.");

        await using Stream stream = file.OpenReadStream();
        EgyptianIdOcrResult result = await _ocrService.ExtractAsync(stream, ct);
        return Json(result);
    }


[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public IActionResult Error()
{
    return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
}
