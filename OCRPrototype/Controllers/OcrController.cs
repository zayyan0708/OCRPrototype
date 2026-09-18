using Microsoft.AspNetCore.Mvc;
using OCRPrototype.Models;
using OCRPrototype.Services;
using System.Diagnostics;

namespace OCRPrototype.Controllers;

public class OcrController : Controller
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
        return View(new OcrUploadViewModel());
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
                "Choose an ID card image.");

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
                "OCR failed for {FileName}",
                model.IdImage.FileName);

            ModelState.AddModelError(
                string.Empty,
                "The image could not be processed.");
        }

        return View(model);
    }

    [HttpPost("api/ocr/id-card")]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> ExtractApi(
        IFormFile file,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest("No image was uploaded.");

        await using Stream stream =
            file.OpenReadStream();

        EgyptianIdOcrResult result =
            await _ocrService.ExtractAsync(
                stream,
                ct);

        if (!result.IsSuccess)
            return UnprocessableEntity(result);

        return Ok(result);
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