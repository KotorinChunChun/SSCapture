using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void OutputConditionTests()
    {
        Run("保存先とフォルダー表示はFileに従い命名欄は未保存でも編集できる", () =>
        {
            for (int bits = 0; bits < 8; bits++)
            {
                var destination = (OutputDestination)bits;
                var settings = new AppSettings { DefaultDestination = destination, SaveDirectory = "relative",
                    FileNamePattern = "{unknown}", OneOcrDirectory = "relative" };
                bool rejected = false; try { settings.Validate(); } catch (ArgumentException) { rejected = true; }
                Equal(destination.HasFlag(OutputDestination.File), rejected);
                using var form = new SettingsForm(settings, _ => null, _ => { }, () => [], listDisplays: () => []);
                foreach (string name in new[] { "SaveDirectory", "FileNamePattern", "OcrSeparator", "OneOCRDirectory", "OpenFolderAfterSave" })
                {
                    var fields = form.Controls.Find(name, true);
                    if (fields.Length == 0 && name == "OneOCRDirectory") fields = form.Controls.Find("OneOcrDirectory", true);
                    bool fileOnly = name is "SaveDirectory" or "OpenFolderAfterSave";
                    Equal(!fileOnly || destination.HasFlag(OutputDestination.File), fields.Single().Enabled);
                }
                ((CheckBox)form.Controls.Find("FileOutput", true).Single()).Checked = true;
                Equal("relative", form.Controls.Find("SaveDirectory", true).Single().Text);
                Equal("{unknown}", form.Controls.Find("FileNamePattern", true).Single().Text);
            }
        });
        Run("コピーと外部アプリだけでは無効なファイル命名を使用しない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = new Bitmap(3, 3);
            foreach (var destination in new[] { OutputDestination.None, OutputDestination.Clipboard, OutputDestination.ExternalApp, OutputDestination.Clipboard | OutputDestination.ExternalApp })
            {
                int copies = 0, launches = 0;
                using var router = new OutputRouter(Path.Combine(directory, "work"), _ => { copies++; return Task.CompletedTask; }, launch: _ => launches++);
                var settings = OutputSettings(directory) with { DefaultDestination = destination, SaveDirectory = "relative", FileNamePattern = "{unknown}", ImageEditorPath = CreateFakeEditor(directory) };
                await router.ScreenshotAsync(image, destination, settings);
                Equal(destination.HasFlag(OutputDestination.Clipboard) ? 1 : 0, copies);
                Equal(destination.HasFlag(OutputDestination.ExternalApp) ? 1 : 0, launches);
            }
        })));
        Run("即時コピーも無効な保存先と命名を解釈しない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = new Bitmap(3, 3); int copied = 0;
            using var router = new OutputRouter(directory, _ => { copied++; return Task.CompletedTask; });
            await InstantCapture.DeliverAsync(image, new AppSettings { DefaultDestination = OutputDestination.Clipboard, FileNamePattern = "{unknown}", SaveDirectory = "relative" }, DateTimeOffset.Now, router);
            Equal(1, copied);
        })));
        Run("出力プリセットの命名は未保存でも有効で保存先の値を保持する", () =>
        {
            using var editor = new CapturePresetEditor(new AppSettings(), () => new AppSettings());
            editor.Controls.Find("SaveDirectory", true).Single().Text = "relative";
            var pattern = editor.Controls.Find("FileNamePattern", true).Single();
            pattern.Text = "{region}_{##}.{ext}";
            Check(pattern.Enabled, "未保存の命名パターンが無効");
            editor.Controls.Find("CapturePresetName", true).Single().Text = "コピー専用";
            editor.StoreCurrent(); Equal(1, editor.Presets.Length);
            Check(!editor.Controls.Find("SaveDirectory", true).Single().Enabled, "プリセットの保存欄が有効");
            ((CheckBox)editor.Controls.Find("FileOutput", true).Single()).Checked = true;
            Check(editor.Controls.Find("SaveDirectory", true).Single().Enabled, "File ONでも戻らない");
            Equal("relative", editor.Controls.Find("SaveDirectory", true).Single().Text);
        });
    }
}
