using System.IO;
using System.Windows.Media.Imaging;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Brings a picked photo into the archive, on the archive's terms.
///
/// The file is copied in, never referenced where it was found: a profile that points at a photo
/// in Downloads breaks the day Downloads is tidied, and the archive must not depend on any folder
/// it does not own. On the way in the image is shrunk (a contact photo is decoration, not
/// evidence) and re-encoded — which also strips EXIF metadata, including GPS coordinates. A
/// stranger's photo carries their location data; the archive keeps their face, not their
/// whereabouts.
/// </summary>
public static class ContactPhotoStore
{
    /// <summary>Longest edge kept. Twice the largest avatar at high DPI, and no more.</summary>
    public const int MaxEdge = 512;

    /// <summary>
    /// Copies and shrinks a picked image into the photos directory.
    ///
    /// Returns the stored FILENAME — never a full path, because the data directory can be moved —
    /// or null when the file cannot be read as an image. Written to a temporary name first, so a
    /// failure mid-encode never leaves a half-photo behind.
    /// </summary>
    public static string? Import(string sourcePath, long contactId, string photosDirectory)
    {
        try
        {
            Directory.CreateDirectory(photosDirectory);

            var frame = ReadOriented(sourcePath);

            // Never upscale: a small photo made large is just a blurry photo.
            var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(frame.PixelWidth, frame.PixelHeight));

            System.Windows.Media.Imaging.BitmapSource output = scale < 1.0
                ? new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale))
                : frame;

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(output));

            // A fresh name per import, deliberately: WPF caches bitmaps by URI, so replacing a
            // photo under the same name shows the old face until restart.
            var name = $"contact-{contactId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.jpg";
            var final = Path.Combine(photosDirectory, name);
            var temp = final + ".tmp";

            using (var stream = File.Create(temp)) encoder.Save(stream);
            File.Move(temp, final);

            return name;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException
                                  or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Corrupt file, unreadable codec, a cloud placeholder that is not really local yet.
            // The caller shows one sentence; nothing was written.
            return null;
        }
    }

    /// <summary>
    /// The image with its EXIF orientation applied. Phone photos are routinely stored sideways
    /// with a flag saying which way is up; ignoring the flag puts a sideways face in the header.
    /// </summary>
    private static BitmapFrame ReadOriented(string sourcePath)
    {
        var frame = BitmapFrame.Create(
            new Uri(sourcePath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        var orientation = (frame.Metadata as BitmapMetadata)?.GetQuery("System.Photo.Orientation");

        var rotation = orientation switch
        {
            ushort and 6 => Rotation.Rotate90,
            ushort and 3 => Rotation.Rotate180,
            ushort and 8 => Rotation.Rotate270,
            _ => Rotation.Rotate0,
        };

        if (rotation == Rotation.Rotate0) return frame;

        var oriented = new BitmapImage();
        oriented.BeginInit();
        oriented.UriSource = new Uri(sourcePath);
        oriented.CacheOption = BitmapCacheOption.OnLoad;
        oriented.Rotation = rotation;
        oriented.EndInit();

        return BitmapFrame.Create(oriented);
    }

    /// <summary>Removes a stored photo. A file already gone is the outcome asked for, not an error.</summary>
    public static void Delete(string? photoFile, string photosDirectory)
    {
        if (string.IsNullOrWhiteSpace(photoFile)) return;

        try
        {
            var path = Path.Combine(photosDirectory, photoFile);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked by a viewer. It carries no reference any more and does no harm where it is.
        }
    }

    /// <summary>Filename → full path, or null for no photo. The one place the join happens.</summary>
    public static string? PathFor(string? photoFile, string photosDirectory) =>
        string.IsNullOrWhiteSpace(photoFile) ? null : Path.Combine(photosDirectory, photoFile);
}
