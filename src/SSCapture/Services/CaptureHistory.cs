namespace SSCapture.Services;

internal sealed record HistoryEntry(Guid Id, DateTimeOffset CapturedAt, Size Size, string? SavedPath, Rectangle? CaptureBounds = null);

// Own only memory. In particular, clipboard-only captures never become disk files.
internal sealed class CaptureHistory(int capacity = 20, long byteLimit = 256L * 1024 * 1024) : IDisposable
{
    private readonly List<(HistoryEntry Entry, Bitmap Image, long Bytes)> items = [];
    private readonly object gate = new();
    private long usedBytes;
    private bool disposed;

    internal IReadOnlyList<HistoryEntry> Entries { get { lock (gate) return items.Select(item => item.Entry).ToArray(); } }
    internal void Add(Bitmap image, string? savedPath, DateTimeOffset? capturedAt = null, Rectangle? captureBounds = null)
    {
        long bytes = checked((long)image.Width * image.Height * 4);
        if (capacity <= 0 || bytes > byteLimit) return;
        var owned = (Bitmap)image.Clone();
        lock (gate)
        {
            if (disposed) { owned.Dispose(); return; }
            DateTimeOffset captureTime = capturedAt ?? DateTimeOffset.Now;
            int index = items.FindIndex(item => item.Entry.CapturedAt <= captureTime);
            items.Insert(index < 0 ? items.Count : index, (new HistoryEntry(Guid.NewGuid(), captureTime, image.Size, savedPath, captureBounds), owned, bytes));
            usedBytes += bytes;
            while (items.Count > capacity || usedBytes > byteLimit) RemoveAt(items.Count - 1);
        }
    }
    internal Bitmap? CloneImage(Guid id)
    {
        lock (gate)
        {
            var item = items.FirstOrDefault(item => item.Entry.Id == id);
            return item.Image is null ? null : (Bitmap)item.Image.Clone();
        }
    }
    internal void Clear() { lock (gate) while (items.Count > 0) RemoveAt(items.Count - 1); }
    private void RemoveAt(int index)
    {
        var item = items[index]; items.RemoveAt(index); usedBytes -= item.Bytes; item.Image.Dispose();
    }
    public void Dispose() { lock (gate) { disposed = true; Clear(); } }
}
