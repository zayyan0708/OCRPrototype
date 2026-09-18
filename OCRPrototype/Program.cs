using OCRPrototype.Models;
using OCRPrototype.Services;

// Set before loading native engines; avoid each request spawning another OpenMP team.
Environment.SetEnvironmentVariable("OMP_THREAD_LIMIT", "1");
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews().AddJsonOptions(o =>
    o.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, OcrJsonContext.Default));
builder.Services.AddOptions<OcrOptions>().BindConfiguration("Ocr").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TesseractPool>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TesseractPool>());
builder.Services.AddSingleton<PortraitStore>();
builder.Services.AddHostedService<PortraitCleanup>();
builder.Services.AddSingleton<IEgyptianIdOcrService, EgyptianIdOcrService>();
var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Ocr/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();
app.MapStaticAssets();
app.MapControllerRoute("default", "{controller=Ocr}/{action=Index}/{id?}").WithStaticAssets();
app.Run();
