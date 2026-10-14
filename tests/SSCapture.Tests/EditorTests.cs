using System.Diagnostics;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    static partial void EditorTests()
    {
        Run("ペイント旧パスは実行エイリアスへ解決し任意の欠落exeは置換しない", () =>
        {
            string legacy = EditorExecutable.LegacyPaint, alias = EditorExecutable.PaintAlias;
            Equal(alias, EditorExecutable.Resolve(legacy, p => p == alias));
            Equal(legacy, EditorExecutable.Resolve(legacy, p => p == legacy || p == alias));
            string custom = Path.Combine(Path.GetTempPath(), "任意アプリ", "mspaint.exe");
            Equal(custom, EditorExecutable.Resolve(custom, p => p == custom));
            bool rejected = false;
            try { EditorExecutable.Resolve(custom, p => p == alias); }
            catch (FileNotFoundException e) { rejected = e.FileName == custom; }
            Check(rejected, "任意の欠落アプリをペイントへ置換した");
            foreach (string invalid in new[] { "", "mspaint.exe", "C:\\sample.txt" })
            {
                rejected = false;
                try { EditorExecutable.Resolve(invalid, _ => true); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "不正な起動先を許可した");
            }
        });
        Run("ペイント旧設定の保存ON/OFFは正しい実行先と完成PNGを渡す", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            string paint = EditorExecutable.PaintPreset;
            if (!File.Exists(paint)) return; // 実ペイント未導入環境の解決規則は上記fixtureで検証する。
            using var bitmap = OutputImage();
            foreach (bool file in new[] { false, true })
            {
                ProcessStartInfo? launch = null;
                var settings = OutputSettings(directory) with { ImageEditorPath = EditorExecutable.LegacyPaint };
                var router = new OutputRouter(Path.Combine(directory, "work"), launch: value => launch = value);
                await router.ScreenshotAsync(bitmap, OutputDestination.ExternalApp | (file ? OutputDestination.File : 0), settings);
                Check(launch is not null, "起動引数がない");
                Equal(EditorExecutable.Resolve(settings.ImageEditorPath), launch!.FileName);
                Check(!launch.UseShellExecute, "引数をシェルへ渡した");
                string path = launch.ArgumentList.Single();
                Check(File.Exists(path), "完成画像がない");
                Equal(file, Path.GetDirectoryName(path) == settings.SaveDirectory);
                using var opened = new Bitmap(path); Equal(bitmap.Size, opened.Size);
            }
        })));
        Run("引数は解析後に変数を展開し空白・記号を1引数に保つ", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "日本語 & {filename}", "a b.png");
            string[] args = EditorArguments.Expand("--open \"{file}\" --folder \"{directory}\" {filename}", path);
            Equal(5, args.Length); Equal(Path.GetFullPath(path), args[1]);
            Equal(Path.GetDirectoryName(path), args[3]); Equal("a b.png", args[4]);
            Equal(Path.GetFullPath(path), EditorArguments.Expand("{file}", path)[0]);
        });
        Run("Windows引数の引用符・末尾backslash・空文字を保持", () =>
        {
            string[] args = EditorArguments.Parse("\"\" \"C:\\two words\\\\\" \"a\\\"b\" bare");
            Equal(4, args.Length); Equal("", args[0]); Equal("C:\\two words\\", args[1]); Equal("a\"b", args[2]); Equal("bare", args[3]);
            foreach (string invalid in new[] { "\"unclosed", "{unknown}", "a\0b" })
            {
                bool rejected = false; try { EditorArguments.Validate(invalid); } catch (ArgumentException) { rejected = true; }
                Check(rejected, invalid + " が拒否されない");
            }
        });
        Run("ショートカット選択部品が4修飾・PrintScreen・数字・なしを往復", () =>
        {
            using var editor = new HotkeyEditor("Key1", "Win+Shift+S");
            foreach (string value in new[] { "Win+Shift+S", "PrintScreen", "Ctrl+Alt+F8", "Win+Ctrl+Alt+Shift+9", "" })
            { editor.Value = value; Equal(value, editor.Value); }
            var combo = (ComboBox)editor.Controls.Find("Key1Key", true).Single();
            Equal("なし", combo.SelectedItem!.ToString());
            Check(combo.Items.Count > 80, "通常キーの選択肢が不足");
            Check(!editor.Controls.Find("Win", true).Single().Enabled, "なしの時は修飾キー無効");
        });
        Run("編集アプリプリセットは既定で同じファイルを渡す", () =>
        {
            Check(EditorPresets.Images.Any(p => p.Name == "MS ペイント"), "Paintなし");
            Check(EditorPresets.Videos.Any(p => p.Name == "Shotcut"), "Shotcutなし");
            foreach (var preset in EditorPresets.Images.Concat(EditorPresets.Videos))
                Equal(Path.GetFullPath("sample.png"), EditorArguments.Expand(preset.Arguments, "sample.png").Single());
        });
    }
}
