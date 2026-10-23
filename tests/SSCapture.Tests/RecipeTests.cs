using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;
internal static partial class Program
{
    static void RecipeTests()
    {
        Run("OCR命名キャンセルでコピーが成功しても直前の再撮影条件を更新しない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int remembers = 0, copies = 0;
            using var image = OutputImage();
            using var router = new OutputRouter(Path.Combine(directory, "work"), _ => { copies++; return Task.CompletedTask; },
                captureSucceeded: (_, _) => remembers++, readCaptureLayout: () => RegionLayout);
            await router.ScreenshotAsync(image, OutputDestination.Clipboard | OutputDestination.File, OutputSettings(directory),
                captureBounds: new(1, 1, image.Width, image.Height), requestFileName: () => Task.FromResult<ScreenshotFileName?>(null));
            Equal(1, copies); Equal(0, remembers);
        })));
        Run("直前設定キーは未割当で前回範囲と独立し重複を拒否", () =>
        {
            Equal("", new AppSettings().LastRecipeShortcut);
            var settings = new AppSettings { LastRecipeShortcut = "Ctrl+F11" };
            var state = new HotkeyState(HotkeyBinding.ReadAssignments(settings), KeyboardModifiers.LeftControl);
            Equal(AppSettings.LastRecipeKeyId, state.Process((int)Keys.F11, true, true).RegionId);
            bool rejected = false;
            try { (settings with { LastRecipeShortcut = settings.LastRegionShortcut }).Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "重複が通過");
        });
        Run("直前設定は配列と自由形状を独立保持し負座標・構成変更を検証", () =>
        {
            var bounds = new Rectangle(-30, 20, 10, 10);
            var points = new[] { new Point(-30, 20), new Point(-20, 20), new Point(-30, 30) };
            var source = new AppSettings { InstantShortcuts = Enumerable.Repeat("", 10).ToArray() };
            DisplayLayout[] layout = [new("left", "左", new(-100, 0, 100, 100), 96, 96)];
            var recipe = LastCaptureRecipe.Create(1, bounds, points, source, layout);
            points[0] = Point.Empty; source.InstantShortcuts[0] = "Ctrl+F1";
            Equal(new Point(-30, 20), recipe.Polygon![0]); Equal("", recipe.Settings.InstantShortcuts[0]);
            var returned = recipe.Settings; returned.InstantShortcuts[0] = "Ctrl+F2";
            Equal("", recipe.Settings.InstantShortcuts[0]); Equal(bounds, recipe.Resolve(layout));
            Equal(new Point(70, 20), recipe.PolygonFor(new(70, 20, 10, 10))![0]);
            bool rejected = false;
            try { recipe.Resolve([layout[0] with { DpiX = 144 }]); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "拡大率変更を無視");
            using var red = new Bitmap(10, 10); using (var g = Graphics.FromImage(red)) g.Clear(Color.Red);
            using var first = recipe.Shape(red); Equal(0, first.GetPixel(9, 9).A);
            using (var g = Graphics.FromImage(red)) g.Clear(Color.Blue);
            using var second = recipe.Shape(red); Equal(Color.Blue.ToArgb(), second.GetPixel(1, 1).ToArgb());
        });
        Run("直前設定は削除・変更された外部アプリへ古い設定で起動しない", () =>
        {
            var app = new ExternalApplication { Id = "editor", Name = "編集", Executable = "C:\\editor.exe", Arguments = "{file}" };
            var settings = new AppSettings { ImageEditors = [app], SelectedImageEditorId = app.Id, DefaultDestination = OutputDestination.ExternalApp };
            var recipe = LastCaptureRecipe.Create(1, new(1, 1, 2, 2), null, settings, RegionLayout);
            recipe.ValidateApplications(settings);
            foreach (var changed in new[] { settings with { ImageEditors = [] }, settings with { ImageEditors = [app with { Arguments = "--new {file}" }] } })
            {
                bool rejected = false; try { recipe.ValidateApplications(changed); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "変更済みアプリを起動");
            }
        });
        Run("直前設定は新しい成功を優先し再現できない最新成功で無効化", () =>
        {
            var book = new CaptureRecipeBook(); var first = LastCaptureRecipe.Create(1, new(1, 1, 2, 2), null, new(), RegionLayout);
            var second = LastCaptureRecipe.Create(2, new(3, 3, 2, 2), null, new(), RegionLayout);
            book.Remember(2, second); book.Remember(1, first); Equal(second, book.Current);
            book.Remember(3, null); Check(book.Current is null, "古い範囲へ誤復帰");
        });
        Run("出力全失敗は直前設定を変更せず部分成功は履歴OFFでも記録", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var book = new CaptureRecipeBook(); int remembered = 0;
            using var router = new OutputRouter(Path.Combine(directory, "work"), _ => Task.FromException(new IOException("busy")),
                captureSucceeded: (n, r) => { remembered++; book.Remember(n, r); }, readCaptureLayout: () => RegionLayout);
            using var image = OutputImage(); var settings = OutputSettings(directory) with { KeepHistory = false };
            try { await router.ScreenshotAsync(image, OutputDestination.Clipboard, settings, captureBounds: new(1, 1, image.Width, image.Height)); } catch (IOException) { }
            Equal(0, remembered);
            try { await router.ScreenshotAsync(image, OutputDestination.File | OutputDestination.Clipboard, settings, captureBounds: new(2, 2, image.Width, image.Height)); } catch (IOException) { }
            Equal(1, remembered); Equal(OutputDestination.File | OutputDestination.Clipboard, book.Current!.Settings.DefaultDestination);
            Equal(2, book.Current.Bounds.X);
        })));
        Run("直前設定の再出力は外部アプリも含め新しいOCR・日時・連番で作成", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int copies = 0, launches = 0, recognitions = 0;
            using var router = new OutputRouter(Path.Combine(directory, "work"), _ => { copies++; return Task.CompletedTask; }, launch: _ => launches++);
            using var image = OutputImage();
            var settings = OutputSettings(directory) with { DefaultDestination = OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp,
                ImageEditorPath = Environment.ProcessPath!, FileNamePattern = "{yyyymmdd}_{ocr}_{##}.{ext}" };
            for (int i = 0; i < 2; i++)
                await InstantCapture.DeliverAsync(image, settings, new DateTimeOffset(2026, 10, 23, 1, 2, 3, TimeSpan.Zero), router,
                    (_, _, _) => { recognitions++; return Task.FromResult(new OcrResult(["新しい文字"], null)); }, preserveOutputs: true);
            Equal(2, copies); Equal(2, launches); Equal(2, recognitions);
            Check(File.Exists(Path.Combine(settings.SaveDirectory, "20261023_新しい文字_01.png")), "新しいOCR命名がない");
            Check(File.Exists(Path.Combine(settings.SaveDirectory, "20261023_新しい文字_02.png")), "連番が重複");
        })));
    }
}
