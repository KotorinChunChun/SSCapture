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
        void Log(string action, object? value = null) => File.AppendAllText(Path.Combine(directory, underlay ? "underlay.jsonl" : "actions.jsonl"),
            JsonSerializer.Serialize(new { at = DateTimeOffset.Now, pid = Environment.ProcessId, action, foreground = (long)PreviewForegroundWindow(), value }) + Environment.NewLine);
        if (underlay)
        {
            int clicks = 0;
            void Clicked(object? sender, MouseEventArgs e) { clicks++; status.Text = $"下敷きクリック: {clicks} / {e.Button}"; Log("background-click", new { clicks, e.Button, e.Location }); }
            host.MouseDown += Clicked; status.MouseDown += Clicked;
            host.Activated += (_, _) => Log("activated");
            host.Shown += (_, _) => File.WriteAllText(Path.Combine(directory, "underlay-ready.json"), JsonSerializer.Serialize(new { handle = (long)host.Handle, host.Bounds }));
            Application.Run(host); return 0;
        }
        Process? child = null;
        using var toast = new CaptureToastForm(location: () => (host.Bounds, host.DeviceDpi));
        using var feedback = new CaptureFeedback(createToast: () => toast, openSavedFile: _ => Log("folder-button"), launch: info =>
        { using var image = new Bitmap(info.ArgumentList[0]); Log("edit-button", new { image.Width, image.Height, fileExists = File.Exists(info.ArgumentList[0]) }); });
        var button = new Button { Text = "別プロセス上に通知を表示", Dock = DockStyle.Top, Height = 52 };
        var savedMode = new CheckBox { Text = "保存済み画像で確認（OFFは編集時だけ一時画像生成）", Dock = DockStyle.Top, Height = 36 };
        host.Controls.Add(savedMode); savedMode.BringToFront();
        host.Controls.Add(button); button.BringToFront();
        button.Click += async (_, _) =>
        {
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
                using var image = new Bitmap(120, 80); using (var g = Graphics.FromImage(image)) g.Clear(Color.LightCoral);
                var app = new ExternalApplication { Id = "fixture", Name = "専用の代替編集処理", Executable = Environment.ProcessPath! };
                string? saved = savedMode.Checked ? CaptureStorage.SavePng(image, directory, DateTimeOffset.Now, "fixture.png") : null;
                feedback.Notify(new(CaptureIntent.Screenshot, saved is null ? OutputDestination.Clipboard : OutputDestination.Clipboard | OutputDestination.File, OutputDestination.None,
                    SavedPath: saved, NotificationImage: saved is null ? new(image, Path.Combine(directory, "images")) : null), new AppSettings {
                    ImageEditors = [app], NotificationApplicationId = app.Id, NotificationCorner = NotificationCorner.Center,
                    NotificationDurationSeconds = 60, PlayCaptureSound = false, LastRecipeShortcut = "Ctrl+F11" });
                Log("shown", new { toast.Bounds, underlayPid = child.Id });
            }
            catch (Exception ex) { host.Show(); status.Text = ex.Message; Log("failed", ex.Message); }
        };
        using var poll = new System.Windows.Forms.Timer { Interval = 250 };
        bool hadToast = false;
        poll.Tick += (_, _) => { if (toast.Visible) hadToast = true; else if (hadToast) { hadToast = false; host.Show(); } };
        poll.Start();
        host.FormClosed += (_, _) => { feedback.HideForCapture(); if (child is { HasExited: false }) child.CloseMainWindow(); child?.Dispose(); };
        Application.Run(host); return 0;
    }
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint PreviewForegroundWindow();
}
