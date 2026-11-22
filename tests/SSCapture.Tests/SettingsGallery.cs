using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

// docs/settings.md に載せる設定画面の画像を生成する。
// 新規設定の初期値を使い、このPCのユーザーフォルダ・モニター・マイク・自動起動の予約を例の値に差し替える。
// 画像は設定画面のウィンドウだけをPrintWindowで取得し、デスクトップや他のウィンドウは取得しない。
internal static partial class Program
{
    private const uint RenderFullContent = 2;
    private const string SampleProfile = @"C:\Users\user";

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint deviceContext, uint flags);

    internal static int SettingsGallery(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        // 本体のProgram.ConfigureUiと同じ既定フォントで描く。最初のウィンドウを作る前に設定する。
        Application.SetDefaultFont(new Font("Yu Gothic UI", 12F));
        DisplayTarget[] displays =
        [
            new(@"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), true, "モニター1"),
            new(@"\\.\DISPLAY2", new Rectangle(1920, 0, 1920, 1080), false, "モニター2")
        ];
        DisplayLayout[] layouts = [.. displays.Select((display, index) =>
            new DisplayLayout("sample-display-" + (index + 1), display.MonitorName!, display.Bounds, 96, 96))];
        using var form = new SettingsForm(SampleSettings(), _ => null, _ => { },
            () => [new MicrophoneInfo("sample-microphone", "マイク（例）")],
            now: () => new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.FromHours(9)),
            listDisplays: () => displays, regionDisplays: () => layouts, startupRegistration: new FakeStartup());
        form.StartPosition = FormStartPosition.Manual;
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        form.Location = work.Location;
        int exitCode = 1;
        form.Shown += async (_, _) =>
        {
            try
            {
                await CaptureSettingsPagesAsync(form, output);
                exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { form.Close(); }
        };
        Application.Run(form);
        return exitCode;
    }

    private static AppSettings SampleSettings()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Clean(string path) => !string.IsNullOrEmpty(profile) && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            ? SampleProfile + path[profile.Length..] : path;
        AppSettings settings = new AppSettings().ApplyDefaults();
        return settings with
        {
            SaveDirectory = Clean(settings.SaveDirectory),
            CapturePresets = [.. settings.CapturePresets.Select(preset => preset with { SaveDirectory = Clean(preset.SaveDirectory) })],
            ImageEditors = [.. settings.ImageEditors.Select(app => app with { Executable = Clean(app.Executable) })],
            VideoEditors = [.. settings.VideoEditors.Select(app => app with { Executable = Clean(app.Executable) })]
        };
    }

    private static async Task CaptureSettingsPagesAsync(SettingsForm form, string output)
    {
        (string Page, string? InnerTabs, string File)[] pages =
        [
            ("基本", null, "01-basic"),
            ("通知", null, "02-notification"),
            ("入力プリセット", "InputPresetSections", "03-input-presets"),
            ("出力プリセット", "OutputPresetSections", "04-output-presets"),
            ("ショートカット", "ShortcutGroups", "05-shortcuts"),
            ("外部アプリ", "ExternalApplicationTabs", "06-external-apps"),
            ("コマンド生成", null, "07-command"),
            ("履歴", null, "08-history")
        ];
        Size defaultSize = form.Size;
        foreach (var (page, innerTabs, file) in pages)
        {
            form.SelectCategory(page);
            Check(form.SelectedCategory == page, $"「{page}」を開けない");
            if (innerTabs is null)
            {
                await SaveSettingsPageAsync(form, defaultSize, Path.Combine(output, file + ".png"));
                continue;
            }
            var tabs = (TabControl)form.Controls.Find(innerTabs, true).Single();
            for (int index = 0; index < tabs.TabPages.Count; index++)
            {
                tabs.SelectedIndex = index;
                await SaveSettingsPageAsync(form, defaultSize, Path.Combine(output, $"{file}-{index + 1}.png"));
                Console.WriteLine($"{page} / {tabs.TabPages[index].Text}");
            }
        }
    }

    private static async Task SaveSettingsPageAsync(SettingsForm form, Size defaultSize, string path)
    {
        form.Size = defaultSize;
        await RenderAsync(form);
        // 表示しきれないページは、スクロールなしで全体が入るまでウィンドウを縦に伸ばす。
        int overflow = form.Controls.Find("SettingsTabs", true).Single() is TabControl { SelectedTab: { } page }
            ? HiddenHeight(page) : 0;
        if (overflow > 0)
        {
            form.Height = Math.Min(defaultSize.Height + overflow, SystemInformation.MaxWindowTrackSize.Height);
            await RenderAsync(form);
        }
        using var image = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            nint deviceContext = graphics.GetHdc();
            try { Check(PrintWindow(form.Handle, deviceContext, RenderFullContent), "設定画面の画像を取得できない"); }
            finally { graphics.ReleaseHdc(deviceContext); }
        }
        image.Save(path, ImageFormat.Png);
        Console.WriteLine($"{Path.GetFileName(path)} {image.Width}x{image.Height}");
    }

    // ページ内で最も多くはみ出しているスクロール領域の高さ。
    private static int HiddenHeight(Control root)
    {
        int hidden = root is ScrollableControl { AutoScroll: true } scrollable && scrollable.VerticalScroll.Visible
            ? scrollable.DisplayRectangle.Height - scrollable.ClientSize.Height : 0;
        foreach (Control child in root.Controls)
            if (child.Visible) hidden = Math.Max(hidden, HiddenHeight(child));
        return Math.Max(0, hidden);
    }

    private static async Task RenderAsync(Form form)
    {
        form.PerformLayout();
        form.Refresh();
        await Task.Delay(400);
        Application.DoEvents();
        form.Refresh();
    }
}
