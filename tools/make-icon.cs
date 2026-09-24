#:package SkiaSharp@2.88.9
using SkiaSharp;
// Redraw of the provided glyph on a 200-unit grid: rounded frame + two rows of "subtitle" dashes.
// dotnet run tools/make-icon.cs src/SubMatcher.Gui/Assets
var outDir = args[0];
int[] sizes = [16, 24, 32, 48, 64, 128, 256];
var pngs = new List<byte[]>();
foreach (var s in sizes.Append(512))
{
    using var bmp = new SKBitmap(s, s, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var c = new SKCanvas(bmp);
    c.Clear(SKColors.Transparent);
    c.Scale(s / 200f);
    using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
    using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 14, StrokeCap = SKStrokeCap.Round };
    var frame = new SKRoundRect(new SKRect(24, 32, 176, 168), 42, 42);
    c.DrawRoundRect(frame, fill);
    c.DrawRoundRect(frame, ink);
    ink.StrokeWidth = 10;
    foreach (var (y, x0, x1) in new[] { (106, 50, 60), (106, 74, 90), (106, 112, 150), (131, 50, 90), (131, 106, 118), (131, 132, 150) })
        c.DrawLine(x0, y, x1, y, ink);
    using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
    var bytes = data.ToArray();
    if (s == 512) File.WriteAllBytes(Path.Combine(outDir, "icon.png"), bytes); else pngs.Add(bytes);
}
// ICO = header + directory + PNG payloads (Vista+ reads PNG entries at every size).
using var ms = new MemoryStream();
using var w = new BinaryWriter(ms);
w.Write((short)0); w.Write((short)1); w.Write((short)pngs.Count);
int offset = 6 + 16 * pngs.Count;
for (int i = 0; i < pngs.Count; i++)
{
    w.Write((byte)(sizes[i] % 256)); w.Write((byte)(sizes[i] % 256)); w.Write((byte)0); w.Write((byte)0);
    w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(offset);
    offset += pngs[i].Length;
}
foreach (var p in pngs) w.Write(p);
File.WriteAllBytes(Path.Combine(outDir, "icon.ico"), ms.ToArray());
Console.WriteLine("ok");
