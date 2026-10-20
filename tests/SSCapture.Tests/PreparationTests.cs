using System.Drawing.Imaging;
using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void PreparationTests()
    {
        Run("撮影準備は遷移抑止・非表示・描画同期の順で取得を許可", () =>
        {
            using var form = new Form(); form.Show();
            var steps = new List<string>();
            form.VisibleChanged += (_, _) => { if (!form.Visible) steps.Add("hide"); };
            CaptureUiPreparation.Prepare([form], () => steps.Add("extra"),
                disableTransitions: f => { Check(f.Visible, "抑止前に非表示になった"); steps.Add("disable"); },
                synchronize: () => { Check(!form.Visible, "表示中に同期した"); steps.Add("sync"); });
            steps.Add("capture"); Equal("disable,hide,extra,sync,capture", string.Join(',', steps));
        });
        Run("撮影準備は同期失敗を隠さず取得を止める", () =>
        {
            using var form = new Form(); form.Show(); bool captured = false;
            try
            {
                CaptureUiPreparation.Prepare([form], () => { }, disableTransitions: _ => { }, synchronize: () => throw new IOException("同期fixture"));
                captured = true;
            }
            catch (IOException) { }
            Check(!captured && !form.Visible, "同期失敗後も撮影した");
        });
        Run("表示画面なしの撮影準備は不要な描画待ちを省く", () =>
        {
            int synchronized = 0;
            using var form = new Form();
            CaptureUiPreparation.Prepare([form, null], () => { }, synchronize: () => synchronized++);
            Equal(0, synchronized);
            CaptureUiPreparation.Prepare([], () => { }, syncRequired: true, synchronize: () => synchronized++);
            Equal(1, synchronized);
        });
    }

    // 明示実行専用。自作画面の範囲だけを取得し、隠した設定画面の残像を検査する。
    private static int PreparationSmoke(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        var target = new Rectangle(work.Right - Math.Min(work.Width, 980), work.Top, Math.Min(work.Width, 980), Math.Min(work.Height, 1000));
        using var pattern = new Form { Text = WindowTitle.Create("撮影準備検証"), FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, Bounds = target, BackColor = Color.FromArgb(37, 79, 131), TopMost = true };
        Exception? failure = null;
        pattern.Shown += (_, _) => pattern.BeginInvoke((Action)(() =>
        {
            try
            {
                pattern.Update();
                using var settings = new SettingsForm(new AppSettings(), _ => null, () => { });
                settings.StartPosition = FormStartPosition.Manual; settings.Location = new Point(target.Left + 12, target.Top + 12);
                settings.TopMost = true; settings.Show(); settings.Update(); CaptureNative.SynchronizeDesktop();
                CaptureUiPreparation.Prepare([settings], () => { });
                using var image = InstantCapture.Take(target);
                image.Save(Path.Combine(directory, "after-hide.png"), ImageFormat.Png);
                int different = 0;
                for (int y = 2; y < image.Height - 2; y++)
                    for (int x = 2; x < image.Width - 2; x++)
                        if (image.GetPixel(x, y).ToArgb() != pattern.BackColor.ToArgb()) different++;
                File.WriteAllText(Path.Combine(directory, "result.txt"), $"取得範囲={target}; 残像画素={different}");
                if (different != 0) throw new IOException($"設定画面の残像等が{different}画素あります。");
                Console.WriteLine($"PREPARATION PASS: {target}; residualPixels=0");
            }
            catch (Exception ex) { failure = ex; }
            finally { pattern.Close(); }
        }));
        Application.Run(pattern);
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }
}
