using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SSCapture.Capture;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void CursorTests()
    {
        Run("カーソルのホットスポットを画面内の物理座標に変換", () =>
        {
            Equal(new Point(90, 45), CursorSnapshot.DrawLocation(new Point(110, 65), new Point(10, 10), new Point(10, 10)));
            Equal(new Point(17, 8), CursorSnapshot.DrawLocation(new Point(-1900, -280), new Point(3, 12), new Point(-1920, -300)));
            Equal(new Point(-2, -3), CursorSnapshot.DrawLocation(new Point(-20, -10), new Point(2, 3), new Point(-20, -10)));
        });
        Run("カーソルは既定では含めない", () =>
        {
            var method = typeof(DesktopSnapshot).GetMethod("Take", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            Equal(false, (bool)method.GetParameters()[0].DefaultValue!);
        });
        Run("凍結カーソルは元画像の変更と解放の影響を受けない", () =>
        {
            Bitmap source = new(4, 4);
            using (Graphics initial = Graphics.FromImage(source)) initial.Clear(Color.Red);
            using CursorSnapshot cursor = CursorSnapshot.FromImage(source, new Point(-95, -45), new Point(1, 1));
            source.SetPixel(0, 0, Color.Blue);
            source.Dispose();
            using Bitmap target = CursorTarget();
            cursor.Draw(target, new Point(-100, -50));
            Equal(Color.Red.ToArgb(), target.GetPixel(4, 4).ToArgb());
            Equal(Color.Red.ToArgb(), target.GetPixel(7, 7).ToArgb());
            Equal(Color.White.ToArgb(), target.GetPixel(3, 3).ToArgb());
        });
        Run("カーソルが画像端を跨ぐときは範囲内だけ描画", () =>
        {
            using Bitmap source = new(4, 4);
            using (Graphics initial = Graphics.FromImage(source)) initial.Clear(Color.Red);
            using CursorSnapshot cursor = CursorSnapshot.FromImage(source, new Point(-100, -50), new Point(2, 3));
            using Bitmap target = CursorTarget();
            cursor.Draw(target, new Point(-100, -50));
            Equal(Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
            Equal(Color.Red.ToArgb(), target.GetPixel(1, 0).ToArgb());
            Equal(Color.White.ToArgb(), target.GetPixel(2, 0).ToArgb());
            Equal(Color.White.ToArgb(), target.GetPixel(0, 1).ToArgb());
        });
        Run("画像外のカーソルは画像を変更しない", () =>
        {
            using Bitmap source = new(4, 4);
            using (Graphics initial = Graphics.FromImage(source)) initial.Clear(Color.Red);
            using CursorSnapshot cursor = CursorSnapshot.FromImage(source, new Point(50, 50), Point.Empty);
            using Bitmap target = CursorTarget();
            cursor.Draw(target, Point.Empty);
            Equal(Color.White.ToArgb(), target.GetPixel(0, 0).ToArgb());
            Equal(Color.White.ToArgb(), target.GetPixel(11, 11).ToArgb());
        });
        Run("カーソルの透過とモニタ間のクリップを維持", () =>
        {
            using Bitmap source = new(4, 4, PixelFormat.Format32bppArgb);
            source.SetPixel(0, 0, Color.FromArgb(128, 255, 0, 0));
            source.SetPixel(2, 0, Color.Red);
            using CursorSnapshot cursor = CursorSnapshot.FromImage(source, new Point(3, 3), Point.Empty);
            using Bitmap target = CursorTarget();
            using Region clip = new(new Rectangle(0, 0, 5, 12));
            cursor.Draw(target, Point.Empty, clip);
            Color blend = target.GetPixel(3, 3);
            Check(blend.A == 255 && blend.R == 255 && blend.G is >= 126 and <= 128, "半透明カーソルを背景と合成する");
            Equal(Color.White.ToArgb(), target.GetPixel(4, 3).ToArgb());
            Equal(Color.White.ToArgb(), target.GetPixel(5, 3).ToArgb());
        });
        Run("カーソルの解放は複数回可能で解放後の描画を拒否", () =>
        {
            using Bitmap source = new(2, 2);
            CursorSnapshot cursor = CursorSnapshot.FromImage(source, Point.Empty, Point.Empty);
            cursor.Dispose();
            cursor.Dispose();
            using Bitmap target = CursorTarget();
            bool rejected = false;
            try { cursor.Draw(target, Point.Empty); }
            catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "破棄済みリソースを使用しない");
            source.SetPixel(0, 0, Color.Blue);
            Equal(Color.Blue.ToArgb(), source.GetPixel(0, 0).ToArgb());
        });
        Run("合成HICONのコピーは元ハンドル解放後も背景と不透明度を維持して描画", () =>
        {
            using Bitmap source = new(8, 8, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(source)) graphics.Clear(Color.Red);
            nint icon = source.GetHicon();
            CursorSnapshot? cursor;
            try { cursor = CursorSnapshot.FromIcon(icon, new Point(6, 6)); }
            finally { DestroyTestIcon(icon); }
            Check(cursor is not null, "合成したネイティブアイコンを複製");
            using (cursor)
            {
                using Bitmap target = CursorTarget();
                cursor!.Draw(target, Point.Empty);
                Equal(Color.Red.ToArgb(), target.GetPixel(3, 3).ToArgb());
                Equal(Color.White.ToArgb(), target.GetPixel(0, 0).ToArgb());
                Equal(255, (int)target.GetPixel(3, 3).A);
            }
            Check(CursorSnapshot.FromIcon(0, Point.Empty) is null, "無効なカーソルは取得なしとして扱う");
        });
        Run("ネイティブカーソル描画も透過と非矩形クリップを維持", () =>
        {
            using Bitmap source = new(8, 8, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(source))
            {
                graphics.Clear(Color.Transparent);
                graphics.FillRectangle(Brushes.Red, 0, 0, 8, 2);
            }
            nint icon = source.GetHicon();
            CursorSnapshot? cursor;
            try { cursor = CursorSnapshot.FromIcon(icon, new Point(4, 4)); }
            finally { DestroyTestIcon(icon); }
            using (cursor)
            {
                using Bitmap target = CursorTarget();
                using Region clip = new(new Rectangle(0, 0, 2, 12));
                clip.Union(new Rectangle(6, 0, 2, 12));
                cursor!.Draw(target, Point.Empty, clip);
                Equal(Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
                Equal(Color.Red.ToArgb(), target.GetPixel(6, 0).ToArgb());
                Equal(Color.White.ToArgb(), target.GetPixel(3, 0).ToArgb());
                Equal(Color.White.ToArgb(), target.GetPixel(0, 3).ToArgb());
                Equal(255, (int)target.GetPixel(0, 3).A);
            }
        });
    }

    private static Bitmap CursorTarget()
    {
        Bitmap target = new(12, 12, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(target);
        graphics.Clear(Color.White);
        return target;
    }

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyTestIcon(nint icon);
}
