namespace SSCapture.Services;

internal static class AtomicFile
{
    // The callback borrows the stream; this helper owns its flush and disposal.
    internal static void Write(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        string filePath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        bool ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                write(stream);
                stream.Flush(true);
            }
            // Keeping both paths on the same volume preserves the old file if
            // serialization or publication fails, without a delete-before-move gap.
            if (File.Exists(filePath)) File.Replace(temporary, filePath, null);
            else File.Move(temporary, filePath);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
            {
                // Delete only the file whose CreateNew succeeded. Cleanup failure
                // must not replace the original write or publication exception.
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
