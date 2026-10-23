using System.Text.Json;
using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    // 新しい多段メニューの明示操作確認。合成画素だけを使い常駐設定・クリップボードは変更しない。
    private static int CaptureMenuPreview(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        using var host = new CaptureMenuPreviewHost { Text = WindowTitle.Create("撮影メニュー最終確認"), StartPosition = FormStartPosition.Manual,
            Bounds = new(work.X + work.Width / 2, work.Y, work.Width - work.Width / 2, work.Height), Font = new Font("Yu Gothic UI", 12) };
        var status = new Label { Dock = DockStyle.Fill, Padding = new(20), Text = "開始後、右クリックでプリセットと一時カスタマイズを確認。末端コマンド後に矩形へ切替えて左ドラッグしてください。画面固定・カーソルの切替で合成背景が変わります。入力フォームはありません。" };
        var button = SettingsForm.MakeButton("メニュー確認を開始", "StartMenuPreview"); button.Dock = DockStyle.Top; button.Height = 56;
        host.Controls.Add(status); host.Controls.Add(button);
        AppSettings settings = new AppSettings { DefaultMode = CaptureMode.FixedRegion, DefaultDestination = OutputDestination.File,
            FixedWidth = 320, FixedHeight = 180, SaveDirectory = directory }.EnsurePresets();
        settings = settings with { CapturePresets = settings.CapturePresets.Append(CapturePreset.FromSettings("256色ファイル", settings with { ScreenshotColorDepth = ScreenshotColorDepth.Indexed8 })).ToArray(),
            SavedRegions = settings.SavedRegions.Append(SavedRegion.FromSettings("小さい固定枠", settings with { FixedWidth = 200, FixedHeight = 120, ScreenshotPaddingPixels = 10 })).ToArray() };
        var configure = SettingsForm.MakeButton("設定画面を確認", "ConfigurePresetPreview"); configure.Dock = DockStyle.Top; configure.Height = 56; host.Controls.Add(configure);
        SettingsForm? settingsForm = null;
        configure.Click += (_, _) =>
        {
            settingsForm?.Dispose();
            settingsForm = new SettingsForm(settings, next => { next.Validate(); settings = next; new SettingsStore(Path.Combine(directory, "settings.json")).Save(next); Log("settings-saved"); return null; }, () => { }, () => []);
            settingsForm.StartPosition = FormStartPosition.Manual; settingsForm.Bounds = host.Bounds; settingsForm.Show(); settingsForm.Activate();
        };
        SessionController? session = null; int acquired = 0;
        host.KeyPreview = true;
        using var repaint = new System.Windows.Forms.Timer { Interval = 100 };
        repaint.Tick += (_, _) => { if (session is not null) host.Invalidate(); };
        repaint.Start();
        host.Paint += (_, e) =>
        {
            if (session is null || session.Completion.IsCompleted) return;
            e.Graphics.DrawImageUnscaled(session.Snapshot.Image, 0, 0);
            var rect = session.Selection; rect.Offset(-session.Snapshot.Bounds.X, -session.Snapshot.Bounds.Y);
            e.Graphics.DrawRectangle(Pens.DeepSkyBlue, rect);
        };
        host.MouseDown += (_, e) =>
        {
            if (session is null) return;
            if (e.Button == MouseButtons.Right) session.SelectionMenu.Show(host, e.Location);
        };
        host.CommandKey = key => session?.HandleCommandKey(key) == true;
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            button.Visible = status.Visible = configure.Visible = false;
            try
            {
                Rectangle bounds = host.RectangleToScreen(host.ClientRectangle);
                DesktopSnapshot Frame(bool cursor, bool observe)
                {
                    bool hidden = Application.OpenForms.OfType<CaptureOverlay>().All(f => !f.Visible);
                    if (!hidden || session?.SelectionMenu.Visible == true) throw new InvalidOperationException("再取得前に選択UIが隠れていません。");
                    acquired++;
                    var image = new Bitmap(bounds.Width, bounds.Height);
                    using (var g = Graphics.FromImage(image))
                    {
                        g.Clear(cursor ? Color.LightSkyBlue : acquired % 2 == 0 ? Color.PaleGreen : Color.MistyRose);
                        g.DrawString($"合成画面 取得{acquired}回 / cursor={cursor} / metadata={observe}", host.Font, Brushes.Black, 32, 32);
                        g.DrawString("右クリックで撮影範囲 / 画像・出力 / 表示・操作", host.Font, Brushes.Black, 32, 80);
                    }
                    Log("reacquired", new { acquired, cursor, observe, overlaysHidden = hidden });
                    return new(image, bounds, [bounds]);
                }
                session = new(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, settings.DefaultDestination, settings.FreezeFrame,
                    IncludeCursor: settings.IncludeCursor, FixedSize: new(settings.FixedWidth, settings.FixedHeight), FixedAnchor: settings.FixedCaptureAnchor,
                    ShowSelectionGuide: false, OutputSettings: settings), Frame(settings.IncludeCursor, false), [],
                    initialPointer: new(bounds.X + 420, bounds.Y + 300), recapture: Frame);
                // 合成画素の専用面だけに実オーバーレイを出し、製品と同じ入力経路でT03を確認する。
                session.Start();
                host.Invalidate();
                using var outcome = await session.Completion;
                if (outcome.Image is null) { status.Text = "キャンセルしました。再開できます。"; Log("cancelled"); }
                else
                {
                    outcome.Image.Save(Path.Combine(directory, "result.png"));
                    status.Text = $"完了: {outcome.Image.Width}×{outcome.Image.Height}px / 再取得{acquired}回 / カーソル={outcome.OutputSettings!.IncludeCursor} / 画面固定={outcome.OutputSettings.FreezeFrame}";
                    Log("completed", new { width = outcome.Image.Width, height = outcome.Image.Height, acquired,
                        outcome.OutputSettings.IncludeCursor, outcome.OutputSettings.FreezeFrame, outcome.OutputSettings.ScreenshotColorDepth,
                        outcome.OutputSettings.ScreenshotPaddingPixels, outcome.OutputSettings.SnapDistancePixels, outcome.OutputSettings.GridStepPixels });
                }
            }
            catch (Exception ex) { status.Text = ex.ToString(); Log("failed", ex.ToString()); }
            finally { session?.Cancel(); session = null; button.Enabled = true; button.Visible = status.Visible = configure.Visible = true; host.Invalidate(); if (!host.IsDisposed) host.Activate(); }
        };
        host.FormClosed += (_, _) => { session?.Cancel(); settingsForm?.Dispose(); };
        Application.Run(host); return 0;
        void Log(string action, object? data = null) => File.AppendAllText(Path.Combine(directory, "operations.jsonl"),
            JsonSerializer.Serialize(new { at = DateTimeOffset.Now, action, data }) + Environment.NewLine);
    }
    private sealed class CaptureMenuPreviewHost : Form
    {
        internal Func<Keys, bool>? CommandKey;
        internal CaptureMenuPreviewHost() { DoubleBuffered = true; }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => CommandKey?.Invoke(keyData) == true || base.ProcessCmdKey(ref msg, keyData);
    }
}
