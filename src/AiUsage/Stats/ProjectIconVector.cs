using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace AiUsage.Stats;

// The vector half of the loader: a small, safe subset of SVG and of Android's vector drawable and
// adaptive icon, built into frozen drawings. Text, scripts, images, filters and references to other
// files are never followed, except the drawables an adaptive icon names inside its own res folder.
internal static partial class ProjectIconLoader
{
    private const int MaxElements = 4000;
    private const int MaxDepth = 24;
    private const int MaxPathChars = 200_000;
    private const double AdaptiveSize = 108;
    private const double AdaptiveInset = 18;
    private const int LayerPixelWidth = 256;

    private static readonly Regex NumberPattern = new(@"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant);
    private static readonly Regex TransformPattern = new(@"(\w+)\s*\(([^)]*)\)", RegexOptions.CultureInvariant);
    private static readonly Regex AndroidResourcePattern = new(@"^@(?:drawable|mipmap)/([A-Za-z0-9_]+)$", RegexOptions.CultureInvariant);
    private static readonly string[] Densities = ["xxxhdpi", "xxhdpi", "xhdpi", "hdpi", "mdpi"];

    /// <summary>One drawing in its own square <see cref="Viewport"/>, with the hues it shows (each
    /// fill weighed by the share of the viewport its shape covers).</summary>
    private sealed record Layer(Drawing Drawing, Rect Viewport, HueHistogram Hues)
    {
        public DrawingImage ToImage()
        {
            var image = new DrawingImage(Drawing);
            image.Freeze();
            return image;
        }
    }

    private static Layer? LoadVector(string path)
    {
        var root = ReadXml(path)?.Root;
        return root?.Name.LocalName switch
        {
            "svg" => ParseSvg(root),
            "vector" => ParseAndroidVector(root, path),
            "adaptive-icon" => ParseAdaptiveIcon(root, path),
            _ => null,
        };
    }

    // DTDs are skipped rather than refused (many exported SVGs carry a doctype), and never resolved,
    // so no entity or external file is ever read.
    private static XDocument? ReadXml(string path)
    {
        if (!IsSafeFile(path))
            return null;

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = MaxFileBytes,
            };
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- shared helpers ----

    private static string? Get(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static double Number(string? text, double fallback)
    {
        if (text is null)
            return fallback;
        var match = NumberPattern.Match(text);
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : fallback;
    }

    private static List<double> Numbers(string? text) =>
        text is null
            ? []
            : NumberPattern.Matches(text)
                .Select(match => double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
                .Where(double.IsFinite)
                .ToList();

    private static Color? ParseHex(string text, bool alphaFirst)
    {
        var hex = text.Trim();
        if (hex.Length < 2 || hex[0] != '#' || !hex.Skip(1).All(Uri.IsHexDigit))
            return null;

        hex = hex[1..];
        if (hex.Length is 3 or 4)
            hex = string.Concat(hex.Select(digit => new string(digit, 2)));
        if (hex.Length is not (6 or 8))
            return null;

        byte Pair(int index) => Convert.ToByte(hex.Substring(index * 2, 2), 16);
        if (hex.Length == 6)
            return Color.FromRgb(Pair(0), Pair(1), Pair(2));
        return alphaFirst
            ? Color.FromArgb(Pair(0), Pair(1), Pair(2), Pair(3))
            : Color.FromArgb(Pair(3), Pair(0), Pair(1), Pair(2));
    }

    // Only plain words reach the named-color lookup: the converter also reads color profile files.
    private static Color? ParseCssColor(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('#'))
            return ParseHex(value, alphaFirst: false);

        if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var parts = Numbers(value);
            if (parts.Count < 3)
                return null;
            double Channel(double v) => value.Contains('%') ? v * 2.55 : v;
            byte Clamp(double v) => (byte)Math.Clamp(v + 0.5, 0, 255);
            var alpha = parts.Count > 3 ? Math.Clamp(parts[3], 0, 1) : 1;
            return Color.FromArgb(Clamp(alpha * 255), Clamp(Channel(parts[0])), Clamp(Channel(parts[1])), Clamp(Channel(parts[2])));
        }

        if (!value.All(char.IsAsciiLetter))
            return null;
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return Colors.Transparent;

        try
        {
            return ColorConverter.ConvertFromString(value) is Color named ? named : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Color WithAlpha(Color color, double factor) =>
        Color.FromArgb((byte)Math.Clamp(color.A * factor + 0.5, 0, 255), color.R, color.G, color.B);

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Geometry? ParsePathData(string? data, bool evenOdd)
    {
        if (string.IsNullOrWhiteSpace(data) || data.Length > MaxPathChars)
            return null;

        try
        {
            var geometry = Geometry.Parse(data);
            if (geometry.IsFrozen)
                geometry = geometry.CloneCurrentValue();
            var rule = evenOdd ? FillRule.EvenOdd : FillRule.Nonzero;
            if (geometry is StreamGeometry stream)
                stream.FillRule = rule;
            else if (geometry is PathGeometry path)
                path.FillRule = rule;
            return geometry;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            // One path the parser refuses costs only that path.
            return null;
        }
    }

    /// <summary>The shapes of one icon, in the order they are drawn.</summary>
    private sealed class ShapeSink
    {
        private readonly List<Drawing> _drawings = [];
        private readonly List<(Color Color, double Area)> _fills = [];
        private Rect _union = Rect.Empty;

        public int Count => _drawings.Count;

        public void Add(Geometry geometry, Matrix matrix, Color? fill, Color? stroke, double strokeWidth)
        {
            var filled = fill is { A: > 0 };
            var stroked = stroke is { A: > 0 } && strokeWidth > 0;
            if ((!filled && !stroked) || _drawings.Count >= MaxElements)
                return;

            if (geometry.IsFrozen)
                geometry = geometry.CloneCurrentValue();
            if (!matrix.IsIdentity)
                geometry.Transform = new MatrixTransform(matrix);
            geometry.Freeze();

            var bounds = geometry.Bounds;
            if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
                return;

            Pen? pen = null;
            if (stroked)
            {
                // The geometry's own transform moves its points but not the pen, so the width follows by hand.
                pen = new Pen(FrozenBrush(stroke!.Value), strokeWidth * Math.Sqrt(Math.Abs(matrix.Determinant)))
                {
                    LineJoin = PenLineJoin.Round,
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                pen.Freeze();
            }

            var drawing = new GeometryDrawing(filled ? FrozenBrush(fill!.Value) : null, pen, geometry);
            drawing.Freeze();
            _drawings.Add(drawing);
            _union.Union(bounds);

            var area = bounds.Width * bounds.Height;
            // A line drawing covers far less than its bounds.
            _fills.Add(filled ? (fill!.Value, area) : (stroke!.Value, area * 0.25));
        }

        public Layer? Finish(Rect? declaredViewport)
        {
            if (_drawings.Count == 0)
                return null;

            var viewport = declaredViewport ?? _union;
            var side = Math.Max(viewport.Width, viewport.Height);
            if (viewport.IsEmpty || !double.IsFinite(side) || side <= 0)
                return null;

            // Drawn into a square so a wide or tall viewport is never stretched.
            var square = new Rect(
                viewport.X + (viewport.Width - side) / 2, viewport.Y + (viewport.Height - side) / 2, side, side);
            var group = new DrawingGroup { ClipGeometry = FrozenRectangle(square) };
            // Pins the group's bounds to the viewport, so the content never decides the scale.
            group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, FrozenRectangle(square)));
            foreach (var drawing in _drawings)
                group.Children.Add(drawing);
            group.Freeze();

            var hues = new HueHistogram();
            foreach (var (color, area) in _fills)
                hues.Add(color, Math.Min(1.0, area / (side * side)));
            return new Layer(group, square, hues);
        }
    }

    private static RectangleGeometry FrozenRectangle(Rect rect)
    {
        var geometry = new RectangleGeometry(rect);
        geometry.Freeze();
        return geometry;
    }

    // ---- SVG ----

    private sealed record SvgStyle(Color? Fill, double FillOpacity, Color? Stroke, double StrokeWidth, double StrokeOpacity, double Opacity, bool EvenOdd)
    {
        public static readonly SvgStyle Default = new(Colors.Black, 1, null, 1, 1, 1, false);
    }

    private sealed class SvgContext
    {
        public Dictionary<string, Color> Gradients { get; init; } = [];
        public ShapeSink Sink { get; } = new();
        public int Elements;
    }

    private static Layer? ParseSvg(XElement root)
    {
        var context = new SvgContext { Gradients = CollectGradients(root) };
        WalkSvg(root, SvgStyle.Default, Matrix.Identity, 0, context);
        return context.Sink.Finish(SvgViewport(root));
    }

    private static Rect? SvgViewport(XElement root)
    {
        var box = Numbers(Get(root, "viewBox"));
        if (box.Count == 4 && box[2] > 0 && box[3] > 0)
            return new Rect(box[0], box[1], box[2], box[3]);

        var widthText = Get(root, "width");
        var heightText = Get(root, "height");
        if (widthText is null || heightText is null || widthText.Contains('%') || heightText.Contains('%'))
            return null;
        var width = Number(widthText, 0);
        var height = Number(heightText, 0);
        return width > 0 && height > 0 ? new Rect(0, 0, width, height) : null;
    }

    private static Dictionary<string, Color> CollectGradients(XElement root)
    {
        var own = new Dictionary<string, Color>();
        var links = new Dictionary<string, string>();
        foreach (var gradient in root.Descendants().Where(e => e.Name.LocalName is "linearGradient" or "radialGradient"))
        {
            var id = Get(gradient, "id");
            if (string.IsNullOrEmpty(id))
                continue;

            var stop = gradient.Elements().FirstOrDefault(e => e.Name.LocalName == "stop");
            if (stop is not null)
            {
                var props = Properties(stop);
                var color = props.TryGetValue("stop-color", out var text) ? ParseCssColor(text) : Colors.Black;
                own[id] = WithAlpha(color ?? Colors.Black, Number(props.GetValueOrDefault("stop-opacity"), 1));
            }
            else if (Get(gradient, "href") is { } href && href.StartsWith('#'))
            {
                links[id] = href[1..];
            }
        }

        // A gradient that only points at another one takes that one's first stop.
        foreach (var (id, first) in links)
        {
            var target = first;
            for (var hop = 0; hop < 3 && !own.ContainsKey(id); hop++)
            {
                if (own.TryGetValue(target, out var color))
                    own[id] = color;
                else if (links.TryGetValue(target, out var next))
                    target = next;
                else
                    break;
            }
        }

        return own;
    }

    private static Dictionary<string, string> Properties(XElement element)
    {
        var properties = new Dictionary<string, string>();
        foreach (var attribute in element.Attributes().Where(a => a.Name.NamespaceName.Length == 0))
            properties[attribute.Name.LocalName] = attribute.Value;

        if (properties.TryGetValue("style", out var style))
        {
            foreach (var part in style.Split(';'))
            {
                var colon = part.IndexOf(':');
                if (colon > 0)
                    properties[part[..colon].Trim()] = part[(colon + 1)..].Replace("!important", "").Trim();
            }
        }

        return properties;
    }

    // False leaves the inherited paint alone.
    private static bool TryParsePaint(string value, Dictionary<string, Color> gradients, out Color? paint)
    {
        paint = null;
        var text = value.Trim();
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            paint = Colors.Black;
            return true;
        }

        if (text.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var hash = text.IndexOf('#');
            var end = text.IndexOf(')');
            if (hash > 0 && end > hash && gradients.TryGetValue(text[(hash + 1)..end].Trim('\'', '"', ' '), out var gradient))
                paint = gradient;
            return true;
        }

        var color = ParseCssColor(text);
        paint = color;
        return color is not null;
    }

    private static SvgStyle InheritStyle(SvgStyle parent, Dictionary<string, string> props, Dictionary<string, Color> gradients)
    {
        var style = parent;
        if (props.TryGetValue("fill", out var fill) && TryParsePaint(fill, gradients, out var fillPaint))
            style = style with { Fill = fillPaint };
        if (props.TryGetValue("stroke", out var stroke) && TryParsePaint(stroke, gradients, out var strokePaint))
            style = style with { Stroke = strokePaint };
        if (props.TryGetValue("fill-opacity", out var fillOpacity))
            style = style with { FillOpacity = Math.Clamp(Number(fillOpacity, 1), 0, 1) };
        if (props.TryGetValue("stroke-opacity", out var strokeOpacity))
            style = style with { StrokeOpacity = Math.Clamp(Number(strokeOpacity, 1), 0, 1) };
        if (props.TryGetValue("stroke-width", out var strokeWidth))
            style = style with { StrokeWidth = Math.Max(0, Number(strokeWidth, 1)) };
        if (props.TryGetValue("fill-rule", out var rule))
            style = style with { EvenOdd = rule.Trim().Equals("evenodd", StringComparison.OrdinalIgnoreCase) };
        if (props.TryGetValue("opacity", out var opacity))
            style = style with { Opacity = style.Opacity * Math.Clamp(Number(opacity, 1), 0, 1) };
        return style;
    }

    private static void WalkSvg(XElement element, SvgStyle parent, Matrix parentMatrix, int depth, SvgContext context)
    {
        if (depth > MaxDepth || ++context.Elements > MaxElements)
            return;

        var props = Properties(element);
        if (props.GetValueOrDefault("display")?.Trim() == "none" || props.GetValueOrDefault("visibility")?.Trim() is "hidden" or "collapse")
            return;

        var style = InheritStyle(parent, props, context.Gradients);
        var matrix = ParseSvgTransform(props.GetValueOrDefault("transform")) * parentMatrix;
        var name = element.Name.LocalName;

        if (name is "svg" or "g" or "a" or "switch")
        {
            foreach (var child in element.Elements())
                WalkSvg(child, style, matrix, depth + 1, context);
            return;
        }

        var geometry = SvgGeometry(element, name, style.EvenOdd);
        if (geometry is null)
            return;

        var fill = style.Fill is { } f ? WithAlpha(f, style.FillOpacity * style.Opacity) : (Color?)null;
        var stroke = style.Stroke is { } s ? WithAlpha(s, style.StrokeOpacity * style.Opacity) : (Color?)null;
        context.Sink.Add(geometry, matrix, fill, stroke, style.StrokeWidth);
    }

    private static Geometry? SvgGeometry(XElement element, string name, bool evenOdd)
    {
        switch (name)
        {
            case "path":
                return ParsePathData(Get(element, "d"), evenOdd);
            case "rect":
            {
                var width = Number(Get(element, "width"), 0);
                var height = Number(Get(element, "height"), 0);
                if (width <= 0 || height <= 0)
                    return null;
                var rx = Number(Get(element, "rx"), double.NaN);
                var ry = Number(Get(element, "ry"), double.NaN);
                rx = double.IsNaN(rx) ? (double.IsNaN(ry) ? 0 : ry) : rx;
                ry = double.IsNaN(ry) ? rx : ry;
                return new RectangleGeometry(
                    new Rect(Number(Get(element, "x"), 0), Number(Get(element, "y"), 0), width, height),
                    Math.Clamp(rx, 0, width / 2), Math.Clamp(ry, 0, height / 2));
            }
            case "circle":
            {
                var radius = Number(Get(element, "r"), 0);
                return radius > 0
                    ? new EllipseGeometry(new Point(Number(Get(element, "cx"), 0), Number(Get(element, "cy"), 0)), radius, radius)
                    : null;
            }
            case "ellipse":
            {
                var rx = Number(Get(element, "rx"), 0);
                var ry = Number(Get(element, "ry"), 0);
                return rx > 0 && ry > 0
                    ? new EllipseGeometry(new Point(Number(Get(element, "cx"), 0), Number(Get(element, "cy"), 0)), rx, ry)
                    : null;
            }
            case "polygon":
            case "polyline":
                return PointsGeometry(Numbers(Get(element, "points")), closed: name == "polygon", evenOdd);
            default:
                return null;
        }
    }

    private static StreamGeometry? PointsGeometry(List<double> numbers, bool closed, bool evenOdd)
    {
        if (numbers.Count < 4)
            return null;

        var points = new List<Point>();
        for (var i = 0; i + 1 < numbers.Count; i += 2)
            points.Add(new Point(numbers[i], numbers[i + 1]));

        var geometry = new StreamGeometry { FillRule = evenOdd ? FillRule.EvenOdd : FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true, isClosed: closed);
            context.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: false);
        }

        return geometry;
    }

    // The listed transforms apply right to left, so each new one is applied before the earlier ones.
    private static Matrix ParseSvgTransform(string? text)
    {
        var result = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (Match match in TransformPattern.Matches(text))
        {
            var n = Numbers(match.Groups[2].Value);
            var step = Matrix.Identity;
            switch (match.Groups[1].Value)
            {
                case "translate" when n.Count >= 1:
                    step.Translate(n[0], n.Count > 1 ? n[1] : 0);
                    break;
                case "scale" when n.Count >= 1:
                    step.Scale(n[0], n.Count > 1 ? n[1] : n[0]);
                    break;
                case "rotate" when n.Count >= 1:
                    step.RotateAt(n[0], n.Count > 2 ? n[1] : 0, n.Count > 2 ? n[2] : 0);
                    break;
                case "matrix" when n.Count >= 6:
                    step = new Matrix(n[0], n[1], n[2], n[3], n[4], n[5]);
                    break;
                case "skewX" when n.Count >= 1:
                    step = new Matrix(1, 0, Math.Tan(n[0] * Math.PI / 180), 1, 0, 0);
                    break;
                case "skewY" when n.Count >= 1:
                    step = new Matrix(1, Math.Tan(n[0] * Math.PI / 180), 0, 1, 0, 0);
                    break;
            }

            result = step * result;
        }

        return result;
    }

    // ---- Android vector drawable ----

    private sealed class AndroidColors(string? resFolder)
    {
        private Dictionary<string, string>? _named;

        public Color? Resolve(string? value, int depth = 0)
        {
            if (string.IsNullOrWhiteSpace(value) || depth > 3)
                return null;

            var text = value.Trim();
            if (text.StartsWith('#'))
                return ParseHex(text, alphaFirst: true);
            if (!text.StartsWith("@color/", StringComparison.Ordinal))
                return null;

            _named ??= Load();
            return _named.TryGetValue(text["@color/".Length..], out var target) ? Resolve(target, depth + 1) : null;
        }

        private Dictionary<string, string> Load()
        {
            var named = new Dictionary<string, string>();
            if (resFolder is null)
                return named;

            var root = ReadXml(Path.Combine(resFolder, "values", "colors.xml"))?.Root;
            foreach (var color in root?.Elements().Where(e => e.Name.LocalName == "color") ?? [])
                if (Get(color, "name") is { } name)
                    named[name] = color.Value.Trim();
            return named;
        }
    }

    // The res folder an Android resource file lies in: res/<drawable or mipmap folder>/<file>.
    private static string? ResFolderOf(string path)
    {
        var resFolder = Path.GetDirectoryName(Path.GetDirectoryName(path));
        return resFolder is not null && Path.GetFileName(resFolder).Equals("res", StringComparison.OrdinalIgnoreCase) ? resFolder : null;
    }

    private static Layer? ParseAndroidVector(XElement root, string path)
    {
        var width = Number(Get(root, "viewportWidth"), 0);
        var height = Number(Get(root, "viewportHeight"), 0);
        if (width <= 0 || height <= 0)
            return null;

        var sink = new ShapeSink();
        var colors = new AndroidColors(ResFolderOf(path));
        var count = 0;
        WalkAndroid(root, Matrix.Identity, Math.Clamp(Number(Get(root, "alpha"), 1), 0, 1), 0, colors, sink, ref count);
        return sink.Finish(new Rect(0, 0, width, height));
    }

    private static void WalkAndroid(XElement parent, Matrix parentMatrix, double alpha, int depth, AndroidColors colors, ShapeSink sink, ref int count)
    {
        if (depth > MaxDepth)
            return;

        foreach (var element in parent.Elements())
        {
            if (++count > MaxElements)
                return;

            switch (element.Name.LocalName)
            {
                case "group":
                    WalkAndroid(element, AndroidGroupMatrix(element) * parentMatrix, alpha, depth + 1, colors, sink, ref count);
                    break;
                case "path":
                    AddAndroidPath(element, parentMatrix, alpha, colors, sink);
                    break;
            }
        }
    }

    private static Matrix AndroidGroupMatrix(XElement group)
    {
        var pivotX = Number(Get(group, "pivotX"), 0);
        var pivotY = Number(Get(group, "pivotY"), 0);
        var matrix = Matrix.Identity;
        matrix.Translate(-pivotX, -pivotY);
        matrix.Scale(Number(Get(group, "scaleX"), 1), Number(Get(group, "scaleY"), 1));
        matrix.Rotate(Number(Get(group, "rotation"), 0));
        matrix.Translate(pivotX + Number(Get(group, "translateX"), 0), pivotY + Number(Get(group, "translateY"), 0));
        return matrix;
    }

    private static void AddAndroidPath(XElement path, Matrix matrix, double alpha, AndroidColors colors, ShapeSink sink)
    {
        var evenOdd = string.Equals(Get(path, "fillType"), "evenOdd", StringComparison.OrdinalIgnoreCase);
        var geometry = ParsePathData(Get(path, "pathData"), evenOdd);
        if (geometry is null)
            return;

        // A gradient fill is read as its first color.
        var fill = colors.Resolve(Get(path, "fillColor"))
            ?? (Get(path, "fillColor") is null
                ? colors.Resolve(path.Descendants().Where(e => e.Name.LocalName == "item").Select(e => Get(e, "color")).FirstOrDefault(c => c is not null))
                : null);
        var stroke = colors.Resolve(Get(path, "strokeColor"));
        sink.Add(
            geometry, matrix,
            fill is { } f ? WithAlpha(f, alpha * Math.Clamp(Number(Get(path, "fillAlpha"), 1), 0, 1)) : null,
            stroke is { } s ? WithAlpha(s, alpha * Math.Clamp(Number(Get(path, "strokeAlpha"), 1), 0, 1)) : null,
            Number(Get(path, "strokeWidth"), 0));
    }

    // ---- Android adaptive icon ----

    // Launchers crop an adaptive icon to its inner 72 of 108 units; that is what the icon shows.
    private static Layer? ParseAdaptiveIcon(XElement root, string path)
    {
        var resFolder = ResFolderOf(path);
        var foregroundElement = root.Elements().FirstOrDefault(e => e.Name.LocalName == "foreground");
        if (resFolder is null || foregroundElement is null)
            return null;

        var colors = new AndroidColors(resFolder);
        var foreground = ResolveLayer(LayerReference(foregroundElement), resFolder, colors, 0);
        if (foreground is null)
            return null;

        var backgroundElement = root.Elements().FirstOrDefault(e => e.Name.LocalName == "background");
        var background = backgroundElement is null ? null : ResolveLayer(LayerReference(backgroundElement), resFolder, colors, 0);

        var viewport = new Rect(AdaptiveInset, AdaptiveInset, AdaptiveSize - 2 * AdaptiveInset, AdaptiveSize - 2 * AdaptiveInset);
        var group = new DrawingGroup { ClipGeometry = FrozenRectangle(viewport) };
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, FrozenRectangle(viewport)));
        var hues = new HueHistogram();
        foreach (var layer in new[] { background, foreground })
        {
            if (layer is null)
                continue;
            group.Children.Add(layer.Drawing);
            hues.Add(layer.Hues);
        }

        group.Freeze();
        return new Layer(group, viewport, hues);
    }

    private static string? LayerReference(XElement layer) =>
        Get(layer, "drawable") ?? layer.Elements().Select(e => Get(e, "drawable")).FirstOrDefault(d => d is not null);

    // A layer as a drawing in the 108 by 108 space of an adaptive icon.
    private static Layer? ResolveLayer(string? reference, string resFolder, AndroidColors colors, int depth)
    {
        if (string.IsNullOrWhiteSpace(reference) || depth > 3)
            return null;

        var full = new Rect(0, 0, AdaptiveSize, AdaptiveSize);
        if (colors.Resolve(reference) is { } solid)
        {
            var hues = new HueHistogram();
            hues.Add(solid, 1);
            var fill = new GeometryDrawing(FrozenBrush(solid), null, FrozenRectangle(full));
            fill.Freeze();
            return new Layer(fill, full, hues);
        }

        var match = AndroidResourcePattern.Match(reference.Trim());
        var file = match.Success ? FindDrawable(resFolder, match.Groups[1].Value) : null;
        if (file is null)
            return null;

        if (!HasExtension(file, ".xml"))
            return BitmapLayer(file, full);

        var root = ReadXml(file)?.Root;
        switch (root?.Name.LocalName)
        {
            case "vector":
                return ParseAndroidVector(root, file) is { } vector ? Scaled(vector, full) : null;
            case "inset":
                return ResolveLayer(Get(root, "drawable"), resFolder, colors, depth + 1);
            default:
                return null;
        }
    }

    private static Layer? BitmapLayer(string file, Rect full)
    {
        var bitmap = LoadBitmap(file, LayerPixelWidth);
        if (bitmap is null)
            return null;

        var hues = new HueHistogram();
        AddBitmap(hues, bitmap, 1.0 / ((double)bitmap.PixelWidth * bitmap.PixelHeight));
        var drawing = new ImageDrawing(bitmap, full);
        drawing.Freeze();
        return new Layer(drawing, full, hues);
    }

    private static Layer Scaled(Layer layer, Rect target)
    {
        var scale = target.Width / layer.Viewport.Width;
        var matrix = Matrix.Identity;
        matrix.Translate(-layer.Viewport.X, -layer.Viewport.Y);
        matrix.Scale(scale, scale);
        var group = new DrawingGroup { Transform = new MatrixTransform(matrix) };
        group.Children.Add(layer.Drawing);
        group.Freeze();
        return new Layer(group, target, layer.Hues);
    }

    /// <summary>A drawable or mipmap resource by name: the densest bitmap first, then a vector. The
    /// name is plain letters, digits and underscores, so it can only pick a file inside the res
    /// folder.</summary>
    private static string? FindDrawable(string resFolder, string name)
    {
        try
        {
            var folders = Directory.EnumerateDirectories(resFolder)
                .Where(folder =>
                {
                    var folderName = Path.GetFileName(folder);
                    return (folderName.StartsWith("drawable", StringComparison.OrdinalIgnoreCase) || folderName.StartsWith("mipmap", StringComparison.OrdinalIgnoreCase))
                        && !IsLink(folder);
                })
                .OrderBy(folder => DensityOrder(Path.GetFileName(folder)))
                .ThenBy(folder => folder, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var extension in new[] { ".png", ".xml" })
            {
                foreach (var folder in folders)
                {
                    var candidate = Path.Combine(folder, name + extension);
                    if (IsSafeFile(candidate))
                        return candidate;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static int DensityOrder(string folderName)
    {
        var index = Array.FindIndex(Densities, density => folderName.EndsWith("-" + density, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : Densities.Length;
    }
}
