using SkiaSharp;
using System.Xml.Linq;

class GlyphInfo
{
    public int id, x, y, w, h, xoffset, yoffset, xadvance;
}

class Program
{
    const int ThaiBase = 0x0E01; // ก — base character สำหรับ combining marks

    static void Main()
    {
        string fontPath = "font.ttf";
        int fontSize = 512;
        int supersample = 4;
        int renderSize = fontSize * supersample;
        int atlasSize = 16384;
        int padding = 4;

        Console.WriteLine("Loading font...");
        using var typeface = SKTypeface.FromFile(fontPath)
            ?? throw new Exception("โหลดฟอนต์ไม่ได้");

        // ── SKFont hi-res (render) ────────────────────────────────────────────
        using var fontHi = new SKFont(typeface, renderSize)
        {
            Subpixel = true,
            LinearMetrics = true,
            Edging = SKFontEdging.SubpixelAntialias,
        };

        // ── SKFont final (วัด metrics / advance) ─────────────────────────────
        using var fontFinal = new SKFont(typeface, fontSize)
        {
            Subpixel = true,
            LinearMetrics = true,
            Edging = SKFontEdging.SubpixelAntialias,
        };

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
        };

        // ── Metrics จาก fontFinal ─────────────────────────────────────────────
        SKFontMetrics m = fontFinal.Metrics;
        int ascent = (int)Math.Ceiling(-m.Ascent);
        int descent = (int)Math.Ceiling(m.Descent);
        int vPad = (int)(fontSize * 0.20f);
        int hPad = (int)(fontSize * 0.08f);
        int cellH = ascent + descent + vPad * 2;

        // hi-res equivalents
        int vPadHi = vPad * supersample;
        int hPadHi = hPad * supersample;

        SKFontMetrics mHi = fontHi.Metrics;
        int ascentHi = (int)Math.Ceiling(-mHi.Ascent);

        List<int> charset = BuildCharset();
        Console.WriteLine($"Glyph count : {charset.Count}");
        Console.WriteLine($"Atlas size  : {atlasSize}x{atlasSize} px");
        Console.WriteLine($"Supersample : {supersample}x  (render {renderSize}px → {fontSize}px)");
        Console.WriteLine("Rendering glyphs...");

        using var atlasBmp = new SKBitmap(atlasSize, atlasSize,
                                             SKColorType.Rgba8888, SKAlphaType.Premul);
        using var atlasCanvas = new SKCanvas(atlasBmp);
        atlasCanvas.Clear(SKColors.Transparent);

        var glyphs = new List<GlyphInfo>();
        int cx = padding, cy = padding, rowH = 0;

        foreach (int code in charset)
        {
            string s = char.ConvertFromUtf32(code);
            bool isCombining = IsThaiCombining(code);
            string measureStr = isCombining
                ? char.ConvertFromUtf32(ThaiBase) + s
                : s;

            // ── วัด advance (final size) ──────────────────────────────────────
            float pairAdv = fontFinal.MeasureText(measureStr);
            float baseAdv = isCombining
                ? fontFinal.MeasureText(char.ConvertFromUtf32(ThaiBase))
                : 0f;

            int advance = isCombining
                ? Math.Max(0, (int)Math.Ceiling(pairAdv - baseAdv))
                : (int)Math.Ceiling(pairAdv);
            if (advance <= 0 && !isCombining) advance = 1;

            // ── วัด bounds (final size) ───────────────────────────────────────
            fontFinal.MeasureText(measureStr, out SKRect bounds);

            int bmpW = Math.Max(advance, (int)Math.Ceiling(bounds.Right) + 1) + hPad * 2;
            int bmpH = cellH;
            if (bmpW <= 0) bmpW = 1;

            // ── hi-res cell ───────────────────────────────────────────────────
            int bmpWHi = bmpW * supersample;
            int bmpHHi = bmpH * supersample;
            float drawYHi = vPadHi + ascentHi;

            // ── Render hi-res ─────────────────────────────────────────────────
            using var glyphHi = RenderGlyphHiRes(
                fontHi, paint, measureStr, s, isCombining,
                bmpWHi, bmpHHi, hPadHi, drawYHi);

            // ── Downsample → final size ───────────────────────────────────────
            var targetInfo = new SKImageInfo(bmpW, bmpH,
                                             SKColorType.Rgba8888, SKAlphaType.Premul);
            using var glyphFinal = glyphHi.Resize(targetInfo,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            if (glyphFinal is null)
            {
                Console.WriteLine($"⚠️  Resize ล้มเหลว U+{code:X4}");
                continue;
            }

            // ── Pack atlas ────────────────────────────────────────────────────
            if (cx + bmpW + padding >= atlasSize)
            {
                cx = padding; cy += rowH + padding; rowH = 0;
            }
            if (cy + bmpH + padding >= atlasSize)
            {
                Console.WriteLine($"⚠️  Atlas เต็ม หยุดที่ U+{code:X4}");
                break;
            }

            atlasCanvas.DrawBitmap(glyphFinal, cx, cy, paint);

            glyphs.Add(new GlyphInfo
            {
                id = code,
                x = cx,
                y = cy,
                w = bmpW,
                h = bmpH,
                xoffset = -hPad,
                yoffset = -(ascent + vPad),
                xadvance = advance,
            });

            rowH = Math.Max(rowH, bmpH);
            cx += bmpW + padding;
        }

        // ── บันทึกไฟล์ ────────────────────────────────────────────────────────
        Console.WriteLine("Saving font.png...");
        using var img = SKImage.FromBitmap(atlasBmp);
        using var imgData = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.OpenWrite("font.png");
        imgData.SaveTo(fs);

        SaveXml("font.xml", glyphs, atlasSize, atlasSize, fontSize);
        Console.WriteLine($"DONE ✔  ({glyphs.Count} glyphs)  atlas={atlasSize}x{atlasSize}");
    }

    // ── Render glyph hi-res (รองรับ combining) ────────────────────────────────
    static SKBitmap RenderGlyphHiRes(
        SKFont font, SKPaint paint,
        string measureStr, string s, bool isCombining,
        int w, int h, float hPad, float drawY)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);

        if (isCombining)
        {
            using var pairBmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var pairCanvas = new SKCanvas(pairBmp);
            pairCanvas.Clear(SKColors.Transparent);
            DrawString(pairCanvas, font, paint, measureStr, hPad, drawY);

            using var baseBmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var baseCanvas = new SKCanvas(baseBmp);
            baseCanvas.Clear(SKColors.Transparent);
            DrawString(baseCanvas, font, paint, char.ConvertFromUtf32(ThaiBase), hPad, drawY);

            EraseBase(pairBmp, baseBmp, bmp);
        }
        else
        {
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Transparent);
            DrawString(canvas, font, paint, s, hPad, drawY);
        }

        return bmp;
    }

    // ── วาด string ผ่าน SKTextBlob ────────────────────────────────────────────
    static void DrawString(SKCanvas canvas, SKFont font, SKPaint paint,
                           string text, float x, float y)
    {
        using var blob = SKTextBlob.Create(text, font);
        if (blob is not null)
            canvas.DrawText(blob, x, y, paint);
    }

    // ── ลบ pixel ของ base character ออกจาก pair ──────────────────────────────
    static void EraseBase(SKBitmap pair, SKBitmap baseB, SKBitmap dst)
    {
        int w = pair.Width, h = pair.Height;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                SKColor p = pair.GetPixel(x, y);
                SKColor b = baseB.GetPixel(x, y);
                byte outA = (byte)Math.Max(0, p.Alpha - b.Alpha);
                dst.SetPixel(x, y, new SKColor(p.Red, p.Green, p.Blue, outA));
            }
    }

    // ── Charset ───────────────────────────────────────────────────────────────
    static List<int> BuildCharset()
    {
        var set = new HashSet<int>();
        for (int i = 32; i <= 126; i++) set.Add(i); // ASCII
        for (int i = 0x0E01; i <= 0x0E2E; i++) set.Add(i); // พยัญชนะไทย
        for (int i = 0x0E30; i <= 0x0E3A; i++) set.Add(i); // สระ
        for (int i = 0x0E40; i <= 0x0E5B; i++) set.Add(i); // วรรณยุกต์ + อื่นๆ
        foreach (char c in "€£¥©®™°•…—–\u201C\u201D\u2018\u2019«»±×÷≈≠≤≥")
            set.Add(c);
        return set.ToList();
    }

    // ── Thai combining marks ──────────────────────────────────────────────────
    static bool IsThaiCombining(int code) =>
        (code >= 0x0E30 && code <= 0x0E3A) ||
        (code >= 0x0E47 && code <= 0x0E4E);

    // ── บันทึก XML ────────────────────────────────────────────────────────────
    static void SaveXml(string path, List<GlyphInfo> glyphs,
                        int texW, int texH, int fontSize)
    {
        var root = new XElement("font",
            new XElement("info",
                new XAttribute("size", fontSize)),
            new XElement("common",
                new XAttribute("scaleW", texW),
                new XAttribute("scaleH", texH)),
            new XElement("chars",
                glyphs.Select(g => new XElement("char",
                    new XAttribute("id", g.id),
                    new XAttribute("x", g.x),
                    new XAttribute("y", g.y),
                    new XAttribute("width", g.w),
                    new XAttribute("height", g.h),
                    new XAttribute("xoffset", g.xoffset),
                    new XAttribute("yoffset", g.yoffset),
                    new XAttribute("xadvance", g.xadvance)
                ))
            )
        );
        root.Save(path);
    }
}