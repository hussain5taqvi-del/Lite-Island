using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LiteIsland;

/// <summary>
/// Tiny streaming PDF writer: one JPEG picture per page. No library, almost no RAM
/// (each page is written to the file at once, only the object offsets are kept).
/// </summary>
internal sealed class PdfWriter : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly FileStream _o;
    // index = object number. 0 is unused, 1 = catalog, 2 = page tree (both written at the end)
    private readonly List<long> _offsets = new() { 0, 0, 0 };
    private readonly List<int> _pages = new();
    private bool _finished;

    public PdfWriter(string path)
    {
        _o = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        Text("%PDF-1.4\n");
        _o.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);
    }

    public int PageCount => _pages.Count;

    private void Text(string s)
    {
        byte[] b = Encoding.ASCII.GetBytes(s);
        _o.Write(b, 0, b.Length);
    }

    private int NewObj()
    {
        _offsets.Add(_o.Position);
        int n = _offsets.Count - 1;
        Text(n.ToString(Inv) + " 0 obj\n");
        return n;
    }

    /// <param name="jpeg">3-channel (RGB) JPEG bytes</param>
    public void AddJpegPage(byte[] jpeg, int pxW, int pxH, double ptW, double ptH)
    {
        int img = NewObj();
        Text("<< /Type /XObject /Subtype /Image /Width " + pxW.ToString(Inv) + " /Height " + pxH.ToString(Inv) +
             " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpeg.Length.ToString(Inv) +
             " >>\nstream\n");
        _o.Write(jpeg, 0, jpeg.Length);
        Text("\nendstream\nendobj\n");

        string content = "q " + ptW.ToString("0.##", Inv) + " 0 0 " + ptH.ToString("0.##", Inv) + " 0 0 cm /Im0 Do Q";
        int c = NewObj();
        Text("<< /Length " + content.Length.ToString(Inv) + " >>\nstream\n" + content + "\nendstream\nendobj\n");

        int p = NewObj();
        Text("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + ptW.ToString("0.##", Inv) + " " + ptH.ToString("0.##", Inv) +
             "] /Resources << /XObject << /Im0 " + img.ToString(Inv) + " 0 R >> >> /Contents " + c.ToString(Inv) +
             " 0 R >>\nendobj\n");
        _pages.Add(p);
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;

        _offsets[2] = _o.Position;
        var kids = new StringBuilder();
        foreach (int p in _pages) kids.Append(p.ToString(Inv)).Append(" 0 R ");
        Text("2 0 obj\n<< /Type /Pages /Count " + _pages.Count.ToString(Inv) + " /Kids [ " + kids + "] >>\nendobj\n");

        _offsets[1] = _o.Position;
        Text("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        long xref = _o.Position;
        Text("xref\n0 " + _offsets.Count.ToString(Inv) + "\n");
        Text("0000000000 65535 f \n");
        for (int i = 1; i < _offsets.Count; i++)
            Text(_offsets[i].ToString("D10", Inv) + " 00000 n \n");
        Text("trailer\n<< /Size " + _offsets.Count.ToString(Inv) + " /Root 1 0 R >>\nstartxref\n" +
             xref.ToString(Inv) + "\n%%EOF\n");
        _o.Flush();
    }

    public void Dispose()
    {
        try { Finish(); } catch { }
        _o.Dispose();
    }
}
