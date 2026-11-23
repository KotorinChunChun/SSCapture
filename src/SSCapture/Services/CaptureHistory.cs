namespace SSCapture.Services;

internal sealed record HistoryEntry(Guid Id, DateTimeOffset CapturedAt, Size Size, string? SavedPath, Rectangle? CaptureBounds = null);

// 保存済みの撮影ファイルは参照として保持する。履歴が管理するのは独自の画像コピーのみ。
internal sealed class CaptureHistory(int capacity = 20, long byteLimit = 256L * 1024 * 1024,
    CaptureHistoryStorage? storage = null, TimeProvider? clock = null, Action<string>? onStorageError = null) : IDisposable
{
    internal const int MaximumRetentionDays = 365;
    private readonly List<(HistoryEntry Entry, Bitmap Image, long Bytes)> items = [];
    private readonly object gate = new();
    private long usedBytes;
    private int retentionDays;
    private bool disposed, persistent, enabled = true;
    private DateTimeOffset UtcNow => (clock ?? TimeProvider.System).GetUtcNow();

    internal IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (gate) { Prune(); return items.Select(item => item.Entry).ToArray(); } }
    }

    internal void Configure(bool keep, int days, bool persist, bool restore = false)
    {
        lock (gate)
        {
            retentionDays = days;
            enabled = keep;
            bool wasPersistent = persistent;
            persistent = storage is not null && keep && persist;
            if (persistent && restore)
                TryStorage(Restore);
            if ((wasPersistent || restore) && !persistent && storage is not null)
                TryStorage(storage.Clear);
            if (!keep) Clear();
            else
            {
                Prune();
                if (persistent && !wasPersistent && !restore)
                    foreach (var item in items) TryStorage(() => storage!.Save(item.Entry, item.Image));
            }
        }
    }

    internal void Add(Bitmap image, string? savedPath, DateTimeOffset? capturedAt = null, Rectangle? captureBounds = null)
    {
        long bytes = checked((long)image.Width * image.Height * 4);
        if (capacity <= 0 || bytes > byteLimit) return;
        lock (gate)
        {
            if (disposed || !enabled) return;
            Prune();
            DateTimeOffset captureTime = capturedAt ?? UtcNow.ToLocalTime();
            if (Expired(captureTime)) return;
            var entry = new HistoryEntry(Guid.NewGuid(), captureTime, image.Size, savedPath, captureBounds);
            var owned = (Bitmap)image.Clone();
            int index = items.FindIndex(item => item.Entry.CapturedAt <= captureTime);
            index = index < 0 ? items.Count : index;
            items.Insert(index, (entry, owned, bytes));
            usedBytes += bytes;
            while (items.Count > capacity || usedBytes > byteLimit) RemoveAt(items.Count - 1);
            if (persistent && index < items.Count) TryStorage(() => storage!.Save(entry, owned));
        }
    }
    internal Bitmap? CloneImage(Guid id)
    {
        lock (gate)
        {
            Prune();
            var item = items.FirstOrDefault(item => item.Entry.Id == id);
            return item.Image is null ? null : (Bitmap)item.Image.Clone();
        }
    }

    internal HistoryEntry? Find(Guid id)
    {
        lock (gate) { Prune(); return items.FirstOrDefault(item => item.Entry.Id == id).Entry; }
    }

    internal void UpdateSavedPath(string previous, string? current)
    {
        lock (gate)
        {
            Prune();
            for (int index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (string.Equals(item.Entry.SavedPath, previous, StringComparison.OrdinalIgnoreCase))
                {
                    var entry = item.Entry with { SavedPath = current };
                    items[index] = (entry, item.Image, item.Bytes);
                    if (persistent) TryStorage(() => storage!.Update(entry));
                }
            }
        }
    }

    internal int Remove(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        lock (gate)
        {
            int before = items.Count;
            for (int index = items.Count - 1; index >= 0; index--)
                if (selected.Contains(items[index].Entry.Id)) RemoveAt(index);
            return before - items.Count;
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            ReleaseImages();
            if (persistent) TryStorage(() => storage!.Clear());
        }
    }

    private bool Expired(DateTimeOffset time) => retentionDays > 0 && UtcNow - time >= TimeSpan.FromDays(retentionDays);

    internal bool Prune()
    {
        lock (gate)
        {
            int before = items.Count;
            while (items.Count > 0 && Expired(items[^1].Entry.CapturedAt)) RemoveAt(items.Count - 1);
            return items.Count != before;
        }
    }

    private void Restore()
    {
        foreach (var entry in storage!.ReadEntries().OrderByDescending(entry => entry.CapturedAt))
        {
            long bytes = (long)entry.Size.Width * entry.Size.Height * 4;
            if (Expired(entry.CapturedAt) || items.Count >= capacity || bytes > byteLimit - usedBytes)
            {
                storage.Remove(entry.Id);
                continue;
            }
            if (storage.ReadImage(entry, byteLimit) is { } image)
            {
                items.Add((entry, image, bytes));
                usedBytes += bytes;
            }
            else storage.Remove(entry.Id);
        }
    }

    private void TryStorage(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { onStorageError?.Invoke("撮影履歴をディスク上で更新できません: " + ex.Message); }
    }

    private void RemoveAt(int index)
    {
        var item = items[index]; items.RemoveAt(index); usedBytes -= item.Bytes; item.Image.Dispose();
        if (persistent) TryStorage(() => storage!.Remove(item.Entry.Id));
    }

    private void ReleaseImages()
    {
        foreach (var item in items) item.Image.Dispose();
        items.Clear();
        usedBytes = 0;
    }

    public void Dispose() { lock (gate) { disposed = true; ReleaseImages(); } }
}
