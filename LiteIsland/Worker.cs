using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace LiteIsland;

/// <summary>
/// Helper mode of this same exe:  LiteIsland.exe --worker OPERATION RESULTFILE args...
/// It does one heavy job (picture, PDF, OCR, background removal) and exits, so the island's own RAM stays tiny.
/// The result (OK + text, or ERR + message) is written to RESULTFILE.
/// </summary>
internal static class Worker
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string[] a)
    {
        string op = a.Length > 1 ? a[1] : "";
        string resultFile = a.Length > 2 ? a[2] : "";
        string[] args = a.Skip(3).ToArray();
        int code = 1;

        // WPF imaging and the Windows runtime want a single-threaded apartment
        var t = new Thread(() =>
        {
            try
            {
                string r = Dispatch(op, args);
                WriteResult(resultFile, "OK\n" + r);
                code = 0;
            }
            catch (Exception ex)
            {
                string msg = ex is AggregateException ag && ag.InnerException != null ? ag.InnerException.Message : ex.Message;
                WriteResult(resultFile, "ERR\n" + msg);
                code = 1;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return code;
    }

    private static void WriteResult(string file, string text)
    {
        try { if (file.Length > 0) File.WriteAllText(file, text, new UTF8Encoding(false)); } catch { }
    }

    /// <summary>Progress for the island (0..1), read from the helper's output.</summary>
    public static void Progress(double f)
    {
        try
        {
            Console.Out.WriteLine("P " + Math.Clamp(f, 0, 1).ToString("0.###", Inv));
            Console.Out.Flush();
        }
        catch { }
    }

    private static string Dispatch(string op, string[] a)
    {
        switch (op)
        {
            case "img": // in out fmt quality maxSide mustBeSmaller
                ImageOps.ConvertPhoto(a[0], a[1], a[2], int.Parse(a[3], Inv), int.Parse(a[4], Inv), a[5] == "1");
                return a[1];

            case "removebg": // in out model
                ImageOps.RemoveBackground(a[0], a[1], a[2]);
                return a[1];

            case "img2pdf": // out in1 in2 ...
                ImageOps.ImagesToPdf(a.Skip(1).ToArray(), a[0]);
                return a[0];

            case "doc2pdf": // in out
                return DocOps.ToPdf(a[0], a[1]);

            case "pdf2img": // pdf outDir
                return PdfToImages(a[0], a[1]);

            case "pdfraster": // out jpegQuality dpi pdf1 pdf2 ...
                return PdfRaster(a[0], int.Parse(a[1], Inv), double.Parse(a[2], Inv), a.Skip(3).ToArray());

            case "ocr": // image
                return WinRtOps.OcrFileAsync(WinRtOps.Engine(), a[0]).GetAwaiter().GetResult();

            case "pick": // text | qr | color  (copy text from the screen, scan a QR code, pick a color)
                return ScreenPick.Run(a[0]);

            case "pdfocr": // pdf
                return PdfOcr(a[0]);

            default:
                throw new InvalidOperationException("Unknown helper job: " + op);
        }
    }

    private static string PdfToImages(string pdf, string outDir)
    {
        Directory.CreateDirectory(outDir);
        WinRtOps.RenderPdfAsync(pdf, 170, (i, n, png, wpt, hpt) =>
        {
            File.WriteAllBytes(Path.Combine(outDir, "page-" + (i + 1).ToString("000", Inv) + ".png"), png);
            Progress((i + 1.0) / n);
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        return outDir;
    }

    // Merge and compress: every page is drawn to a picture and written into a new PDF
    private static string PdfRaster(string dst, int quality, double dpi, string[] pdfs)
    {
        using var w = new PdfWriter(dst);
        for (int f = 0; f < pdfs.Length; f++)
        {
            int fi = f;
            WinRtOps.RenderPdfAsync(pdfs[f], dpi, (i, n, png, wpt, hpt) =>
            {
                var dec = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource bmp = dec.Frames[0];
                w.AddJpegPage(ImageOps.JpegBytes(bmp, quality), bmp.PixelWidth, bmp.PixelHeight, wpt, hpt);
                Progress((fi + (i + 1.0) / n) / pdfs.Length);
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
        }
        w.Finish();
        return dst;
    }

    private static string PdfOcr(string pdf)
    {
        var eng = WinRtOps.Engine();
        var sb = new StringBuilder();
        WinRtOps.RenderPdfAsync(pdf, 200, async (i, n, png, wpt, hpt) =>
        {
            string t = await WinRtOps.OcrBytesAsync(eng, png);
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(t);
            Progress((i + 1.0) / n);
        }).GetAwaiter().GetResult();
        return sb.ToString();
    }
}
