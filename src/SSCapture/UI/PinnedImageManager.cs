using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class PinnedImageManager(Action<string, bool>? report = null, Func<Bitmap, Task>? copyImage = null) : IDisposable
{
    private readonly List<PinnedImageForm> windows = [];
    private bool disposed;
    internal int Count => windows.Count;
    internal bool Hidden { get; private set; }
    internal IReadOnlyList<PinnedImageForm> Windows => windows.ToArray();
    internal event EventHandler? Changed;
    internal void Show(Bitmap image, Rectangle captureBounds, AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(image);
        // 余白の内側にある元画素を、撮影した物理座標へ合わせる。
        int padding = settings.ScreenshotPaddingPixels;
        long x = (long)captureBounds.X - padding, y = (long)captureBounds.Y - padding;
        if (x < int.MinValue || y < int.MinValue) throw new ArgumentException("ピン留めの表示座標が範囲外です。");
        var window = new PinnedImageForm(image, new((int)x, (int)y), settings, report, copyImage);
        try
        {
            windows.Add(window);
            window.FormClosed += (_, _) => { windows.Remove(window); if (windows.Count == 0) Hidden = false; Changed?.Invoke(this, EventArgs.Empty); };
            // 新規出力は表示する。既存窓の一時非表示を勝手に解除しない。
            window.Show();
            if (Hidden) window.Hide();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch { windows.Remove(window); window.Dispose(); throw; }
    }
    internal void ToggleVisibility()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (windows.Count == 0) return;
        Hidden = !Hidden;
        foreach (var window in windows.ToArray()) { if (Hidden) window.Hide(); else window.Show(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    internal void CloseAll()
    {
        foreach (var window in windows.ToArray()) { window.Close(); window.Dispose(); }
        windows.Clear(); Hidden = false;
    }
    public void Dispose() { if (disposed) return; CloseAll(); disposed = true; }
}
