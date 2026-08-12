using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Builds DriveBye's icon assets from the mascot render: cuts the subject out of its background
// if it has one, squares it up, and emits a multi-size .ico plus a 512px tile and a preview
// strip. See README.md for how the output maps onto the app.
//
//   iconmaker <source.png> <outputDir> [tightCentreX tightCentreY tightSide]

internal static class Program
{
    private static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256 };
    private static readonly int[] PreviewSizes = { 256, 64, 48, 32, 16 };

    /// <summary>How far a neighbouring pixel may differ and still count as more background.
    /// Small, because the backdrop is a smooth gradient while the subject's edge is a hard step.</summary>
    private const int LocalTolerance = 16;

    /// <summary>The backdrop is light; refusing to fill dark pixels stops leaks through thin edges.</summary>
    private const int MinBackgroundLuma = 135;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: iconmaker <source.png> <outputDir> [tightCentreX tightCentreY tightSide]");
            return 1;
        }

        string outDir = args[1];
        Directory.CreateDirectory(outDir);

        BitmapSource source = LoadPng(args[0]);
        Console.WriteLine($"source: {source.PixelWidth} x {source.PixelHeight}");

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);

        // A source that already carries alpha has better edges than anything a flood fill can
        // recover, and eroding them would eat the subject rather than a halo.
        if (HasTransparency(pixels))
        {
            Console.WriteLine("source already has an alpha channel — skipping background removal");
        }
        else
        {
            int cleared = RemoveBackground(pixels, width, height, stride);
            Console.WriteLine($"background: {cleared} px cleared ({cleared * 100.0 / (width * height):0.0}%)");
            ShrinkAlpha(pixels, width, height, stride);
        }

        BitmapSource cutout = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

        (int minX, int minY, int maxX, int maxY) = ContentBounds(pixels, width, height);
        int contentWidth = maxX - minX + 1;
        int contentHeight = maxY - minY + 1;
        Console.WriteLine($"content: {contentWidth} x {contentHeight} at ({minX},{minY})");

        // Whole character, squared. Only a hair of margin — every pixel of padding shrinks him
        // at the sizes that matter, and nothing should touch the frame edge.
        int fullSide = (int)(Math.Max(contentWidth, contentHeight) * 1.03);
        BitmapSource full = Square(cutout, width, height,
            minX + (contentWidth / 2.0), minY + (contentHeight / 2.0), fullSide);

        Emit(full, "full", outDir);

        // An optional hand-placed square, for when the automatic one frames the subject badly.
        // A character with outstretched arms is wider than tall, so squaring the whole of it
        // shrinks the face; cropping to the interesting part can double it at a given icon size.
        // There is no way to find "the interesting part" from an alpha mask, hence coordinates.
        if (args.Length >= 5)
        {
            BitmapSource tight = Square(cutout, width, height,
                double.Parse(args[2]), double.Parse(args[3]), int.Parse(args[4]));
            Emit(tight, "tight", outDir);
        }

        return 0;
    }

    /// <summary>Writes the three artefacts for one square: the icon, the tile, and a preview.</summary>
    private static void Emit(BitmapSource square, string name, string outDir)
    {
        File.WriteAllBytes(Path.Combine(outDir, $"{name}.ico"), BuildIco(square));
        SavePng(Resize(square, 512), Path.Combine(outDir, $"{name}-tile.png"));
        SavePng(BuildPreview(square), Path.Combine(outDir, $"{name}-preview.png"));
        Console.WriteLine($"wrote {name}.ico ({square.PixelWidth}px square), -tile.png, -preview.png");
    }

    private static BitmapSource LoadPng(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
            .Frames[0];
    }

    /// <summary>
    /// Flood-fills inward from every border pixel, letting each step compare against the pixel it
    /// came from rather than a fixed colour — that tracks the backdrop's gradient without a global
    /// threshold, and stops dead at the subject's outline. Returns how many pixels were cleared.
    /// </summary>
    private static int RemoveBackground(byte[] pixels, int width, int height, int stride)
    {
        var queue = new Queue<int>();
        var seen = new bool[width * height];

        void Seed(int x, int y)
        {
            int index = (y * width) + x;
            if (seen[index] || Luma(pixels, index * 4) < MinBackgroundLuma) return;
            seen[index] = true;
            queue.Enqueue(index);
        }

        for (int x = 0; x < width; x++) { Seed(x, 0); Seed(x, height - 1); }
        for (int y = 0; y < height; y++) { Seed(0, y); Seed(width - 1, y); }

        int cleared = 0;
        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            int offset = index * 4;
            pixels[offset + 3] = 0;      // alpha
            cleared++;

            int x = index % width;
            int y = index / width;

            void Visit(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) return;
                int next = (ny * width) + nx;
                if (seen[next]) return;

                int nextOffset = next * 4;
                if (Luma(pixels, nextOffset) < MinBackgroundLuma) return;
                if (Distance(pixels, offset, nextOffset) > LocalTolerance) return;

                seen[next] = true;
                queue.Enqueue(next);
            }

            Visit(x - 1, y);
            Visit(x + 1, y);
            Visit(x, y - 1);
            Visit(x, y + 1);
        }

        return cleared;
    }

    /// <summary>
    /// Pulls the alpha edge in slightly. Anti-aliased pixels along the outline are part backdrop,
    /// and against a dark UI they would otherwise read as a pale halo.
    /// </summary>
    private static void ShrinkAlpha(byte[] pixels, int width, int height, int stride)
    {
        byte[] original = (byte[])pixels.Clone();

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (((y * width) + x) * 4) + 3;
                if (original[offset] == 0) continue;

                bool touchesHole = false;
                for (int dy = -1; dy <= 1 && !touchesHole; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        if (original[((((ny * width) + nx)) * 4) + 3] == 0) { touchesHole = true; break; }
                    }
                }

                if (touchesHole) pixels[offset] = (byte)(original[offset] * 0.45);
            }
        }
    }

    /// <summary>Bounding box of everything the cutout kept.</summary>
    private static (int MinX, int MinY, int MaxX, int MaxY) ContentBounds(
        byte[] pixels, int width, int height)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (pixels[((((y * width) + x)) * 4) + 3] < 24) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0) throw new InvalidOperationException("nothing survived the cutout");
        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Draws the image onto a transparent square of the given size, centred on a point. Rendering
    /// rather than CroppedBitmap so the square may extend past the source edges without being
    /// clamped, which would shove the subject off centre.
    /// </summary>
    private static BitmapSource Square(
        BitmapSource image, int width, int height, double centreX, double centreY, int side)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(
                (side / 2.0) - centreX, (side / 2.0) - centreY, width, height));
        }

        var target = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    private static bool HasTransparency(byte[] pixels)
    {
        for (int offset = 3; offset < pixels.Length; offset += 4)
            if (pixels[offset] != 255) return true;
        return false;
    }

    private static int Luma(byte[] pixels, int offset) =>
        ((pixels[offset + 2] * 299) + (pixels[offset + 1] * 587) + (pixels[offset] * 114)) / 1000;

    private static int Distance(byte[] pixels, int a, int b) =>
        Math.Abs(pixels[a] - pixels[b])
        + Math.Abs(pixels[a + 1] - pixels[b + 1])
        + Math.Abs(pixels[a + 2] - pixels[b + 2]);

    private static BitmapSource Resize(BitmapSource source, int size)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);
            dc.DrawImage(source, new Rect(0, 0, size, size));
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    private static void SavePng(BitmapSource image, string path) =>
        File.WriteAllBytes(path, EncodePng(image));

    private static byte[] BuildIco(BitmapSource square)
    {
        var images = new List<byte[]>();
        foreach (int size in IconSizes)
            images.Add(EncodePng(Resize(square, size)));

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)IconSizes.Length);

        int offset = 6 + (16 * IconSizes.Length);
        for (int i = 0; i < IconSizes.Length; i++)
        {
            int size = IconSizes[i];
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }

        foreach (byte[] png in images) writer.Write(png);
        writer.Flush();
        return memory.ToArray();
    }

    /// <summary>Sizes side by side on the app's own background, which is where halos show up.</summary>
    private static BitmapSource BuildPreview(BitmapSource square)
    {
        const int gap = 20;
        const int pad = 24;

        int width = pad * 2;
        foreach (int size in PreviewSizes) width += size + gap;
        width -= gap;
        int height = 256 + (pad * 2);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16)), null,
                new Rect(0, 0, width, height));

            double x = pad;
            foreach (int size in PreviewSizes)
            {
                double y = pad + ((256 - size) / 2.0);
                dc.DrawImage(Resize(square, size), new Rect(x, y, size, size));
                x += size + gap;
            }
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }
}
