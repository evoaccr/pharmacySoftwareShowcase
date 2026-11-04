using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;

namespace pharmacyApp.Modules.Products;

public sealed class WarningIconConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource?> Cache = [];

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes) return null;
        var key = System.Convert.ToHexString(SHA256.HashData(bytes));
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var image))
            {
                image = CreateSvgDrawing(bytes);
                Cache[key] = image;
            }
            return image;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static ImageSource? CreateSvgDrawing(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var document = XDocument.Load(stream, LoadOptions.None);
            if (document.Root is null) return null;

            var drawingGroup = new DrawingGroup();
            foreach (var element in document.Root.Descendants())
            {
                var geometry = element.Name.LocalName switch
                {
                    "path" => ParsePath(element),
                    "polygon" => ParsePolygon(element),
                    "rect" => ParseRectangle(element),
                    _ => null
                };
                if (geometry is null) continue;

                var fill = FindInheritedAttribute(element, "fill");
                if (string.Equals(fill, "none", StringComparison.OrdinalIgnoreCase)) continue;
                var brush = ParseBrush(fill ?? "#000000");
                var opacityText = FindInheritedAttribute(element, "fill-opacity");
                if (double.TryParse(opacityText, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
                    brush.Opacity = Math.Clamp(opacity, 0, 1);
                if (brush.CanFreeze) brush.Freeze();

                var transform = BuildTransform(element);
                if (transform is not null)
                {
                    if (geometry.IsFrozen) geometry = geometry.Clone();
                    geometry.Transform = transform;
                }
                if (geometry.CanFreeze) geometry.Freeze();
                drawingGroup.Children.Add(new GeometryDrawing(brush, null, geometry));
            }

            if (drawingGroup.Children.Count == 0) return null;
            drawingGroup.Freeze();
            var image = new DrawingImage(drawingGroup);
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static Geometry? ParsePath(XElement element)
    {
        var data = element.Attribute("d")?.Value;
        return string.IsNullOrWhiteSpace(data) ? null : Geometry.Parse(data);
    }

    private static Geometry? ParsePolygon(XElement element)
    {
        var points = ParseNumbers(element.Attribute("points")?.Value).ToArray();
        if (points.Length < 6 || points.Length % 2 != 0) return null;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new System.Windows.Point(points[0], points[1]), true, true);
            for (var index = 2; index < points.Length; index += 2)
                context.LineTo(new System.Windows.Point(points[index], points[index + 1]), true, false);
        }
        return geometry;
    }

    private static Geometry? ParseRectangle(XElement element)
    {
        if (!TryNumber(element, "width", out var width) || !TryNumber(element, "height", out var height) || width <= 0 || height <= 0)
            return null;
        TryNumber(element, "x", out var x);
        TryNumber(element, "y", out var y);
        return new RectangleGeometry(new System.Windows.Rect(x, y, width, height));
    }

    private static Transform? BuildTransform(XElement element)
    {
        var transforms = element.AncestorsAndSelf()
            .Reverse()
            .Select(ancestor => ancestor.Attribute("transform")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(ParseTransforms)
            .ToList();
        if (transforms.Count == 0) return null;
        var group = new TransformGroup();
        foreach (var transform in transforms) group.Children.Add(transform);
        return group;
    }

    private static IEnumerable<Transform> ParseTransforms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (Match match in Regex.Matches(value, @"(?<name>matrix|translate|scale)\s*\((?<args>[^)]*)\)", RegexOptions.IgnoreCase))
        {
            var numbers = ParseNumbers(match.Groups["args"].Value).ToArray();
            switch (match.Groups["name"].Value.ToLowerInvariant())
            {
                case "matrix" when numbers.Length >= 6:
                    yield return new MatrixTransform(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]);
                    break;
                case "translate" when numbers.Length >= 1:
                    yield return new TranslateTransform(numbers[0], numbers.Length > 1 ? numbers[1] : 0);
                    break;
                case "scale" when numbers.Length >= 1:
                    yield return new ScaleTransform(numbers[0], numbers.Length > 1 ? numbers[1] : numbers[0]);
                    break;
            }
        }
    }

    private static IEnumerable<double> ParseNumbers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (Match match in Regex.Matches(value, @"[-+]?(?:\d*\.\d+|\d+)(?:[eE][-+]?\d+)?"))
            if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                yield return number;
    }

    private static bool TryNumber(XElement element, string name, out double value) =>
        double.TryParse(element.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string? FindInheritedAttribute(XElement element, string name)
    {
        foreach (var candidate in element.AncestorsAndSelf())
        {
            var direct = candidate.Attribute(name)?.Value;
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
            var style = candidate.Attribute("style")?.Value;
            if (string.IsNullOrWhiteSpace(style)) continue;
            var match = Regex.Match(style, $@"(?:^|;)\s*{Regex.Escape(name)}\s*:\s*(?<value>[^;]+)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups["value"].Value.Trim();
        }
        return null;
    }

    private static SolidColorBrush ParseBrush(string value)
    {
        var rgb = Regex.Match(value, @"rgb\(\s*(?<r>[-+\d.]+)%?\s*,\s*(?<g>[-+\d.]+)%?\s*,\s*(?<b>[-+\d.]+)%?\s*\)", RegexOptions.IgnoreCase);
        if (rgb.Success)
        {
            var usesPercent = value.Contains('%');
            byte Channel(string name)
            {
                var number = double.Parse(rgb.Groups[name].Value, CultureInfo.InvariantCulture);
                if (usesPercent) number = number * 255 / 100;
                return (byte)Math.Clamp(Math.Round(number), 0, 255);
            }
            return new SolidColorBrush(Color.FromRgb(Channel("r"), Channel("g"), Channel("b")));
        }
        var color = (Color)ColorConverter.ConvertFromString(value)!;
        return new SolidColorBrush(color);
    }
}
