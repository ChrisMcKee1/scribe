# Regenerates Scribe tray icon assets for the Signal On palette.
# Reads src\Scribe.App\Assets\scribe.ico for the current silhouette frames and docs\icon.png for new 20 and 40 px idle frames.
# Writes src\Scribe.App\Assets\scribe.ico, scribe-recording.ico, scribe-processing.ico and scribe-paused.ico.
# Run from the repository root with: pwsh .\scripts\New-TrayIcons.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$source = @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class TrayIconGenerator
{
    private static readonly int[] Sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    // These literals are checked against Scribe.Core.Appearance.ScribeBrand.
    private static readonly Rgb Ink = Rgb.Parse("#07142F");
    private static readonly Rgb Paper = Rgb.Parse("#FCFCFC");
    private static readonly Rgb Signal = Rgb.Parse("#1C83FE");
    private static readonly Rgb Slate = Rgb.Parse("#6B7689");
    private static readonly Rgb ProcessingDots = Rgb.Parse("#82B6FF");

    public static void Generate(string root)
    {
        string assetDir = Path.Combine(root, "src", "Scribe.App", "Assets");
        string idlePath = Path.Combine(assetDir, "scribe.ico");
        string recordingPath = Path.Combine(assetDir, "scribe-recording.ico");
        string processingPath = Path.Combine(assetDir, "scribe-processing.ico");
        string pausedPath = Path.Combine(assetDir, "scribe-paused.ico");
        string documentIconPath = Path.Combine(root, "docs", "icon.png");

        var originalIdle = IconFile.Read(idlePath);
        using var documentIcon = new Bitmap(documentIconPath);
        var idleFrames = new List<IconFrame>();
        var idleBitmaps = new Dictionary<int, Bitmap>();

        foreach (int size in Sizes)
        {
            if (originalIdle.TryGetFrame(size, out var frame))
            {
                idleFrames.Add(frame.WithPayloadCopy());
                idleBitmaps[size] = DecodeFrame(frame.Payload, size);
            }
            else
            {
                using var resized = Resize(documentIcon, size);
                idleBitmaps[size] = CloneBitmap(resized);
                idleFrames.Add(IconFrame.Create(size, EncodeDib(resized), isPng: false));
            }
        }

        IconFile.Write(idlePath, idleFrames);
        IconFile.Write(recordingPath, BuildStateFrames(idleBitmaps, State.Recording));
        IconFile.Write(processingPath, BuildStateFrames(idleBitmaps, State.Processing));
        IconFile.Write(pausedPath, BuildStateFrames(idleBitmaps, State.Paused));

        foreach (var bitmap in idleBitmaps.Values)
        {
            bitmap.Dispose();
        }
    }

    private static List<IconFrame> BuildStateFrames(Dictionary<int, Bitmap> idleBitmaps, State state)
    {
        var frames = new List<IconFrame>();
        foreach (int size in Sizes)
        {
            using var rendered = state switch
            {
                State.Recording => RenderRecording(idleBitmaps[size], size),
                State.Processing => RenderProcessing(idleBitmaps[size], size),
                State.Paused => RenderPaused(idleBitmaps[size], size),
                _ => throw new InvalidOperationException()
            };
            bool png = size == 256;
            frames.Add(IconFrame.Create(size, png ? EncodePng(rendered) : EncodeDib(rendered), png));
        }

        return frames;
    }

    private static Bitmap RenderRecording(Bitmap idle, int size)
    {
        var output = NewBitmap(size);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var c = idle.GetPixel(x, y);
                if (c.A == 0)
                {
                    output.SetPixel(x, y, Color.FromArgb(0, 0, 0, 0));
                    continue;
                }

                var weights = Decompose(c);
                var r = weights.Signal >= 0.45 ? Ink : weights.Paper >= 0.75 ? Paper : weights.Ink >= 0.75 ? Signal : Compose(weights.Ink, weights.Paper, weights.Signal, Signal, Paper, Ink);
                output.SetPixel(x, y, Color.FromArgb(c.A, r.R, r.G, r.B));
            }
        }

        if (size == 16 || size == 20 || size == 24)
        {
            ClearRecordingWaveform(output, idle, size);
            SymmetrizeHorizontal(output);
        }
        else if (size == 32 || size == 40)
        {
            SymmetrizeHorizontal(output);
            TuneRecordingWaveform(output, idle, size);
        }

        return output;
    }

    private static void ClearRecordingWaveform(Bitmap output, Bitmap idle, int size)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (IsWaveformSource(idle.GetPixel(x, y)))
                {
                    SetIfVisible(output, idle, x, y, Paper);
                }
            }
        }
    }

    private static void TuneRecordingWaveform(Bitmap output, Bitmap idle, int size)
    {
        ClearRecordingWaveform(output, idle, size);
        if (size == 32)
        {
            DrawRecordingWaveform(output, idle, new[] { 12, 14, 17, 19 }, 8, 15, new[] { (13, 10, 13), (15, 8, 15), (16, 8, 15), (18, 10, 13) });
            SymmetrizeHorizontal(output);
            DrawRecordingWaveform(output, idle, new[] { 12, 14, 17, 19 }, 8, 15, new[] { (13, 10, 13), (15, 8, 15), (16, 8, 15), (18, 10, 13) });
            return;
        }

        if (size == 40)
        {
            DrawRecordingWaveform(output, idle, new[] { 14, 16, 18, 21, 23, 25 }, 10, 19, new[] { (15, 14, 15), (17, 12, 17), (19, 10, 19), (20, 10, 19), (22, 12, 17), (24, 14, 15) });
            SymmetrizeHorizontal(output);
            DrawRecordingWaveform(output, idle, new[] { 14, 16, 18, 21, 23, 25 }, 10, 19, new[] { (15, 14, 15), (17, 12, 17), (19, 10, 19), (20, 10, 19), (22, 12, 17), (24, 14, 15) });
        }
    }

    private static void DrawRecordingWaveform(Bitmap output, Bitmap idle, int[] paperColumns, int paperTop, int paperBottom, (int X, int Top, int Bottom)[] inkColumns)
    {
        foreach (int x in paperColumns)
        {
            for (int y = paperTop; y <= paperBottom; y++)
            {
                if (IsCapsuleSource(idle.GetPixel(x, y)))
                {
                    SetIfVisible(output, idle, x, y, Paper);
                }
            }
        }

        foreach (var bar in inkColumns)
        {
            for (int y = bar.Top; y <= bar.Bottom; y++)
            {
                if (IsCapsuleSource(idle.GetPixel(bar.X, y)))
                {
                    SetIfVisible(output, idle, bar.X, y, Ink);
                }
            }
        }
    }

    private static Bitmap RenderProcessing(Bitmap idle, int size)
    {
        var output = FillSilhouette(idle, Ink, size);
        using var g = Graphics.FromImage(output);
        g.SmoothingMode = size <= 20 ? SmoothingMode.None : SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(ProcessingDots.ToColor());
        if (size == 16)
        {
            DrawDotGrid(output, 3, 7, 2, ProcessingDots, ProcessingDots);
            DrawDotGrid(output, 7, 7, 2, ProcessingDots, ProcessingDots);
            DrawDotGrid(output, 11, 7, 2, ProcessingDots, ProcessingDots);
        }
        else if (size == 20)
        {
            DrawDotGrid(output, 3, 8, 4, ProcessingDots, Blend(ProcessingDots, Ink, 0.4));
            DrawDotGrid(output, 8, 8, 4, ProcessingDots, Blend(ProcessingDots, Ink, 0.4));
            DrawDotGrid(output, 13, 8, 4, ProcessingDots, Blend(ProcessingDots, Ink, 0.4));
        }
        else
        {
            g.PixelOffsetMode = PixelOffsetMode.Half;
            float scale = size / 512f;
            float radius = 44f * scale;
            foreach (float cx in new[] { 126f * scale, 256f * scale, 386f * scale })
            {
                float cy = 256f * scale;
                g.FillEllipse(brush, cx - radius, cy - radius, radius * 2f, radius * 2f);
            }
            SymmetrizeBoth(output);
        }

        return output;
    }

    private static Bitmap RenderPaused(Bitmap idle, int size)
    {
        var output = FillSilhouette(idle, Slate, size);
        using var g = Graphics.FromImage(output);
        g.SmoothingMode = size <= 20 ? SmoothingMode.None : SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Paper.ToColor());
        if (size == 16)
        {
            DrawPauseBar(output, 4, 4, 3, 8, Paper, Blend(Paper, Slate, 0.4));
            DrawPauseBar(output, 9, 4, 3, 8, Paper, Blend(Paper, Slate, 0.4));
        }
        else if (size == 20)
        {
            DrawPauseBar(output, 6, 5, 3, 10, Paper, Blend(Paper, Slate, 0.4));
            DrawPauseBar(output, 11, 5, 3, 10, Paper, Blend(Paper, Slate, 0.4));
        }
        else
        {
            g.PixelOffsetMode = PixelOffsetMode.Half;
            float scale = size / 512f;
            float barWidth = 70f * scale;
            float gap = 64f * scale;
            float height = 250f * scale;
            float y = (size - height) / 2f;
            float left = 256f * scale - gap / 2f - barWidth;
            float right = 256f * scale + gap / 2f;
            FillRoundBar(g, brush, left, y, barWidth, height, barWidth / 2f);
            FillRoundBar(g, brush, right, y, barWidth, height, barWidth / 2f);
            SymmetrizeBoth(output);
        }

        return output;
    }

    private static void DrawDotGrid(Bitmap output, int left, int top, int size, Rgb fill, Rgb corner)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool isCorner = (x == 0 || x == size - 1) && (y == 0 || y == size - 1);
                SetPixelPreservingAlpha(output, left + x, top + y, isCorner ? corner : fill);
            }
        }
    }

    private static void DrawPauseBar(Bitmap output, int left, int top, int width, int height, Rgb fill, Rgb endCorner)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool isEndCorner = (y == 0 || y == height - 1) && (x == 0 || x == width - 1);
                SetPixelPreservingAlpha(output, left + x, top + y, isEndCorner ? endCorner : fill);
            }
        }
    }

    private static Bitmap FillSilhouette(Bitmap idle, Rgb fill, int size)
    {
        var output = NewBitmap(size);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var c = idle.GetPixel(x, y);
                output.SetPixel(x, y, Color.FromArgb(c.A, fill.R, fill.G, fill.B));
            }
        }

        return output;
    }

    private static Weights Decompose(Color c)
    {
        double[] p = new[] { (double)(Paper.R - Ink.R), (double)(Paper.G - Ink.G), (double)(Paper.B - Ink.B) };
        double[] s = new[] { (double)(Signal.R - Ink.R), (double)(Signal.G - Ink.G), (double)(Signal.B - Ink.B) };
        double[] v = new[] { (double)(c.R - Ink.R), (double)(c.G - Ink.G), (double)(c.B - Ink.B) };
        double pp = Dot(p, p);
        double ps = Dot(p, s);
        double ss = Dot(s, s);
        double pv = Dot(p, v);
        double sv = Dot(s, v);
        double det = pp * ss - ps * ps;
        double paper = det == 0 ? 0 : (pv * ss - sv * ps) / det;
        double signal = det == 0 ? 0 : (sv * pp - pv * ps) / det;
        double ink = 1.0 - paper - signal;
        ink = Clamp01(ink);
        paper = Clamp01(paper);
        signal = Clamp01(signal);
        double sum = ink + paper + signal;
        if (sum <= 0.00001)
        {
            return new Weights(1, 0, 0);
        }

        return new Weights(ink / sum, paper / sum, signal / sum);
    }

    private static Rgb Compose(double inkWeight, double paperWeight, double signalWeight, Rgb ink, Rgb paper, Rgb signal)
    {
        return new Rgb(
            ClampByte(inkWeight * ink.R + paperWeight * paper.R + signalWeight * signal.R),
            ClampByte(inkWeight * ink.G + paperWeight * paper.G + signalWeight * signal.G),
            ClampByte(inkWeight * ink.B + paperWeight * paper.B + signalWeight * signal.B));
    }

    private static void SetIfVisible(Bitmap output, Bitmap idle, int x, int y, Rgb color)
    {
        if (x < 0 || y < 0 || x >= output.Width || y >= output.Height)
        {
            return;
        }

        int alpha = idle.GetPixel(x, y).A;
        if (alpha > 0)
        {
            output.SetPixel(x, y, Color.FromArgb(alpha, color.R, color.G, color.B));
        }
    }

    private static void SetPixelPreservingAlpha(Bitmap output, int x, int y, Rgb color)
    {
        if (x < 0 || y < 0 || x >= output.Width || y >= output.Height)
        {
            return;
        }

        var current = output.GetPixel(x, y);
        if (current.A > 0)
        {
            output.SetPixel(x, y, Color.FromArgb(current.A, color.R, color.G, color.B));
        }
    }

    private static bool IsWaveformSource(Color color) => color.A > 0 && Decompose(color).Signal >= 0.18;

    private static void SymmetrizeHorizontal(Bitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width / 2; x++)
            {
                int mirrorX = bitmap.Width - 1 - x;
                var average = Average(bitmap.GetPixel(x, y), bitmap.GetPixel(mirrorX, y));
                bitmap.SetPixel(x, y, average);
                bitmap.SetPixel(mirrorX, y, average);
            }
        }
    }

    private static void SymmetrizeBoth(Bitmap bitmap)
    {
        for (int y = 0; y < (bitmap.Height + 1) / 2; y++)
        {
            int mirrorY = bitmap.Height - 1 - y;
            for (int x = 0; x < (bitmap.Width + 1) / 2; x++)
            {
                int mirrorX = bitmap.Width - 1 - x;
                var average = Average(bitmap.GetPixel(x, y), bitmap.GetPixel(mirrorX, y), bitmap.GetPixel(x, mirrorY), bitmap.GetPixel(mirrorX, mirrorY));
                bitmap.SetPixel(x, y, average);
                bitmap.SetPixel(mirrorX, y, average);
                bitmap.SetPixel(x, mirrorY, average);
                bitmap.SetPixel(mirrorX, mirrorY, average);
            }
        }
    }

    private static Color Average(params Color[] colors)
    {
        int alpha = 0;
        int red = 0;
        int green = 0;
        int blue = 0;
        foreach (var color in colors)
        {
            alpha += color.A;
            red += color.R;
            green += color.G;
            blue += color.B;
        }

        return Color.FromArgb(
            ClampByte(alpha / (double)colors.Length),
            ClampByte(red / (double)colors.Length),
            ClampByte(green / (double)colors.Length),
            ClampByte(blue / (double)colors.Length));
    }

    private static bool IsCapsuleSource(Color color)
    {
        if (color.A == 0)
        {
            return false;
        }

        var weights = Decompose(color);
        return weights.Paper >= 0.12 || weights.Signal >= 0.12;
    }

    private static bool TryFindWaveformBounds(Bitmap idle, out Bounds bounds)
    {
        int left = idle.Width;
        int top = idle.Height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < idle.Height; y++)
        {
            for (int x = 0; x < idle.Width; x++)
            {
                if (!IsWaveformSource(idle.GetPixel(x, y)))
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        bounds = new Bounds(left, top, right, bottom);
        return right >= left && bottom >= top;
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));
    private static int ClampByte(double value) => Math.Max(0, Math.Min(255, (int)Math.Round(value)));

    private static Rgb Blend(Rgb foreground, Rgb background, double amount) => new(
        ClampByte(foreground.R * amount + background.R * (1 - amount)),
        ClampByte(foreground.G * amount + background.G * (1 - amount)),
        ClampByte(foreground.B * amount + background.B * (1 - amount)));

    private static Bitmap DecodeFrame(byte[] payload, int size)
    {
        if (payload.Length > 8 && payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47)
        {
            using var stream = new MemoryStream(payload);
            using var bitmap = new Bitmap(stream);
            return CloneBitmap(bitmap);
        }

        return DecodeDib(payload, size);
    }

    private static Bitmap DecodeDib(byte[] payload, int size)
    {
        int width = BitConverter.ToInt32(payload, 4);
        int height = BitConverter.ToInt32(payload, 8) / 2;
        if (width != size || height != size)
        {
            throw new InvalidOperationException("The icon frame size does not match its directory entry.");
        }

        var bitmap = NewBitmap(size);
        int pixelOffset = BitConverter.ToInt32(payload, 0);
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            int sourceY = height - 1 - y;
            int row = pixelOffset + sourceY * stride;
            for (int x = 0; x < width; x++)
            {
                int index = row + x * 4;
                bitmap.SetPixel(x, y, Color.FromArgb(payload[index + 3], payload[index + 2], payload[index + 1], payload[index]));
            }
        }

        return bitmap;
    }
    private static Bitmap Resize(Bitmap source, int size)
    {
        var output = NewBitmap(size);
        using var g = Graphics.FromImage(output);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        return output;
    }

    private static Bitmap CloneBitmap(Bitmap source)
    {
        var output = NewBitmap(source.Width);
        using var g = Graphics.FromImage(output);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(source, 0, 0, source.Width, source.Height);
        return output;
    }

    private static Bitmap NewBitmap(int size) => new Bitmap(size, size, PixelFormat.Format32bppArgb);

    private static void FillEllipse(Graphics g, Brush brush, float x, float y, float width, float height) =>
        g.FillEllipse(brush, x, y, width, height);

    private static void FillRoundBar(Graphics g, Brush brush, float x, float y, float width, float height, float radius)
    {
        using var path = new GraphicsPath();
        float diameter = radius * 2f;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] EncodeDib(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int xorStride = width * 4;
        int andStride = ((width + 31) / 32) * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(xorStride * height);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                writer.Write(c.B);
                writer.Write(c.G);
                writer.Write(c.R);
                writer.Write(c.A);
            }
        }

        writer.Write(new byte[andStride * height]);
        return stream.ToArray();
    }

    private enum State
    {
        Recording,
        Processing,
        Paused
    }

    private readonly record struct Weights(double Ink, double Paper, double Signal);

    private readonly record struct Bounds(int Left, int Top, int Right, int Bottom);

    private readonly record struct Rgb(int R, int G, int B)
    {
        public static Rgb Parse(string hex) => new(
            Convert.ToInt32(hex.Substring(1, 2), 16),
            Convert.ToInt32(hex.Substring(3, 2), 16),
            Convert.ToInt32(hex.Substring(5, 2), 16));

        public Color ToColor() => Color.FromArgb(255, R, G, B);
    }

    private sealed class IconFile
    {
        private readonly Dictionary<int, IconFrame> _frames;

        private IconFile(IEnumerable<IconFrame> frames)
        {
            _frames = new Dictionary<int, IconFrame>();
            foreach (var frame in frames)
            {
                _frames[frame.Size] = frame;
            }
        }

        public static IconFile Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            ushort count = BitConverter.ToUInt16(bytes, 4);
            var frames = new List<IconFrame>();
            for (int i = 0; i < count; i++)
            {
                int entry = 6 + i * 16;
                int width = bytes[entry] == 0 ? 256 : bytes[entry];
                int length = BitConverter.ToInt32(bytes, entry + 8);
                int offset = BitConverter.ToInt32(bytes, entry + 12);
                var payload = new byte[length];
                Array.Copy(bytes, offset, payload, 0, length);
                frames.Add(IconFrame.Create(width, payload, IsPng(payload)));
            }

            return new IconFile(frames);
        }

        public bool TryGetFrame(int size, out IconFrame frame) => _frames.TryGetValue(size, out frame!);

        public static void Write(string path, IReadOnlyList<IconFrame> frames)
        {
            var ordered = new List<IconFrame>(frames);
            ordered.Sort((left, right) => left.Size.CompareTo(right.Size));
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)ordered.Count);
            int offset = 6 + ordered.Count * 16;
            foreach (var frame in ordered)
            {
                writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
                writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(frame.Payload.Length);
                writer.Write(offset);
                offset += frame.Payload.Length;
            }

            foreach (var frame in ordered)
            {
                writer.Write(frame.Payload);
            }

            File.WriteAllBytes(path, stream.ToArray());
        }

        private static bool IsPng(byte[] payload) =>
            payload.Length > 8 && payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47;
    }

    private readonly record struct IconFrame(int Size, byte[] Payload, bool IsPng)
    {
        public static IconFrame Create(int size, byte[] payload, bool isPng) => new(size, payload, isPng);

        public IconFrame WithPayloadCopy()
        {
            var copy = new byte[Payload.Length];
            Array.Copy(Payload, copy, Payload.Length);
            return new IconFrame(Size, copy, IsPng);
        }
    }
}
"@

Add-Type -TypeDefinition $source -ReferencedAssemblies System.Runtime,System.Collections,System.Drawing.Common,System.Drawing.Primitives,System.Private.Windows.GdiPlus,System.Private.Windows.Core
[TrayIconGenerator]::Generate($root)
