using System.Globalization;
using System.Drawing.Imaging;

namespace SSCapture.Services;

internal static class CaptureStorage
{
    public static string SavePng(Bitmap image, string folder, DateTimeOffset capturedAt, string? fileName = null, FileNamePlan? namePlan = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string directory = Path.GetFullPath(folder);
        fileName = namePlan?.Candidate(0) ?? fileName ?? capturedAt.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".png";
        FileNamePattern.ValidateFileName(fileName);
        if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("静止画の拡張子は.pngです。");
        string prefix = fileName[..^4];
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".SSCapture-{Guid.NewGuid():N}.tmp");
        bool ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                image.Save(stream, ImageFormat.Png);
                stream.Flush(true);
            }
            for (int sequence = 0; ; sequence++)
            {
                string suffix = sequence == 0 ? "" : $"_{sequence:D4}";
                string candidate = namePlan?.Candidate(sequence) ?? $"{prefix}{suffix}.png";
                FileNamePattern.ValidateFileName(candidate);
                string destination = Path.Combine(directory, candidate);
                try
                {
                    // Move with overwrite=false publishes only a complete PNG. A racing
                    // saver may win the filename, but can never overwrite another image.
                    File.Move(temporary, destination, false);
                    ownsTemporary = false;
                    return destination;
                }
                catch (IOException) when (File.Exists(destination)) { }
            }
        }
        finally
        {
            if (ownsTemporary)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
