using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Text.Json;

namespace SSCapture.Services;

internal sealed class CaptureHistoryStorage(string directory)
{
    internal IReadOnlyList<HistoryEntry> ReadEntries()
    {
        if (!OpenDirectory()) return [];
        var entries = new List<HistoryEntry>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out Guid id) || !RegularFile(file)) continue;
            try
            {
                using var input = File.OpenRead(file);
                if (input.Length <= 16 * 1024 && JsonSerializer.Deserialize<HistoryEntry>(input) is { } entry && entry.Id == id
                    && entry.Size.Width > 0 && entry.Size.Height > 0 && entry.Size.Width <= 32768 && entry.Size.Height <= 32768)
                {
                    entries.Add(entry);
                    continue;
                }
            }
            catch (JsonException) { }
            Remove(id);
        }
        var committed = entries.Select(entry => entry.Id).ToHashSet();
        foreach (string file in Directory.EnumerateFiles(directory, "*.png"))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out Guid id) && !committed.Contains(id))
                DeleteRegular(file);
        }
        foreach (string file in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            if (IsTemporary(file)) DeleteRegular(file);
        }
        return entries;
    }

    internal Bitmap? ReadImage(HistoryEntry entry, long byteLimit)
    {
        string file = EntryPath(entry.Id, ".png");
        if (!OpenDirectory() || !RegularFile(file)) return null;
        using var input = File.OpenRead(file);
        Span<byte> header = stackalloc byte[24];
        if (input.Length < header.Length || input.Length > byteLimit + 1024 * 1024) return null;
        input.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !header[12..16].SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadInt32BigEndian(header[16..20]) != entry.Size.Width
            || BinaryPrimitives.ReadInt32BigEndian(header[20..24]) != entry.Size.Height) return null;
        input.Position = 0;
        try
        {
            using var image = new Bitmap(input);
            return image.Size == entry.Size ? (Bitmap)image.Clone() : null;
        }
        catch (ArgumentException) { return null; }
    }

    internal void Save(HistoryEntry entry, Bitmap image)
    {
        OpenDirectory(create: true);
        string path = EntryPath(entry.Id, ".png");
        if (!RegularFile(path))
            AtomicFile.Write(path, stream => image.Save(stream, ImageFormat.Png), overwrite: false);
        Update(entry);
    }

    internal void Update(HistoryEntry entry)
    {
        if (OpenDirectory() && RegularFile(EntryPath(entry.Id, ".png")))
            AtomicFile.Write(EntryPath(entry.Id, ".json"), stream => JsonSerializer.Serialize(stream, entry));
    }

    internal void Remove(Guid id)
    {
        if (!OpenDirectory()) return;
        DeleteRegular(EntryPath(id, ".json"));
        DeleteRegular(EntryPath(id, ".png"));
    }

    internal void Clear()
    {
        if (!OpenDirectory()) return;
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (IsTemporary(file) || Path.GetExtension(file) is ".json" or ".png"
                && Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _))
                DeleteRegular(file);
        }
    }

    private bool OpenDirectory(bool create = false)
    {
        if (create) Directory.CreateDirectory(directory);
        if (!Directory.Exists(directory)) return false;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("履歴フォルダを参照できません。");
        return true;
    }

    private string EntryPath(Guid id, string extension) => Path.Combine(directory, id.ToString("N") + extension);

    private static bool IsTemporary(string path) => Path.GetFileName(path).Split('.') is ["", var id, "json" or "png", var nonce, "tmp"]
        && Guid.TryParseExact(id, "N", out _) && Guid.TryParseExact(nonce, "N", out _);

    private static bool RegularFile(string path) => File.Exists(path)
        && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

    private static void DeleteRegular(string path)
    {
        if (RegularFile(path)) File.Delete(path);
    }
}
