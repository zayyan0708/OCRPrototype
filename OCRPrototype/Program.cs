using OCRPrototype.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton(sp => PaddleOcrEngine.CreateAsync().GetAwaiter().GetResult());                                 // ADD — was missing entirely
builder.Services.AddScoped<IEgyptianIdOcrService, EgyptianIdOcrService>();         // ADD — was missing entirely

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Ocr/Error");   // CHANGED from "/Home/Error"
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Ocr}/{action=Index}/{id?}")   // CHANGED default controller from Home to Ocr
    .WithStaticAssets();

app.Run();