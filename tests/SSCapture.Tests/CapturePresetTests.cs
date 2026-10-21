using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void CapturePresetTests()
    {
        Run("プリセットは4項目だけを適用しショートカットや履歴を維持", () =>
        {
            var baseline = new AppSettings { ScreenshotPaddingPixels = 3, IncludeCursor = true, KeepHistory = false };
            var preset = CapturePreset.FromSettings("資料", baseline with { ScreenshotPaddingPixels = 20,
                DefaultDestination = OutputDestination.Clipboard | OutputDestination.File, FileNamePattern = "preset.png" });
            var next = preset.Apply(baseline);
            Equal(20, next.ScreenshotPaddingPixels); Equal(preset.Destination, next.DefaultDestination);
            Equal("preset.png", next.FileNamePattern); Equal(3, baseline.ScreenshotPaddingPixels);
            Check(next.IncludeCursor && !next.KeepHistory, "無関係の設定が変化");
            Equal(baseline.ScreenshotShortcut1, next.ScreenshotShortcut1);
        });
        Run("旧設定のプリセットは空で保存往復後も4項目を維持", () => WithServiceDirectory(directory =>
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{}");
            Equal(0, store.Load(out var warning).CapturePresets.Length); Equal<string?>(null, warning);
            var settings = new AppSettings { CapturePresets = [CapturePreset.FromSettings("資料", new AppSettings { ScreenshotPaddingPixels = 20 })] };
            store.Save(settings); SettingsEqual(settings, store.Load(out warning)); Equal<string?>(null, warning);
        }));
        Run("プリセットの不正値・重複・nullを拒否", () =>
        {
            var good = CapturePreset.FromSettings("資料", new AppSettings());
            foreach (var invalid in new[] { good with { Name = " " }, good with { Id = "" }, good with { PaddingPixels = -1 },
                good with { PaddingPixels = 4097 }, good with { SaveDirectory = "relative", Destination = OutputDestination.File }, good with { FileNamePattern = "../escape.png", Destination = OutputDestination.File },
                good with { Destination = (OutputDestination)128 } })
                PaddingRejected(invalid.Validate);
            PaddingRejected(() => new AppSettings { CapturePresets = null! }.Validate());
            PaddingRejected(() => new AppSettings { CapturePresets = [good, good with { Id = "different" }] }.Validate());
            PaddingRejected(() => new AppSettings { CapturePresets = [good, good with { Name = "別名" }] }.Validate());
        });
        Run("今回の余白とプリセットの適用は次回セッションへ漏れない", () =>
        {
            var settings = new AppSettings { ScreenshotPaddingPixels = 7, CapturePresets = [CapturePreset.FromSettings("資料", new AppSettings { ScreenshotPaddingPixels = 20 })] };
            var session = OutputSession(settings);
            try
            {
                session.ChangePadding(10); Equal(10, session.OutputSettings.ScreenshotPaddingPixels);
                session.ApplyPreset(settings.CapturePresets[0]); Equal(20, session.OutputSettings.ScreenshotPaddingPixels);
                Equal(7, settings.ScreenshotPaddingPixels);
            }
            finally { session.Cancel(); }
            var next = OutputSession(settings);
            try { Equal(7, next.OutputSettings.ScreenshotPaddingPixels); } finally { next.Cancel(); }
        });
        Run("プリセットの出力先適用後の個別ON/OFFは最新状態から切替", () =>
        {
            OutputDestination destination = OutputDestination.None;
            using var menu = new CaptureContextMenu(OutputDestination.Clipboard, value => destination = value, () => { }, () => { });
            menu.SetDestination(OutputDestination.File | OutputDestination.ExternalApp);
            ((ToolStripMenuItem)menu.Items[0]).PerformClick();
            Equal(OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp, destination);
        });
        Run("右メニューの一時余白とプリセット項目はそれぞれの値を渡す", () =>
        {
            int padding = -1; CapturePreset? selected = null;
            var preset = CapturePreset.FromSettings("資料", new AppSettings());
            using var menu = new CaptureContextMenu(OutputDestination.Clipboard, _ => { }, () => { }, () => { },
                defaultPadding: 7, changePadding: p => padding = p, presets: [preset], applyPreset: p => selected = p);
            var margins = (ToolStripMenuItem)menu.Items["ScreenshotPaddingMenu"]!;
            Equal("画像の余白（7px）", margins.Text); Equal(4, margins.DropDownItems.Count);
            Equal("0px,10px,20px,50px", string.Join(",", margins.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text)));
            ((ToolStripMenuItem)margins.DropDownItems[2]).PerformClick(); Equal(20, padding);
            ((ToolStripMenuItem)margins.DropDownItems[3]).PerformClick(); Equal(50, padding);
            menu.SetPadding(50); Equal("画像の余白（50px）", margins.Text);
            ((ToolStripMenuItem)margins.DropDownItems[0]).PerformClick(); Equal(0, padding);
            var presets = (ToolStripMenuItem)menu.Items["CapturePresetMenu"]!;
            ((ToolStripMenuItem)presets.DropDownItems[0]).PerformClick(); Equal(preset.Id, selected!.Id);
        });
        Run("設定フォーム内でプリセットを登録し保存コールバックに渡す", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings { ScreenshotPaddingPixels = 10 }, s => { saved = s; return null; }, () => { }, () => []);
            var editor = (CapturePresetEditor)form.Controls.Find("CapturePresetEditor", true).Single();
            form.Controls.Find("CapturePresetName", true).Single().Text = "資料";
            editor.StoreCurrent(); Equal(1, editor.Presets.Length); Equal(10, editor.Presets[0].PaddingPixels);
            ((NumericUpDown)form.Controls.Find("PresetPaddingPixels", true).Single()).Value = 20;
            editor.StoreCurrent(); Equal(1, editor.Presets.Length); Equal(20, editor.Presets[0].PaddingPixels);
            typeof(SettingsForm).GetMethod("SaveSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(form, null);
            Equal(1, saved!.CapturePresets.Length); Equal(20, saved.CapturePresets[0].PaddingPixels);
            Equal(10, saved.ScreenshotPaddingPixels);
        });
        Run("プリセットは選択結果から共通出力まで適用され余白と命名を共有", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var settings = OutputSettings(directory);
            var preset = CapturePreset.FromSettings("資料", settings with { ScreenshotPaddingPixels = 20, FileNamePattern = "preset.png" });
            // SessionControllerはSTA検証側で作成するため、出力側はその不変な設定を使用する。
            var applied = preset.Apply(settings);
            using var image = new Bitmap(20, 20);
            var router = new OutputRouter(Path.Combine(directory, "work"));
            await router.ScreenshotAsync(image, OutputDestination.File, applied);
            using var saved = new Bitmap(Path.Combine(settings.SaveDirectory, "preset.png"));
            Equal(new Size(60, 60), saved.Size); Equal(Color.White.ToArgb(), saved.GetPixel(0, 0).ToArgb());
            Equal(0, settings.ScreenshotPaddingPixels);
        })));
        Run("セッションの確定結果にプリセット出力設定が付く", () =>
        {
            var settings = new AppSettings { ScreenshotPaddingPixels = 3 };
            var session = OutputSession(settings);
            try
            {
                session.ApplyPreset(CapturePreset.FromSettings("資料", settings with { ScreenshotPaddingPixels = 20, FileNamePattern = "preset.png" }));
                session.CompleteSelection(new(new Rectangle(10, 10, 40, 30), null, OutputDestination.File));
                using var outcome = session.Completion.GetAwaiter().GetResult();
                Equal(20, outcome.OutputSettings!.ScreenshotPaddingPixels); Equal("preset.png", outcome.OutputSettings.FileNamePattern);
                Equal(new Size(40, 30), outcome.Image!.Size);
            }
            finally { session.Cancel(); }
        });
    }

    private static SessionController OutputSession(AppSettings settings) => new(
        new(CaptureMode.Rectangle, CaptureIntent.Screenshot, settings.DefaultDestination, OutputSettings: settings),
        new DesktopSnapshot(new Bitmap(400, 300), new(0, 0, 400, 300), [new(0, 0, 400, 300)]), []);
}
