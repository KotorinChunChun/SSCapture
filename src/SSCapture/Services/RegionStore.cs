using System.Text.Json;

namespace SSCapture.Services;

internal sealed class RegionStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string filePath = Path.GetFullPath(path);

    internal SavedRegion? Load(out string? warning)
    {
        warning = null;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 2 * 1024 * 1024) throw new JsonException("範囲の保存ファイルが大きすぎます。");
            SavedRegion region = JsonSerializer.Deserialize<SavedRegion>(stream, Options)
                ?? throw new JsonException("撮影範囲が空です。");
            region.Validate();
            return region;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or OverflowException)
        {
            warning = $"前回の撮影範囲を読み込めませんでした。元のファイルは保持されています。{Environment.NewLine}{ex.Message}";
            return null;
        }
    }

    internal void Save(SavedRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        region.Validate();
        AtomicFile.Write(filePath, stream => JsonSerializer.Serialize(stream, region, Options));
    }
}
