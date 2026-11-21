using System.Drawing.Imaging;

namespace SSCapture.Services;

internal readonly record struct WindowCrop(int Top, int Bottom, int Left, int Right)
{
    internal bool IsEmpty => this == default;

    internal void Validate()
    {
        if (Top < 0 || Bottom < 0 || Left < 0 || Right < 0)
            throw new ArgumentException("ウィンドウの切り抜き量は0以上の整数で指定してください。");
    }

    internal Bitmap Apply(Bitmap image)
    {
        Validate();
        if ((long)Left + Right >= image.Width)
            throw new ArgumentException("左右の切り抜き量が画像の幅以上です。");
        if ((long)Top + Bottom >= image.Height)
            throw new ArgumentException("上下の切り抜き量が画像の高さ以上です。");
        return image.Clone(new Rectangle(Left, Top, image.Width - Left - Right, image.Height - Top - Bottom), PixelFormat.DontCare);
    }
}
