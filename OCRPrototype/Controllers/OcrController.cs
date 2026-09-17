using Microsoft.AspNetCore.Mvc;
using OCRPrototype.Models;
using OCRPrototype.Services;
using System.Diagnostics;

namespace OCRPrototype.Controllers;

public sealed class OcrController : Controller
{
    private readonly IEgyptianIdOcrService _ocrService;
    private readonly ILogger<OcrController> _logger;

    public OcrController(
        IEgyptianIdOcrService ocrService,
        ILogger<OcrController> logger)
    {
        _ocrService = ocrService;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        return View(
            new OcrUploadViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> Index(
        OcrUploadViewModel model,
        CancellationToken ct)
    {
        if (model.IdImage is null ||
            model.IdImage.Length == 0)
        {
            ModelState.AddModelError(
                nameof(model.IdImage),
                "Choose an image of the ID card first.");

            return View(model);
        }

        try
        {
            await using Stream stream =
                model.IdImage.OpenReadStream();

            model.Result =
                await _ocrService.ExtractAsync(
                    stream,
                    ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "OCR extraction failed for {FileName}",
                model.IdImage.FileName);

            ModelState.AddModelError(
                string.Empty,
                "The ID card could not be read. Try a clearer, upright photo with the full card visible.");
        }

        return View(model);
    }

    [HttpPost("api/ocr/id-card")]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> ExtractApi(
        IFormFile file,
        CancellationToken ct)
    {
        if (file is null ||
            file.Length == 0)
        {
            return BadRequest(
                new
                {
                    error = "No image was uploaded."
                });
        }

        try
        {
            await using Stream stream =
                file.OpenReadStream();

            EgyptianIdOcrResult result =
                await _ocrService.ExtractAsync(
                    stream,
                    ct);

            return Json(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "API OCR extraction failed for {FileName}",
                file.FileName);

            return BadRequest(
                new
                {
                    error =
                        "The uploaded image could not be processed as an Egyptian ID card."
                });
        }
    }

    [ResponseCache(
        Duration = 0,
        Location = ResponseCacheLocation.None,
        NoStore = true)]
    public IActionResult Error()
    {
        return View(
            new ErrorViewModel
            {
                RequestId =
                    Activity.Current?.Id ??
                    HttpContext.TraceIdentifier
            });
    }
}