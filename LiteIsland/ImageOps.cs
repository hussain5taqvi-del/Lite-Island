using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LiteIsland;

/// <summary>
/// Picture work that runs ONLY inside the helper process (never in the island itself):
/// JPG / PNG conversion, compress, remove background (small offline AI model), pictures to PDF.
/// </summary>
internal static class ImageOps
{
    // ---------- Loading / saving ----------

    public static BitmapSource Load(string path)
    {
        var dec = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource src = dec.Frames[0];

        // Phone photos: respect the EXIF rotation
        int orientation = 1;
        try
        {
            if (src.Metadata is BitmapMetadata md && md.ContainsQuery("/app1/ifd/{ushort=274}"))
                orientation = Convert.ToInt32(md.GetQuery("/app1/ifd/{ushort=274}"));
        }
        catch { }
        double rot = orientation == 3 ? 180 : orientation == 6 ? 90 : orientation == 8 ? 270 : 0;
        if (rot != 0) src = new TransformedBitmap(src, new RotateTransform(rot));

        if (src.CanFreeze && !src.IsFrozen) src.Freeze();
        return src;
    }

    private static bool HasAlpha(BitmapSource s)
    {
        PixelFormat f = s.Format;
        if (f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Prgba64 ||
            f == PixelFormats.Rgba64 || f == PixelFormats.Prgba128Float || f == PixelFormats.Rgba128Float)
            return true;
        var pal = s.Palette;
        return pal != null && pal.Colors.Any(c => c.A < 255);
    }

    private static BitmapSource Scale(BitmapSource s, int maxSide)
    {
        int m = Math.Max(s.PixelWidth, s.PixelHeight);
        if (maxSide <= 0 || m <= maxSide) return s;
        double k = (double)maxSide / m;
        var t = new TransformedBitmap(s, new ScaleTransform(k, k));
        t.Freeze();
        return t;
    }

    // Transparent pixels become white (JPEG has no transparency)
    private static BitmapSource FlattenOnWhite(BitmapSource s)
    {
        int w = s.PixelWidth, h = s.PixelHeight;
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawImage(s, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource ToBgr24(BitmapSource s)
    {
        if (s.Format == PixelFormats.Bgr24) return s;
        var c = new FormatConvertedBitmap(s, PixelFormats.Bgr24, null, 0);
        c.Freeze();
        return c;
    }

    public static byte[] JpegBytes(BitmapSource s, int quality)
    {
        if (HasAlpha(s)) s = FlattenOnWhite(s);
        s = ToBgr24(s);
        var enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 10, 100) };
        enc.Frames.Add(BitmapFrame.Create(s));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static void Save(BitmapSource s, string path, string fmt, int quality)
    {
        BitmapEncoder enc;
        if (fmt == "jpg")
        {
            if (HasAlpha(s)) s = FlattenOnWhite(s);
            s = ToBgr24(s);
            enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 10, 100) };
        }
        else if (fmt == "bmp")
        {
            if (HasAlpha(s)) s = FlattenOnWhite(s);
            enc = new BmpBitmapEncoder();
        }
        else if (fmt == "tiff")
        {
            enc = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
        }
        else if (fmt == "gif")
        {
            enc = new GifBitmapEncoder();
        }
        else
        {
            enc = new PngBitmapEncoder();
        }
        enc.Frames.Add(BitmapFrame.Create(s));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        enc.Save(fs);
    }

    // ---------- JPG / PNG / compress ----------

    public static void ConvertPhoto(string src, string dst, string fmt, int quality, int maxSide, bool mustBeSmaller)
    {
        BitmapSource img = Scale(Load(src), maxSide);
        Save(img, dst, fmt, quality);

        if (mustBeSmaller && new FileInfo(dst).Length >= new FileInfo(src).Length)
        {
            File.Delete(dst);
            throw new InvalidOperationException("Already as small as it gets");
        }
    }

    // ---------- Pictures to PDF ----------

    public static void ImagesToPdf(string[] inputs, string dst)
    {
        using var pdf = new PdfWriter(dst);
        foreach (string f in inputs)
        {
            BitmapSource img = Load(f);
            double dpi = img.DpiX > 1 ? img.DpiX : 96;
            double ptW = img.PixelWidth * 72.0 / dpi, ptH = img.PixelHeight * 72.0 / dpi;
            double longSide = Math.Max(ptW, ptH);
            if (longSide > 842) { double k = 842 / longSide; ptW *= k; ptH *= k; }
            pdf.AddJpegPage(JpegBytes(img, 92), img.PixelWidth, img.PixelHeight, ptW, ptH);
        }
        pdf.Finish();
    }

    // ---------- Remove background (U2-Net small model, runs offline) ----------

    public static void RemoveBackground(string src, string dst, string modelPath)
    {
        BitmapSource img = Load(src);
        int w = img.PixelWidth, h = img.PixelHeight;
        const int S = 320;

        // 1) the model input: 320 x 320, normalized
        var small = new TransformedBitmap(img, new ScaleTransform((double)S / w, (double)S / h));
        var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int sw = conv.PixelWidth, sh = conv.PixelHeight;
        byte[] sp = new byte[sw * sh * 4];
        conv.CopyPixels(sp, sw * 4, 0);

        float max = 1e-6f;
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                int i = (Math.Min(sh - 1, y * sh / S) * sw + Math.Min(sw - 1, x * sw / S)) * 4;
                max = Math.Max(max, Math.Max(sp[i], Math.Max(sp[i + 1], sp[i + 2])));
            }

        float[] mean = { 0.485f, 0.456f, 0.406f };
        float[] std = { 0.229f, 0.224f, 0.225f };
        var tensor = new DenseTensor<float>(new[] { 1, 3, S, S });
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                int i = (Math.Min(sh - 1, y * sh / S) * sw + Math.Min(sw - 1, x * sw / S)) * 4;
                tensor[0, 0, y, x] = (sp[i + 2] / max - mean[0]) / std[0]; // R
                tensor[0, 1, y, x] = (sp[i + 1] / max - mean[1]) / std[1]; // G
                tensor[0, 2, y, x] = (sp[i] / max - mean[2]) / std[2];     // B
            }

        // 2) run the model
        float[] m;
        using (var opts = new SessionOptions { IntraOpNumThreads = 2 })
        using (var sess = new InferenceSession(modelPath, opts))
        {
            string name = sess.InputMetadata.Keys.First();
            using var res = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(name, tensor) });
            m = res.First().AsTensor<float>().ToArray();
        }

        float mn = float.MaxValue, mx = float.MinValue;
        for (int i = 0; i < S * S; i++) { if (m[i] < mn) mn = m[i]; if (m[i] > mx) mx = m[i]; }
        float range = Math.Max(1e-6f, mx - mn);
        for (int i = 0; i < S * S; i++) m[i] = (m[i] - mn) / range;

        // 3) the mask becomes the transparency of the original picture (smoothly scaled up)
        var full = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        byte[] px = new byte[w * h * 4];
        full.CopyPixels(px, w * 4, 0);

        for (int y = 0; y < h; y++)
        {
            float fy = Math.Clamp((y + 0.5f) * S / h - 0.5f, 0, S - 1);
            int y0 = (int)fy, y1 = Math.Min(S - 1, y0 + 1);
            float wy = fy - y0;
            for (int x = 0; x < w; x++)
            {
                float fx = Math.Clamp((x + 0.5f) * S / w - 0.5f, 0, S - 1);
                int x0 = (int)fx, x1 = Math.Min(S - 1, x0 + 1);
                float wx = fx - x0;
                float top = m[y0 * S + x0] * (1 - wx) + m[y0 * S + x1] * wx;
                float bot = m[y1 * S + x0] * (1 - wx) + m[y1 * S + x1] * wx;
                float a = top * (1 - wy) + bot * wy;
                a = Math.Clamp((a - 0.12f) / 0.76f, 0f, 1f); // crisper edge
                int idx = (y * w + x) * 4 + 3;
                px[idx] = (byte)(px[idx] * a);
            }
        }

        var outImg = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        outImg.Freeze();
        Save(outImg, dst, "png", 100);
    }
}
