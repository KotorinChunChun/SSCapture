using System.Reflection;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    static void NotificationActionTests()
    {
        Run("通知9位置は旧enum値を保ち負座標・DPI・拡張高さで作業領域に収まる", () =>
        {
            Equal(0, (int)NotificationCorner.BottomRight); Equal(1, (int)NotificationCorner.BottomLeft);
            Equal(9, Enum.GetValues<NotificationCorner>().Length);
            foreach (int dpi in new[] { 96, 144, 192 })
            {
                var work = new Rectangle(-2560, -1440, 2560, 1400);
                var positions = Enum.GetValues<NotificationCorner>().Select(p => CaptureToastForm.PlaceSize(work, dpi, p, new(700, 280))).ToArray();
                Equal(9, positions.Distinct().Count()); Check(positions.All(work.Contains), "領域からはみ出し");
                var center = positions[(int)NotificationCorner.Center];
                Equal(work.X + (work.Width - center.Width) / 2, center.X); Equal(work.Y + (work.Height - center.Height) / 2, center.Y);
            }
        });
        Run("通知の本文は常時透過し独立ホストは2ボタンの矩形だけ入力を受ける", () =>
        {
            using var form = new CaptureToastForm(_ => new(true, null), _ => { }, () => (new Rectangle(-1920, -100, 1920, 1040), 144));
            form.SetActions(new(() => { }, () => { }, "直前の設定で再撮影: Ctrl+F11"));
            Check(form.TryShow("コピーが完了しました", false, NotificationCorner.Center, out var warning, () => throw new Exception("本文は起動しない")), warning ?? "表示失敗");
            var flags = (CreateParams)typeof(CaptureToastForm).GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            Check((flags.ExStyle & 0x20) != 0, "旧クリック引数で本文が不透過に戻る");
            var host = (ToastActionForm)typeof(CaptureToastForm).GetField("actionHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            var actionFlags = (CreateParams)typeof(ToastActionForm).GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            Check((actionFlags.ExStyle & 0x20) == 0 && (actionFlags.ExStyle & 0x08000000) != 0, "ボタンが透過またはアクティブ化");
            Check(host.Region is not null && !host.Region.IsVisible(1, 1), "ボタン外が入力を遮断");
            foreach (Control button in host.Controls)
            {
                Check(host.ClientRectangle.Contains(button.Bounds), "ボタンがつぶれる/はみ出す");
                Check(host.Region!.IsVisible(button.Left + 2, button.Top + 2), "ボタンが入力領域にない");
            }
            Check(!host.ShowInTaskbar, "タスクバーに表示");
            form.Hide(); Check(!host.Visible, "撮影前にボタンだけ残留");
        });
        Run("通知の操作ホスト除外に失敗したら本文も表示しない", () =>
        {
            var handles = new HashSet<nint>(); int shown = 0;
            using var form = new CaptureToastForm(h => { handles.Add(h); return new(handles.Count == 1, "操作窓の除外失敗"); }, _ => shown++, () => (new Rectangle(0, 0, 1920, 1080), 96));
            form.SetActions(new(() => { }, () => { }, "未設定"));
            Check(!form.TryShow("完了", false, NotificationCorner.BottomRight, out string? warning), "操作窓が保護されないまま表示");
            Equal(0, shown); Check(warning?.Contains("除外失敗") == true && !form.Visible, "fail-closedでない");
        });
        Run("通知ボタンの可否は保存結果と登録アプリで決まり旧クリック指定に依存しない", () => WithServiceDirectory(directory =>
        {
            var app = new ExternalApplication { Id = "image", Name = "編集", Executable = Environment.ProcessPath! };
            var settings = new AppSettings { ImageEditors = [app], SelectedImageEditorId = app.Id, NotificationClickAction = NotificationClickAction.None, LastRecipeShortcut = "Ctrl+F11" };
            var toast = new FakeCaptureToast();
            using var feedback = new CaptureFeedback(createToast: () => toast, sound: new FakeCompletionSound(), timer: new FakeFeedbackTimer());
            using var bitmap = new Bitmap(3, 2);
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None,
                NotificationImage: new(bitmap, directory), CaptureSettings: settings), settings);
            Check(toast.Activate is null && toast.Actions!.OpenFolder is null && toast.Actions.Edit is not null, "Clipboard通知の可否が不正");
            Check(toast.Actions!.RepeatHint.Contains("Ctrl+F11"), "再実行キーがない");
            feedback.Notify(new(CaptureIntent.Recording, OutputDestination.File, OutputDestination.None, SavedPath: "video.mp4", CaptureSettings: settings), settings with { LastRecipeShortcut = "" });
            Check(toast.Actions!.OpenFolder is not null && toast.Actions.Edit is null, "画像アプリを動画の既定として流用");
            Check(toast.Actions.RepeatHint.Contains("未設定") && toast.Actions.RepeatHint.Contains("録画の再開ではありません"), "録画で誤案内");
            Equal<ExternalApplication?>(null, NotificationPresentation.ResolveApplication(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None, CaptureSettings: settings), settings with { ImageEditors = [] }));
        }));
        Run("通知の設定画面は9候補と編集アプリだけを表示し旧クリック選択を除去", () =>
        {
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => []);
            Equal(9, ((ComboBox)form.Controls.Find("NotificationCorner", true).Single()).Items.Count);
            Check(!form.Controls.Find("NotificationClickAction", true).Any(), "旧本文クリック設定が残る");
            Check(form.Controls.Find("NotificationApplication", true).Single().Enabled, "アプリ選択が無効");
        });
        Run("通知は外部アプリ出力の実ファイルと撮影時アプリを使い直せる", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            CaptureDelivery? delivered = null; var app = new ExternalApplication { Id = "edit", Name = "編集", Executable = Environment.ProcessPath! };
            using var router = new OutputRouter(Path.Combine(directory, "work"), launch: _ => { }, delivered: value => delivered = value);
            using var image = OutputImage();
            var settings = OutputSettings(directory) with { ImageEditors = [app], SelectedImageEditorId = app.Id };
            await router.ScreenshotAsync(image, OutputDestination.ExternalApp, settings);
            Check(delivered!.NotificationPath is not null && File.Exists(delivered.NotificationPath), "起動済み実ファイルを保持しない");
            Check(delivered.NotificationImage is null, "不要な画像を複製");
            Equal(app, NotificationPresentation.ResolveApplication(delivered, settings));
        })));
    }
}
