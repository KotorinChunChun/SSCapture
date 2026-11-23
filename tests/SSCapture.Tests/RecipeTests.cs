using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;
internal static partial class Program
{
    static void RecipeTests()
    {
        Run("ウィンドウ単独撮影の直前設定は画面座標の撮影へ切り替えない", () =>
        {
            var bounds = new Rectangle(-20, 5, 240, 160);
            var target = new CaptureWindow(bounds, "撮影対象", (nint)8, 2, 3);
            var recipe = LastCaptureRecipe.Create(1, bounds, null, new AppSettings(), [],
                new CaptureReplaySource.Windows([target], Isolated: true, IncludeOwned: true), _ => 10);
            Equal(bounds, recipe.Bounds);
            Check(recipe.UseIsolatedWindow, "単独撮影の方式が失われた");
            Equal(target, recipe.ResolveWindows([target], _ => 10).Single());
            try
            {
                _ = recipe.Resolve([]);
                throw new Exception("ウィンドウを画面座標へ展開した");
            }
            catch (InvalidOperationException) { }
        });
        Run("ウィンドウ再撮影は同じPIDのハンドルと現在位置を優先し、一意な再作成にも追従する", () =>
        {
            var original = new CaptureWindow(new(30, 40, 240, 160), "原稿", 8, 2, 3, ClassName: "Editor");
            var moved = original with { Bounds = new(-500, 90, 400, 300), Title = "編集中の原稿" };
            var other = original with { Handle = 9, Title = "別の原稿" };
            var reopened = original with { Handle = 10, ThreadId = 4, Bounds = new(-900, -5, 300, 270) };
            var recipe = LastCaptureRecipe.Create(1, original.Bounds, null, new(), [],
                new CaptureReplaySource.Windows([original], Isolated: false, IncludeOwned: false), _ => 10);
            Check(!recipe.UseIsolatedWindow, "見えている画面の撮影方式が変わった");
            Equal(moved, recipe.ResolveWindows([other, moved], _ => 10).Single());
            Equal(reopened, recipe.ResolveWindows([other, reopened], _ => 10).Single());
            foreach (var attempt in new (CaptureWindow[] Windows, long Start)[]
            {
                ([moved], 11),
                ([reopened, reopened with { Handle = 11 }], 10),
                ([other, reopened with { ProcessId = 12 }], 10)
            })
            {
                bool rejected = false;
                try { _ = recipe.ResolveWindows(attempt.Windows, _ => attempt.Start); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "別のウィンドウを再撮影対象にした");
            }
        });
        Run("コントロール撮影はモニター構成に依存せず元の親ウィンドウと部品IDを記録", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var book = new CaptureRecipeBook();
            using var router = new OutputRouter(Path.Combine(directory, "work"), _ => Task.CompletedTask,
                captureSucceeded: book.Remember, readCaptureLayout: () => throw new IOException("モニターを取得できない"));
            using var image = OutputImage();
            var window = new CaptureWindow(new(20, 30, 200, 100), "対象", 8, 0, 3, ClassName: "Editor");
            var control = new ControlIdentity("target-control", 50000);
            await router.ScreenshotAsync(image, OutputDestination.Clipboard, OutputSettings(directory),
                captureBounds: new(25, 35, image.Width, image.Height), replaySource: new CaptureReplaySource.Control(window, control, Isolated: true));
            var recipe = book.Current!;
            Check(recipe.TracksControl && recipe.UseIsolatedWindow && book.LastRegion is null, "コントロールを矩形として記録した");
            Equal(control, recipe.ControlIdentity);
            var moved = window with { Bounds = new(40, 50, 200, 100) };
            Equal(moved, recipe.ResolveWindows([moved], requireOriginalWindow: true).Single());
            try
            {
                _ = recipe.ResolveWindows([moved with { Handle = 9 }], requireOriginalWindow: true);
                throw new Exception("別の親ウィンドウを代用した");
            }
            catch (InvalidOperationException) { }
        })));
        Run("モニターの再撮影は座標が同じ別のモニターで代用しない", () =>
        {
            DisplayLayout display = new("元のモニター", "左", new(-1920, 0, 1920, 1080), 96, 96);
            var recipe = LastCaptureRecipe.Create(1, display.Bounds, null, new(), [display], new CaptureReplaySource.Monitors([display.Bounds]));
            var moved = display with { Bounds = new(2560, -200, 2560, 1440), DpiX = 120, DpiY = 120 };
            var replacement = display with { Identity = "別のモニター" };
            Equal(moved.Bounds, recipe.RegionsFor([replacement, moved])!.Single());
            bool rejected = false;
            try { _ = recipe.RegionsFor([replacement]); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "切断したモニターを別のモニターに置き換えた");
        });
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
        Run("直前設定キーは新規でWin＋Ctrl＋Alt＋Sを割り当て、押し続けと二重登録を防ぐ", () =>
        {
            Equal("Win+Ctrl+Alt+S", new AppSettings().LastRecipeShortcut);
            var settings = new AppSettings { LastRecipeShortcut = "Ctrl+F11" };
            var state = new HotkeyState(HotkeyBinding.ReadAssignments(settings), KeyboardModifiers.LeftControl);
            Equal(new HotkeyDecision(true, true, CaptureIntent.Screenshot, RegionId: AppSettings.LastRecipeKeyId), state.Process((int)Keys.F11, true, true));
            Equal(new HotkeyDecision(true, false, CaptureIntent.Screenshot, RegionId: AppSettings.LastRecipeKeyId), state.Process((int)Keys.F11, true, true));
            Equal(new HotkeyDecision(true, false, CaptureIntent.Screenshot, RegionId: AppSettings.LastRecipeKeyId), state.Process((int)Keys.F11, false, true));
            bool rejected = false;
            try { (settings with { LastRecipeShortcut = settings.ScreenshotShortcut1 }).Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "重複が通過");
        });
        Run("再撮影キーの既存設定は省略・明示なし・独自割り当てを保持する", () => WithServiceDirectory(directory =>
        {
            string file = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(file);
            Equal(AppSettings.DefaultLastRecipeShortcut, store.Load(out _).LastRecipeShortcut);
            foreach (var (json, expected) in new (string, string)[]
            {
                ("""{"SchemaVersion":3,"ScreenshotShortcut1":"Win+Ctrl+Alt+S"}""", ""),
                ("""{"SchemaVersion":3,"LastRecipeShortcut":""}""", ""),
                ("""{"SchemaVersion":3,"LastRecipeShortcut":"Ctrl+F11"}""", "Ctrl+F11"),
                ("""{"SchemaVersion":3,"LastRecipeShortcut":"Win+Ctrl+Alt+S"}""", AppSettings.DefaultLastRecipeShortcut)
            })
            {
                File.WriteAllText(file, json);
                Equal(expected, store.Load(out string? warning).LastRecipeShortcut);
                Equal<string?>(null, warning);
            }
        }));
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
            Check(book.Current is null && book.LastRegion is null, "起動直後に前回範囲がある");
            book.Remember(2, second); book.Remember(1, first); Equal(second, book.Current);
            book.Remember(3, null); book.Remember(2, second);
            Check(book.Current is null, "古い範囲へ誤復帰");
            Equal(second.Bounds, RegionCoordinates.Resolve(book.LastRegion!, RegionLayout));
        });
        Run("モニター撮影成功後に識別できなかった場合は前回矩形の代わりに選び直す", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var book = new CaptureRecipeBook();
            bool lookupFailure = false;
            int copied = 0;
            using var router = new OutputRouter(Path.Combine(directory, "work"), _ => { copied++; return Task.CompletedTask; },
                captureSucceeded: book.Remember, readCaptureLayout: () => lookupFailure ? throw new IOException("識別失敗") : RegionLayout);
            using var image = OutputImage();
            var settings = OutputSettings(directory);
            Rectangle monitor = RegionLayout[0].Bounds;
            var source = new CaptureReplaySource.Monitors([monitor]);
            await router.ScreenshotAsync(image, OutputDestination.Clipboard, settings, captureBounds: new(1, 1, 2, 2));
            Equal(new Rectangle(1, 1, 2, 2), RegionCoordinates.Resolve(book.LastRegion!, RegionLayout));
            lookupFailure = true;
            await router.ScreenshotAsync(image, OutputDestination.Clipboard, settings, captureBounds: monitor, replaySource: source);
            Equal(2, copied);
            Check(book.Current is null, "識別できない対象の代わりに以前の撮影を再撮影する");
            lookupFailure = false;
            await router.ScreenshotAsync(image, OutputDestination.Clipboard, settings, captureBounds: monitor, replaySource: source);
            Equal(monitor, book.Current!.RegionsFor(RegionLayout)!.Single());
            Equal(new Rectangle(1, 1, 2, 2), RegionCoordinates.Resolve(book.LastRegion!, RegionLayout));
        })));
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
        Run("登録範囲の成功を記録し、命名の一時変更と出力先を次の再撮影に引き継ぐ", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var book = new CaptureRecipeBook();
            int folderOpens = 0;
            using var router = new OutputRouter(Path.Combine(directory, "work"), _ => Task.CompletedTask,
                launch: _ => folderOpens++, captureSucceeded: book.Remember, readCaptureLayout: () => RegionLayout);
            using var image = OutputImage();
            var settings = OutputSettings(directory) with { FileNamePattern = "変更前_{ocr}_{##}.{ext}", ScreenshotPaddingPixels = 2, IncludeCursor = true };
            var capturedAt = new DateTimeOffset(2026, 10, 23, 1, 2, 3, TimeSpan.Zero);
            var input = RegionCoordinates.FromBounds("登録位置", new(10, 20, image.Width, image.Height), RegionLayout);
            Rectangle bounds = RegionCoordinates.Resolve(input, RegionLayout);
            var destination = OutputDestination.File | OutputDestination.Clipboard;
            var confirmed = new FileNamePlan("変更後_{ocr}_{##}.{ext}", capturedAt, CaptureIntent.Screenshot, "選んだ文字");
            await router.ScreenshotAsync(image, destination, settings, captureBounds: bounds,
                requestFileName: () => Task.FromResult<ScreenshotFileName?>(new(null, confirmed, true, "-")));
            var replay = book.Current!.SettingsForReplay(new AppSettings());
            Equal(bounds, RegionCoordinates.Resolve(book.LastRegion!, RegionLayout));
            Equal(confirmed.Pattern, replay.FileNamePattern); Equal("-", replay.OcrSeparator);
            Check(replay.OpenFolderAfterSave && replay.IncludeCursor, "確定時の保存動作・入力条件を引き継がない");
            Equal(destination, replay.DefaultDestination); Equal(2, replay.ScreenshotPaddingPixels);
            await InstantCapture.DeliverAsync(image, replay, capturedAt.AddMinutes(1), router,
                (_, _, _) => Task.FromResult(new OcrResult(["再認識"], null)), captureBounds: bounds, preserveOutputs: true);
            Equal(2, folderOpens);
            Check(File.Exists(Path.Combine(settings.SaveDirectory, "変更後_選んだ文字_01.png")), "一時変更で保存していない");
            Check(File.Exists(Path.Combine(settings.SaveDirectory, "変更後_再認識_01.png")), "一時変更で再出力できない");
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
