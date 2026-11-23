using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace SSCapture.Capture;

/// <summary>指定されたウィンドウのWGCフレームを1枚取得する。</summary>
internal static class WindowStillCapture
{
    private const int CaptureTimeoutMilliseconds = 3000;

    /// <summary>戻り値の画像は呼び出し元で破棄する。</summary>
    internal static async Task<Bitmap> CaptureAsync(CaptureWindow target, bool includeCursor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new NotSupportedException("このWindowsではウィンドウ単独取得を利用できません。");

        try
        {
            return await Task.Run(() => Capture(target.Handle, (target.ProcessId, target.ThreadId), includeCursor, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            CaptureNative.RequireWindowIdentity(target);
            throw new InvalidOperationException("選択したウィンドウをWindows Graphics Captureで取得できませんでした。", error);
        }
    }

    private static Bitmap Capture(nint hwnd, (uint ProcessId, uint ThreadId) identity, bool includeCursor, CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + CaptureTimeoutMilliseconds;
        int closed = 0;
        Bitmap? bitmap = null;

        void OnClosed(GraphicsCaptureItem sender, object args) => Volatile.Write(ref closed, 1);

        void CheckCapture()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref closed) != 0) throw WindowUnavailable();
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("選択したウィンドウの画像を制限時間内に取得できませんでした。");
        }

        void CheckTarget()
        {
            CheckCapture();
            if (GetWindowIdentity(hwnd) != identity) throw WindowUnavailable();
            if (IsIconic(hwnd))
                throw new InvalidOperationException("選択したウィンドウは最小化されています。ウィンドウを表示してから撮影してください。");
        }

        CheckTarget();
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("この環境ではWindows Graphics Captureを利用できません。");

        try
        {
            using (var graphics = new CaptureDirect3D())
            {
                CheckTarget();
                var item = CaptureDirect3D.CreateWindowItem(hwnd);
                item.Closed += OnClosed;
                try
                {
                    CheckTarget();
                    SizeInt32 poolSize = item.Size;
                    while (bitmap is null)
                    {
                        CheckTarget();
                        ValidateSize(poolSize);
                        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                            graphics.ProjectedDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, poolSize);
                        using var session = pool.CreateCaptureSession(item);
                        session.IsCursorCaptureEnabled = includeCursor;
                        CheckTarget();
                        session.StartCapture();

                        while (true)
                        {
                            CheckTarget();
                            using var frame = pool.TryGetNextFrame();
                            if (frame is null)
                            {
                                Thread.Sleep(10);
                                continue;
                            }

                            var size = frame.ContentSize;
                            ValidateSize(size);
                            if (size.Width > poolSize.Width || size.Height > poolSize.Height)
                            {
                                // 取得中に拡大した場合は、同じ対象で完全な初回フレームを要求し直す。
                                poolSize = size;
                                break;
                            }

                            using var surface = frame.Surface;
                            bitmap = graphics.ReadBitmap(surface, size.Width, size.Height, CheckCapture);
                            break;
                        }
                    }
                }
                finally
                {
                    item.Closed -= OnClosed;
                }
            }

            CheckTarget();
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            throw;
        }
    }

    private static void ValidateSize(SizeInt32 size)
    {
        const int maxTextureDimension = 16384;
        if (size.Width is < 1 or > maxTextureDimension || size.Height is < 1 or > maxTextureDimension)
            throw new InvalidOperationException("選択したウィンドウの画像サイズは対応範囲外です。");
    }

    private static (uint ProcessId, uint ThreadId) GetWindowIdentity(nint hwnd)
    {
        if (hwnd == 0) throw WindowUnavailable();
        uint threadId = GetWindowThreadProcessId(hwnd, out uint processId);
        if (threadId == 0 || processId == 0) throw WindowUnavailable();
        return (processId, threadId);
    }

    private static InvalidOperationException WindowUnavailable() =>
        new("選択したウィンドウは利用できなくなりました。選び直してください。");

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
}
