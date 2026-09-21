using Application.Interfaces;
using Application.Services;
using CloudinaryDotNet;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Cloudinary
builder.Services.AddSingleton(
    new Cloudinary(
        builder.Configuration.GetValue<string>("Cloudinary:CloudinaryUrl")
    )
);

// Services
builder.Services.AddSingleton<IEssaysService, EssaysService>();

var app = builder.Build();

if (!app.Environment.IsProduction())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ext-redigiai-api"));
}

app.UseRouting();
app.MapControllers();
app.Run();