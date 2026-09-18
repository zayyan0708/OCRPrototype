# Egyptian ID front OCR (.NET 9 / C# 13)

Refactors the staging implementation at `32f9e8623b05d8a547991d5b406ea69bf5168138` from whole-image PaddleOCR text to a typed front-side pipeline. Windows x64 is the supported runtime for this package set. No System.Drawing, IronOCR license, runtime model download, or manually reversed Arabic text is used.

## Run

Install the .NET 9 SDK and Microsoft Visual C++ 2015–2022 x64 redistributable. From the repository root:

```powershell
./scripts/setup-models.ps1
dotnet build OCRPrototype.sln -c Release
dotnet run --project OCRPrototype
```

The setup script downloads `ara` and `eng` from the pinned tessdata_fast 4.1.0 tag and OpenCV's face cascade from 4.11.0. It needs internet access once. Model initialization fails at startup with an actionable error if a required file is missing. For deployment, publish after installing the models; the project copies them into the output. The application uses the content root to resolve paths. Configure absolute paths when hosting with a different content root.

Open the displayed local URL, or POST multipart field `file` to `/api/ocr/id-card`:

```powershell
curl.exe -F "file=@C:\images\card-front.jpg" https://localhost:PORT/api/ocr/id-card
```

The existing MVC upload page and API route remain available. Response JSON changes intentionally from an untyped line array to the requested records with camelCase property names:

```json
{
  "isSuccess": false,
  "qualityScore": 73.5,
  "data": {
    "fullName": null,
    "address": null,
    "nationalId": null,
    "cardId": null,
    "dateOfBirth": null
  },
  "artifacts": { "photoPath": null },
  "errorMessage": "Some front-side fields are missing, low-confidence, or ambiguous. Review the partial result or retake the card."
}
```

This is a schema example, not a sample-image extraction. HTTP statuses: 200 complete text fields, 400 invalid upload, 422 unreadable or partial extraction, 429 bounded capacity reached (`Retry-After: 2`), 503 processing/storage failure. ASP.NET request-size/multipart errors may be rejected by the server before the controller and use its response format.

## Processing and ownership

1. Admit at most `Workers + QueueCapacity` requests. Rent one upload buffer, enforce byte limits while reading asynchronously, and inspect PNG/JPEG dimensions before native decoding. Pixel limits bound decompression memory. `ReadOnlySpan<byte>` slices avoid copying the uploaded buffer into another array.
2. Measure Laplacian variance at a capped probe scale, brightness and contrast before enhancement. `ImageQuality` also contains metadata DPI (PNG pHYs or JPEG JFIF, when present) and estimated DPI from the physical card size. DPI tags are not a rejection criterion: phone photos often have absent or meaningless metadata. Quality score is a heuristic from 0–100, **not OCR accuracy or identity confidence**.
3. Normalize a card-only scan to ID-1 aspect ratio and configurable width; deskew using consensus line angles within ±12 degrees, apply mild bilateral denoising and CLAHE, and retain both grayscale and adaptive threshold variants. Morphological closing is used only for localization, not to remove Arabic dots from the recognition image.
4. Detect a portrait using a face cascade. An expanded detected face supplies the portrait crop; it is not guaranteed to include the exact printed photo border. With zero/multiple faces, return a null photo rather than guessing. Detect text contours, merge aligned fragments, pad actual boxes, and cap the number of regions processed. No fixed pixel field crops are used.
5. Each exclusive worker holds independent Arabic and Latin LSTM engines. Arabic uses its trained RTL model with combining marks preserved. Normalize both Arabic-Indic and Persian digits without reversing numeric strings. Re-read low-confidence Arabic regions with the threshold variant. Latin recognition uses a serial-character whitelist on candidate code rows. OCR is CPU-bound and synchronous internally; only bounded work is dispatched via Task.Run. Async upload and artifact I/O do not block threads. Cancellation is checked between native calls; an in-progress native call cannot be interrupted safely and keeps its lease and buffers until completion.
6. Use header, portrait and number anchors to map the detected front rows. The current layout model supports the common Egyptian front with two name rows and one/two address rows. It does not infer names/addresses when row counts or anchors are ambiguous. Name/address extraction no longer requires a valid national ID. Sparse-text passes on plain and enhanced grayscale supplement contours; overlapping row alternatives are deduplicated and the personal text block is selected using shared right-edge alignment. This preserves short first names and avoids enhanced security artwork suppressing readable fields. IDs must have 14 digits, a recognized century/governorate, and a valid nonfuture birth date. Conflicting plausible values remain null. Structural checks are **not registry verification or a validated checksum**. No invented OCR character replacements or sample-specific corrections are used.
7. DOB is decoded from the validated national ID. A separately detected date row can also supply DOB (year-first or day-first, with slash/hyphen separators). Conflicting confident printed/encoded dates set DOB to null and prevent success. A missing/unreadable printed date leaves the validated ID date available. Card serials currently use the common two-letter/seven-digit pattern. Unknown serial formats remain null. Success requires all text fields; portrait availability is independent.
8. Save any portrait asynchronously outside wwwroot under a random filename. `PhotoPath` is a server-local file path, not a publicly accessible URL. Retention cleanup runs at startup and hourly (24 hours by default). No card text, upload filenames, or image data is logged. Native Mat, Pix, Page and engine objects have explicit owners. Upload buffers are cleared when returned to the pool.

## Input scope and limitations

Input must be an upright, tightly cropped front of the card. Small skew and stretched scans are handled; arbitrary perspective, upside-down/90-degree cards, frames, screenshots and large backgrounds are not rectified. Rejecting every unsupported background automatically is not guaranteed. Some supplied examples have backgrounds/red frames and need cropping before evaluation. The template with field labels is not an identity card for accuracy evaluation.

Contour and face detection are heuristic and may miss faint digits, merge rows, or include security artwork. Returning partial data reduces silent field shifts but cannot eliminate OCR errors. Arabic diacritic recognition depends on input resolution and model training; preserving Unicode marks is not a guarantee that every mark is recognized. Thresholds and confidence cutoffs need calibration on held-out labeled card scans. Do not lower them solely to make these samples pass.

The front parser is separate from decoding, preprocessing, native engines and storage. Add a separate back parser/layout handler and explicit side routing later; do not feed back-side text into this parser. No back-side fields or processing are claimed today.

The API is a prototype without authentication. Before exposing real ID data in a deployed application, connect the existing application's authentication/authorization and artifact access policy. Do not add a public static mapping to App_Data. Native models and packages have their own licenses; retain notices when distributing them.

## Verification

```powershell
dotnet run --project tests/OCRPrototype.Checks -c Release
dotnet run --project tests/OCRPrototype.Checks -c Release -- --native
```

Checks cover digit/RTL normalization, diacritics, invalid/future/leap dates, conflicting IDs, anchor-based field mapping, corrupt headers, generated JSON round-tripping, overload admission, cancellation, blank-image rejection and a native Latin OCR smoke test. The native flag requires the model setup script and Windows x64. GitHub Actions runs the native checks on Windows.

The authoring environment did not have the .NET SDK and could not reach SDK/NuGet download endpoints. Compilation and these .NET checks have therefore **not been run locally**. Sample images were inspected visually, but Arabic extraction accuracy and latency have **not been measured**. Windows CI subsequently built commit `dac750ae46d56a4c4cfc78ba62c370d6df2874b0` in Release with **0 warnings and 0 errors**, and **all 24 checks passed**, including native integration ([run](https://github.com/zayyan0708/OCRPrototype/actions/runs/35320684807)). An actual labeled Arabic regression corpus remains necessary before production use. No claim of perfect extraction or a specific latency is made.

## Primary references

- [Tesseract quality, segmentation and preprocessing](https://tesseract-ocr.github.io/tessdoc/ImproveQuality.html)
- [Native .NET Tesseract wrapper and runtime prerequisites](https://github.com/charlesw/tesseract)
- [Tesseract fast language data](https://github.com/tesseract-ocr/tessdata_fast)
- [OpenCvSharp](https://github.com/shimat/opencvsharp)

## Sample2 correction

A quality score of 79 means the image passed readability checks; it is not a field-accuracy score. The original contour-only implementation could miss the text/serial, and the parser then suppressed name/address when no national ID passed validation. The correction uses Tesseract sparse line boxes from plain/enhanced grayscale, retains independent readable fields, and names missing fields in the error message. Invalid national-number structures remain null and keep `isSuccess` false.

Sample2 was tested locally using Tesseract CLI with the same `tessdata_fast` Arabic model and grayscale variants. Those probes recognized the name/address and Latin serial; this is not an end-to-end Windows wrapper benchmark. The printed national number appears to start with 7, so accepting it as a validated Egyptian national ID would be incorrect. No sample-specific text or numeric correction is coded.
