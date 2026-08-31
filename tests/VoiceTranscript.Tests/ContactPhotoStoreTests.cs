using System.Windows.Media;
using System.Windows.Media.Imaging;
using VoiceTranscript.App.Services;

namespace VoiceTranscript.Tests;

/// <summary>
/// Bringing a photo into the archive: copied, shrunk, and owned.
///
/// The properties under test are the ones a person would eventually notice missing: a huge photo
/// stays huge on disk forever, a corrupt file crashes the profile tab, a replaced photo shows the
/// old face because the image cache keyed on the name.
/// </summary>
public sealed class ContactPhotoStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-photo-{Guid.NewGuid():N}");
    private readonly string _photos;

    public ContactPhotoStoreTests()
    {
        _photos = Path.Combine(_root, "photos");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A real PNG of the given size, drawn in memory.</summary>
    private string Png(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(Brushes.SteelBlue, null, new System.Windows.Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.png");
        using var stream = File.Create(path);
        encoder.Save(stream);

        return path;
    }

    private static (int Width, int Height) SizeOf(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return (frame.PixelWidth, frame.PixelHeight);
    }

    [Fact]
    public void ABigPhotoIsShrunkToTheEdgeLimitKeepingItsShape()
    {
        var source = Png(2000, 1000);

        var stored = ContactPhotoStore.Import(source, contactId: 1, _photos);

        Assert.NotNull(stored);

        var (w, h) = SizeOf(Path.Combine(_photos, stored));

        Assert.Equal(ContactPhotoStore.MaxEdge, w);
        Assert.InRange(h, 254, 258); // half the width, give or take rounding — aspect kept.

        // The original is untouched where it was found.
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void ASmallPhotoIsNotUpscaled()
    {
        var stored = ContactPhotoStore.Import(Png(100, 100), contactId: 2, _photos);

        var (w, h) = SizeOf(Path.Combine(_photos, stored!));

        Assert.Equal(100, w);
        Assert.Equal(100, h);
    }

    [Fact]
    public void ACorruptFileFailsSoftAndWritesNothing()
    {
        var junk = Path.Combine(_root, "junk.jpg");
        File.WriteAllBytes(junk, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Null(ContactPhotoStore.Import(junk, contactId: 3, _photos));

        Assert.False(Directory.Exists(_photos) && Directory.EnumerateFiles(_photos).Any());
    }

    /// <summary>
    /// A replacement gets a new filename. WPF caches bitmaps by URI, so re-using the name shows
    /// the old face until restart — a bug nobody can diagnose from what the screen shows.
    /// </summary>
    [Fact]
    public void ReplacementsGetFreshNames()
    {
        var first = ContactPhotoStore.Import(Png(300, 300), contactId: 4, _photos);

        // The name carries a millisecond stamp; a same-millisecond import would collide.
        Thread.Sleep(5);

        var second = ContactPhotoStore.Import(Png(300, 300), contactId: 4, _photos);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DeleteIsQuietWhenTheFileIsAlreadyGone()
    {
        ContactPhotoStore.Delete("yok.jpg", _photos);
        ContactPhotoStore.Delete(null, _photos);
    }
}
