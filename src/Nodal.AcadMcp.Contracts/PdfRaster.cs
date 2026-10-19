using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nodal.AcadMcp.Contracts;

/// <summary>What to render out of a page: a source rectangle in DIPs (top-left origin) and an output pixel size.</summary>
public readonly record struct PdfRenderPlan(double X, double Y, double Width, double Height, int PixelWidth, int PixelHeight);

public sealed record PdfRenderResult(int PageCount, double PageWidthDips, double PageHeightDips, int PixelWidth, int PixelHeight);

/// <summary>
/// Rasterises one PDF page with Windows' own PDF renderer (Windows.Data.Pdf) — the same engine
/// the drawing session drove from PowerShell — without a Windows SDK projection or any package.
///
/// The WinRT objects are reached through their raw ABI: activation factories from combase, file
/// streams from shcore's CreateRandomAccessStreamOnFile, and each method called through its vtable
/// slot. Interface IDs and slot orders were read out of the OS metadata (Windows.Data.winmd); the
/// six IInspectable/IUnknown slots come first in every vtable. Async operations are polled through
/// IAsyncInfo rather than given completion delegates, on a dedicated MTA thread so the caller's
/// apartment (AutoCAD's STA main thread, say) is never involved.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static class PdfRaster
{
    public static readonly Guid PngEncoder = new("27949969-876a-41d7-9447-568f6a35a4dc");
    public static readonly Guid BmpEncoder = new("69be8bb4-d66d-47c8-865a-ed1589433782");

    private static readonly Guid IidPdfDocumentStatics = new("433a0b5f-c007-4788-90f2-08143d922599");
    private static readonly Guid IidPdfPageRenderOptions = new("3c98056f-b7cf-4c29-9a04-52d90267f425");
    private static readonly Guid IidRandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    private static readonly Guid IidOutputStream = new("905a0fe6-bc53-11df-8c49-001e4fc686da");
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    private const int AsyncStarted = 0, AsyncCompleted = 1, AsyncCanceled = 2;

    /// <summary>
    /// Renders page <paramref name="pageIndex"/> (0-based) of <paramref name="pdfPath"/> into
    /// <paramref name="imagePath"/> (created or truncated). <paramref name="plan"/> receives the
    /// page size in DIPs (1/96 inch) and says what to render. PNG unless another WIC encoder is named.
    /// </summary>
    public static PdfRenderResult Render(string pdfPath, string imagePath, int pageIndex,
        Func<double, double, PdfRenderPlan> plan, Guid? encoder = null, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(120);
        PdfRenderResult? result = null;
        ExceptionDispatchInfo? error = null;

        var thread = new Thread(() =>
        {
            try { result = RenderCore(pdfPath, imagePath, pageIndex, plan, encoder ?? PngEncoder, limit); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true, Name = "Nodal PDF raster" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        if (!thread.Join(limit + TimeSpan.FromSeconds(5)))
            throw new TimeoutException($"Rendering the PDF page took longer than {limit.TotalSeconds:0} s.");
        error?.Throw();
        return result!;
    }

    private static PdfRenderResult RenderCore(string pdfPath, string imagePath, int pageIndex,
        Func<double, double, PdfRenderPlan> plan, Guid encoder, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        var initialized = RoInitialize(1) >= 0; // RO_INIT_MULTITHREADED; S_FALSE (already) counts too
        var owned = new List<IntPtr>();
        IntPtr Own(IntPtr p) { if (p != IntPtr.Zero) owned.Add(p); return p; }

        try
        {
            var iidStream = IidRandomAccessStream;
            Check(CreateRandomAccessStreamOnFile(pdfPath, 0, ref iidStream, out var input), "open the PDF");
            Own(input);

            var statics = Own(Factory("Windows.Data.Pdf.PdfDocument", IidPdfDocumentStatics));
            Check(Slot<PtrInPtrOut>(statics, 8)(statics, input, out var loadOp), "start loading the PDF"); // LoadFromStreamAsync
            Own(loadOp);
            Await(loadOp, deadline, "load the PDF");
            Check(Slot<PtrOut>(loadOp, 8)(loadOp, out var document), "load the PDF");                   // GetResults
            Own(document);

            Check(Slot<UIntOut>(document, 7)(document, out var pageCount), "count the PDF's pages");        // get_PageCount
            if (pageIndex < 0 || pageIndex >= pageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex), $"The PDF has {pageCount} page(s); page {pageIndex + 1} does not exist.");
            Check(Slot<UIntInPtrOut>(document, 6)(document, (uint)pageIndex, out var page), "open the page"); // GetPage
            Own(page);
            Check(Slot<SizeOut>(page, 10)(page, out var size), "read the page size");                       // get_Size

            var p = plan(size.Width, size.Height);
            if (p.PixelWidth <= 0 || p.PixelHeight <= 0 || !(p.Width > 0) || !(p.Height > 0))
                throw new ArgumentException("The render plan must have a positive source rectangle and pixel size.");

            var options = Own(Activate("Windows.Data.Pdf.PdfPageRenderOptions", IidPdfPageRenderOptions));
            Check(Slot<RectIn>(options, 7)(options, new WinRect((float)p.X, (float)p.Y, (float)p.Width, (float)p.Height)), "set the source rectangle");
            Check(Slot<UIntIn>(options, 9)(options, (uint)p.PixelWidth), "set the image width");
            Check(Slot<UIntIn>(options, 11)(options, (uint)p.PixelHeight), "set the image height");
            Check(Slot<ColorIn>(options, 13)(options, new WinColor(255, 255, 255, 255)), "set the background");
            Check(Slot<GuidIn>(options, 17)(options, encoder), "choose the image encoder");

            using (File.Create(imagePath)) { } // CreateRandomAccessStreamOnFile opens, it does not create
            Check(CreateRandomAccessStreamOnFile(imagePath, 1, ref iidStream, out var output), "open the image file");
            try
            {
                Check(Slot<PtrPtrInPtrOut>(page, 7)(page, output, options, out var renderOp), "start rendering"); // RenderToStreamAsync(stream, options)
                Own(renderOp);
                Await(renderOp, deadline, "render the page");
                Check(Slot<NoArgs>(renderOp, 8)(renderOp), "render the page");                                    // GetResults

                var iidOut = IidOutputStream;
                Check(Marshal.QueryInterface(output, in iidOut, out var outStream), "flush the image");
                try
                {
                    Check(Slot<PtrOut>(outStream, 7)(outStream, out var flushOp), "flush the image");          // FlushAsync
                    Own(flushOp);
                    Await(flushOp, deadline, "flush the image");
                }
                finally { Marshal.Release(outStream); }
            }
            finally { Marshal.Release(output); } // last reference: closes the file

            return new PdfRenderResult((int)pageCount, size.Width, size.Height, p.PixelWidth, p.PixelHeight);
        }
        finally
        {
            for (var i = owned.Count - 1; i >= 0; i--) Marshal.Release(owned[i]);
            if (initialized) RoUninitialize();
        }
    }

    // ---------------------------------------------------------------- WinRT plumbing

    private static IntPtr Factory(string className, Guid iid)
    {
        var name = HString(className);
        try
        {
            Check(RoGetActivationFactory(name, ref iid, out var factory), $"reach {className}");
            return factory;
        }
        finally { WindowsDeleteString(name); }
    }

    private static IntPtr Activate(string className, Guid iid)
    {
        var name = HString(className);
        try
        {
            Check(RoActivateInstance(name, out var inspectable), $"create {className}");
            try
            {
                Check(Marshal.QueryInterface(inspectable, in iid, out var typed), $"use {className}");
                return typed;
            }
            finally { Marshal.Release(inspectable); }
        }
        finally { WindowsDeleteString(name); }
    }

    private static IntPtr HString(string s)
    {
        Check(WindowsCreateString(s, (uint)s.Length, out var h), "create a WinRT string");
        return h;
    }

    /// <summary>Polls an IAsyncOperation/IAsyncAction to completion through its IAsyncInfo.</summary>
    private static void Await(IntPtr async, DateTime deadline, string what)
    {
        var iid = IidAsyncInfo;
        Check(Marshal.QueryInterface(async, in iid, out var info), what);
        try
        {
            var status = AsyncStarted;
            while (true)
            {
                Check(Slot<IntOut>(info, 7)(info, out status), what); // get_Status
                if (status != AsyncStarted) break;
                if (DateTime.UtcNow > deadline)
                {
                    Slot<NoArgs>(info, 9)(info);                       // Cancel
                    throw new TimeoutException($"Timed out trying to {what}.");
                }
                Thread.Sleep(2);
            }

            if (status == AsyncCompleted) return;
            if (status == AsyncCanceled) throw new OperationCanceledException($"Windows cancelled the attempt to {what}.");
            Check(Slot<IntOut>(info, 8)(info, out var hr), what);     // get_ErrorCode
            Check(hr == 0 ? unchecked((int)0x80004005) : hr, what);
        }
        finally { Marshal.Release(info); }
    }

    private static T Slot<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    private static void Check(int hr, string what)
    {
        if (hr >= 0) return;
        var inner = Marshal.GetExceptionForHR(hr);
        throw new InvalidOperationException($"Windows could not {what} (HRESULT 0x{hr:X8}: {inner?.Message.Trim()}).", inner);
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct WinRect(float X, float Y, float Width, float Height);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct WinSize(float Width, float Height);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct WinColor(byte A, byte R, byte G, byte B);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgs(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtrOut(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IntOut(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UIntOut(IntPtr self, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UIntIn(IntPtr self, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SizeOut(IntPtr self, out WinSize value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RectIn(IntPtr self, WinRect value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ColorIn(IntPtr self, WinColor value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GuidIn(IntPtr self, Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtrInPtrOut(IntPtr self, IntPtr arg, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UIntInPtrOut(IntPtr self, uint arg, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtrPtrInPtrOut(IntPtr self, IntPtr a, IntPtr b, out IntPtr result);

    [DllImport("combase.dll")] private static extern int RoInitialize(int initType);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")] private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, uint length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("shcore.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateRandomAccessStreamOnFile(string filePath, uint accessMode, ref Guid riid, out IntPtr stream);
}
