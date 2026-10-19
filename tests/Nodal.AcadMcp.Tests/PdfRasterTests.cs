using System.Text;
using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

/// <summary>
/// plot_preview's renderer, run against a hand-written PDF so it needs no AutoCAD: a 200 x 100 pt
/// page whose left half is filled black. Rendering to BMP lets the test read pixels back without an
/// image library. The renderer is raw WinRT ABI calls, so a wrong vtable slot shows up here as a
/// crash or garbage rather than in a user's preview.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
public sealed class PdfRasterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nodal-pdfraster-" + Guid.NewGuid().ToString("N"));

    public PdfRasterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240);

    [Fact]
    public void A_whole_page_renders_at_the_requested_size_with_the_right_pixels()
    {
        if (!Supported) return;
        var pdf = HalfBlackPdf();
        var bmp = Path.Combine(_dir, "whole.bmp");

        var result = PdfRaster.Render(pdf, bmp, 0, (w, h) => new PdfRenderPlan(0, 0, w, h, 200, 100), PdfRaster.BmpEncoder);

        Assert.Equal(1, result.PageCount);
        Assert.Equal(200.0 / 72 * 96, result.PageWidthDips, 1);   // DIPs are 1/96 inch, PDF points 1/72
        Assert.Equal(100.0 / 72 * 96, result.PageHeightDips, 1);
        var image = Bitmap.Read(bmp);
        Assert.Equal((200, 100), (image.Width, image.Height));
        Assert.True(image.IsDark(50, 50), "the left half should be black");
        Assert.False(image.IsDark(150, 50), "the right half should be white");
    }

    [Fact]
    public void A_source_rectangle_crops_the_page()
    {
        if (!Supported) return;
        var pdf = HalfBlackPdf();
        var right = Path.Combine(_dir, "right.bmp");
        var left = Path.Combine(_dir, "left.bmp");

        PdfRaster.Render(pdf, right, 0, (w, h) => new PdfRenderPlan(w / 2 + 2, 0, w / 2 - 2, h, 80, 80), PdfRaster.BmpEncoder);
        PdfRaster.Render(pdf, left, 0, (w, h) => new PdfRenderPlan(0, 0, w / 2 - 2, h, 80, 80), PdfRaster.BmpEncoder);

        var r = Bitmap.Read(right); var l = Bitmap.Read(left);
        Assert.Equal((80, 80), (r.Width, r.Height));
        Assert.False(r.IsDark(5, 5) || r.IsDark(75, 75) || r.IsDark(40, 40), "the right half has no ink");
        Assert.True(l.IsDark(5, 5) && l.IsDark(75, 75) && l.IsDark(40, 40), "the left half is all ink");
    }

    [Fact]
    public void Png_is_the_default_and_the_file_is_released_afterwards()
    {
        if (!Supported) return;
        var pdf = HalfBlackPdf();
        var png = Path.Combine(_dir, "out.png");

        PdfRaster.Render(pdf, png, 0, (w, h) => new PdfRenderPlan(0, 0, w, h, 321, 123));

        var bytes = File.ReadAllBytes(png);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        Assert.Equal(321, BigEndian(bytes, 16));   // IHDR width
        Assert.Equal(123, BigEndian(bytes, 20));   // IHDR height
        File.Delete(png);                          // throws if a stream still holds the file
        File.Delete(pdf);
        Assert.False(File.Exists(png));
    }

    [Fact]
    public void A_missing_page_is_an_error_not_a_blank_image()
    {
        if (!Supported) return;
        var pdf = HalfBlackPdf();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfRaster.Render(pdf, Path.Combine(_dir, "p2.png"), 1, (w, h) => new PdfRenderPlan(0, 0, w, h, 10, 10)));
    }

    [Fact]
    public void A_file_that_is_not_a_pdf_is_an_error()
    {
        if (!Supported) return;
        var bogus = Path.Combine(_dir, "bogus.pdf");
        File.WriteAllText(bogus, "this is not a PDF");
        Assert.ThrowsAny<Exception>(() =>
            PdfRaster.Render(bogus, Path.Combine(_dir, "b.png"), 0, (w, h) => new PdfRenderPlan(0, 0, w, h, 10, 10)));
    }

    private static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    /// <summary>A one-page PDF, 200 x 100 pt, with the left 100 x 100 pt filled black.</summary>
    private string HalfBlackPdf()
    {
        const string content = "0 0 0 rg 0 0 100 100 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        ];
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        var path = Path.Combine(_dir, "half.pdf");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        return path;
    }

    /// <summary>Just enough of a BMP reader for WIC's 24/32-bit output.</summary>
    private sealed record Bitmap(int Width, int Height, int Bpp, int Stride, bool TopDown, int Offset, byte[] Data)
    {
        public static Bitmap Read(string path)
        {
            var d = File.ReadAllBytes(path);
            Assert.Equal((byte)'B', d[0]);
            Assert.Equal((byte)'M', d[1]);
            var offset = BitConverter.ToInt32(d, 10);
            var width = BitConverter.ToInt32(d, 18);
            var height = BitConverter.ToInt32(d, 22);
            var bpp = BitConverter.ToInt16(d, 28);
            Assert.True(bpp is 24 or 32, $"unexpected {bpp} bpp");
            return new Bitmap(width, Math.Abs(height), bpp, (bpp * width + 31) / 32 * 4, height < 0, offset, d);
        }

        public bool IsDark(int x, int y)
        {
            var row = TopDown ? y : Height - 1 - y;
            var i = Offset + row * Stride + x * (Bpp / 8);
            return Data[i] + Data[i + 1] + Data[i + 2] < 3 * 64;
        }
    }
}
