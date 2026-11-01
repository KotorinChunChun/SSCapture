using System.Diagnostics;
using System.Text.Json;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    // 明示起動専用。合成画像・専用フォルダだけを扱い、設定・Clipboardは操作しない。
    // このプレビューだけは録画できるよう撮影除外を注入し、実装と同じ描画を表示する。
    private static int NotificationDragPreview(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string sources = Path.Combine(directory, "source"), target = Path.Combine(directory, "target"), imported = Path.Combine(directory, "imported");
        foreach (string path in new[] { sources, target, imported }) Directory.CreateDirectory(path);
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        using var host = new Form { Text = WindowTitle.Create("通知ドラッグ確認"), StartPosition = FormStartPosition.Manual,
            Bounds = new(work.X + work.Width / 2, work.Y, work.Width - work.Width / 2, work.Height),
            BackColor = Color.FromArgb(233, 240, 245), Font = new Font("Yu Gothic UI", 12) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 58, Padding = new(8), WrapContents = false };
        var status = new Label { Name = "NotificationDragStatus", Dock = DockStyle.Top, Height = 66, Padding = new(16), Text = "SSCapture v0.1.9" };
        var drop = new Panel { Name = "NotificationAppDrop", AllowDrop = true, BackColor = Color.FromArgb(217, 233, 240),
            Bounds = new(40, 180, 420, 200), BorderStyle = BorderStyle.FixedSingle };
        var title = new Label { Text = "アプリ取込", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Enabled = false };
        drop.Controls.Add(title);
        host.Controls.Add(drop); host.Controls.Add(status); host.Controls.Add(buttons);
        using var fixture = new Bitmap(640, 360);
        using (var graphics = Graphics.FromImage(fixture))
        {
            graphics.Clear(Color.FromArgb(243, 247, 250));
            using var accent = new SolidBrush(Color.FromArgb(0, 106, 119));
            using var amber = new SolidBrush(Color.FromArgb(246, 188, 68));
            using var coral = new SolidBrush(Color.FromArgb(232, 112, 99));
            using var text = new Font("Segoe UI", 28, FontStyle.Bold);
            graphics.FillRectangle(accent, 0, 0, 640, 100); graphics.DrawString("SSCapture", text, Brushes.White, 24, 20);
            graphics.FillRectangle(amber, 28, 140, 160, 140); graphics.FillEllipse(coral, 238, 140, 140, 140);
            graphics.FillRectangle(accent, 428, 140, 160, 140);
        }
        using var toast = new CaptureToastForm(protect: _ => new(true, null), location: () => (host.RectangleToScreen(host.ClientRectangle), host.DeviceDpi));
        int sequence = 0;
        var observedPreviews = new HashSet<Control>();
        void Log(string action, object? value = null) => File.AppendAllText(Path.Combine(directory, "operations.jsonl"),
            JsonSerializer.Serialize(new { at = DateTimeOffset.Now, action, foreground = (long)PreviewForegroundWindow(), host = (long)host.Handle, value }) + Environment.NewLine);
        using var feedback = new CaptureFeedback(warning: text => { status.Text = text; Log("warning", text); }, createToast: () => toast,
            sound: new FakeCompletionSound(), openSavedFile: path => { status.Text = "フォルダ操作"; Log("folder-button", path); },
            launch: info => { status.Text = "編集操作"; Log("edit-button", info.ArgumentList.ToArray()); });
        var app = new ExternalApplication { Id = "fixture", Name = "検証", Executable = Environment.ProcessPath! };
        var settings = new AppSettings { NotificationDurationSeconds = 3600, PlayCaptureSound = false,
            NotificationCorner = NotificationCorner.BottomRight, ImageEditors = [app], NotificationApplicationId = app.Id, LastRecipeShortcut = "Ctrl+F11" };
        void Show(bool saved, bool failure = false)
        {
            string? path = saved ? CaptureStorage.SavePng(fixture, sources, DateTimeOffset.Now, $"capture-{++sequence}.png") : null;
            feedback.Notify(new(CaptureIntent.Screenshot, saved ? OutputDestination.Clipboard | OutputDestination.File : OutputDestination.Clipboard,
                failure ? OutputDestination.ExternalApp : OutputDestination.None, SavedPath: path, CaptureDescription: "640×360px",
                NotificationImage: new(fixture, Path.Combine(directory, "work", "notification-images"))), settings);
            status.Text = path is null ? "未保存" : Path.GetFileName(path);
            var actions = Application.OpenForms.OfType<ToastActionForm>().Single();
            Control preview = actions.Controls.Find("NotificationDragImage", true).Single();
            if (observedPreviews.Add(preview))
            {
                preview.MouseDown += (_, e) => Log("preview-down", new { e.Button, e.Location, preview.Capture });
                preview.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) Log("preview-move", new { e.Button, e.Location, preview.Capture }); };
                preview.MouseUp += (_, e) => Log("preview-up", new { e.Button, e.Location, preview.Capture });
                preview.MouseCaptureChanged += (_, _) => Log("preview-capture", new { preview.Capture });
            }
            Log("shown", new { toast.Bounds, saved = path, body = (long)toast.Handle, actions = (long)actions.Handle,
                preview = preview.RectangleToScreen(preview.ClientRectangle),
                files = Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories).Count() });
        }
        Button("未保存通知", "ShowUnsavedNotification", () => Show(false));
        Button("保存済み通知", "ShowSavedNotification", () => Show(true));
        Button("部分失敗通知", "ShowFailureNotification", () => Show(false, true));
        Button("ドロップ先フォルダ", "OpenDropFolder", () =>
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add(target);
            using var process = Process.Start(start);
        });
        Button("閉じる", "CloseNotificationPreview", () => host.Close());
        drop.DragEnter += (_, e) => { e.Effect = Accept(e) ? DragDropEffects.Copy : DragDropEffects.None; Log("drag-enter", new { e.AllowedEffect, e.Effect }); };
        drop.DragOver += (_, e) => e.Effect = Accept(e) ? DragDropEffects.Copy : DragDropEffects.None;
        drop.DragDrop += (_, e) =>
        {
            if (!Accept(e) || e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (string path in files)
            {
                string copy = Path.Combine(imported, $"import-{Guid.NewGuid():N}.png"); File.Copy(path, copy);
                using var image = new Bitmap(copy); status.Text = $"取込完了 {image.Width}×{image.Height}px";
                Log("app-drop", new { path, copy, image.Width, image.Height, originalExists = File.Exists(path) });
            }
            e.Effect = DragDropEffects.Copy;
        };
        using var poll = new System.Windows.Forms.Timer { Interval = 200 };
        bool visible = false;
        poll.Tick += (_, _) =>
        {
            if (visible == toast.Visible) return;
            visible = toast.Visible; Log(visible ? "notification-visible" : "notification-hidden");
        };
        host.Shown += (_, _) => { Show(false); poll.Start(); };
        Application.Run(host); return 0;

        static bool Accept(DragEventArgs e) => (e.AllowedEffect & DragDropEffects.Copy) != 0 && e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        void Button(string text, string name, Action click)
        {
            var button = new Button { Text = text, Name = name, AutoSize = true, Height = 36 };
            button.Click += (_, _) => click(); buttons.Controls.Add(button);
        }
    }
}
