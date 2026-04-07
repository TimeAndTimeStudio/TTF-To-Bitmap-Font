using SkiaSharp;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Xml.Linq;

// ── Per-thread reusable resources ────────────────────────────────────────────
sealed class ThreadResources : IDisposable
{
    public readonly SKFont FontHi;
    public readonly SKFont FontFinal;
    public readonly SKPaint Paint;

    SKBitmap? _pairBmp, _baseBmp;
    int _scratchW, _scratchH;

    public ThreadResources(SKTypeface tf, int renderSize, int fontSize)
    {
        FontHi = new SKFont(tf, renderSize)
        { Subpixel = true, LinearMetrics = true, Edging = SKFontEdging.Antialias };
        FontFinal = new SKFont(tf, fontSize)
        { Subpixel = true, LinearMetrics = true, Edging = SKFontEdging.Antialias };
        Paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
    }

    public (SKBitmap pair, SKBitmap baseB) GetScratch(int w, int h)
    {
        if (_pairBmp is null || _scratchW < w || _scratchH < h)
        {
            _pairBmp?.Dispose();
            _baseBmp?.Dispose();
            _scratchW = Math.Max(w, _scratchW);
            _scratchH = Math.Max(h, _scratchH);
            _pairBmp = new SKBitmap(_scratchW, _scratchH, SKColorType.Rgba8888, SKAlphaType.Premul);
            _baseBmp = new SKBitmap(_scratchW, _scratchH, SKColorType.Rgba8888, SKAlphaType.Premul);
        }
        return (_pairBmp, _baseBmp!);
    }

    public void Dispose()
    {
        FontHi.Dispose(); FontFinal.Dispose(); Paint.Dispose();
        _pairBmp?.Dispose(); _baseBmp?.Dispose();
    }
}

// ── Per-glyph result ──────────────────────────────────────────────────────────
struct GlyphSlot
{
    public SKBitmap? Bitmap;
    public int BmpW, BmpH, Advance, XOffset, YOffset;
    public bool Valid;
}

class GlyphInfo
{
    public int id, x, y, w, h, xoffset, yoffset, xadvance;
}

class Program
{
    const int ThaiBase = 0x0E01; // ใช้ "ก" เป็นตัวฐาน

    static void Main()
    {
        string fontPath = "font.ttf";
        int fontSize = 64;
        int supersample = 4;
        int renderSize = fontSize * supersample;
        int atlasSize = 16384;
        int padding = 4;
        int threadCount = Environment.ProcessorCount;

        Console.WriteLine($"Threads     : {threadCount}");
        Console.WriteLine("Loading font...");

        using var typeface = SKTypeface.FromFile(fontPath)
            ?? throw new Exception("โหลดฟอนต์ไม่ได้");

        using var fontMetric = new SKFont(typeface, fontSize) { LinearMetrics = true };
        SKFontMetrics m = fontMetric.Metrics;
        int stdAscent = (int)Math.Ceiling(-m.Ascent);
        int stdDescent = (int)Math.Ceiling(m.Descent);
        int vPad = (int)(fontSize * 0.20f);
        int hPad = (int)(fontSize * 0.08f);

        List<int> charset = BuildCharset();
        int total = charset.Count;
        Console.WriteLine($"Glyph count : {total}");
        Console.WriteLine($"Atlas size  : {atlasSize}x{atlasSize} px");
        Console.WriteLine($"Supersample : {supersample}x  ({renderSize}px→{fontSize}px)");
        Console.WriteLine("Rendering (parallel, max speed)...");

        var slots = new GlyphSlot[total];
        int done = 0;

        var tlPool = new ThreadLocal<ThreadResources>(
            () => new ThreadResources(typeface, renderSize, fontSize),
            trackAllValues: true);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        Parallel.For(0, total,
            new ParallelOptions { MaxDegreeOfParallelism = threadCount },
            i =>
            {
                int code = charset[i];
                var res = tlPool.Value!;

                string s = char.ConvertFromUtf32(code);
                bool isCombining = IsThaiCombining(code);
                string measureStr = isCombining ? char.ConvertFromUtf32(ThaiBase) + s : s;

                // ── advance ───────────────────────────────────────────────────
                float pairAdv = res.FontFinal.MeasureText(measureStr);
                float baseAdv = isCombining ? res.FontFinal.MeasureText(char.ConvertFromUtf32(ThaiBase)) : 0f;
                int advance = isCombining
                    ? Math.Max(0, (int)Math.Ceiling(pairAdv - baseAdv))
                    : (int)Math.Ceiling(pairAdv);
                if (advance <= 0 && !isCombining) advance = 1;

                // ── bounds — Dynamic Bounding Box (แก้บัคสระโดนตัด) ────────────
                res.FontFinal.MeasureText(measureStr, out SKRect bounds);

                int minLeft = (int)Math.Floor(bounds.Left);
                int maxRight = (int)Math.Ceiling(bounds.Right);
                int minTop = (int)Math.Floor(bounds.Top);
                int maxBottom = (int)Math.Ceiling(bounds.Bottom);

                // ขยายกล่อง X ถ้าตัวอักษรล้ำไปด้านซ้าย
                int minX = Math.Min(0, minLeft);
                int maxX = Math.Max(advance, maxRight);
                int drawX = hPad - minX;
                int bmpW = (maxX - minX) + hPad * 2;
                if (bmpW <= 0) bmpW = 1;

                // ขยายกล่อง Y ถ้าสระสูง/ต่ำทะลุ Ascent หรือ Descent มาตรฐาน
                int actualAscent = Math.Max(stdAscent, -minTop);
                int actualDescent = Math.Max(stdDescent, maxBottom);
                int bmpH = actualAscent + actualDescent + vPad * 2;
                int baselineY = actualAscent + vPad;

                int bmpWHi = bmpW * supersample;
                int bmpHHi = bmpH * supersample;
                float drawXHi = drawX * supersample;
                float drawYHi = baselineY * supersample;

                using var hiRes = RenderHiRes(res, measureStr, s, isCombining,
                                              bmpWHi, bmpHHi, drawXHi, drawYHi);

                var info = new SKImageInfo(bmpW, bmpH, SKColorType.Rgba8888, SKAlphaType.Premul);
                var final = hiRes.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

                if (final is null) return;

                // คำนวณ Offset ชดเชยพิกัดที่ดึงหลบเข้ามา ให้เอนจินวาดลงไปตรงจุดเดิมเป๊ะ
                int xOffset = isCombining ? -(drawX + (int)Math.Ceiling(baseAdv)) : -drawX;
                int yOffset = -baselineY;

                slots[i] = new GlyphSlot
                {
                    Bitmap = final,
                    BmpW = bmpW,
                    BmpH = bmpH,
                    Advance = advance,
                    XOffset = xOffset,
                    YOffset = yOffset,
                    Valid = true,
                };

                int current = Interlocked.Increment(ref done);
                if (current % 50 == 0 || current == total)
                    Console.WriteLine($"  Rendering  : {current}/{total} ({current * 100 / total}%)");
            });

        sw.Stop();
        Console.WriteLine($"Render      : {sw.ElapsedMilliseconds} ms");

        foreach (var r in tlPool.Values) r.Dispose();
        tlPool.Dispose();

        // ── Pack atlas ────────────────────────────────────────────────────────
        Console.WriteLine("Packing atlas...");
        sw.Restart();

        using var atlasBmp = new SKBitmap(atlasSize, atlasSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var atlasCanvas = new SKCanvas(atlasBmp);
        atlasCanvas.Clear(SKColors.Transparent);
        using var paintAtlas = new SKPaint { IsAntialias = false };

        var glyphs = new List<GlyphInfo>(total);
        int cx = padding, cy = padding, rowH = 0;

        for (int i = 0; i < total; i++)
        {
            ref GlyphSlot s = ref slots[i];
            if (!s.Valid) continue;

            if (cx + s.BmpW + padding >= atlasSize)
            { cx = padding; cy += rowH + padding; rowH = 0; }

            if (cy + s.BmpH + padding >= atlasSize)
            {
                s.Bitmap!.Dispose(); continue;
            }

            atlasCanvas.DrawBitmap(s.Bitmap!, cx, cy, paintAtlas);
            s.Bitmap!.Dispose();

            glyphs.Add(new GlyphInfo
            {
                id = charset[i],
                x = cx,
                y = cy,
                w = s.BmpW,
                h = s.BmpH,
                xoffset = s.XOffset,
                yoffset = s.YOffset,
                xadvance = s.Advance,
            });

            rowH = Math.Max(rowH, s.BmpH);
            cx += s.BmpW + padding;
        }

        sw.Stop();
        Console.WriteLine($"Pack        : {sw.ElapsedMilliseconds} ms");

        // ── Save PNG ──────────────────────────────────────────────────────────
        Console.WriteLine("Saving font.png...");
        using var img = SKImage.FromBitmap(atlasBmp);
        using var imgData = img.Encode(SKEncodedImageFormat.Png, 100);
        using (var fs = File.OpenWrite("font.png")) imgData.SaveTo(fs);

        SaveXml("font.xml", glyphs, atlasSize, atlasSize, fontSize);
        Console.WriteLine($"DONE ✔  ({glyphs.Count} glyphs)  atlas={atlasSize}x{atlasSize}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static SKBitmap RenderHiRes(ThreadResources res,
        string measureStr, string s, bool isCombining,
        int w, int h, float drawX, float drawY)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);

        if (!isCombining)
        {
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Transparent);
            DrawString(canvas, res.FontHi, res.Paint, s, drawX, drawY);
            return bmp;
        }

        var (pairBmp, baseBmp) = res.GetScratch(w, h);

        using (var c = new SKCanvas(pairBmp))
        {
            c.Clear(SKColors.Transparent);
            DrawString(c, res.FontHi, res.Paint, measureStr, drawX, drawY);
        }
        using (var c = new SKCanvas(baseBmp))
        {
            c.Clear(SKColors.Transparent);
            DrawString(c, res.FontHi, res.Paint, char.ConvertFromUtf32(ThaiBase), drawX, drawY);
        }

        EraseBase(pairBmp, baseBmp, bmp, w, h);
        return bmp;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void DrawString(SKCanvas canvas, SKFont font, SKPaint paint, string text, float x, float y)
    {
        using var blob = SKTextBlob.Create(text, font);
        if (blob is not null) canvas.DrawText(blob, x, y, paint);
    }

    static void EraseBase(SKBitmap pair, SKBitmap baseB, SKBitmap dst, int w, int h)
    {
        var spanP = MemoryMarshal.Cast<byte, uint>(pair.GetPixelSpan());
        var spanB = MemoryMarshal.Cast<byte, uint>(baseB.GetPixelSpan());
        var spanD = MemoryMarshal.Cast<byte, uint>(dst.GetPixelSpan());

        int pairStride = pair.Width;
        int baseStride = baseB.Width;
        int dstStride = dst.Width;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint p = spanP[y * pairStride + x];
                uint b = spanB[y * baseStride + x];
                byte pA = (byte)(p >> 24);
                byte bA = (byte)(b >> 24);

                byte outA = pA > bA ? (byte)(pA - bA) : (byte)0;
                spanD[y * dstStride + x] = (uint)outA | ((uint)outA << 8) | ((uint)outA << 16) | ((uint)outA << 24);
            }
    }

    static List<int> BuildCharset()
    {
        var set = new HashSet<int>();
        for (int i = 32; i <= 126; i++) set.Add(i);
        for (int i = 0x0E01; i <= 0x0E2E; i++) set.Add(i);
        for (int i = 0x0E30; i <= 0x0E3A; i++) set.Add(i);
        for (int i = 0x0E40; i <= 0x0E5B; i++) set.Add(i);
        foreach (char c in "€£¥©®™°•…—–\u201C\u201D\u2018\u2019«»±×÷≈≠≤≥") set.Add(c);
        return set.ToList();
    }

    static bool IsThaiCombining(int code) =>
        code == 0x0E31 ||
        (code >= 0x0E34 && code <= 0x0E3A) ||
        (code >= 0x0E47 && code <= 0x0E4E);

    static void SaveXml(string path, List<GlyphInfo> glyphs, int texW, int texH, int fontSize)
    {
        var root = new XElement("font",
            new XElement("info", new XAttribute("size", fontSize)),
            new XElement("common", new XAttribute("scaleW", texW),
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