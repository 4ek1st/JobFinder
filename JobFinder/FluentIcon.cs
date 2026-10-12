using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace JobFinder;

// Original SVG paths from Microsoft's Fluent UI System Icons (MIT).
public sealed class FluentIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(FluentIcon), new FrameworkPropertyMetadata("search", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = Control.ForegroundProperty.AddOwner(typeof(FluentIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private static readonly Dictionary<string, Geometry> Shapes = [];
    public FluentIcon() { Width = 20; Height = 20; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext drawing)
    {
        if (!Shapes.TryGetValue(Kind, out var shape))
        {
            if (Kind.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) return;
            var resource = Application.GetResourceStream(new Uri($"Assets/Icons/{Kind}.svg", UriKind.Relative));
            if (resource is null) return;
            using var stream = resource.Stream;
            using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit });
            var doc = XDocument.Load(reader);
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var path in doc.Descendants().Where(x => x.Name.LocalName == "path"))
                if (path.Attribute("d")?.Value is string data) group.Children.Add(Geometry.Parse("F1 " + data));
            group.Freeze(); Shapes[Kind] = shape = group;
        }
        drawing.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        drawing.DrawGeometry(Foreground, null, shape); drawing.Pop();
    }
}
