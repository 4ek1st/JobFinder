using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JobFinder;

internal static class IconBuilder
{
    public static void Create(string file)
    {
        var images = new List<(int Size, byte[] Bytes)>();
        foreach (int size in new[] { 16, 32, 48, 256 })
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(23, 109, 97)), null, new(0, 0, size, size), size * 0.22, size * 0.22);
                var letter = new FormattedText("J", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size * 0.7, Brushes.White, 1);
                drawing.DrawText(letter, new((size - letter.Width) / 2, (size - letter.Height) / 2 - size * 0.025));
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var data = new MemoryStream(); png.Save(data); images.Add((size, data.ToArray()));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var stream = File.Create(file); using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)images.Count);
        uint offset = (uint)(6 + images.Count * 16);
        foreach (var (size, bytes) in images) { writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((uint)bytes.Length); writer.Write(offset); offset += (uint)bytes.Length; }
        foreach (var image in images) writer.Write(image.Bytes);
    }
}
