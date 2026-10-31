using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace LiteIsland;

/// <summary>
/// Word / RTF / text to PDF (helper process only). Best result first:
/// 1) Microsoft Word, if installed (real PDF with selectable text)
/// 2) LibreOffice, if installed
/// 3) built-in: text, RTF and DOCX text are drawn page by page into a PDF (works for every language, text is not selectable)
/// </summary>
internal static class DocOps
{
    public static string ToPdf(string src, string dst)
    {
        string ext = Path.GetExtension(src).ToLowerInvariant();

        if (ext is ".doc" or ".docx" or ".rtf" or ".odt")
        {
            if (TryWord(src, dst)) return dst;
            if (TryLibreOffice(src, dst)) return dst;
        }
        else if (TryLibreOffice(src, dst))
        {
            return dst;
        }

        BuiltIn(src, dst, ext);
        return dst;
    }

    private static bool TryWord(string src, string dst)
    {
        Type? t = Type.GetTypeFromProgID("Word.Application");
        if (t == null) return false;

        dynamic app;
        try { app = Activator.CreateInstance(t)!; }
        catch { return false; }

        try
        {
            app.Visible = false;
            app.DisplayAlerts = 0;
            dynamic doc = app.Documents.Open(src, false, true); // no conversion dialog, read only
            doc.ExportAsFixedFormat(dst, 17);                    // 17 = PDF
            doc.Close(false);
            return File.Exists(dst);
        }
        catch
        {
            return false;
        }
        finally
        {
            try { app.Quit(false); } catch { }
        }
    }

    private static bool TryLibreOffice(string src, string dst)
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe")
        };
        string? exe = candidates.FirstOrDefault(File.Exists);
        if (exe == null) return false;

        string tmp = Path.Combine(Path.GetTempPath(), "li_lo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add("pdf");
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add(src);
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(180000)) { try { p.Kill(true); } catch { } return false; }

            string made = Path.Combine(tmp, Path.GetFileNameWithoutExtension(src) + ".pdf");
            if (!File.Exists(made)) return false;
            File.Move(made, dst, true);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // ---------- Built-in renderer ----------

    private static void BuiltIn(string src, string dst, string ext)
    {
        FlowDocument doc;
        if (ext == ".rtf") doc = RtfDocument(src);
        else if (ext == ".docx") doc = TextDocument(DocxText(src));
        else if (ext is ".doc" or ".odt")
            throw new InvalidOperationException("Install Microsoft Word or LibreOffice to convert this file type");
        else doc = TextDocument(File.ReadAllText(src)); // txt, md, log, csv ... (UTF-8 / UTF-16 with a BOM is detected)

        const double PageW = 794, PageH = 1123; // A4 at 96 dpi
        doc.PageWidth = PageW;
        doc.PageHeight = PageH;
        doc.PagePadding = new Thickness(64);
        doc.ColumnWidth = 5000;

        var pag = ((IDocumentPaginatorSource)doc).DocumentPaginator;
        pag.PageSize = new Size(PageW, PageH);
        pag.ComputePageCount();
        int n = Math.Max(1, pag.PageCount);

        using var pdf = new PdfWriter(dst);
        for (int i = 0; i < n; i++)
        {
            using DocumentPage page = pag.GetPage(i);
            var rtb = new RenderTargetBitmap((int)(PageW * 2), (int)(PageH * 2), 192, 192, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (DrawingContext dc = bg.RenderOpen())
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageW, PageH));
            rtb.Render(bg);
            rtb.Render(page.Visual);
            rtb.Freeze();
            pdf.AddJpegPage(ImageOps.JpegBytes(rtb, 88), rtb.PixelWidth, rtb.PixelHeight, 595.28, 841.89);
            Worker.Progress((i + 1.0) / n);
        }
        pdf.Finish();
    }

    private static FlowDocument TextDocument(string text)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI, Nirmala UI, Arial"),
            FontSize = 14,
            Foreground = Brushes.Black
        };
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Length == 0 ? " " : raw.Replace("\t", "    ");
            var p = new Paragraph(new Run(line)) { Margin = new Thickness(0, 0, 0, 3) };
            if (IsRtl(line))
            {
                p.FlowDirection = FlowDirection.RightToLeft;
                p.TextAlignment = TextAlignment.Left; // "left" means "start" for right-to-left text
            }
            doc.Blocks.Add(p);
        }
        return doc;
    }

    private static bool IsRtl(string s)
    {
        foreach (char c in s)
        {
            if (c >= 0x0590 && c <= 0x08FF) return true;   // Hebrew, Arabic, Urdu, Syriac, Thaana...
            if (char.IsLetter(c)) return false;            // first letter decides
        }
        return false;
    }

    private static FlowDocument RtfDocument(string src)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI, Nirmala UI, Arial"), FontSize = 14 };
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var fs = File.OpenRead(src);
        range.Load(fs, DataFormats.Rtf);
        return doc;
    }

    private static string DocxText(string src)
    {
        using var zip = ZipFile.OpenRead(src);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidOperationException("This is not a valid DOCX file");
        XDocument x;
        using (var s = entry.Open()) x = XDocument.Load(s);

        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new StringBuilder();
        foreach (XElement p in x.Descendants(w + "p"))
        {
            foreach (XElement e in p.Descendants())
            {
                if (e.Name == w + "t") sb.Append(e.Value);
                else if (e.Name == w + "tab") sb.Append("    ");
                else if (e.Name == w + "br") sb.Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
