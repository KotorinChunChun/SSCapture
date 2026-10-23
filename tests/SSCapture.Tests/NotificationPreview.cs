using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    // T08専用。別プロセスの下敷きへの入力と、通知操作時の前面HWNDを記録する。
    private static int NotificationPreview(string directory, bool underlay)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        using var host = new Form { Text = WindowTitle.Create(underlay ? "通知透過の受け側" : "通知最終確認"),
            StartPosition = FormStartPosition.Manual, Bounds = new(work.X + work.Width / 2, work.Y, work.Width - work.Width / 2, work.Height),
            BackColor = Color.AliceBlue, Font = new Font("Yu Gothic UI", 12) };
        var status = new Label { Dock = DockStyle.Top, Height = 140, Padding = new(16), Text = underlay
            ? "別プロセスの受け側です。通知の本文の上をクリックすると、ここへカウントされます。通知ボタンのクリックは通過しないはずです。"
            : "通知は中央に表示します。本文のクリック通過、ボタンの1回受信と非アクティブ維持を確認します。個人画像・クリップボードは使用しません。" };
        host.Controls.Add(status);
        int attempt = 0; long expectedForeground = 0;
        void Log(string action, object? value = null) => AppendPreviewObservation(Path.Combine(directory, underlay ? "underlay.jsonl" : "actions.jsonl"),
            JsonSerializer.Serialize(new { at = DateTimeOffset.Now, pid = Environment.ProcessId, attempt, action,
                foreground = (long)PreviewForegroundWindow(), expectedForeground, value }));
        if (underlay)
        {
            int clicks = 0;
            void Clicked(object? sender, MouseEventArgs e) { clicks++; status.Text = $"下敷きクリック: {clicks} / {e.Button}"; Log("background-click", new { clicks, e.Button, e.Location }); }
            host.MouseDown += Clicked; status.MouseDown += Clicked;
            host.Activated += (_, _) => Log("activated");
            host.Deactivate += (_, _) => Log("deactivated");
            host.Shown += (_, _) => File.WriteAllText(Path.Combine(directory, "underlay-ready.json"), JsonSerializer.Serialize(new { handle = (long)host.Handle, host.Bounds }));
            var observation = new Label { Dock = DockStyle.Bottom, Height = 100, Padding = new(16), Text = "通知のボタン操作を待っています。" };
            host.Controls.Add(observation);
            var next = new Button { Dock = DockStyle.Bottom, Height = 48, Text = "通知が消えた後、次の試験の設定画面に戻る" };
            next.Click += (_, _) => File.WriteAllText(Path.Combine(directory, "return-to-controls.request"), Guid.NewGuid().ToString("N"));
            host.Controls.Add(next);
            using var observe = new System.Windows.Forms.Timer { Interval = 150 };
            observe.Tick += (_, _) =>
            {
                try
                {
                    using var stream = new FileStream(Path.Combine(directory, "actions.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    string? last = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                    if (last is null) return;
                    using var item = JsonDocument.Parse(last);
                    var root = item.RootElement;
                    observation.Text = $"通知側の最新記録: {root.GetProperty("action").GetString()} / 前面HWND={root.GetProperty("foreground").GetInt64()} / 受け側HWND={(long)host.Handle}";
                }
                catch (IOException) { }
                catch (JsonException) { }
            };
            observe.Start();
            Application.Run(host); return 0;
        }
        Process? child = null;
        var observedActions = new HashSet<ToastActionForm>();
        using var toast = new CaptureToastForm(location: () => (host.Bounds, host.DeviceDpi));
        using var feedback = new CaptureFeedback(createToast: () => toast, openSavedFile: _ => Log("folder-button"), launch: info =>
        { using var image = new Bitmap(info.ArgumentList[0]); Log("edit-button", new { image.Width, image.Height,
            fileExists = File.Exists(info.ArgumentList[0]), path = info.ArgumentList[0] }); });
        toast.Activated += (_, _) => Log("toast-activated");
        var button = new Button { Text = "別プロセス上に通知を表示", Dock = DockStyle.Top, Height = 52 };
        var savedMode = new CheckBox { Text = "保存済み画像で確認（OFFは編集時だけ一時画像生成）", Dock = DockStyle.Top, Height = 36 };
        var recreate = new CheckBox { Text = "次回表示前に通知のハンドルを作り直す", Dock = DockStyle.Top, Height = 36 };
        host.Controls.Add(recreate); recreate.BringToFront();
        host.Controls.Add(savedMode); savedMode.BringToFront();
        host.Controls.Add(button); button.BringToFront();
        button.Click += async (_, _) =>
        {
            attempt++;
            try
            {
                if (child is null || child.HasExited)
                {
                    string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
                    var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true };
                    foreach (string arg in new[] { typeof(Program).Assembly.Location, "--notification-underlay", directory }) start.ArgumentList.Add(arg);
                    child = Process.Start(start) ?? throw new IOException("下敷きを起動できません。");
                }
                for (int i = 0; i < 100 && !File.Exists(Path.Combine(directory, "underlay-ready.json")); i++) await Task.Delay(50);
                if (!File.Exists(Path.Combine(directory, "underlay-ready.json"))) throw new TimeoutException("下敷きの表示待ち");
                host.Hide(); await Task.Delay(200);
                using (var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "underlay-ready.json"))))
                    expectedForeground = ready.RootElement.GetProperty("handle").GetInt64();
                Log("before-notification", new { saved = savedMode.Checked });
                if (recreate.Checked)
                {
                    var oldActions = Application.OpenForms.OfType<ToastActionForm>().SingleOrDefault();
                    long oldBody = (long)toast.Handle, oldAction = oldActions is null ? 0L : (long)oldActions.Handle;
                    var rebuild = typeof(Control).GetMethod("RecreateHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    rebuild.Invoke(toast, null);
                    if (oldActions is not null) rebuild.Invoke(oldActions, null);
                    Log("handles-recreated", new { oldBody, body = (long)toast.Handle, oldAction,
                        actionHost = oldActions is null ? 0L : (long)oldActions.Handle });
                }
                using var image = new Bitmap(120, 80); using (var g = Graphics.FromImage(image)) g.Clear(Color.LightCoral);
                var app = new ExternalApplication { Id = "fixture", Name = "専用の代替編集処理", Executable = Environment.ProcessPath! };
                string? saved = savedMode.Checked ? CaptureStorage.SavePng(image, directory, DateTimeOffset.Now, "fixture.png") : null;
                feedback.Notify(new(CaptureIntent.Screenshot, saved is null ? OutputDestination.Clipboard : OutputDestination.Clipboard | OutputDestination.File, OutputDestination.None,
                    SavedPath: saved, NotificationImage: saved is null ? new(image, Path.Combine(directory, "images")) : null), new AppSettings {
                    ImageEditors = [app], NotificationApplicationId = app.Id, NotificationCorner = NotificationCorner.Center,
                    NotificationDurationSeconds = 180, PlayCaptureSound = false, LastRecipeShortcut = "Ctrl+F11" });
                var actions = Application.OpenForms.OfType<ToastActionForm>().SingleOrDefault();
                if (actions is not null && observedActions.Add(actions))
                {
                    actions.Activated += (_, _) => Log("action-host-activated");
                    foreach (Button action in actions.Controls.OfType<Button>())
                    {
                        action.MouseDown += (_, e) => Log("button-mouse-down", new { action.Name, e.Button, e.Location });
                        action.MouseUp += (_, e) => Log("button-mouse-up", new { action.Name, e.Button, e.Location });
                        action.Click += (_, _) => Log("button-click", new { action.Name });
                    }
                }
                Log("shown", new { toast.Bounds, toast.Visible, body = (long)toast.Handle,
                    bodyProtected = FeedbackNative.GetWindowDisplayAffinity(toast.Handle, out uint bodyAffinity) && bodyAffinity == 0x11,
                    underlayPid = child.Id, imageFiles = Directory.Exists(Path.Combine(directory, "images")) ? Directory.GetFiles(Path.Combine(directory, "images")).Length : 0,
                    actionHost = actions is null ? 0L : (long)actions.Handle, actionsVisible = actions?.Visible,
                    actionsProtected = actions is not null && FeedbackNative.GetWindowDisplayAffinity(actions.Handle, out uint actionAffinity) && actionAffinity == 0x11,
                    buttons = actions?.Controls.OfType<Button>().Select(c => new { c.Name, c.Bounds, c.Enabled }).ToArray() });
            }
            catch (Exception ex) { host.Show(); status.Text = ex.Message; Log("failed", ex.Message); }
        };
        using var poll = new System.Windows.Forms.Timer { Interval = 25 };
        bool hadToast = false;
        long previousForeground = 0;
        string? returnRequest = null;
        poll.Tick += (_, _) =>
        {
            if (toast.Visible)
            {
                hadToast = true;
                long current = (long)PreviewForegroundWindow();
                if (current != previousForeground) { previousForeground = current; Log("foreground-change"); }
            }
            else if (hadToast) { hadToast = false; Log("notification-hidden"); }
            // 非同期の画像生成/編集コールバックへ再表示が割り込まない。利用者の明示操作でだけ戻る。
            if (!toast.Visible && File.Exists(Path.Combine(directory, "return-to-controls.request")))
            {
                string request = File.ReadAllText(Path.Combine(directory, "return-to-controls.request"));
                if (request != returnRequest) { returnRequest = request; Log("return-request"); host.Show(); }
            }
        };
        poll.Start();
        host.FormClosed += (_, _) => { feedback.HideForCapture(); if (child is { HasExited: false }) child.CloseMainWindow(); child?.Dispose(); };
        Application.Run(host); return 0;
    }
    private static void AppendPreviewObservation(string path, string line)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"観測ログの書込み失敗: {ex.Message}"); }
    }
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint PreviewForegroundWindow();
}
