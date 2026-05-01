using SkiaSharp;

var outputPath = args.Length > 0 ? args[0] : "app-icon.png";

int size = 256;
using var surface = SKSurface.Create(new SKImageInfo(size, size));
var canvas = surface.Canvas;

canvas.Clear(SKColors.Transparent);

// Background - dark rounded square
using var bgPaint = new SKPaint
{
    IsAntialias = true,
    Shader = SKShader.CreateLinearGradient(
        new SKPoint(0, 0), new SKPoint(size, size),
        new[] { new SKColor(28, 18, 52), new SKColor(12, 8, 28) },
        SKShaderTileMode.Clamp)
};
var rrect = new SKRoundRect(new SKRect(0, 0, size, size), size * 0.2f);
canvas.DrawRoundRect(rrect, bgPaint);

// Purple glow circle behind
using var glowPaint = new SKPaint
{
    IsAntialias = true,
    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.12f),
    Color = new SKColor(124, 58, 237, 120)
};
canvas.DrawCircle(size * 0.5f, size * 0.5f, size * 0.28f, glowPaint);

// Inner gradient circle
float cx = size * 0.5f;
float cy = size * 0.48f;
float cr = size * 0.26f;
using var circlePaint = new SKPaint
{
    IsAntialias = true,
    Shader = SKShader.CreateLinearGradient(
        new SKPoint(cx - cr, cy - cr), new SKPoint(cx + cr, cy + cr),
        new[] { new SKColor(167, 139, 250), new SKColor(109, 40, 217) },
        SKShaderTileMode.Clamp)
};
canvas.DrawCircle(cx, cy, cr, circlePaint);

// "AI" text
using var textPaint = new SKPaint
{
    IsAntialias = true,
    Color = SKColors.White,
    TextSize = size * 0.28f,
    FakeBoldText = true,
    TextAlign = SKTextAlign.Center,
    Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                ?? SKTypeface.Default
};
var textBounds = new SKRect();
textPaint.MeasureText("AI", ref textBounds);
canvas.DrawText("AI", cx, cy - textBounds.MidY, textPaint);

// Small sparkle dots
using var dotPaint = new SKPaint { IsAntialias = true, Color = new SKColor(196, 181, 253, 200) };
float dotR = size * 0.025f;
canvas.DrawCircle(size * 0.72f, size * 0.28f, dotR, dotPaint);
canvas.DrawCircle(size * 0.78f, size * 0.38f, dotR * 0.6f, dotPaint);
canvas.DrawCircle(size * 0.26f, size * 0.72f, dotR * 0.7f, dotPaint);

// Save PNG
using var image = surface.Snapshot();
using var data = image.Encode(SKEncodedImageFormat.Png, 100);
File.WriteAllBytes(outputPath, data.ToArray());

Console.WriteLine($"Icon saved: {outputPath}");
