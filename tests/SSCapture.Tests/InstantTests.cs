using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    internal static int DisplayRoutingSmoke()
    {
        var displays = InstantCapture.ReadDisplays();
        var state = new HotkeyState(HotkeyBinding.ReadAssignments(new AppSettings()), KeyboardModifiers.LeftWindows | KeyboardModifiers.LeftShift);
        var decision = state.Process('1', true, true);
        Rectangle bounds = InstantCapture.ResolveDisplay(decision.DisplayNumber, displays);
        if (!decision.Capture || displays.Count == 0 || bounds != displays[0].Bounds) return 1;
        Console.WriteLine($"PASS current display routing: Win+Shift+1 -> {displays[0].DeviceName}, {bounds}, remote={SystemInformation.TerminalServerSession}, screens={displays.Count}");
        return 0; // Deliberately do not call Take or read any screen pixels.
    }

    static partial void InstantTests()
    {
        Run("即時キー1～9・0の対象番号と押しっぱなしを保持", () =>
        {
            var assignments = HotkeyBinding.ReadAssignments(new AppSettings());
            foreach (int number in Enumerable.Range(1, 10))
            {
                int key = '0' + number % 10;
                var state = new HotkeyState(assignments, KeyboardModifiers.LeftWindows | KeyboardModifiers.LeftShift);
                Equal(new HotkeyDecision(true, true, CaptureIntent.Screenshot, number), state.Process(key, true, true));
                Equal(new HotkeyDecision(true, false, CaptureIntent.Screenshot, number), state.Process(key, true, true));
                Equal(new HotkeyDecision(true, false, CaptureIntent.Screenshot, number), state.Process(key, false, false));
            }
        });
        Run("即時キー変更・なし・重複拒否と保存", () => WithServiceDirectory(directory =>
        {
            var keys = Enumerable.Repeat("", 10).ToArray(); keys[9] = "Ctrl+F12";
            var settings = new AppSettings { InstantShortcuts = keys };
            var state = new HotkeyState(HotkeyBinding.ReadAssignments(settings), KeyboardModifiers.LeftControl);
            Equal(10, state.Process((int)Keys.F12, true, true).DisplayNumber);
            Check(!state.IsTargetKey('1'), "なしが有効");
            var store = new SettingsStore(Path.Combine(directory, "settings.json")); store.Save(settings);
            Equal("Ctrl+F12", store.Load(out _).InstantShortcuts[9]);
            keys[0] = "Win+Shift+S";
            bool rejected = false;
            try { settings.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "通常キーとの重複を許可");
        }));
        Run("旧カスタムキーを新既定キーより優先して移行", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"SchemaVersion\":2,\"ScreenshotShortcut1\":\"Win+Shift+1\",\"SaveDirectory\":\"C:\\\\captures\"}");
            var settings = new SettingsStore(path).Load(out var warning);
            Equal<string?>(null, warning); Equal("Win+Shift+1", settings.ScreenshotShortcut1);
            Equal("", settings.InstantShortcuts[0]); Equal("Win+Shift+0", settings.InstantShortcuts[9]);
        }));
        Run("即時画面番号は主画面から左・上の順で列挙順やDISPLAY番号に依存しない", () =>
        {
            DisplayTarget[] displays = [new(@"\\.\DISPLAY3", new(-2560, 0, 2560, 1440)),
                new(@"\\.\DISPLAY97", new(0, 0, 1920, 1080), true), new(@"\\.\DISPLAY22", new(-2560, -1440, 2560, 1440))];
            foreach (var source in new[] { displays, displays.Reverse().ToArray(), [displays[1], displays[0], displays[2]] })
            {
                Equal(displays[1].Bounds, InstantCapture.ResolveDisplay(1, source));
                Equal(displays[2].Bounds, InstantCapture.ResolveDisplay(2, source));
                Equal(displays[0].Bounds, InstantCapture.ResolveDisplay(3, source));
            }
        });
        Run("RDPのDISPLAY97だけでもWinShift1を主画面へ解決する", () =>
        {
            var state = new HotkeyState(HotkeyBinding.ReadAssignments(new AppSettings()), KeyboardModifiers.LeftWindows | KeyboardModifiers.LeftShift);
            HotkeyDecision decision = state.Process('1', true, true);
            Check(decision.Capture && decision.Suppress, "WinShift1を撮影として受け付けない");
            var bounds = new Rectangle(0, 0, 2560, 1440);
            Equal(bounds, InstantCapture.ResolveDisplay(decision.DisplayNumber, [new(@"\\.\DISPLAY97", bounds, true)]));
        });
        Run("接続数外の即時画面指定は主画面へ代替せず拒否し再接続で番号を更新", () =>
        {
            var main = new DisplayTarget(@"\\.\DISPLAY97", new(0, 0, 2560, 1440), true);
            var left = new DisplayTarget(@"\\.\DISPLAY105", new(-1920, 0, 1920, 1080));
            foreach (int number in new[] { 2, 10 })
            {
                string error = "";
                try { InstantCapture.ResolveDisplay(number, [main]); } catch (InvalidOperationException ex) { error = ex.Message; }
                Check(error.Contains("接続中: 1画面", StringComparison.Ordinal), "未接続を別画面に置換");
            }
            Equal(left.Bounds, InstantCapture.ResolveDisplay(2, [left, main]));
            Equal(left.Bounds, InstantCapture.ResolveDisplay(1, [left with { IsPrimary = true }]));
            foreach (int number in new[] { 0, 11 })
            {
                bool rejected = false;
                try { InstantCapture.ResolveDisplay(number, [main]); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected, "指定可能範囲外を許可");
            }
            bool emptyRejected = false;
            try { InstantCapture.ResolveDisplay(1, [new("invalid", Rectangle.Empty, true)]); }
            catch (InvalidOperationException) { emptyRejected = true; }
            Check(emptyRejected, "有効な画面のない状態を許可");
        });
        Run("即時撮影は先頭OCR行を使いコピーと連番保存だけを実行", () => WithServiceDirectory(directory => RunOutputTask(async () =>
        {
            using var image = new Bitmap(8, 6); image.SetPixel(2, 3, Color.Red);
            int copies = 0, launches = 0, recognitions = 0;
            var output = new OutputRouter(directory, bitmap => { Equal(Color.Red.ToArgb(), bitmap.GetPixel(2, 3).ToArgb()); copies++; return Task.CompletedTask; }, launch: _ => launches++);
            var settings = new AppSettings { SaveDirectory = directory, FileNamePattern = "{yyyymmdd}_{hhmmss}_{ocr}_{##}.png", OpenFolderAfterSave = true,
                DefaultDestination = OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp };
            var time = new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9));
            Task<OcrResult> Recognize(Bitmap bitmap, string _, CancellationToken token)
            { recognitions++; Equal(8, bitmap.Width); return Task.FromResult(new OcrResult(["先頭 行{#}", "次 行"])); }
            for (int n = 0; n < 2; n++) await InstantCapture.DeliverAsync(image, settings, time, output, Recognize);
            Equal(2, copies); Equal(0, launches); Equal(2, recognitions);
            Check(File.Exists(Path.Combine(directory, "20261010_123456_先頭行{#}_01.png")), "先頭行・日時不正");
            Check(File.Exists(Path.Combine(directory, "20261010_123456_先頭行{#}_02.png")), "連番不正");
        })));
        Run("登録範囲の即時出力は範囲名を保持して再展開せず連番保存", () => WithServiceDirectory(directory => RunOutputTask(async () =>
        {
            using var image = new Bitmap(6, 4); image.SetPixel(2, 2, Color.Red);
            int launches = 0;
            var output = new OutputRouter(directory, launch: _ => launches++);
            var settings = new AppSettings { SaveDirectory = directory, FileNamePattern = "{region}_{##}.png",
                DefaultDestination = OutputDestination.File | OutputDestination.ExternalApp, OpenFolderAfterSave = true };
            for (int n = 0; n < 2; n++)
                await InstantCapture.DeliverAsync(image, settings, DateTimeOffset.Now, output, regionName: "進捗:{#}");
            Equal(0, launches);
            foreach (int n in new[] { 1, 2 })
            {
                using var saved = new Bitmap(Path.Combine(directory, $"進捗{{#}}_{n:D2}.png"));
                Equal(Color.Red.ToArgb(), saved.GetPixel(2, 2).ToArgb());
            }
        })));
        Run("即時範囲撮影は不正・過大座標を画面読取前に拒否", () =>
        {
            foreach (Rectangle bounds in new[] { Rectangle.Empty, new Rectangle(int.MaxValue, 0, 2, 2),
                new Rectangle(0, int.MaxValue, 2, 2), new Rectangle(0, 0, 32769, 1), new Rectangle(0, 0, 20000, 20000) })
            {
                bool rejected = false;
                try { using var unexpected = InstantCapture.Take(bounds, includeCursor: true); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "不正範囲を受け入れた");
            }
        });
        Run("即時コピーだけではOCR不要・OCR失敗でも画像保存", () => WithServiceDirectory(directory => RunOutputTask(async () =>
        {
            using var image = new Bitmap(4, 4); int recognitions = 0;
            var output = new OutputRouter(directory, _ => Task.CompletedTask);
            var settings = new AppSettings { SaveDirectory = directory, FileNamePattern = "{ocr}_{#}.png" };
            Task<OcrResult> Recognize(Bitmap _, string path, CancellationToken token)
            { recognitions++; return Task.FromResult(new OcrResult([], "test error")); }
            await InstantCapture.DeliverAsync(image, settings, DateTimeOffset.Now, output, Recognize);
            Equal(0, recognitions);
            string result = await InstantCapture.DeliverAsync(image, settings with { DefaultDestination = OutputDestination.File }, DateTimeOffset.Now, output, Recognize);
            Check(result.Contains("OCRなし") && File.Exists(Path.Combine(directory, "OCRなし_1.png")), "失敗で画像を失った");
        })));
        Run("即時設定はRDPの画面番号対応を示し再表示で構成を更新する", () =>
        {
            IReadOnlyList<DisplayTarget> displays = [new(@"\\.\DISPLAY97", new(0, 0, 2560, 1440), true)];
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => [], listDisplays: () => displays);
            Equal("画面1（主）", form.Controls.Find("InstantDisplayLabel1", true).Single().Text);
            Equal("画面2（未接続）", form.Controls.Find("InstantDisplayLabel2", true).Single().Text);
            Check(form.Controls.Find("InstantDisplays", true).Single().Text.Contains("画面1（主）: DISPLAY97 (2560×1440)", StringComparison.Ordinal), "主画面番号とデバイス名の対応がない");
            displays = [new(@"\\.\DISPLAY101", new(-1920, 0, 1920, 1080)), displays[0]];
            typeof(Form).GetMethod("OnActivated", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, [EventArgs.Empty]);
            Equal("画面2", form.Controls.Find("InstantDisplayLabel2", true).Single().Text);
            Check(form.Controls.Find("InstantDisplays", true).Single().Text.Contains("画面2: DISPLAY101", StringComparison.Ordinal), "再表示で接続構成が更新されない");
            Equal("Win+Shift+1", ((HotkeyEditor)form.Controls.Find("InstantShortcut1", true).Single()).Value);
        });
        Run("即時キー10件はスクロールして到達し変更を保存", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings(), next => { saved = next; return null; }, () => { }, () => []);
            _ = form.Handle; foreach (Control child in Descendants(form)) _ = child.Handle;
            var tabs = (TabControl)form.Controls.Find("SettingsTabs", true).Single();
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "即時撮影キー"); LayoutTree(form);
            var last = (HotkeyEditor)form.Controls.Find("InstantShortcut10", true).Single();
            var table = last.Parent!;
            tabs.SelectedTab.ScrollControlIntoView(last); LayoutTree(form);
            Check(last.Width >= 400 && last.Height >= 32, "入力領域不足");
            Check(last.RectangleToScreen(last.ClientRectangle).IntersectsWith(tabs.SelectedTab.RectangleToScreen(tabs.SelectedTab.ClientRectangle)), "10番がスクロールできない");
            ((ComboBox)last.Controls.Find("InstantShortcut10Key", true).Single()).SelectedIndex = 0;
            typeof(SettingsForm).GetMethod("SaveSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(form, null);
            Check(saved is not null, "保存されない"); Equal("", saved!.InstantShortcuts[9]);
            table.Dock = DockStyle.None; table.Width = 240; LayoutTree(form);
            Check(last.Width < 400, "Dock断線検出不可");
        });
    }
}
