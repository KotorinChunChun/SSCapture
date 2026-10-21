using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    // 個人設定・クリップボード・既存アプリへ送らない、明示起動の最終操作確認。
    private static int UpgradeUiPreview(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        var apps = new[] { "A", "B" }.Select(name => new ExternalApplication { Id = name, Name = "検証アプリ " + name,
            Executable = dotnet, Arguments = $"\"{typeof(Program).Assembly.Location}\" --editor-received {name} \"{{file}}\" \"{directory}\"" }).ToArray();
        AppSettings settings = new() { SaveDirectory = directory, DefaultDestination = OutputDestination.File | OutputDestination.ExternalApp,
            MetadataMode = CaptureMetadataMode.Both, ScreenshotPaddingPixels = 12, ScreenshotColorDepth = ScreenshotColorDepth.Indexed8,
            ImageEditors = apps, SelectedImageEditorId = "A", PlayCaptureSound = false, ShowSelectionGuide = false };
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        using var host = new Form { Text = WindowTitle.Create("v0.1.4最終確認"), StartPosition = FormStartPosition.Manual,
            Bounds = new(work.X + work.Width / 2, work.Y, work.Width - work.Width / 2, work.Height), BackColor = Color.FromArgb(229, 241, 248),
            Font = new Font("Yu Gothic UI", 12), AutoScaleMode = AutoScaleMode.Dpi };
        var status = new Label { Name = "UpgradeStatus", Dock = DockStyle.Top, Height = 100, Padding = new Padding(16), Text = "設定・撮影・通知を操作して確認します。画像はこの専用画面の内部だけを囲んでください。" };
        host.Controls.Add(status);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 116 };
        host.Controls.Add(buttons); buttons.BringToFront();
        SettingsForm? settingsForm = null; bool operating = false; int captures = 0;
        using var toast = new CaptureToastForm(location: () => (host.RectangleToScreen(host.ClientRectangle), host.DeviceDpi));
        using var feedback = new CaptureFeedback(createToast: () => toast, openSavedFile: path => { status.Text = "PASS 通知クリック: " + Path.GetFileName(path); Log("notification-click"); },
            launch: info => { using var child = Process.Start(info) ?? throw new IOException("検証アプリ起動失敗"); status.Text = "PASS 未保存通知: 生成後に検証アプリを起動"; Log("lazy-launched", new { arguments = info.ArgumentList.ToArray() }); });
        using var router = new OutputRouter(Path.Combine(directory, "work"));
        var notify = SettingsForm.MakeButton("通知の近接保持", "ShowUpgradeToast");
        notify.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; notify.Width = 230;
        host.Controls.Add(notify);
        host.Resize += (_, _) => notify.Location = new(host.ClientSize.Width - notify.Width - 24, host.ClientSize.Height - notify.Height - 24);
        using var check = new System.Windows.Forms.Timer { Interval = 100 };
        long shownAt = 0; bool holdingChecked = false;
        check.Tick += (_, _) =>
        {
            if (shownAt == 0 || holdingChecked || Environment.TickCount64 - shownAt < 3500) return;
            holdingChecked = true; Point cursor = CaptureNative.CursorPosition();
            bool near = Rectangle.Inflate(toast.Bounds, 32, 32).Contains(cursor);
            status.Text = $"通知3.5秒後: visible={toast.Visible}, pointerNear={near}";
            Log("notification-held", new { visible = toast.Visible, pointerNear = near });
        };
        notify.Click += (_, _) =>
        {
            shownAt = Environment.TickCount64; holdingChecked = false;
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.File, OutputDestination.None, SavedPath: Path.Combine(directory, "fixture.png")), settings with { NotificationDurationSeconds = 1 });
            status.Text = "ポインタを動かさず3.5秒待ち、通知が残ることを確認します。クリックで専用コールバックを確認。"; check.Start();
        };
        Button("設定", "UpgradeSettings", () =>
        {
            if (settingsForm is null || settingsForm.IsDisposed)
            {
                settingsForm = new SettingsForm(settings, next => { next.Validate(); settings = next; new SettingsStore(Path.Combine(directory, "preview-settings.json")).Save(next); Log("settings-saved"); return null; }, () => { }, () => []);
                settingsForm.StartPosition = FormStartPosition.Manual; settingsForm.Bounds = host.Bounds;
            }
            settingsForm.Show(); settingsForm.Activate();
        });
        Button("固定ONで撮影", "UpgradeFreezeOn", () => Capture(true));
        Button("固定OFFで撮影", "UpgradeFreezeOff", () => Capture(false));
        Button("撮影前の通知消去", "UpgradeHideToast", () => { feedback.HideForCapture(); status.Text = "通知visible=" + toast.Visible; Log("notification-force-hidden", new { visible = toast.Visible }); });
        Button("未保存画像の通知", "UpgradeLazyNotification", () =>
        {
            using var image = new Bitmap(120, 80); using (var g = Graphics.FromImage(image)) g.Clear(Color.AliceBlue);
            string folder = Path.Combine(directory, "lazy-notification");
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None,
                NotificationImage: new NotificationImage(image, folder)), settings with
                { NotificationClickAction = NotificationClickAction.ExternalApplication, NotificationApplicationId = "B", NotificationDurationSeconds = 30 });
            int files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.png").Length : 0;
            status.Text = "未保存画像の通知。生成前のファイル数=" + files + "。通知クリックで生成して検証アプリBへ。";
            Log("lazy-shown", new { files });
        });
        Button("余白・入力メニュー", "UpgradeCaptureMenu", () =>
        {
            var menu = new CaptureContextMenu(OutputDestination.Clipboard, _ => { }, () => { }, () => { }, defaultPadding: 12,
                changePadding: value => { status.Text = "今回の余白=" + value + "px"; Log("padding-selected", value); },
                inputPresets: settings.SavedRegions, applyInputPreset: preset => { status.Text = "入力枠=" + preset.Name; Log("input-selected", preset.Name); });
            menu.Closed += (_, _) => host.BeginInvoke((Action)(() => menu.Dispose()));
            menu.Show(host, new Point(200, 170));
        });
        host.Shown += (_, _) => { notify.Location = new(host.ClientSize.Width - notify.Width - 24, host.ClientSize.Height - notify.Height - 24); File.WriteAllText(Path.Combine(directory, "layout.json"), JsonSerializer.Serialize(new { host.Bounds, work })); };
        host.FormClosed += (_, _) => { settingsForm?.Dispose(); CaptureContextClient.Shared.Dispose(); };
        Application.Run(host); return 0;

        void Button(string text, string name, Action action)
        {
            var button = SettingsForm.MakeButton(text, name); button.Width = 190; button.Click += (_, _) => action(); buttons.Controls.Add(button);
        }
        void Log(string action, object? data = null) => File.AppendAllText(Path.Combine(directory, "operations.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.Now, action, data }) + Environment.NewLine);
        async void Capture(bool freeze)
        {
            if (operating) return; operating = true;
            try
            {
                CaptureUiPreparation.Prepare([settingsForm], feedback.HideForCapture);
                using var outcome = await CaptureSession.SelectAsync(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, settings.DefaultDestination,
                    FreezeFrame: freeze, ShowSelectionGuide: false, OutputSettings: settings, DimCaptureBackground: settings.DimCaptureBackground));
                if (outcome.Image is null || outcome.Selection is null) { status.Text = "キャンセル"; return; }
                if (!host.RectangleToScreen(host.ClientRectangle).Contains(outcome.Selection.Bounds)) throw new IOException($"専用画面の外側を選択したため保存しません。選択={outcome.Selection.Bounds}; 許容={host.RectangleToScreen(host.ClientRectangle)}");
                string name = (freeze ? "freeze-on-" : "freeze-off-") + ++captures + ".png";
                status.Text = await router.ScreenshotAsync(outcome.Image, outcome.Selection.Destination, outcome.OutputSettings ?? settings,
                    name, metadata: outcome.Metadata, captureBounds: outcome.Selection.Bounds);
                Log("captured", new { freeze, file = name, bounds = outcome.Selection.Bounds, context = outcome.Metadata?.CollectionStatus });
            }
            catch (Exception ex) { status.Text = "FAIL: " + ex.Message; Log("failure", ex.Message); }
            finally { operating = false; }
        }
    }
}
