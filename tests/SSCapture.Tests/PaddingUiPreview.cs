using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    // 個人設定・画面撮影・クリップボードを使わない、設定と実出力の操作確認用。
    private static int PaddingUiPreview(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        var settings = store.Load(out _ ) with { SaveDirectory = directory, DefaultDestination = OutputDestination.File };
        using var source = new Bitmap(240, 120);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.FromArgb(24, 80, 144));
            graphics.FillRectangle(Brushes.Orange, 0, 0, 32, 32);
            graphics.FillRectangle(Brushes.LimeGreen, 208, 88, 32, 32);
            using var font = new Font("Yu Gothic UI", 18);
            graphics.DrawString("SSCapture", font, Brushes.White, 46, 43);
        }
        Bitmap? preview = null;
        var picture = new PictureBox { Name = "PaddingPreviewImage", Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.FromArgb(90, 90, 90) };
        var info = new Label { Name = "PaddingPreviewInfo", Dock = DockStyle.Top, Height = 48, Text = "基本タブで余白を指定して保存すると、合成画像の実際の出力をここに表示します。" };
        int outputs = 0;
        using var form = new SettingsForm(settings, next =>
        {
            try
            {
                next = next with { SaveDirectory = directory, DefaultDestination = OutputDestination.File };
                store.Save(next);
                string name = $"padding-{next.ScreenshotPaddingPixels}-{++outputs}.png";
                RunOutputTask(async () => await new OutputRouter(Path.Combine(directory, "work")).ScreenshotAsync(source, OutputDestination.File, next, name));
                string file = Path.Combine(directory, name);
                using var output = new Bitmap(file);
                preview?.Dispose();
                preview = (Bitmap)output.Clone();
                picture.Image = preview;
                info.Text = $"白余白: {next.ScreenshotPaddingPixels}px / 元画像: 240×120px / 出力: {output.Width}×{output.Height}px";
                Console.WriteLine($"PADDING SAVED: {next.ScreenshotPaddingPixels}; SIZE: {output.Width}x{output.Height}; FILE: {name}");
                return null;
            }
            catch (Exception error) { return error.Message; }
        }, () => { }, () => []);
        form.Text = WindowTitle.Create("プリセット操作確認");
        var tabs = (TabControl)form.Controls.Find("SettingsTabs", true).Single();
        var page = new TabPage("出力プレビュー");
        page.Controls.Add(picture); page.Controls.Add(info);
        tabs.TabPages.Add(page);
        form.Shown += (_, _) =>
        {
            Rectangle work = Screen.FromControl(form).WorkingArea;
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = new Rectangle(work.Right - Math.Max(form.MinimumSize.Width, work.Width / 2), work.Top,
                Math.Max(form.MinimumSize.Width, work.Width / 2), Math.Max(form.MinimumSize.Height, work.Height));
            Console.WriteLine($"PADDING READY: {settings.ScreenshotPaddingPixels}");
        };
        Application.Run(form);
        preview?.Dispose();
        return 0;
    }
}
