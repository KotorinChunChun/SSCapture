using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void PinnedImageTests()
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
        Run("ピン留めは初期赤3px・操作優先順・倍率の座標変換を独立保持", () =>
        {
            using var source = PinFixture(); using var document = new PinnedImageDocument(source);
            Equal(Color.Red, document.PenColor); Equal(3f, document.PenWidth);
            Equal(PinnedGesture.Move, PinnedImageDocument.Gesture(Keys.None));
            Equal(PinnedGesture.Select, PinnedImageDocument.Gesture(Keys.Shift));
            Equal(PinnedGesture.Draw, PinnedImageDocument.Gesture(Keys.Control));
            Equal(PinnedGesture.AxisLine, PinnedImageDocument.Gesture(Keys.Control | Keys.Shift));
            Equal(new PointF(20, 10), PinnedImageDocument.ImagePoint(new(40, 20), 2));
            Equal(new PointF(30, 5), PinnedImageDocument.AxisEnd(new(5, 5), new(30, 7)));
            Equal(new PointF(5, 30), PinnedImageDocument.AxisEnd(new(5, 5), new(7, 30)));
        });
        Run("ピン留めは原画像の破棄後も独立し描画・消去のUndoRedoを保持", () =>
        {
            var source = PinFixture(); using var document = new PinnedImageDocument(source); source.Dispose();
            using var original = document.Render();
            document.Add(PinnedDrawingTool.Line, [new(5, 10), new(45, 10)]);
            using (var drawn = document.Render()) Equal(Color.Red.ToArgb(), drawn.GetPixel(20, 10).ToArgb());
            document.Clear(); Equal(0, document.AnnotationCount);
            document.Undo(); Equal(1, document.AnnotationCount);
            document.Undo(); Equal(0, document.AnnotationCount);
            using (var restored = document.Render()) Equal(original.GetPixel(20, 10).ToArgb(), restored.GetPixel(20, 10).ToArgb());
            document.Redo(); Equal(1, document.AnnotationCount);
            document.Undo(); document.Add(PinnedDrawingTool.Freehand, [new(20, 20)]);
            Check(!document.CanRedo, "新しい描画で古いRedoを破棄");
        });
        Run("ピン留めの右回転は画像と注釈を一緒に回し選択を解除", () =>
        {
            using var source = PinFixture(); using var document = new PinnedImageDocument(source);
            document.Add(PinnedDrawingTool.Line, [new(5, 10), new(45, 10)]);
            using var before = document.Render();
            document.Select(new(2, 2), new(10, 10)); document.Rotate(true);
            Equal(new Size(40, 60), document.Size); Check(document.Selection is null, "回転で古い範囲を残さない");
            using var after = document.Render();
            Equal(before.GetPixel(20, 10).ToArgb(), after.GetPixel(29, 20).ToArgb());
            document.Rotate(false);
            using var restored = document.Render(); Equal(before.Size, restored.Size);
            Equal(before.GetPixel(20, 10).ToArgb(), restored.GetPixel(20, 10).ToArgb());
            document.Undo(); Equal(new Size(40, 60), document.Size); document.Redo(); Equal(new Size(60, 40), document.Size);
        });
        Run("回転後の描画と文字も後続の回転へ追従", () =>
        {
            using var source = PinFixture(); using var document = new PinnedImageDocument(source);
            document.Rotate(true); document.Add(PinnedDrawingTool.Line, [new(5, 8), new(30, 8)]);
            document.Add(PinnedDrawingTool.Text, [new(5, 15)], "図");
            document.Rotate(false);
            using var result = document.Render(); Equal(new Size(60, 40), result.Size);
            Equal(Color.Red.ToArgb(), result.GetPixel(8, 20).ToArgb()); Equal(2, document.AnnotationCount);
        });
        Run("逆向き・画面外の選択を画像内へ切り詰めコピーだけ切り出す", () =>
        {
            using var source = PinFixture(); using var document = new PinnedImageDocument(source);
            document.Select(new(90, 80), new(-10, -20)); Equal(new Rectangle(0, 0, 60, 40), document.Selection!.Value);
            document.Select(new(30, 25), new(10, 5));
            using var selected = document.Render(true); Equal(new Size(20, 20), selected.Size);
            using var whole = document.Render(); Equal(source.Size, whole.Size);
            document.Select(new(10, 10), new(10, 20)); Check(document.Selection is null, "幅ゼロは選択しない");
        });
        Run("蛍光線は透過合成し原画像・画像寸法を変更しない", () =>
        {
            using var source = PinFixture(); int before = source.GetPixel(20, 10).ToArgb();
            using var document = new PinnedImageDocument(source); document.Highlighter = true;
            document.Add(PinnedDrawingTool.Line, [new(5, 10), new(45, 10)]);
            using var result = document.Render();
            Check(result.GetPixel(20, 10).ToArgb() != Color.Red.ToArgb(), "蛍光線が不透明");
            Check(result.GetPixel(20, 10).ToArgb() != before, "蛍光線が描かれない"); Equal(before, source.GetPixel(20, 10).ToArgb());
        });
        Run("全図形と文字が描画でき全消去も元の撮影画像を保持", () =>
        {
            using var source = PinFixture(); using var document = new PinnedImageDocument(source);
            foreach (var tool in Enum.GetValues<PinnedDrawingTool>())
                document.Add(tool, [new(3, 3), new(35, 25)], tool == PinnedDrawingTool.Text ? "文字" : null);
            Equal(6, document.AnnotationCount); using var all = document.Render();
            document.Clear(); using var clean = document.Render();
            Equal(source.GetPixel(4, 4).ToArgb(), clean.GetPixel(4, 4).ToArgb());
            document.Undo(); Equal(6, document.AnnotationCount);
        });
        Run("ピン留め窓は枠なし最前面・負座標・ズーム復帰と局所キーを保持", () =>
        {
            using var source = PinFixture(); using var form = new PinnedImageForm(source, new(-200, -100), new AppSettings());
            Equal(FormBorderStyle.None, form.FormBorderStyle); Check(form.TopMost, "最前面でない"); Equal(new Point(-200, -100), form.Location);
            form.Document.Add(PinnedDrawingTool.Line, [new(5, 5), new(30, 5)]);
            form.Document.Select(new(1, 1), new(15, 15)); PinKey(form, Keys.Escape);
            Check(form.Document.Selection is null, "Escで選択を解除しない"); Check(!form.IsDisposed, "Escで窓を閉じた");
            PinKey(form, Keys.R); Equal(new Size(40, 60), form.Document.Size);
            PinKey(form, Keys.L); Equal(new Size(60, 40), form.Document.Size);
            PinKey(form, Keys.Control | Keys.Z); Equal(new Size(40, 60), form.Document.Size);
            PinKey(form, Keys.Control | Keys.Y); Equal(new Size(60, 40), form.Document.Size);
            PinKey(form, Keys.G); Equal(Color.Lime, form.Document.PenColor);
            PinKey(form, Keys.Shift | Keys.B); Equal(Color.Blue, form.Document.PenColor); Check(form.Document.Highlighter, "蛍光へ切替わらない");
            form.SetZoom(2, new(10, 10)); Equal(2f, form.Zoom); Equal(new Size(120, 80), form.ClientSize);
            form.ResetView(); Equal(1f, form.Zoom); Equal(new Point(-200, -100), form.Location);
            PinKey(form, Keys.E); Equal(0, form.Document.AnnotationCount); PinKey(form, Keys.Control | Keys.Z); Equal(1, form.Document.AnnotationCount);
        });
        Run("ピン留めの選択コピーは描画込みパレット・保存は全体で余白を二重適用しない", () => WithOutputDirectory(directory => RunPinnedUiTask(async () =>
        {
            using var source = PinFixture();
            var settings = new AppSettings { SaveDirectory = directory, FileNamePattern = "pin_{##}.{ext}", ScreenshotColorDepth = ScreenshotColorDepth.Indexed4, ScreenshotPaddingPixels = 10 };
            Size copied = Size.Empty;
            using var form = new PinnedImageForm(source, Point.Empty, settings, copyImage: bitmap =>
            { copied = bitmap.Size; Equal(PixelFormat.Format4bppIndexed, bitmap.PixelFormat); return Task.CompletedTask; });
            form.Document.Add(PinnedDrawingTool.Line, [new(1, 5), new(40, 5)]);
            form.Document.Select(new(0, 0), new(20, 10)); await form.CopyAsync(); Equal(new Size(20, 10), copied);
            await form.SaveAsync(); string file = Directory.GetFiles(directory, "pin_*.png").Single();
            using var saved = new Bitmap(file); Equal(source.Size, saved.Size); Equal(PixelFormat.Format4bppIndexed, saved.PixelFormat);
            Check(!form.IsDisposed, "保存で窓が閉じた");
        })));
        Run("ピン留め出力は余白・減色済みの画像と撮影座標を配送し複製を保持できる", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = PinFixture(); Bitmap? owned = null; Rectangle received = Rectangle.Empty; int calls = 0;
            using (var router = new OutputRouter(directory, pinImage: (bitmap, bounds, settings) =>
            { calls++; owned = (Bitmap)bitmap.Clone(); received = bounds; Equal(4, settings.ScreenshotPaddingPixels); }))
                await router.ScreenshotAsync(image, OutputDestination.Pinned, new AppSettings { ScreenshotPaddingPixels = 4,
                    ScreenshotColorDepth = ScreenshotColorDepth.Indexed8 }, captureBounds: new(-300, -50, 60, 40));
            using (owned!) { Equal(new Size(68, 48), owned!.Size); Equal(PixelFormat.Format8bppIndexed, owned.PixelFormat); }
            Equal(1, calls); Equal(new Rectangle(-300, -50, 60, 40), received);
        })));
        Run("ピン留めと他出力の成功を分離し失敗分だけ再実行", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = PinFixture(); int copies = 0, pins = 0; bool failPin = true;
            CaptureDelivery? delivery = null;
            using var router = new OutputRouter(directory, copyImage: _ => { copies++; return Task.CompletedTask; },
                pinImage: (_, _, _) => { pins++; if (failPin) throw new IOException("注入失敗"); }, delivered: result => { delivery = result; result.NotificationImage?.Dispose(); });
            try { await router.ScreenshotAsync(image, OutputDestination.Pinned | OutputDestination.Clipboard, new AppSettings()); }
            catch (IOException) { }
            Equal(OutputDestination.Clipboard, delivery!.Completed); Equal(OutputDestination.Pinned, delivery.Failed);
            Check(router.HasPendingRetry, "失敗したピン留めを保持しない");
            failPin = false; await router.RetryFailedAsync(); Equal(1, copies); Equal(2, pins); Check(!router.HasPendingRetry, "成功後も再試行が残る");
        })));
        Run("最後のピン留めを閉じても非同期コピー完了まで窓の寿命を維持", () => RunPinnedUiTask(async () =>
        {
            using var source = PinFixture();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var pins = new PinnedImageManager(copyImage: _ => completion.Task);
            pins.Show(source, new(-30000, -30000, 60, 40), new AppSettings());
            var form = pins.Windows.Single();
            var copying = form.CopyAsync(); form.Close();
            Equal(1, pins.Count); Check(!form.IsDisposed, "コピー完了前に窓を破棄した");
            completion.SetResult(); await copying;
            Equal(0, pins.Count); Check(form.IsDisposed, "コピー完了後の閉じる要求が残った");
        }));
        Run("保存失敗でも保留した閉じる操作を完了し画像を早期破棄しない", () => RunPinnedUiTask(async () =>
        {
            using var source = PinFixture();
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Bitmap? borrowed = null; bool reported = false;
            using var form = new PinnedImageForm(source, new(-30000, -30000), new AppSettings(),
                report: (_, error) => reported = error, saveImage: image => { borrowed = image; return completion.Task; });
            form.Show(); var saving = form.SaveAsync(); form.Close();
            Check(!form.IsDisposed, "保存完了前に窓を破棄した"); Equal(source.Size, borrowed!.Size);
            completion.SetException(new IOException("注入失敗")); await saving;
            Check(reported, "保存失敗を通知しない"); Check(form.IsDisposed, "保存失敗後に閉じる要求が残った");
        }));
        Run("非表示中に追加したピン留めも再表示まで隠し全窓を閉じられる", () =>
        {
            using var source = PinFixture(); using var pins = new PinnedImageManager();
            pins.Show(source, new(-30000, -30000, 60, 40), new AppSettings()); pins.ToggleVisibility();
            pins.Show(source, new(-30000, -30000, 60, 40), new AppSettings());
            Equal(2, pins.Count); Check(pins.Windows.All(w => !w.Visible), "非表示中の追加が画面へ出た");
            pins.ToggleVisibility(); Check(pins.Windows.All(w => w.Visible), "追加分を再表示できない");
            pins.ToggleVisibility();
            pins.Show(source, new(-30000, -30000, 60, 40), new AppSettings());
            pins.CloseAll(); Equal(0, pins.Count); Check(!pins.Hidden, "全窓閉鎖後に非表示状態が残る");
        });
        Run("ピン留め成功後に保存だけ失敗しても再実行で窓を増やさない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = PinFixture(); int pins = 0; string blocked = Path.Combine(directory, "blocked"); File.WriteAllText(blocked, "障害");
            using var router = new OutputRouter(directory, pinImage: (_, _, _) => pins++);
            try { await router.ScreenshotAsync(image, OutputDestination.Pinned | OutputDestination.File, new AppSettings { SaveDirectory = blocked }); }
            catch (IOException) { }
            Equal(1, pins); File.Delete(blocked); await router.RetryFailedAsync(); Equal(1, pins);
        })));
        Run("出力プリセットでピン留めを保存し既定概要へ反映できる", () => WithOutputDirectory(directory =>
        {
            var settings = new AppSettings { DefaultDestination = OutputDestination.Pinned }.ApplyDefaults(); settings.Validate();
            var store = new SettingsStore(Path.Combine(directory, "settings.json")); store.Save(settings);
            var read = store.Load(out var warning); Check(warning is null, warning ?? ""); Equal(OutputDestination.Pinned, read.DefaultDestination);
            Check(SettingsForm.OutputSummary(CapturePreset.FromSettings("ピン", read)).Contains("ピン留め"), "既定概要に出力がない");
        }));
        Run("ピン留めだけでも手動保存のフォルダと命名を登録時に検証", () =>
        {
            foreach (var settings in new[]
            {
                new AppSettings { DefaultDestination = OutputDestination.Pinned, SaveDirectory = "relative" },
                new AppSettings { DefaultDestination = OutputDestination.Pinned, FileNamePattern = "bad?.png" }
            })
            {
                bool rejected = false;
                try { CapturePreset.FromSettings("ピン留め", settings).Validate(); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "手動保存に使えない出力プリセットを許可した");
            }
            new AppSettings { DefaultDestination = OutputDestination.Clipboard, SaveDirectory = "relative", FileNamePattern = "bad?.png" }.Validate();
        });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static Bitmap PinFixture()
    {
        var bitmap = new Bitmap(60, 40); using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.White);
        bitmap.SetPixel(1, 1, Color.Blue); return bitmap;
    }
    private static void PinKey(PinnedImageForm form, Keys keys) => typeof(PinnedImageForm)
        .GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [new Message(), keys]);
    private static void RunPinnedUiTask(Func<Task> action)
    {
        using var context = new ApplicationContext();
        using var dispatcher = new Control(); _ = dispatcher.Handle;
        Exception? failure = null;
        using var timeout = new System.Windows.Forms.Timer { Interval = 15000 };
        timeout.Tick += (_, _) => { failure = new TimeoutException("ピン留めUIテストが完了しません。"); context.ExitThread(); };
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await action(); }
            catch (Exception error) { failure = error; }
            finally { timeout.Stop(); context.ExitThread(); }
        }));
        timeout.Start(); Application.Run(context);
        if (failure is not null) throw failure;
    }
}
