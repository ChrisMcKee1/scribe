using System.Drawing;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Scribe.Core.Appearance;

namespace Scribe.Core.Tests;

public sealed partial class TrayIconAssetTests
{
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private static readonly Dictionary<int, string> IdleOriginalPayloadHashes = new()
    {
        [16] = "0fe46fa806e478c4107809a8c9cb98effd94dc002ddfd03133a9ec658473348d",
        [24] = "addc8ffc03a7f382e2c8715752c8f8d980990cacd96cbf4a29f15e1f97906327",
        [32] = "b5f1f493fd5de9c3693c51c225b3837ab259dbe778410c99269298d61c2fa6b6",
        [48] = "43df631e2e9c5243af41eb2e19e0907fa2a7aafa11693c93ff9a2eaf484cfae8",
        [64] = "0baf172865597a857d0f2118534d0595a8144dc0aeb9abc46162bfcd0fc63ebe",
        [128] = "c6e1f80696d3727c62c2819db86f73e82351c476486aa36ac614ff733d707586",
        [256] = "ec8f13b4145ac12b4f058ce0a818c1ede6fb3103bda83e305209c67d932baba1",
    };

    [Theory]
    [InlineData("scribe.ico")]
    [InlineData("scribe-recording.ico")]
    [InlineData("scribe-processing.ico")]
    [InlineData("scribe-paused.ico")]
    public void Tray_icons_have_the_expected_frames(string fileName)
    {
        var icon = IconFile.Read(AssetPath(fileName));

        Assert.Equal(ExpectedSizes, icon.Frames.Select(frame => frame.Size).ToArray());
        foreach (var frame in icon.Frames)
        {
            if (fileName == "scribe.ico" && IdleOriginalPayloadHashes.ContainsKey(frame.Size))
            {
                Assert.True(frame.IsPng);
            }
            else if (frame.Size == 256)
            {
                Assert.True(frame.IsPng);
            }
            else
            {
                Assert.False(frame.IsPng);
            }
        }
    }

    [Fact]
    public void Idle_original_frames_are_byte_identical_to_the_base_icon()
    {
        var icon = IconFile.Read(AssetPath("scribe.ico"));

        foreach (var (size, expectedHash) in IdleOriginalPayloadHashes)
        {
            var frame = icon.Frame(size);
            var hash = Convert.ToHexStringLower(SHA256.HashData(frame.Payload));
            Assert.Equal(expectedHash, hash);
        }
    }

    [Theory]
    [InlineData("scribe-recording.ico", 256, 128, 24, "Signal")]
    [InlineData("scribe-recording.ico", 32, 5, 5, "Signal")]
    [InlineData("scribe-processing.ico", 256, 128, 24, "Ink")]
    [InlineData("scribe-processing.ico", 32, 5, 5, "Ink")]
    [InlineData("scribe-paused.ico", 256, 128, 24, "Slate")]
    [InlineData("scribe-paused.ico", 32, 5, 5, "Slate")]
    [InlineData("scribe-recording.ico", 256, 128, 128, "Ink")]
    [InlineData("scribe-recording.ico", 32, 16, 14, "Ink")]
    [InlineData("scribe-processing.ico", 256, 63, 128, "ProcessingDots")]
    [InlineData("scribe-processing.ico", 32, 16, 16, "ProcessingDots")]
    [InlineData("scribe-paused.ico", 256, 94, 128, "Paper")]
    [InlineData("scribe-paused.ico", 32, 12, 16, "Paper")]
    public void Tray_state_frames_sample_to_brand_colours(string fileName, int size, int x, int y, string brandColour)
    {
        using var bitmap = IconFile.Read(AssetPath(fileName)).Frame(size).Decode();

        AssertContainsClose(Brand(brandColour), bitmap, x, y);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void Processing_frames_do_not_contain_capsule_pixels(int size)
    {
        using var bitmap = IconFile.Read(AssetPath("scribe-processing.ico")).Frame(size).Decode();
        var paper = ToColor(ScribeBrand.Paper);

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 0)
                {
                    Assert.False(IsClose(paper, pixel), $"Unexpected paper pixel at {x},{y} in {size} px frame.");
                }
            }
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    public void Recording_frames_without_native_waveforms_have_no_ink_inside_the_capsule_box(int size)
    {
        using var idle = IconFile.Read(AssetPath("scribe.ico")).Frame(size).Decode();
        using var recording = IconFile.Read(AssetPath("scribe-recording.ico")).Frame(size).Decode();
        var box = FindCapsuleBox(idle);
        var ink = ToColor(ScribeBrand.Ink);

        for (var y = box.Top; y <= box.Bottom; y++)
        {
            for (var x = box.Left; x <= box.Right; x++)
            {
                Assert.False(IsClose(ink, recording.GetPixel(x, y)), $"Unexpected ink pixel at {x},{y} in {size} px frame.");
            }
        }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(40)]
    public void Recording_native_waveform_matches_the_hand_tuned_columns(int size)
    {
        using var recording = IconFile.Read(AssetPath("scribe-recording.ico")).Frame(size).Decode();
        var ink = ToColor(ScribeBrand.Ink);
        var paper = ToColor(ScribeBrand.Paper);
        var inkBars = size == 32
            ? new[] { new PixelRect(13, 10, 1, 4), new PixelRect(15, 8, 2, 8), new PixelRect(18, 10, 1, 4) }
            : [new PixelRect(15, 14, 1, 2), new PixelRect(17, 12, 1, 6), new PixelRect(19, 10, 2, 10), new PixelRect(22, 12, 1, 6), new PixelRect(24, 14, 1, 2)];
        var paperColumns = size == 32
            ? new[] { 12, 14, 17, 19 }
            : [14, 16, 18, 21, 23, 25];
        var paperTop = size == 32 ? 8 : 10;
        var paperBottom = size == 32 ? 15 : 19;

        foreach (var bar in inkBars)
        {
            for (var y = bar.Top; y <= bar.Bottom; y++)
            {
                for (var x = bar.Left; x <= bar.Right; x++)
                {
                    Assert.True(IsClose(ink, recording.GetPixel(x, y)), $"Expected ink at {x},{y} in {size} px frame.");
                }
            }
        }

        foreach (var x in paperColumns)
        {
            for (var y = paperTop; y <= paperBottom; y++)
            {
                Assert.True(IsClose(paper, recording.GetPixel(x, y)), $"Expected paper at {x},{y} in {size} px frame.");
            }
        }

        for (var y = paperTop; y <= paperBottom; y++)
        {
            for (var x = paperColumns.Min(); x <= paperColumns.Max(); x++)
            {
                var shouldBeInk = inkBars.Any(bar => bar.Contains(x, y));
                Assert.Equal(shouldBeInk, IsClose(ink, recording.GetPixel(x, y)));
            }
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    public void Processing_small_frames_match_the_hand_tuned_grid(int size)
    {
        using var bitmap = IconFile.Read(AssetPath("scribe-processing.ico")).Frame(size).Decode();
        var ink = ToColor(ScribeBrand.Ink);
        var dot = ToColor(ScribeBrand.ProcessingDots);
        var corner = size == 16 ? dot : Blend(dot, ink, 0.4);
        var dots = size == 16
            ? new[] { new PixelRect(3, 7, 2, 2), new PixelRect(7, 7, 2, 2), new PixelRect(11, 7, 2, 2) }
            : new[] { new PixelRect(3, 8, 4, 4), new PixelRect(8, 8, 4, 4), new PixelRect(13, 8, 4, 4) };

        AssertSmallGrid(bitmap, ink, dots, dot, corner);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    public void Paused_small_frames_match_the_hand_tuned_grid(int size)
    {
        using var bitmap = IconFile.Read(AssetPath("scribe-paused.ico")).Frame(size).Decode();
        var slate = ToColor(ScribeBrand.Slate);
        var paper = ToColor(ScribeBrand.Paper);
        var corner = Blend(paper, slate, 0.4);
        var bars = size == 16
            ? new[] { new PixelRect(4, 4, 3, 8), new PixelRect(9, 4, 3, 8) }
            : new[] { new PixelRect(6, 5, 3, 10), new PixelRect(11, 5, 3, 10) };

        AssertSmallGrid(bitmap, slate, bars, paper, corner);
    }

    [Theory]
    [InlineData("scribe-recording.ico", 16, 40)]
    [InlineData("scribe-recording.ico", 20, 40)]
    [InlineData("scribe-recording.ico", 24, 40)]
    [InlineData("scribe-recording.ico", 32, 40)]
    [InlineData("scribe-recording.ico", 40, 40)]
    [InlineData("scribe-processing.ico", 16, 40)]
    [InlineData("scribe-processing.ico", 20, 40)]
    [InlineData("scribe-processing.ico", 24, 12)]
    [InlineData("scribe-processing.ico", 32, 12)]
    [InlineData("scribe-processing.ico", 40, 12)]
    [InlineData("scribe-processing.ico", 48, 12)]
    [InlineData("scribe-processing.ico", 64, 12)]
    [InlineData("scribe-processing.ico", 128, 12)]
    [InlineData("scribe-processing.ico", 256, 12)]
    [InlineData("scribe-paused.ico", 16, 40)]
    [InlineData("scribe-paused.ico", 20, 40)]
    [InlineData("scribe-paused.ico", 24, 12)]
    [InlineData("scribe-paused.ico", 32, 12)]
    [InlineData("scribe-paused.ico", 40, 12)]
    [InlineData("scribe-paused.ico", 48, 12)]
    [InlineData("scribe-paused.ico", 64, 12)]
    [InlineData("scribe-paused.ico", 128, 12)]
    [InlineData("scribe-paused.ico", 256, 12)]
    public void State_frames_are_mirrored_left_to_right(string fileName, int size, int threshold)
    {
        using var bitmap = IconFile.Read(AssetPath(fileName)).Frame(size).Decode();

        AssertMirrorWithin(bitmap, threshold, horizontal: true);
    }

    [Theory]
    [InlineData("scribe-processing.ico", 24)]
    [InlineData("scribe-processing.ico", 32)]
    [InlineData("scribe-processing.ico", 40)]
    [InlineData("scribe-processing.ico", 48)]
    [InlineData("scribe-processing.ico", 64)]
    [InlineData("scribe-processing.ico", 128)]
    [InlineData("scribe-processing.ico", 256)]
    [InlineData("scribe-paused.ico", 24)]
    [InlineData("scribe-paused.ico", 32)]
    [InlineData("scribe-paused.ico", 40)]
    [InlineData("scribe-paused.ico", 48)]
    [InlineData("scribe-paused.ico", 64)]
    [InlineData("scribe-paused.ico", 128)]
    [InlineData("scribe-paused.ico", 256)]
    public void Processing_and_paused_large_frames_are_mirrored_top_to_bottom(string fileName, int size)
    {
        using var bitmap = IconFile.Read(AssetPath(fileName)).Frame(size).Decode();

        AssertMirrorWithin(bitmap, 12, horizontal: false);
    }

    [Fact]
    public void Generator_brand_literals_match_scribe_brand()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "New-TrayIcons.ps1"));
        var matches = BrandLiteralRegex().Matches(script)
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);

        Assert.Equal(ToHex(ScribeBrand.Ink), matches["Ink"]);
        Assert.Equal(ToHex(ScribeBrand.Paper), matches["Paper"]);
        Assert.Equal(ToHex(ScribeBrand.Signal), matches["Signal"]);
        Assert.Equal(ToHex(ScribeBrand.Slate), matches["Slate"]);
        Assert.Equal(ToHex(ScribeBrand.ProcessingDots), matches["ProcessingDots"]);
    }

    [GeneratedRegex("private static readonly Rgb (Ink|Paper|Signal|Slate|ProcessingDots) = Rgb\\.Parse\\(\\\"(#[0-9A-F]{6})\\\"\\);")]
    private static partial Regex BrandLiteralRegex();

    private static Color Brand(string name) => name switch
    {
        "Ink" => ToColor(ScribeBrand.Ink),
        "Paper" => ToColor(ScribeBrand.Paper),
        "Signal" => ToColor(ScribeBrand.Signal),
        "Slate" => ToColor(ScribeBrand.Slate),
        "ProcessingDots" => ToColor(ScribeBrand.ProcessingDots),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static Color ToColor(SrgbColor color) => Color.FromArgb(255, color.R, color.G, color.B);

    private static string ToHex(SrgbColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static void AssertSmallGrid(Bitmap bitmap, Color baseColor, IReadOnlyList<PixelRect> shapes, Color fill, Color corner)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A == 0)
                {
                    continue;
                }

                var expected = baseColor;
                foreach (var shape in shapes)
                {
                    if (shape.Contains(x, y))
                    {
                        expected = shape.IsCorner(x, y) ? corner : fill;
                        break;
                    }
                }

                Assert.True(IsClose(expected, pixel), $"Expected {expected} at {x},{y} but found {pixel}.");
            }
        }
    }

    private static PixelRect FindCapsuleBox(Bitmap bitmap)
    {
        PixelRect? bounds = null;
        var paper = ToColor(ScribeBrand.Paper);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 0 && (IsClose(paper, pixel) || IsSourceWaveform(pixel)))
                {
                    bounds = bounds is null ? new PixelRect(x, y, 1, 1) : bounds.Value.Include(x, y);
                }
            }
        }

        Assert.NotNull(bounds);
        return bounds.Value;
    }

    private static PixelRect FindSourceWaveformBox(Bitmap bitmap)
    {
        PixelRect? bounds = null;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (IsSourceWaveform(bitmap.GetPixel(x, y)))
                {
                    bounds = bounds is null ? new PixelRect(x, y, 1, 1) : bounds.Value.Include(x, y);
                }
            }
        }

        Assert.NotNull(bounds);
        return bounds.Value;
    }

    private static void AssertContainsClose(Color expected, Bitmap bitmap, int x, int y)
    {
        const int radius = 3;
        for (var sampleY = Math.Max(0, y - radius); sampleY <= Math.Min(bitmap.Height - 1, y + radius); sampleY++)
        {
            for (var sampleX = Math.Max(0, x - radius); sampleX <= Math.Min(bitmap.Width - 1, x + radius); sampleX++)
            {
                if (IsClose(expected, bitmap.GetPixel(sampleX, sampleY)))
                {
                    return;
                }
            }
        }

        Assert.Fail($"Expected {expected} near {x},{y} in {bitmap.Width} px frame.");
    }
    private static bool IsClose(Color expected, Color actual) =>
        Math.Abs(expected.R - actual.R) <= 2 &&
        Math.Abs(expected.G - actual.G) <= 2 &&
        Math.Abs(expected.B - actual.B) <= 2;

    private static void AssertMirrorWithin(Bitmap bitmap, int threshold, bool horizontal)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var mirrorX = horizontal ? bitmap.Width - 1 - x : x;
                var mirrorY = horizontal ? y : bitmap.Height - 1 - y;
                var left = bitmap.GetPixel(x, y);
                var right = bitmap.GetPixel(mirrorX, mirrorY);
                Assert.True(
                    ChannelDifference(left, right) <= threshold,
                    $"Mirror difference exceeded {threshold} at {x},{y} vs {mirrorX},{mirrorY}: {left} vs {right}.");
            }
        }
    }

    private static int ChannelDifference(Color left, Color right) => Math.Max(
        Math.Max(Math.Abs(left.R - right.R), Math.Abs(left.G - right.G)),
        Math.Max(Math.Abs(left.B - right.B), Math.Abs(left.A - right.A)));

    private static bool IsSourceWaveform(Color color) => color.A > 0 && color.B > color.R + 40 && color.B > 150;

    private static Color Blend(Color foreground, Color background, double amount) => Color.FromArgb(
        255,
        (int)Math.Round(foreground.R * amount + background.R * (1 - amount)),
        (int)Math.Round(foreground.G * amount + background.G * (1 - amount)),
        (int)Math.Round(foreground.B * amount + background.B * (1 - amount)));

    private static string AssetPath(string fileName) =>
        Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Assets", fileName);

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    private sealed class IconFile
    {
        public IconFile(IReadOnlyList<IconFrame> frames) => Frames = frames.OrderBy(frame => frame.Size).ToArray();

        public IReadOnlyList<IconFrame> Frames { get; }

        public static IconFile Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var count = BitConverter.ToUInt16(bytes, 4);
            var frames = new List<IconFrame>();
            for (var i = 0; i < count; i++)
            {
                var entry = 6 + i * 16;
                var width = bytes[entry] == 0 ? 256 : bytes[entry];
                var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
                var length = BitConverter.ToInt32(bytes, entry + 8);
                var offset = BitConverter.ToInt32(bytes, entry + 12);
                var payload = new byte[length];
                Array.Copy(bytes, offset, payload, 0, length);
                frames.Add(new IconFrame(width, height, payload));
            }

            return new IconFile(frames);
        }

        public IconFrame Frame(int size) => Frames.Single(frame => frame.Size == size);
    }

    private sealed class IconFrame
    {
        private readonly int _height;

        public IconFrame(int width, int height, byte[] payload)
        {
            Size = width;
            _height = height;
            Payload = payload;
        }

        public int Size { get; }

        public byte[] Payload { get; }

        public bool IsPng => Payload.Length > 8 && Payload[0] == 0x89 && Payload[1] == 0x50 && Payload[2] == 0x4E && Payload[3] == 0x47;

        public Bitmap Decode()
        {
            Assert.Equal(Size, _height);
            if (IsPng)
            {
                using var stream = new MemoryStream(Payload);
                using var decoded = new Bitmap(stream);
                return new Bitmap(decoded);
            }

            return DecodeDib();
        }

        private Bitmap DecodeDib()
        {
            var dibWidth = BitConverter.ToInt32(Payload, 4);
            var dibHeight = BitConverter.ToInt32(Payload, 8) / 2;
            Assert.Equal(Size, dibWidth);
            Assert.Equal(_height, dibHeight);
            var bitmap = new Bitmap(Size, _height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var pixelOffset = BitConverter.ToInt32(Payload, 0);
            var stride = Size * 4;
            for (var y = 0; y < _height; y++)
            {
                var sourceY = _height - 1 - y;
                var row = pixelOffset + sourceY * stride;
                for (var x = 0; x < Size; x++)
                {
                    var index = row + x * 4;
                    bitmap.SetPixel(x, y, Color.FromArgb(Payload[index + 3], Payload[index + 2], Payload[index + 1], Payload[index]));
                }
            }

            return bitmap;
        }
    }

    private readonly record struct PixelRect(int Left, int Top, int Width, int Height)
    {
        public int Right => Left + Width - 1;

        public int Bottom => Top + Height - 1;

        public bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

        public bool IsCorner(int x, int y) => (x == Left || x == Right) && (y == Top || y == Bottom);

        public PixelRect Include(int x, int y)
        {
            var left = Math.Min(Left, x);
            var top = Math.Min(Top, y);
            var right = Math.Max(Right, x);
            var bottom = Math.Max(Bottom, y);
            return new PixelRect(left, top, right - left + 1, bottom - top + 1);
        }
    }
}

