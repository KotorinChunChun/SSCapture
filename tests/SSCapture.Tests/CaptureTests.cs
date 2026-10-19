using System.Drawing.Imaging;
using SSCapture.Capture;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void CaptureTests()
    {
        Run("GDI撮影引数が.NETの許可する単一ROP値である", () =>
        {
            Check(Enum.IsDefined(DesktopSnapshot.CopyOperation), "CopyFromScreenはROP値のbitwise合成を拒否する");
            Equal(CopyPixelOperation.SourceCopy, DesktopSnapshot.CopyOperation);
        });
        Run("負座標と逆ドラッグを物理矩形へ変換", () =>
        {
            Rectangle expected = new(-1800, -350, 2300, 1000);
            Equal(expected, CaptureGeometry.FromDrag(new Point(500, 650), new Point(-1800, -350)));
            Equal(expected, CaptureGeometry.FromDrag(new Point(-1800, -350), new Point(500, 650)));
        });
        Run("仮想デスクトップの外側をクリップ", () =>
        {
            Equal(new Rectangle(-100, -50, 250, 100),
                CaptureGeometry.Clip(new Rectangle(-200, -100, 350, 150), new Rectangle(-100, -50, 400, 300)));
            Check(!CaptureGeometry.IsUsable(CaptureGeometry.Clip(new Rectangle(1000, 1000, 5, 5),
                new Rectangle(-100, -50, 400, 300))), "範囲外は選択として使えない");
        });
        Run("2px未満の切り出し領域を除外", () =>
        {
            Check(!CaptureGeometry.IsUsable(CaptureGeometry.FromDrag(new Point(-5, 4), new Point(-5, 4))), "クリックを除外");
            Check(!CaptureGeometry.IsUsable(new Rectangle(0, 0, 1, 10)), "細すぎる選択を除外");
            Check(CaptureGeometry.IsUsable(new Rectangle(0, 0, 2, 2)), "2pxの選択は可能");
            using Bitmap source = new(10, 10);
            using Bitmap? empty = CaptureGeometry.Crop(source, new Rectangle(-5, -5, 10, 10), new Rectangle(15, 15, 2, 2));
            Check(empty is null, "範囲外の切り出しはnull");
        });
        Run("合成画像の負座標切り出しで位置と寸法を維持", () =>
        {
            using Bitmap source = PatternBitmap();
            using Bitmap? result = CaptureGeometry.Crop(source, new Rectangle(-20, -10, 12, 12), new Rectangle(-18, -7, 5, 6));
            Check(result is not null, "切り出し画像が必要");
            Equal(new Size(5, 6), result!.Size);
            Equal(source.GetPixel(2, 3).ToArgb(), result.GetPixel(0, 0).ToArgb());
            Equal(source.GetPixel(6, 8).ToArgb(), result.GetPixel(4, 5).ToArgb());
        });
        Run("境界を跨ぐ切り出しもピクセル位置を維持", () =>
        {
            using Bitmap source = PatternBitmap();
            using Bitmap? result = CaptureGeometry.Crop(source, new Rectangle(-20, -10, 12, 12), new Rectangle(-23, -13, 8, 8));
            Equal(new Size(5, 5), result!.Size);
            Equal(source.GetPixel(0, 0).ToArgb(), result.GetPixel(0, 0).ToArgb());
        });
        Run("自由形状の外側は透過、内側は元のピクセル", () =>
        {
            using Bitmap source = PatternBitmap();
            Point[] points = [new(-19, -9), new(-9, -9), new(-19, 1)];
            using Bitmap? result = CaptureGeometry.Freeform(source, new Rectangle(-20, -10, 12, 12), points);
            Equal(new Size(10, 10), result!.Size);
            Equal(0, (int)result.GetPixel(8, 8).A);
            Equal(source.GetPixel(3, 3).ToArgb(), result.GetPixel(2, 2).ToArgb());
        });
        Run("自由形状で点・直線を除外し交差形状は許可", () =>
        {
            Check(!CaptureGeometry.HasPolygonArea([new(0, 0), new(2, 2)]), "2点は除外");
            Check(!CaptureGeometry.HasPolygonArea([new(0, 0), new(0, 0), new(2, 2), new(4, 4)]), "一直線は除外");
            Check(CaptureGeometry.HasPolygonArea([new(0, 0), new(10, 10), new(10, 0), new(0, 10)]), "自己交差の自由形状を許可");
        });
        Run("モニタ間隙の透過を切り出しと自由形状で維持", () =>
        {
            using Bitmap source = PatternBitmap();
            source.SetPixel(4, 4, Color.Transparent);
            using Bitmap? result = CaptureGeometry.Crop(source, new Rectangle(-6, -6, 12, 12), new Rectangle(-5, -5, 10, 10));
            Equal(0, (int)result!.GetPixel(3, 3).A);
            using Bitmap? polygon = CaptureGeometry.Freeform(source, new Rectangle(-6, -6, 12, 12),
                [new(-5, -5), new(5, -5), new(5, 5), new(-5, 5)]);
            Equal(0, (int)polygon!.GetPixel(3, 3).A);
        });
        Run("全画面モードも合成snapshotの物理範囲をそのまま返す", () =>
        {
            Bitmap source = PatternBitmap();
            int expected = source.GetPixel(9, 7).ToArgb();
            Rectangle bounds = new(-8, -4, 12, 12);
            DesktopSnapshot snapshot = new(source, bounds, [bounds]);
            using Bitmap? result = CaptureSession.CaptureAsync(CaptureMode.FullScreen, snapshot).GetAwaiter().GetResult();
            Equal(new Size(12, 12), result!.Size);
            Equal(expected, result.GetPixel(9, 7).ToArgb());
        });
        Run("左クリックと微小な手ぶれは地点のウィンドウ", () =>
        {
            Rectangle left = new(-1920, -200, 1920, 1080);
            Rectangle right = new(0, 0, 2560, 1440);
            foreach (CaptureMode mode in new[] { CaptureMode.Rectangle, CaptureMode.Freeform })
            {
                SelectionGesture gesture = new(mode, [left, right], [left]);
                gesture.Begin(new Point(-300, 40), new Size(8, 8));
                CaptureSelection? selection = gesture.End(new Point(-298, 42), OutputDestination.File);
                Equal(left, selection!.Bounds);
                Equal(OutputDestination.File, selection.Destination);
                Check(selection.Polygon is null, "クリックは自由形状modeでもモニタ矩形");
            }
        });
        Run("DPI別の閾値と一度ドラッグした後の戻りを区別", () =>
        {
            Rectangle monitor = new(0, 0, 300, 200);
            SelectionGesture normal = new(CaptureMode.Rectangle, [monitor], []);
            normal.Begin(new Point(100, 100), new Size(8, 8));
            Equal(new Rectangle(100, 100, 6, 6), normal.End(new Point(106, 106), OutputDestination.Clipboard)!.Bounds);
            SelectionGesture scaled = new(CaptureMode.Rectangle, [monitor], [monitor]);
            scaled.Begin(new Point(100, 100), new Size(16, 16));
            Equal(monitor, scaled.End(new Point(106, 106), OutputDestination.Clipboard)!.Bounds);
            scaled.Begin(new Point(100, 100), new Size(16, 16));
            scaled.Move(new Point(130, 130));
            Check(scaled.End(new Point(100, 100), OutputDestination.Clipboard) is null,
                "一度ドラッグした選択をクリックの全モニタ撮影に戻さない");
        });
        Run("ウィンドウ判定は全modeで凍結Z順の最前面", () =>
        {
            Rectangle front = new(-80, 20, 150, 150);
            Rectangle behind = new(-100, 0, 500, 400);
            foreach (CaptureMode mode in new[] { CaptureMode.Rectangle, CaptureMode.Window, CaptureMode.FullScreen, CaptureMode.Freeform })
            {
                SelectionGesture gesture = new(mode, [behind], [front, behind]);
                Equal(front, gesture.WindowSelection(new Point(10, 40), OutputDestination.ExternalApp)!.Bounds);
                Equal(behind, gesture.MonitorSelection(new Point(10, 40), OutputDestination.ExternalApp)!.Bounds);
                Equal(behind, gesture.WindowSelection(new Point(200, 40), OutputDestination.ExternalApp)!.Bounds);
                Check(gesture.WindowSelection(new Point(999, 999), OutputDestination.File) is null, "地点に窓がなければ未選択");
            }
        });
        Run("右メニューで複数の送信先を次の結果に保持", () =>
        {
            Rectangle bounds = new(-6, -6, 12, 12);
            SessionController session = new(new CaptureOptions(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard),
                new DesktopSnapshot(PatternBitmap(), bounds, [bounds]), []);
            try
            {
                ToolStripMenuItem file = session.SelectionMenu.Items.OfType<ToolStripMenuItem>()
                    .Single(item => Equals(item.Tag, OutputDestination.File));
                file.PerformClick();
                Check(!session.Completion.IsCompleted, "送信先変更だけでは選択を終えない");
                Equal(OutputDestination.Clipboard | OutputDestination.File, session.Destination);
                Equal(2, session.SelectionMenu.Items.OfType<ToolStripMenuItem>().Count(item => item.Tag is OutputDestination && item.Checked));
                session.CompleteSelection(new CaptureSelection(bounds, null, session.Destination));
                using CaptureOutcome result = session.Completion.GetAwaiter().GetResult();
                Equal(OutputDestination.Clipboard | OutputDestination.File, result.Selection!.Destination);
                Check(result.Image is not null, "静止画像を結果に保持");
            }
            finally { session.Cancel(); }
        });
        Run("メニュー内Escapeでも全選択キャンセル、設定要求も結果で返す", () =>
        {
            Rectangle bounds = new(0, 0, 12, 12);
            SessionController canceled = new(new CaptureOptions(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.File),
                new DesktopSnapshot(PatternBitmap(), bounds, [bounds]), []);
            Check(canceled.SelectionMenu.HandleCommandKey(Keys.Escape), "menu Escapeを通常のmenu closeより先に処理");
            using CaptureOutcome canceledResult = canceled.Completion.GetAwaiter().GetResult();
            Check(canceledResult.Image is null && canceledResult.Selection is null && !canceledResult.OpenSettings, "キャンセル結果");
            SessionController settings = new(new CaptureOptions(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.File),
                new DesktopSnapshot(PatternBitmap(), bounds, [bounds]), []);
            settings.SelectionMenu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "設定").PerformClick();
            using CaptureOutcome settingsResult = settings.Completion.GetAwaiter().GetResult();
            Check(settingsResult.OpenSettings && settingsResult.Image is null && settingsResult.Selection is null, "設定は撮影せずに返す");
        });
        Run("固定ONは開始画像、固定OFFだけが注入された未来画像を取得", () =>
        {
            Rectangle bounds = new(-6, -6, 12, 12);
            int newFrames = 0;
            DesktopSnapshot Future()
            {
                newFrames++;
                Bitmap image = PatternBitmap();
                image.SetPixel(4, 4, Color.Magenta);
                return new DesktopSnapshot(image, bounds, [bounds]);
            }
            using CaptureOutcome frozen = CaptureSession.SelectAsync(
                new CaptureOptions(CaptureMode.FullScreen, CaptureIntent.Screenshot, OutputDestination.File, true),
                new DesktopSnapshot(PatternBitmap(), bounds, [bounds]), [], Future).GetAwaiter().GetResult();
            Equal(0, newFrames);
            Equal(Color.FromArgb(255, 80, 80, 100).ToArgb(), frozen.Image!.GetPixel(4, 4).ToArgb());
            using CaptureOutcome fresh = CaptureSession.SelectAsync(
                new CaptureOptions(CaptureMode.FullScreen, CaptureIntent.Screenshot, OutputDestination.File, false),
                new DesktopSnapshot(PatternBitmap(), bounds, [bounds]), [], Future).GetAwaiter().GetResult();
            Equal(1, newFrames);
            Equal(Color.Magenta.ToArgb(), fresh.Image!.GetPixel(4, 4).ToArgb());
        });
        Run("録画は画像取得せず領域と自由形状マスクを返す", () =>
        {
            Rectangle bounds = new(-6, -6, 12, 12);
            Point[] polygon = [new(-4, -4), new(4, -4), new(-4, 4)];
            SessionController session = new(new CaptureOptions(CaptureMode.Freeform, CaptureIntent.Recording, OutputDestination.ExternalApp, false),
                new DesktopSnapshot(PatternBitmap(), bounds, [bounds]), [], () => throw new InvalidOperationException("録画選択完了時に静止画を再取得してはいけない"));
            session.CompleteSelection(new CaptureSelection(new Rectangle(-4, -4, 8, 8), polygon, OutputDestination.ExternalApp));
            using CaptureOutcome outcome = session.Completion.GetAwaiter().GetResult();
            Check(outcome.Image is null, "録画開始は呼出側のStartボタンに任せる");
            Equal(new Rectangle(-4, -4, 8, 8), outcome.Selection!.Bounds);
            Check(outcome.Selection.Polygon!.SequenceEqual(polygon), "自由形状の物理座標を保持");
            Equal(OutputDestination.ExternalApp, outcome.Selection.Destination);
        });
    }

    private static Bitmap PatternBitmap()
    {
        Bitmap bitmap = new(12, 12, PixelFormat.Format32bppArgb);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(255, x * 20, y * 20, 100));
        return bitmap;
    }
}
