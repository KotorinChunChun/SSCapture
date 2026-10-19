using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    // 明示起動のみ。合成画像・専用設定だけを使い、実画面取得とクリップボード操作は行わない。
    private static int SelectionGuidePreview(string fixtureDirectory)
    {
        Directory.CreateDirectory(fixtureDirectory);
        var store = new SettingsStore(Path.Combine(fixtureDirectory, "settings.json"));
        Rectangle working = Screen.PrimaryScreen!.WorkingArea;
        using var host = new Form
        {
            Text = WindowTitle.Create("選択ガイド操作確認"),
            StartPosition = FormStartPosition.Manual,
            Bounds = new(working.Left + working.Width / 2, working.Top, working.Width - working.Width / 2, working.Height),
            Font = new Font("Yu Gothic UI", 12F),
            BackColor = Color.White
        };
        var start = new Button { Text = "合成画像で選択を開始", Name = "StartSelection", Dock = DockStyle.Top, Height = 64 };
        var status = new Label { Text = "実画面・クリップボードは使用しません。×、右メニュー、ドラッグを確認できます。",
            Name = "SelectionStatus", Dock = DockStyle.Top, Height = 120, Padding = new(12) };
        host.Controls.Add(status);
        host.Controls.Add(start);
        SessionController? session = null;
        start.Click += async (_, _) =>
        {
            start.Enabled = false;
            try
            {
                AppSettings settings = store.Load(out _);
                Rectangle bounds = host.Bounds;
                Bitmap pixels = WorkflowImage("選択ガイドの操作確認", "合成画像・出力OFF", bounds.Size, Color.Teal);
                session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.None,
                    LimitToStartingMonitor: false, ShowSelectionGuide: settings.ShowSelectionGuide),
                    new DesktopSnapshot(pixels, bounds, [bounds]), [bounds], initialPointer: new(bounds.Left + 80, bounds.Top + 220),
                    guideChanged: enabled =>
                    {
                        settings = settings with { ShowSelectionGuide = enabled };
                        store.Save(settings);
                        Console.WriteLine($"GUIDE SAVED: {enabled}");
                    });
                session.Start();
                using CaptureOutcome result = await session.Completion;
                status.Text = (result.Selection is null ? "選択を中断" : $"選択確定: {result.Selection.Bounds}")
                    + $"\n次回の案内: {(store.Load(out _).ShowSelectionGuide ? "ON" : "OFF")}";
                Console.WriteLine(status.Text);
            }
            catch (Exception ex) { status.Text = ex.Message; Console.Error.WriteLine(ex); }
            finally
            {
                session?.Cancel();
                session = null;
                if (!host.IsDisposed) { start.Enabled = true; host.Activate(); }
            }
        };
        host.FormClosing += (_, _) => session?.Cancel();
        Application.Run(host);
        return 0;
    }
}
