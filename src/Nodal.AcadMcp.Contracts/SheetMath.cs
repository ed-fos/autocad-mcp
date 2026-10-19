using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nodal.AcadMcp.Contracts;

/// <summary>A drawing scale as paper units per drawing units, e.g. 0.25 : 12 for 1/4" = 1'-0".</summary>
public readonly record struct ScaleRatio(double Paper, double Drawing)
{
    /// <summary>Paper units per drawing unit: what Viewport.CustomScale takes (1/48 for 1/4" = 1'-0").</summary>
    public double Factor => Paper / Drawing;
}

/// <summary>A rectangle with a bottom-left origin, in whatever units the caller is working in.</summary>
public readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
}

/// <summary>A paper size read out of a canonical media name such as ARCH_full_bleed_D_(36.00_x_24.00_Inches).</summary>
public readonly record struct MediaSize(string Name, double Width, double Height, string Unit)
{
    public double WidthInches => Unit == "MM" ? Width / 25.4 : Width;
    public double HeightInches => Unit == "MM" ? Height / 25.4 : Height;
}

/// <summary>Sizes a viewport resolved from what the caller supplied.</summary>
public readonly record struct ViewportSize(double Width, double Height, double Scale);

/// <summary>
/// The arithmetic behind the sheet-production tools: scale strings, viewport sizing, plot-region
/// and crop maths, media names and output file names. None of it touches AutoCAD, so all of it is
/// unit-tested; the tools only feed it numbers read from the drawing.
/// </summary>
public static class SheetMath
{
    private const double Tolerance = 1e-9;

    // ---------------------------------------------------------------- scales

    /// <summary>
    /// Parses a drawing scale. Accepted forms:
    /// <list type="bullet">
    /// <item>a ratio "paper:drawing" — "1:48", "1:100", "2:1";</item>
    /// <item>an equation of lengths in inches and feet — 1/4" = 1'-0", 3/32"=1'-0", 1" = 10', 1-1/2" = 1'-0";</item>
    /// <item>a bare factor (paper units per drawing unit) — "0.25", "1/48".</item>
    /// </list>
    /// Lengths in an equation are read as inches unless marked with a foot sign; a plain "1 = 48"
    /// is the same as "1:48". Throws <see cref="ArgumentException"/> for anything else.
    /// </summary>
    public static ScaleRatio ParseScale(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A scale cannot be blank.");
        var s = text.Trim();

        int sep = s.IndexOf(':');
        if (sep < 0) sep = s.IndexOf('=');
        if (sep >= 0)
        {
            var paper = ParseLength(s[..sep], text);
            var drawing = ParseLength(s[(sep + 1)..], text);
            return Checked(new ScaleRatio(paper, drawing), text);
        }

        var factor = ParseNumber(s) ?? throw Bad(text);
        return Checked(new ScaleRatio(factor, 1.0), text);
    }

    private static ScaleRatio Checked(ScaleRatio r, string text)
    {
        if (!(r.Paper > 0) || !(r.Drawing > 0) || !double.IsFinite(r.Factor) || r.Factor <= 0)
            throw new ArgumentException($"Scale '{text}' must have two positive sides.");
        return r;
    }

    /// <summary>A length in inches: 1'-0", 10', 1/4", 1-1/2", 2'-6 1/2", 0.25 (bare = inches).</summary>
    public static double ParseLength(string part, string? context = null)
    {
        var s = part.Trim().Replace('′', '\'').Replace('″', '"').Replace("''", "\"");
        if (s.Length == 0) throw Bad(context ?? part);

        double feet = 0;
        var foot = s.IndexOf('\'');
        if (foot >= 0)
        {
            feet = ParseNumber(s[..foot]) ?? throw Bad(context ?? part);
            s = s[(foot + 1)..].Trim();
            if (s.StartsWith('-')) s = s[1..].Trim();
        }

        if (s.EndsWith('"')) s = s[..^1].Trim();
        else if (s.Contains('"')) throw Bad(context ?? part);

        var inches = s.Length == 0 ? 0 : ParseNumber(s) ?? throw Bad(context ?? part);
        if (feet < 0 || inches < 0) throw Bad(context ?? part);
        return feet * 12 + inches;
    }

    private static readonly Regex Mixed = new(@"^(\d+)[\s-]+(\d+)\s*/\s*(\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Fraction = new(@"^(\d+(?:\.\d+)?)\s*/\s*(\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant);

    /// <summary>A decimal, a fraction (3/16) or a mixed number (1-1/2, 1 1/2); null when it is none of them.</summary>
    private static double? ParseNumber(string text)
    {
        var s = text.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return double.IsFinite(d) ? d : null;

        var m = Mixed.Match(s);
        if (m.Success)
        {
            var den = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            return den == 0 ? null
                : double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
                  + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / den;
        }

        m = Fraction.Match(s);
        if (m.Success)
        {
            var den = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return den == 0 ? null : double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / den;
        }

        return null;
    }

    private static ArgumentException Bad(string text) =>
        new($"'{text}' is not a scale. Use a ratio (\"1:48\"), an architectural scale " +
            "(\"1/4\\\" = 1'-0\\\"\"), or a factor (\"0.25\").");

    /// <summary>True when two scale factors are the same within a part in a million.</summary>
    public static bool SameScale(double a, double b) =>
        a > 0 && b > 0 && Math.Abs(a - b) <= 1e-6 * Math.Max(a, b);

    // ---------------------------------------------------------------- viewports

    /// <summary>
    /// Resolves a viewport's paper size and scale from what the caller gave. The scale comes from
    /// <paramref name="scale"/>, or from <paramref name="viewHeight"/> (model units shown top to
    /// bottom), or — when both a named view and a full paper size are given — from fitting the view
    /// into it. Missing paper sizes come from the view's size times the scale.
    /// </summary>
    public static ViewportSize ResolveViewport(double? viewWidth, double? viewHeight, double? width,
        double? height, double? scale, double? shownHeight)
    {
        if (width is <= 0 || height is <= 0) throw new ArgumentException("width and height must be positive.");
        if (scale is <= 0) throw new ArgumentException("scale must be positive.");
        if (shownHeight is <= 0) throw new ArgumentException("viewHeight must be positive.");
        if (scale is not null && shownHeight is not null)
            throw new ArgumentException("Give either scale or viewHeight, not both.");
        var hasView = viewWidth is > 0 && viewHeight is > 0;

        double s;
        if (scale is { } given) s = given;
        else if (shownHeight is { } vh)
        {
            if (height is null) throw new ArgumentException("viewHeight needs the viewport's paper height.");
            s = height.Value / vh;
        }
        else if (hasView && width is { } w0 && height is { } h0)
            s = Math.Min(w0 / viewWidth!.Value, h0 / viewHeight!.Value);
        else
            throw new ArgumentException("Give a scale (or viewHeight), or a named view plus both width and height to fit it.");

        double w = width ?? (hasView ? viewWidth!.Value * s : throw new ArgumentException("width is required without a named view."));
        double h = height ?? (hasView ? viewHeight!.Value * s : throw new ArgumentException("height is required without a named view."));
        return new ViewportSize(w, h, s);
    }

    // ---------------------------------------------------------------- plot regions and crops

    /// <summary>
    /// Where a centred window plot lands on the page: the window scaled by <paramref name="scale"/>
    /// (paper units per drawing unit; null = scale to fit the printable area), centred on the
    /// printable area. All page values in one unit, bottom-left origin.
    /// </summary>
    public static (Box Region, double Scale) CenteredPlotRegion(Box printable, double windowWidth, double windowHeight, double? scale)
    {
        if (!(windowWidth > 0) || !(windowHeight > 0)) throw new ArgumentException("The plot window must have a positive size.");
        if (!(printable.Width > 0) || !(printable.Height > 0)) throw new ArgumentException("The printable area must have a positive size.");

        var s = scale ?? Math.Min(printable.Width / windowWidth, printable.Height / windowHeight);
        var w = windowWidth * s; var h = windowHeight * s;
        var cx = (printable.MinX + printable.MaxX) / 2; var cy = (printable.MinY + printable.MaxY) / 2;
        return (new Box(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), s);
    }

    /// <summary>
    /// Narrows <paramref name="region"/> by crop fractions [minX, minY, maxX, maxY] of itself
    /// (0..1, bottom-left origin) and clips the result to the page.
    /// </summary>
    public static Box Crop(Box region, double pageWidth, double pageHeight, double[]? fractions)
    {
        var r = region;
        if (fractions is not null)
        {
            if (fractions.Length != 4) throw new ArgumentException("crop must be [minX, minY, maxX, maxY] fractions.");
            var (a, b, c, d) = (fractions[0], fractions[1], fractions[2], fractions[3]);
            if (a < 0 || b < 0 || c > 1 || d > 1 || c - a < 1e-6 || d - b < 1e-6)
                throw new ArgumentException("crop fractions must satisfy 0 <= minX < maxX <= 1 and 0 <= minY < maxY <= 1.");
            r = new Box(region.MinX + a * region.Width, region.MinY + b * region.Height,
                        region.MinX + c * region.Width, region.MinY + d * region.Height);
        }

        var clipped = new Box(Math.Max(0, r.MinX), Math.Max(0, r.MinY), Math.Min(pageWidth, r.MaxX), Math.Min(pageHeight, r.MaxY));
        if (clipped.Width <= Tolerance || clipped.Height <= Tolerance)
            throw new ArgumentException("The requested area lies outside the plotted page.");
        return clipped;
    }

    /// <summary>
    /// Converts a bottom-left-origin box on a page into the top-left-origin rectangle a PDF
    /// renderer takes, scaled from page units into renderer units (DIPs, say).
    /// </summary>
    public static (double X, double Y, double Width, double Height) ToTopLeft(Box box, double pageWidth, double pageHeight,
        double renderWidth, double renderHeight)
    {
        var sx = renderWidth / pageWidth; var sy = renderHeight / pageHeight;
        return (box.MinX * sx, (pageHeight - box.MaxY) * sy, box.Width * sx, box.Height * sy);
    }

    /// <summary>
    /// Pixel size for an image of a region: from the width, the height, or the largest size that
    /// fits inside both, keeping the region's aspect ratio. Bounded so a typo cannot ask for a
    /// gigapixel bitmap.
    /// </summary>
    public static (int Width, int Height) FitPixels(double regionWidth, double regionHeight, int? width, int? height,
        int defaultWidth = 1600, int maxSide = 12000, long maxPixels = 60_000_000)
    {
        if (!(regionWidth > 0) || !(regionHeight > 0)) throw new ArgumentException("The image region must have a positive size.");
        if (width is <= 0 || height is <= 0) throw new ArgumentException("width and height must be positive.");
        if (width > maxSide || height > maxSide) throw new ArgumentException($"width and height are limited to {maxSide} pixels.");

        var aspect = regionWidth / regionHeight;
        double w, h;
        if (width is { } fw && height is { } fh)
        {
            w = fw; h = w / aspect;
            if (h > fh) { h = fh; w = h * aspect; }
        }
        else if (width is { } ow) { w = ow; h = w / aspect; }
        else if (height is { } oh) { h = oh; w = h * aspect; }
        else { w = defaultWidth; h = w / aspect; }

        var pw = Math.Max(1, (int)Math.Round(w)); var ph = Math.Max(1, (int)Math.Round(h));
        if (pw > maxSide || ph > maxSide || (long)pw * ph > maxPixels)
            throw new ArgumentException($"A {pw} x {ph} image is too large; keep each side under {maxSide} and the total under {maxPixels / 1_000_000} megapixels.");
        return (pw, ph);
    }

    // ---------------------------------------------------------------- media

    private static readonly Regex MediaDims = new(
        @"\((\d+(?:\.\d+)?)_x_(\d+(?:\.\d+)?)_(Inches|MM|Pixels)\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The paper size spelled into a canonical media name, or null when the name has none.</summary>
    public static MediaSize? ParseMediaSize(string canonicalName)
    {
        var m = MediaDims.Match(canonicalName);
        if (!m.Success) return null;
        var unit = m.Groups[3].Value.ToUpperInvariant() switch { "INCHES" => "Inches", "MM" => "MM", _ => "Pixels" };
        return new MediaSize(canonicalName,
            double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), unit);
    }

    /// <summary>
    /// Folds the difference between a canonical media name (ARCH_full_bleed_D_(36.00_x_24.00_Inches))
    /// and its display name (ARCH full bleed D (36.00 x 24.00 Inches)) so either can be matched.
    /// </summary>
    public static string MediaKey(string name)
    {
        var sb = new StringBuilder(name.Length);
        var space = false;
        foreach (var ch in name.Trim())
        {
            var c = ch == '_' ? ' ' : char.ToLowerInvariant(ch);
            if (char.IsWhiteSpace(c)) { if (!space && sb.Length > 0) sb.Append(' '); space = true; continue; }
            space = false;
            sb.Append(c);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Picks a full-bleed media for a window preview: the smallest one, in the same orientation,
    /// that holds <paramref name="widthInches"/> x <paramref name="heightInches"/>; failing that the
    /// largest one in that orientation; failing that the largest of any. Null when no full-bleed
    /// media with a readable size exists.
    /// </summary>
    public static MediaSize? ChoosePreviewMedia(IEnumerable<string> canonicalNames, double widthInches, double heightInches)
    {
        var all = canonicalNames
            .Where(n => n.Contains("full_bleed", StringComparison.OrdinalIgnoreCase))
            .Select(ParseMediaSize)
            .Where(m => m is { Unit: "Inches" or "MM" })
            .Select(m => m!.Value)
            .ToList();
        if (all.Count == 0) return null;

        var landscape = widthInches >= heightInches;
        var oriented = all.Where(m => (m.WidthInches >= m.HeightInches) == landscape).ToList();
        var pool = oriented.Count > 0 ? oriented : all;

        var fits = pool.Where(m => m.WidthInches + 1e-6 >= widthInches && m.HeightInches + 1e-6 >= heightInches)
                       .OrderBy(m => m.WidthInches * m.HeightInches).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
        if (fits.Count > 0) return fits[0];
        return pool.OrderByDescending(m => m.WidthInches * m.HeightInches).ThenBy(m => m.Name, StringComparer.Ordinal).First();
    }

    /// <summary>True for a plot configuration that writes PDF files (DWG To PDF.pc3, AutoCAD PDF (…).pc3).</summary>
    public static bool IsPdfDevice(string? device) =>
        !string.IsNullOrWhiteSpace(device)
        && device.EndsWith(".pc3", StringComparison.OrdinalIgnoreCase)
        && device.Contains("PDF", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- file names

    private static readonly Regex Token = new(@"\{([a-zA-Z]+)\}", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Expands a per-sheet PDF file name. Tokens: {layout}, {index} (1-based position in this
    /// plot, zero-padded to the sheet count's width), {tab} (the layout's tab order) and {drawing}
    /// (the drawing's file name without extension). Characters Windows forbids become '_' and
    /// ".pdf" is appended when missing. An unknown token is an error rather than literal text.
    /// </summary>
    public static string ExpandFileName(string pattern, string layout, int index, int count, int tabOrder, string drawing)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("fileNamePattern cannot be blank.");
        var width = Math.Max(1, count.ToString(CultureInfo.InvariantCulture).Length);
        var name = Token.Replace(pattern, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "layout" => layout,
            "index" => index.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'),
            "tab" => tabOrder.ToString(CultureInfo.InvariantCulture),
            "drawing" => drawing,
            _ => throw new ArgumentException($"Unknown file name token '{m.Value}'. Use {{layout}}, {{index}}, {{tab}} or {{drawing}}."),
        });
        if (name.Contains('{') || name.Contains('}'))
            throw new ArgumentException("fileNamePattern has an unclosed or empty {token}.");

        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var clean = sb.ToString().Trim().TrimEnd('.', ' ');
        if (clean.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4].TrimEnd('.', ' ');
        if (clean.Length == 0) throw new ArgumentException("fileNamePattern expands to an empty file name.");
        if (Reserved.Contains(clean.Split('.')[0])) clean = "_" + clean;
        return clean + ".pdf";
    }
}
