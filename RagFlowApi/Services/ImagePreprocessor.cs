using SkiaSharp;

namespace RagFlowApi.Services;

/// <summary>
/// Image preprocessing pipeline for scanned PDF pages before vision OCR.
/// Steps applied in order:
///   1. Denoise (Gaussian blur)          — remove scanner speckle
///   2. Coarse deskew (±5°, 1° steps)   — correct scanner tilt
///   3. Fine deskew (±1°, 0.1° steps)   — sub-degree refinement
///   4. Local contrast / CLAHE-style     — tiled histogram stretch for uneven lighting
///   5. Border crop                      — remove scanner shadow at edges
///   6. Sharpen (unsharp mask)           — crisp text edges for vision LLM
///   7. Stamp/seal masking               — fill red/blue circular seals with white
/// Binarization is intentionally omitted — the vision LLM reads grayscale better
/// than hard black/white, and binarization destroys thin Vietnamese diacritics.
/// </summary>
public static class ImagePreprocessor
{
    public static byte[] Preprocess(byte[] pngBytes)
    {
        using var original = SKBitmap.Decode(pngBytes);

        // Skip all preprocessing for clean digital-PDF renders.
        if (IsAlreadyClean(original))
            return pngBytes;

        // Step 1: Denoise — light Gaussian blur removes scanner speckle before
        //         deskew and contrast, so noise doesn't skew angle estimation.
        using var denoised = Denoise(original);

        // Step 2 & 3: Deskew — coarse pass (1° steps) then fine pass (0.1° steps)
        using var deskewed = Deskew(denoised);

        // Step 4: Local contrast — tiled histogram stretch handles pages where
        //         scanner lighting is uneven (bright center, dark corners).
        using var contrasted = LocalContrast(deskewed, tiles: 4);

        // Step 5: Border crop — remove scanner shadow at page edges.
        using var cropped = CropBorder(contrasted);

        // Step 6: Sharpen — unsharp mask makes character edges crisp, which
        //         helps the vision LLM distinguish Vietnamese diacritics (ắ vs a).
        using var sharpened = UnsharpMask(cropped);

        // Step 7: Stamp masking — fill red/blue circular seals with white so the
        //         vision LLM doesn't hallucinate text inside the stamp boundary.
        using var masked = MaskStamps(sharpened);

        using var encoded = masked.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    // ── Clean detection ───────────────────────────────────────────────────────

    private static bool IsAlreadyClean(SKBitmap bmp)
    {
        int totalSampled = 0;
        int midGrayCount = 0;

        int stepX = Math.Max(1, bmp.Width  / 80);
        int stepY = Math.Max(1, bmp.Height / 80);

        for (int y = 0; y < bmp.Height; y += stepY)
        for (int x = 0; x < bmp.Width;  x += stepX)
        {
            var p   = bmp.GetPixel(x, y);
            byte lum = (byte)(p.Red * 0.299 + p.Green * 0.587 + p.Blue * 0.114);
            if (lum is > 30 and < 220) midGrayCount++;
            totalSampled++;
        }

        // < 8% mid-gray pixels → clean digital render, skip preprocessing
        return (double)midGrayCount / totalSampled < 0.08;
    }

    // ── Step 1: Denoise ───────────────────────────────────────────────────────

    /// <summary>
    /// Light 3×3 Gaussian blur. Removes salt-and-pepper scanner noise without
    /// softening text strokes meaningfully. Applied before deskew so noise
    /// pixels don't bias the horizontal-projection angle estimator.
    /// </summary>
    private static SKBitmap Denoise(SKBitmap src)
    {
        var result = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(result);
        using var paint  = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(0.8f, 0.8f)
        };
        canvas.DrawBitmap(src, 0, 0, paint);
        return result;
    }

    // ── Steps 2 & 3: Deskew ──────────────────────────────────────────────────

    private static SKBitmap Deskew(SKBitmap src)
    {
        using var thumb     = src.Resize(new SKImageInfo(Math.Max(1, src.Width / 4),
                                                          Math.Max(1, src.Height / 4)),
                                          SKSamplingOptions.Default);
        using var thumbGray = ToGrayscale(thumb);

        // Coarse pass: ±5° at 1° steps
        float coarseAngle = ScanAngle(thumbGray, -5f, 5f, 1f);

        if (Math.Abs(coarseAngle) < 0.3f)
            return src.Copy();

        // Fine pass: ±1° around coarse result at 0.1° steps
        float fineAngle = ScanAngle(thumbGray, coarseAngle - 1f, coarseAngle + 1f, 0.1f);

        return Rotate(src, -fineAngle);
    }

    private static float ScanAngle(SKBitmap gray, float minAngle, float maxAngle, float step)
    {
        float  bestAngle = minAngle;
        double bestScore = double.MinValue;

        for (float angle = minAngle; angle <= maxAngle; angle += step)
        {
            using var rotated = Rotate(gray, angle);
            double score = HorizontalProjectionVariance(rotated);
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }

        return bestAngle;
    }

    private static double HorizontalProjectionVariance(SKBitmap bmp)
    {
        var rowSums = new int[bmp.Height];

        for (int y = 0; y < bmp.Height; y++)
        {
            int sum = 0;
            for (int x = 0; x < bmp.Width; x += 2)
                if (bmp.GetPixel(x, y).Red < 128) sum++;
            rowSums[y] = sum;
        }

        double mean     = rowSums.Average();
        double variance = rowSums.Average(s => (s - mean) * (s - mean));
        return variance;
    }

    // ── Step 4: Local contrast (CLAHE-style tiled stretch) ───────────────────

    /// <summary>
    /// Splits the image into a grid of tiles and applies histogram stretch
    /// independently per tile, then blends at tile borders with bilinear
    /// interpolation. Handles uneven scanner lighting much better than a single
    /// global stretch — e.g. when the centre of the page is overexposed and
    /// the corners are dark from the scanner lid not closing fully.
    /// </summary>
    private static SKBitmap LocalContrast(SKBitmap src, int tiles = 4)
    {
        int w = src.Width, h = src.Height;
        int tileW = w / tiles, tileH = h / tiles;

        // Build per-tile min/max on sampled pixels
        var tileMin = new byte[tiles, tiles];
        var tileMax = new byte[tiles, tiles];

        for (int ty = 0; ty < tiles; ty++)
        for (int tx = 0; tx < tiles; tx++)
        {
            int x0 = tx * tileW, y0 = ty * tileH;
            int x1 = (tx == tiles - 1) ? w : x0 + tileW;
            int y1 = (ty == tiles - 1) ? h : y0 + tileH;

            byte mn = 255, mx = 0;
            int stepX = Math.Max(1, (x1 - x0) / 40);
            int stepY = Math.Max(1, (y1 - y0) / 40);

            for (int y = y0; y < y1; y += stepY)
            for (int x = x0; x < x1; x += stepX)
            {
                var p   = src.GetPixel(x, y);
                byte lum = Lum(p);
                if (lum < mn) mn = lum;
                if (lum > mx) mx = lum;
            }

            // Clamp range to avoid over-stretching nearly-uniform tiles
            if (mx - mn < 30) { mn = 0; mx = 255; }

            tileMin[ty, tx] = mn;
            tileMax[ty, tx] = mx;
        }

        var result = new SKBitmap(w, h, src.ColorType, src.AlphaType);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            // Bilinear interpolation of tile parameters at this pixel
            float fx = (float)x / tileW - 0.5f;
            float fy = (float)y / tileH - 0.5f;

            int tx0 = Math.Clamp((int)Math.Floor(fx), 0, tiles - 1);
            int ty0 = Math.Clamp((int)Math.Floor(fy), 0, tiles - 1);
            int tx1 = Math.Min(tx0 + 1, tiles - 1);
            int ty1 = Math.Min(ty0 + 1, tiles - 1);

            float wx = Math.Clamp(fx - (int)Math.Floor(fx), 0f, 1f);
            float wy = Math.Clamp(fy - (int)Math.Floor(fy), 0f, 1f);

            float mn = Bilerp(tileMin[ty0, tx0], tileMin[ty0, tx1],
                               tileMin[ty1, tx0], tileMin[ty1, tx1], wx, wy);
            float mx = Bilerp(tileMax[ty0, tx0], tileMax[ty0, tx1],
                               tileMax[ty1, tx0], tileMax[ty1, tx1], wx, wy);

            float range = mx - mn;
            if (range < 1f) range = 1f;
            float scale = 255f / range;

            var p = src.GetPixel(x, y);
            byte r = (byte)Math.Clamp((p.Red   - mn) * scale, 0, 255);
            byte g = (byte)Math.Clamp((p.Green - mn) * scale, 0, 255);
            byte b = (byte)Math.Clamp((p.Blue  - mn) * scale, 0, 255);
            result.SetPixel(x, y, new SKColor(r, g, b, p.Alpha));
        }

        return result;
    }

    private static float Bilerp(byte v00, byte v10, byte v01, byte v11, float wx, float wy)
        => v00 * (1 - wx) * (1 - wy)
         + v10 * wx       * (1 - wy)
         + v01 * (1 - wx) * wy
         + v11 * wx       * wy;

    // ── Step 5: Border crop ───────────────────────────────────────────────────

    private static SKBitmap CropBorder(SKBitmap src)
    {
        int marginX = Math.Max(1, (int)(src.Width  * 0.015));
        int marginY = Math.Max(1, (int)(src.Height * 0.015));

        int cropW = src.Width  - marginX * 2;
        int cropH = src.Height - marginY * 2;

        if (cropW <= 0 || cropH <= 0) return src.Copy();

        var result = new SKBitmap(cropW, cropH, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(src, -marginX, -marginY);
        return result;
    }

    // ── Step 6: Sharpen (unsharp mask) ───────────────────────────────────────

    /// <summary>
    /// Classic unsharp mask: subtract a blurred copy from the original then
    /// blend. Strength is intentionally mild (amount=0.6) so we crisp up
    /// Vietnamese diacritic marks without creating ringing artifacts around
    /// thick strokes that would confuse the vision LLM.
    /// </summary>
    private static SKBitmap UnsharpMask(SKBitmap src)
    {
        // Blurred version
        var blurred = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        using (var canvas = new SKCanvas(blurred))
        using (var paint  = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(1.5f, 1.5f) })
            canvas.DrawBitmap(src, 0, 0, paint);

        const float amount = 0.6f; // sharpening strength — keep ≤ 1.0
        var result = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);

        for (int y = 0; y < src.Height; y++)
        for (int x = 0; x < src.Width; x++)
        {
            var s = src.GetPixel(x, y);
            var bl = blurred.GetPixel(x, y);

            byte r = Clamp255(s.Red   + amount * (s.Red   - bl.Red));
            byte g = Clamp255(s.Green + amount * (s.Green - bl.Green));
            byte b = Clamp255(s.Blue  + amount * (s.Blue  - bl.Blue));
            result.SetPixel(x, y, new SKColor(r, g, b, s.Alpha));
        }

        blurred.Dispose();
        return result;
    }

    // ── Step 7: Stamp/seal masking ────────────────────────────────────────────

    /// <summary>
    /// Vietnamese official documents always carry a circular red or blue stamp
    /// (con dấu). Vision LLMs hallucinate text inside the stamp or mistake the
    /// circle border for a table/box. This step detects saturated red/blue
    /// blobs and fills them white, leaving text in other colors untouched.
    /// Detection is conservative: a blob must cover ≥ 0.5% and ≤ 8% of the
    /// page area to be treated as a stamp rather than a logo or a line of text.
    /// </summary>
    private static SKBitmap MaskStamps(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        int totalPixels = w * h;
        int minBlob = totalPixels / 200;  // 0.5%
        int maxBlob = totalPixels / 12;   // ~8%

        // Build a mask of stamp-colored pixels (red or blue, high saturation)
        var stampMask = new bool[h, w];

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var p = src.GetPixel(x, y);
            stampMask[y, x] = IsStampColor(p);
        }

        // Flood-fill connected components of stamp pixels; fill large ones white
        var visited = new bool[h, w];
        var result  = src.Copy();

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (!stampMask[y, x] || visited[y, x]) continue;

            var component = FloodFill(stampMask, visited, x, y, w, h);

            if (component.Count >= minBlob && component.Count <= maxBlob)
            {
                foreach (var (cx, cy) in component)
                    result.SetPixel(cx, cy, SKColors.White);
            }
        }

        return result;
    }

    private static bool IsStampColor(SKColor p)
    {
        // Red stamp: high red, low green, low blue
        bool isRed  = p.Red > 140 && p.Green < 80 && p.Blue < 80;
        // Blue stamp: high blue, low red, low green
        bool isBlue = p.Blue > 120 && p.Red < 100 && p.Green < 100;
        return isRed || isBlue;
    }

    private static List<(int x, int y)> FloodFill(
        bool[,] mask, bool[,] visited, int startX, int startY, int w, int h)
    {
        var result = new List<(int, int)>();
        var queue  = new Queue<(int, int)>();
        queue.Enqueue((startX, startY));
        visited[startY, startX] = true;

        Span<(int dx, int dy)> dirs = [(1,0),(-1,0),(0,1),(0,-1)];

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            result.Add((x, y));

            // Bail early if this blob is already too big to be a stamp
            if (result.Count > w * h / 10) break;

            foreach (var (dx, dy) in dirs)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                if (visited[ny, nx] || !mask[ny, nx]) continue;
                visited[ny, nx] = true;
                queue.Enqueue((nx, ny));
            }
        }

        return result;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static byte Lum(SKColor p)
        => (byte)(p.Red * 0.299f + p.Green * 0.587f + p.Blue * 0.114f);

    private static byte Clamp255(float v)
        => (byte)Math.Clamp((int)v, 0, 255);

    private static SKBitmap ToGrayscale(SKBitmap src)
    {
        var gray = new SKBitmap(src.Width, src.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(gray);
        using var paint  = new SKPaint
        {
            ColorFilter = SKColorFilter.CreateColorMatrix(
            [
                0.299f, 0.587f, 0.114f, 0, 0,
                0.299f, 0.587f, 0.114f, 0, 0,
                0.299f, 0.587f, 0.114f, 0, 0,
                0,      0,      0,      1, 0
            ])
        };
        canvas.DrawBitmap(src, new SKRect(0, 0, src.Width, src.Height), paint);
        return gray;
    }

    private static SKBitmap Rotate(SKBitmap src, float angleDeg)
    {
        double rad  = angleDeg * Math.PI / 180.0;
        float  cos  = (float)Math.Abs(Math.Cos(rad));
        float  sin  = (float)Math.Abs(Math.Sin(rad));

        int newW = (int)(src.Width * cos + src.Height * sin);
        int newH = (int)(src.Width * sin + src.Height * cos);

        var dst = new SKBitmap(newW, newH, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.Translate(newW / 2f, newH / 2f);
        canvas.RotateDegrees(angleDeg);
        canvas.Translate(-src.Width / 2f, -src.Height / 2f);
        canvas.DrawBitmap(src, 0f, 0f);

        return dst;
    }

    // ── Unused but retained ───────────────────────────────────────────────────
    // Binarization is kept for reference but not called.
    // Vision LLMs read grayscale better than hard black/white.

    private static SKBitmap Binarize(SKBitmap src)
    {
        using var gray = ToGrayscale(src);
        var result = new SKBitmap(src.Width, src.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        const int windowSize = 31;
        const int C = 4;
        int w = gray.Width, h = gray.Height, half = windowSize / 2;
        var integral = BuildIntegralImage(gray);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int x1 = Math.Max(0, x - half), y1 = Math.Max(0, y - half);
            int x2 = Math.Min(w - 1, x + half), y2 = Math.Min(h - 1, y + half);
            int count = (x2 - x1 + 1) * (y2 - y1 + 1);
            long sum = integral[y2+1, x2+1] - integral[y1, x2+1]
                     - integral[y2+1, x1]   + integral[y1, x1];
            int threshold = (int)(sum / count) - C;
            byte pv = gray.GetPixel(x, y).Red;
            result.SetPixel(x, y, pv < threshold ? SKColors.Black : SKColors.White);
        }
        return result;
    }

    private static long[,] BuildIntegralImage(SKBitmap gray)
    {
        int w = gray.Width, h = gray.Height;
        var integral = new long[h + 1, w + 1];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            integral[y+1, x+1] = gray.GetPixel(x, y).Red
                                + integral[y, x+1] + integral[y+1, x]
                                - integral[y, x];
        return integral;
    }
}
