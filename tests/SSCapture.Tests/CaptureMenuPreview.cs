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
        using var host = new Form { Text = WindowTitle.Create("撮影メニュー最終確認"), StartPosition = FormStartPosition.Manual,
            Bounds = new(work.X + work.Width / 2, work.Y, work.Width - work.Width / 2, work.Height), Font = new Font("Yu Gothic UI", 12) };
        var status = new Label { Dock = DockStyle.Fill, Padding = new(20), Text = "開始後、右クリックで3分類を確認。画面固定・カーソルの切替で合成背景が変わります。固定寸法を入力しEnterで確定してください。" };
        var button = SettingsForm.MakeButton("メニュー確認を開始", "StartMenuPreview"); button.Dock = DockStyle.Top; button.Height = 56;
        host.Controls.Add(status); host.Controls.Add(button);
        SessionController? session = null; int acquired = 0;
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try
            {
                Rectangle bounds = host.RectangleToScreen(host.ClientRectangle);
                DesktopSnapshot Frame(bool cursor, bool observe)
                {
                    bool hidden = Application.OpenForms.OfType<CaptureOverlay>().All(f => !f.Visible);
                    if (!hidden) throw new InvalidOperationException("再取得前に選択画面が隠れていません。");
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
                session = new(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.File, FixedSize: new(320, 180),
                    ShowSelectionGuide: false, OutputSettings: new AppSettings()), Frame(false, false), [],
                    initialPointer: new(bounds.X + 420, bounds.Y + 300), recapture: Frame);
                session.Start();
                using var outcome = await session.Completion;
                if (outcome.Image is null) { status.Text = "キャンセルしました。再開できます。"; Log("cancelled"); }
                else
                {
                    outcome.Image.Save(Path.Combine(directory, "result.png"));
                    status.Text = $"完了: {outcome.Image.Width}×{outcome.Image.Height}px / 再取得{acquired}回 / カーソル={outcome.OutputSettings!.IncludeCursor} / 画面固定={outcome.OutputSettings.FreezeFrame}";
                    Log("completed", new { width = outcome.Image.Width, height = outcome.Image.Height, acquired,
                        outcome.OutputSettings.IncludeCursor, outcome.OutputSettings.FreezeFrame, outcome.OutputSettings.ScreenshotColorDepth });
                }
            }
            catch (Exception ex) { status.Text = ex.ToString(); Log("failed", ex.ToString()); }
            finally { session?.Cancel(); session = null; button.Enabled = true; host.Activate(); }
        };
        host.FormClosed += (_, _) => session?.Cancel();
        Application.Run(host); return 0;
        void Log(string action, object? data = null) => File.AppendAllText(Path.Combine(directory, "operations.jsonl"),
            JsonSerializer.Serialize(new { at = DateTimeOffset.Now, action, data }) + Environment.NewLine);
    }
}
