using System.Drawing;
using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// Scribe.App's <c>TrayIcons</c> makes every tray icon over one template icon per state, because making it from a stream copies
/// the whole .ico (97 to 117 KB, a Large Object Heap array) on every tray state change. The App is not referenced from here,
/// so these tests hold the System.Drawing behaviour that relies on to the shipped files, and read the App's source for the
/// rest: an icon made from a template is the icon the file makes at every tray size, disposing it (what H.NotifyIcon does to
/// the icon it replaces) leaves the template working, it does not copy the file, and the template itself is never handed out.
/// </summary>
public sealed class TrayIconsTemplateSharingTests
{
    private static readonly string[] Files = ["scribe.ico", "scribe-recording.ico", "scribe-processing.ico", "scribe-paused.ico"];

    // GetSystemMetricsForDpi(SM_CXSMICON) from 100% to 300% scaling.
    private static readonly int[] TraySizes = [16, 20, 24, 32, 40, 48];

    public static TheoryData<string, int> FilesAndTraySizes()
    {
        var data = new TheoryData<string, int>();
        foreach (var file in Files)
        {
            foreach (var size in TraySizes)
            {
                data.Add(file, size);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FilesAndTraySizes))]
    public void An_icon_made_from_the_template_is_the_icon_the_file_makes(string fileName, int size)
    {
        var bytes = File.ReadAllBytes(AssetPath(fileName));
        using var template = FromFile(bytes, 16);
        using var fromFile = FromFile(bytes, size);
        using var fromTemplate = new Icon(template, size, size);

        Assert.Equal(fromFile.Size, fromTemplate.Size);
        Assert.Equal(Pixels(fromFile), Pixels(fromTemplate));
    }

    [Theory]
    [InlineData("scribe.ico")]
    [InlineData("scribe-recording.ico")]
    [InlineData("scribe-processing.ico")]
    [InlineData("scribe-paused.ico")]
    public void The_template_keeps_making_icons_after_the_icons_made_from_it_are_disposed(string fileName)
    {
        var bytes = File.ReadAllBytes(AssetPath(fileName));
        using var template = FromFile(bytes, 24);
        var templatePixels = Pixels(template);

        foreach (var size in TraySizes)
        {
            var handedOut = new Icon(template, size, size);
            Assert.NotEqual(IntPtr.Zero, handedOut.Handle);
            handedOut.Dispose();
        }

        using var again = new Icon(template, 20, 20);
        using var fromFile = FromFile(bytes, 20);
        Assert.Equal(Pixels(fromFile), Pixels(again));
        Assert.Equal(templatePixels, Pixels(template));
    }

    [Fact]
    public void TrayIcons_hands_out_fresh_icons_made_over_the_template_and_never_the_template()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Tray", "TrayIcons.cs"))
            .ReplaceLineEndings("\n");

        // The ownership rule the template must not break: H.NotifyIcon disposes the icon it replaces.
        Assert.Contains("Every call hands back a <b>fresh</b> icon, and the caller owns it.", source, StringComparison.Ordinal);

        // The file is read into an icon once per state, into the template, and every call hands back a new icon over it or
        // the framework icon.
        Assert.Single(Regex.Matches(source, Regex.Escape("new Icon(stream, size, size)")));
        Assert.Contains("_template = new Icon(stream, size, size);", source, StringComparison.Ordinal);
        var create = source[source.IndexOf("public Icon Create(int size)", StringComparison.Ordinal)..];
        create = create[..create.IndexOf("private Icon? Template(int size)", StringComparison.Ordinal)];
        Assert.Equal(
            ["return (Icon)SystemIcons.Application.Clone();", "return new Icon(template, size, size);"],
            Regex.Matches(create, @"return [^;]*;").Select(match => match.Value).Order(StringComparer.Ordinal));
    }

    private static Icon FromFile(byte[] bytes, int size)
    {
        using var stream = new MemoryStream(bytes);
        return new Icon(stream, size, size);
    }

    private static int[] Pixels(Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        var pixels = new int[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                pixels[(y * bitmap.Width) + x] = bitmap.GetPixel(x, y).ToArgb();
            }
        }

        return pixels;
    }

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

    // In the collection that runs alone: no other test runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Theory]
        [InlineData("scribe.ico", 16)]
        [InlineData("scribe.ico", 32)]
        [InlineData("scribe-recording.ico", 16)]
        [InlineData("scribe-recording.ico", 32)]
        [InlineData("scribe-processing.ico", 16)]
        [InlineData("scribe-processing.ico", 32)]
        [InlineData("scribe-paused.ico", 16)]
        [InlineData("scribe-paused.ico", 32)]
        public void An_icon_made_from_the_template_does_not_copy_the_file(string fileName, int size)
        {
            const int Icons = 20;
            var bytes = File.ReadAllBytes(AssetPath(fileName));
            using var template = FromFile(bytes, 16);
            for (var i = 0; i < 3; i++)
            {
                new Icon(template, size, size).Dispose();
                FromFile(bytes, size).Dispose();
            }

            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Icons; i++)
            {
                new Icon(template, size, size).Dispose();
            }

            var fromTemplate = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Icons; i++)
            {
                FromFile(bytes, size).Dispose();
            }

            var fromFile = GC.GetAllocatedBytesForCurrentThread() - before;

            // An icon made from the template allocates its own object (measured 56 to 167 bytes on .NET 10); the bound leaves
            // room for the runtime's own work on this thread and is still far below one copy of the file.
            Assert.True(
                fromTemplate < Icons * 1024L,
                $"{Icons} icons made from the template allocated {fromTemplate} bytes on this thread. During it: {during}.");
            Assert.True(
                fromFile >= Icons * (long)bytes.Length,
                $"{Icons} icons made from the file allocated {fromFile} bytes, less than {Icons} copies of its {bytes.Length} bytes.");
        }
    }
}
