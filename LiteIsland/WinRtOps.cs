using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using WImg = Windows.Graphics.Imaging;
using WOcr = Windows.Media.Ocr;
using WPdf = Windows.Data.Pdf;
using WStorage = Windows.Storage;
using WStreams = Windows.Storage.Streams;

namespace LiteIsland;

/// <summary>Windows' own offline PDF renderer and text recognition (OCR). Helper process only.</summary>
internal static class WinRtOps
{
    /// <summary>Renders every page of a PDF to PNG bytes (one page at a time) and hands it to perPage(index, count, png, widthPt, heightPt).</summary>
    public static async Task RenderPdfAsync(string pdf, double dpi, Func<int, int, byte[], double, double, Task> perPage)
    {
        WPdf.PdfDocument doc;
        try
        {
            var file = await WStorage.StorageFile.GetFileFromPathAsync(pdf);
            doc = await WPdf.PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("This PDF is protected or damaged");
        }

        int n = (int)doc.PageCount;
        for (int i = 0; i < n; i++)
        {
            using var page = doc.GetPage((uint)i);
            double wpx = page.Size.Width * dpi / 96.0;
            double hpx = page.Size.Height * dpi / 96.0;
            double k = Math.Min(1.0, 6000.0 / Math.Max(wpx, hpx)); // very large pages are scaled down
            var opt = new WPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, wpx * k),
                DestinationHeight = (uint)Math.Max(1, hpx * k),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
            };

            using var ms = new WStreams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(ms, opt);
            ms.Seek(0);
            byte[] data = new byte[(int)ms.Size];
            using (var rs = ms.AsStreamForRead())
            {
                int off = 0;
                while (off < data.Length)
                {
                    int r = rs.Read(data, off, data.Length - off);
                    if (r <= 0) break;
                    off += r;
                }
            }
            await perPage(i, n, data, page.Size.Width * 0.75, page.Size.Height * 0.75); // 96 dpi -> points
        }
    }

    // ---------- OCR ----------

    public static WOcr.OcrEngine Engine()
    {
        WOcr.OcrEngine? e = WOcr.OcrEngine.TryCreateFromUserProfileLanguages();
        e ??= WOcr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (e == null)
            throw new InvalidOperationException("No text recognition language is installed (Windows Settings > Time & language > Language)");
        return e;
    }

    public static async Task<string> OcrFileAsync(WOcr.OcrEngine eng, string path)
    {
        var f = await WStorage.StorageFile.GetFileFromPathAsync(path);
        using var s = await f.OpenAsync(WStorage.FileAccessMode.Read);
        return await OcrStreamAsync(eng, s);
    }

    public static async Task<string> OcrBytesAsync(WOcr.OcrEngine eng, byte[] data)
    {
        using var ms = new WStreams.InMemoryRandomAccessStream();
        await ms.WriteAsync(data.AsBuffer());
        ms.Seek(0);
        return await OcrStreamAsync(eng, ms);
    }

    private static async Task<string> OcrStreamAsync(WOcr.OcrEngine eng, WStreams.IRandomAccessStream s)
    {
        var dec = await WImg.BitmapDecoder.CreateAsync(s);
        uint w = dec.PixelWidth, h = dec.PixelHeight;
        uint max = WOcr.OcrEngine.MaxImageDimension;

        var tr = new WImg.BitmapTransform();
        if (w > max || h > max)
        {
            double k = (double)max / Math.Max(w, h);
            tr.ScaledWidth = (uint)Math.Max(1, w * k);
            tr.ScaledHeight = (uint)Math.Max(1, h * k);
        }

        using var sb = await dec.GetSoftwareBitmapAsync(
            WImg.BitmapPixelFormat.Bgra8, WImg.BitmapAlphaMode.Premultiplied, tr,
            WImg.ExifOrientationMode.RespectExifOrientation, WImg.ColorManagementMode.DoNotColorManage);

        var res = await eng.RecognizeAsync(sb);
        return string.Join("\n", res.Lines.Select(l => l.Text));
    }
}
