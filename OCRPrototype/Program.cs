using OCRPrototype.Services;

var builder =
    WebApplication.CreateBuilder(args);

builder.Services
    .AddControllersWithViews();

builder.Services.AddSingleton(
    sp =>
        PaddleOcrEngine
            .CreateAsync()
            .GetAwaiter()
            .GetResult());

builder.Services.AddScoped<
    IEgyptianIdOcrService,
    EgyptianIdOcrService>();

var app =
    builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(
        "/Ocr/Error");

    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
        name: "default",
        pattern:
            "{controller=Ocr}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();