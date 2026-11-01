namespace SSCapture.Services;

/// <summary>通知だけが所有する画像。開く操作までディスクへ書かない。</summary>
internal sealed class NotificationImage : IDisposable
{
    private Bitmap? image;
    private readonly string directory;
    internal NotificationImage(Bitmap image, string directory)
    {
        this.image = (Bitmap)image.Clone();
        this.directory = directory;
    }

    internal Task<string> PrepareAsync()
    {
        // 通知の差替え・撮影開始が非同期保存へ割り込んでも、この保存用コピーは生存する。
        var bitmap = (Bitmap)(image ?? throw new ObjectDisposedException(nameof(NotificationImage))).Clone();
        return Task.Run(() =>
        {
            using (bitmap) return CaptureStorage.SavePng(bitmap, directory, DateTimeOffset.Now, "notify-" + Guid.NewGuid().ToString("N") + ".png");
        });
    }

    internal Bitmap CreateThumbnail() => Thumbnail(image ?? throw new ObjectDisposedException(nameof(NotificationImage)));
    internal static Bitmap Thumbnail(Image image)
    {
        double scale = Math.Min(88d / image.Width, 88d / image.Height);
        var thumbnail = new Bitmap(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
        using var graphics = Graphics.FromImage(thumbnail);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(image, new Rectangle(Point.Empty, thumbnail.Size));
        return thumbnail;
    }

    internal static void CleanupPreviousSession(string workRoot)
    {
        string folder = Path.Combine(Path.GetFullPath(workRoot), "notification-images");
        if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string path in Directory.EnumerateFiles(folder, "notify-*.png", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != 39 || !Guid.TryParseExact(name[7..], "N", out _)) continue;
            try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void RetainAfterLaunch(string path) => _ = DeleteAfterMinuteAsync(path);
    private static async Task DeleteAfterMinuteAsync(string path)
    {
        await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        // 呼出元が生成した通知用ファイル1つだけ。親フォルダや保存済み画像は削除しない。
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() { image?.Dispose(); image = null; }
}
