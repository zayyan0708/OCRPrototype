using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OCRPrototype.Models;
using OCRPrototype.Services;

namespace OCRPrototype.Controllers;

public sealed class OcrController(IEgyptianIdOcrService service) : Controller
{
    [HttpGet]
    public IActionResult Index() => View(new OcrUploadViewModel());

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> Index(OcrUploadViewModel model, CancellationToken ct)
    {
        if (model.IdImage is null || model.IdImage.Length == 0)
        {
            ModelState.AddModelError(nameof(model.IdImage), "Choose a JPEG or PNG of the card front.");
            return View(model);
        }
        await using var stream = model.IdImage.OpenReadStream();
        model.Result = (await service.ExtractAsync(stream, ct)).Result;
        return View(model);
    }

    [HttpPost("api/ocr/id-card"), RequestSizeLimit(10_000_000)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task ExtractApi(IFormFile? file, CancellationToken ct)
    {
        PipelineResult outcome;
        if (file is null || file.Length == 0)
            outcome = EgyptianIdOcrService.Fail(OcrFailure.InvalidImage, "No file was uploaded.");
        else
        {
            await using var stream = file.OpenReadStream();
            outcome = await service.ExtractAsync(stream, ct);
        }
        Response.StatusCode = outcome.Failure switch
        {
            OcrFailure.None => 200,
            OcrFailure.InvalidImage => 400,
            OcrFailure.Busy => 429,
            OcrFailure.Unavailable => 503,
            _ => 422
        };
        if (outcome.Failure == OcrFailure.Busy) Response.Headers.RetryAfter = "2";
        Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(Response.Body, outcome.Result,
            OcrJsonContext.Default.OcrExtractionResult, ct);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel
    { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
