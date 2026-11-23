namespace SSCapture.Services;

/// <summary>通知だけが所有する画像。開く操作までディスクへ書かない。</summary>
internal sealed class NotificationImage : IDisposable
{
    private Bitmap? image;
    private readonly string directory;
    private readonly Bitmap? namingImage;
    private readonly HandoffFileName naming;

    internal NotificationImage(Bitmap image, string directory, HandoffFileName? naming = null, Bitmap? namingImage = null)
    {
        this.image = (Bitmap)image.Clone();
        this.directory = directory;
        this.naming = naming ?? new(new(FileNamePattern.Default, DateTimeOffset.Now, CaptureIntent.Screenshot), null, "");
        this.namingImage = namingImage is null ? null : (Bitmap)namingImage.Clone();
    }

    internal Task<TemporaryHandoff> PrepareForHandoffAsync()
    {
        var bitmap = (Bitmap)(image ?? throw new ObjectDisposedException(nameof(NotificationImage))).Clone();
        var ocrImage = namingImage is not null && naming.NeedsOcr ? (Bitmap)namingImage.Clone() : null;
        return Task.Run(async () =>
        {
            using (bitmap)
            using (ocrImage)
            {
                var plan = await naming.ResolveAsync(ocrImage ?? bitmap).ConfigureAwait(false);
                return TemporaryHandoff.Publish(directory, publish =>
                    CaptureStorage.SavePng(bitmap, directory, naming.Plan.CapturedAt, naming.FileName, HandoffFileName.CachePlan(plan), beforePublish: publish));
            }
        });
    }

    internal Task<string> PrepareAsync()
    {
        var pending = PrepareForHandoffAsync();
        return ReadPathAsync();

        async Task<string> ReadPathAsync()
        {
            using var prepared = await pending;
            return prepared.Path;
        }
    }

    internal Bitmap CloneImage() => (Bitmap)(image ?? throw new ObjectDisposedException(nameof(NotificationImage))).Clone();

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

    internal static void CleanupPreviousSession(string workRoot) =>
        TemporaryHandoff.CleanupPreviousSession(TemporaryHandoff.DirectoryFor(workRoot));

    public void Dispose()
    {
        if (image is null) return;
        image.Dispose();
        image = null;
        namingImage?.Dispose();
    }
}
