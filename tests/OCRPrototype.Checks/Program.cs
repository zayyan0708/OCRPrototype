using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCRPrototype.Models;
using OCRPrototype.Services;
using OpenCvSharp;

int count = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    count++;
}
var today = new DateTime(2026, 9, 18);
Check(ArabicTextHelper.Normalize("أَحمد ١٢٣ ۴۵۶") == "أَحمد 123 456", "Logical RTL, marks and both digit sets");
Check(ArabicTextHelper.Normalize("12\u202e34") == "1234", "Remove bidi formatting controls");
Check(FrontCardParser.TryBirthDate("30002290112345", today, out var leap) && leap == new DateTime(2000,2,29), "Leap date");
Check(!FrontCardParser.TryBirthDate("29902290112345", today, out _), "Reject impossible leap day");
Check(!FrontCardParser.TryBirthDate("32701010112345", today, out _), "Reject future date");
Check(!FrontCardParser.TryBirthDate("30002299912345", today, out _), "Reject unknown governorate");
Check(!FrontCardParser.TryBirthDate("00002290112345", today, out _), "Reject unknown century");
Check(FrontCardParser.CompactNumber("٢ ٨٢ ٠٢٢٤ ١٥٠١٥١٢") == "28202241501512", "Preserve digit order");
Check(FrontCardParser.CompactNumber("ABC28202241501512") == "", "Do not harvest digits out of names");
Check(!ImageHeader.TryRead([1,2,3,4], out _), "Reject corrupt header");
byte[] png = new byte[33];
new byte[]{137,80,78,71,13,10,26,10}.CopyTo(png,0);
BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8),13);
"IHDR"u8.CopyTo(png.AsSpan(12));
BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 640);
BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 400);
Check(ImageHeader.TryRead(png,out var h) && h.Width == 640 && h.Height == 400 && h.Dpi is null, "PNG dimensions without DPI");
Check(!ImageHeader.TryRead(png.AsSpan(0,20),out _), "Truncated PNG");
var options = new OcrOptions {Workers=1, QueueCapacity=0};
var parser = new FrontCardParser(options);
RecognizedLine Line(int y, string t) => new(new Rect(350,y,450,30), t, 90);
List<RecognizedLine> lines = [Line(40,"بطاقة تحقيق الشخصية"),Line(110,"أَحمد"),Line(170,"محمد علي"),
    Line(240,"شارع النيل"),Line(300,"القاهرة"),Line(410,"30002290112345"),Line(470,"AB1234567")];
var parsed = parser.Parse(lines, new Rect(10,80,200,270),today);
Check(parsed.Complete && parsed.Data.FullName == "أَحمد محمد علي" && parsed.Data.DateOfBirth == leap, "Front field mapping");
Check(FrontCardParser.TryPrintedDate("٢٩/٢/٢٠٠٠", today, out var printed) && printed == leap, "Printed Arabic date");
lines.Add(Line(460,"1/1/2001"));
Check(!parser.Parse(lines,null,today).Complete && parser.Parse(lines,null,today).Data.DateOfBirth is null, "Conflicting DOB needs review");
lines.RemoveAt(lines.Count - 1);
lines.Add(Line(410,"30101010112345"));
Check(parser.Parse(lines,null,today).Data.NationalId is null, "Conflicting plausible IDs stay null");
var result = new OcrExtractionResult(false,0,null,null,"Unreadable");
string json = JsonSerializer.Serialize(result,OcrJsonContext.Default.OcrExtractionResult);
Check(JsonSerializer.Deserialize(json,OcrJsonContext.Default.OcrExtractionResult) == result, "Generated JSON round trip");
Check(json.Contains("\"isSuccess\""), "Camel case API contract");

if (args.Contains("--native"))
{
    string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../OCRPrototype"));
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions {ContentRootPath=project});
    using var pool = new TesseractPool(Options.Create(options),builder.Environment);
    await pool.StartAsync(default);
    Check(pool.TryAdmit(), "First admission");
    Check(!pool.TryAdmit(), "Bounded queue rejects excess admission");
    pool.ReleaseAdmission();
    var service = new EgyptianIdOcrService(pool,Options.Create(options),
        new PortraitStore(Options.Create(options),builder.Environment),TimeProvider.System,
        NullLogger<EgyptianIdOcrService>.Instance);
    using var blank = new Mat(500,800,MatType.CV_8UC3,Scalar.White);
    Cv2.ImEncode(".png",blank,out byte[] blankBytes);
    using var stream = new MemoryStream(blankBytes);
    var rejected = await service.ExtractAsync(stream);
    Check(rejected.Failure == OcrFailure.Unreadable && !rejected.Result.IsSuccess, "Native blank image rejection");
    var worker = await pool.RentAsync(default);
    try
    {
        using var textImage = new Mat(160,900,MatType.CV_8UC1,Scalar.White);
        Cv2.PutText(textImage,"AB1234567",new Point(30,100),HersheyFonts.HersheySimplex,2,Scalar.Black,3);
        var read = worker.Read(textImage,new Rect(0,0,900,160),true);
        Check(read.Text == "AB1234567", "Native Tesseract and OpenCV integration");
    }
    finally { pool.Return(worker); }
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await service.ExtractAsync(Stream.Null,canceled.Token); Check(false,"Cancellation propagated"); }
    catch (OperationCanceledException) { count++; }
    Check(pool.TryAdmit(),"Admission recovered after failure and cancellation"); pool.ReleaseAdmission();
}
Console.WriteLine($"Passed {count} checks.");
