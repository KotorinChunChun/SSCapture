using System.Text.Json;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int PinnedImagePreview(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var settings = new AppSettings { SaveDirectory = Path.Combine(directory, "saved"), FileNamePattern = "pin_{##}.{ext}",
            ScreenshotShortcut1 = "", ScreenshotShortcut2 = "", RecordingShortcut1 = "", RecordingShortcut2 = "",
            LastRegionShortcut = "", InstantShortcuts = Enumerable.Repeat("", 10).ToArray(), NotificationDurationSeconds = 0 }.ApplyDefaults();
        void Log(string action, object? value = null) => File.AppendAllText(Path.Combine(directory, "operations.jsonl"),
            JsonSerializer.Serialize(new { at = DateTimeOffset.Now, action, value }) + Environment.NewLine);
        using var host = new Form { Name = "PinnedPreviewHost", Text = WindowTitle.Create("ピン留め検証"),
            StartPosition = FormStartPosition.Manual, BackColor = Color.AliceBlue, Font = new Font("Yu Gothic UI", 12) };
        var area = Screen.PrimaryScreen!.WorkingArea;
        Rectangle right = new(area.X + area.Width / 2, area.Y, area.Width - area.Width / 2, area.Height);
        host.Bounds = right;
        int copies = 0;
        using var pins = new PinnedImageManager((message, error) => Log("report", new { message, error }), image =>
        {
            string path = Path.Combine(directory, $"copy-{++copies:D2}.png"); image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Log("copy", new { image.Width, image.Height, path }); return Task.CompletedTask;
        });
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52 };
        host.Controls.Add(controls);
        Button Button(string text, string name, Action action)
        { var button = new Button { Text = text, Name = name, AutoSize = true, Height = 42 }; button.Click += (_, _) => action(); controls.Controls.Add(button); return button; }
        Button("ピン留めを2枚表示", "PreviewAddPins", () =>
        {
            using var image = new Bitmap(600, 360);
            using (var g = Graphics.FromImage(image))
            {
                g.Clear(Color.White);
                using var brush = new SolidBrush(Color.FromArgb(0, 104, 116)); g.FillRectangle(brush, 0, 0, 600, 60);
                using var font = new Font("Yu Gothic UI", 22); g.DrawString("SSCapture", font, Brushes.White, 20, 10);
                g.FillRectangle(Brushes.LightSkyBlue, 30, 100, 210, 170); g.FillEllipse(Brushes.Gold, 300, 110, 150, 150);
                g.DrawString("600 × 360 px", font, Brushes.DimGray, 20, 305);
            }
            for (int index = 0; index < 2; index++)
                pins.Show(image, new(right.X + 40 + index * 160, right.Y + 100 + index * 410, image.Width, image.Height), settings);
            int windowNumber = 0;
            foreach (var window in pins.Windows)
            {
                window.Text = WindowTitle.Create("ピン留め検証" + (++windowNumber));
                window.Document.Changed += (_, _) => Log("document", new { window.Document.AnnotationCount, window.Document.Orientation, window.Document.Size, window.Zoom });
                window.LocationChanged += (_, _) => Log("move", window.Location);
            }
            Log("pins", new { pins.Count, pins.Hidden, windows = pins.Windows.Select(w => new { w.Bounds, w.TopMost, w.FormBorderStyle }).ToArray() });
        });
        Button("非表示／再表示", "PreviewTogglePins", () => { pins.ToggleVisibility(); Log("visibility", new { pins.Count, pins.Hidden }); });
        Button("設定", "PreviewSettings", () =>
        {
            var form = new SettingsForm(settings, next => { settings = next; Log("settings-saved"); return null; }, () => { }, () => []);
            form.StartPosition = FormStartPosition.Manual; form.Bounds = right;
            form.FormClosed += (_, _) => form.Dispose(); form.Show(); form.SelectCategory("撮影コマンド");
        });
        host.FormClosed += (_, _) => pins.CloseAll();
        Application.Run(host); return 0;
    }
}
