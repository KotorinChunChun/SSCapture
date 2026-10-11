using System.Reflection;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void HistoryUiTests()
    {
        Run("履歴画面の一覧・画像・ボタンが最小サイズで収まる", () =>
        {
            using var history = new CaptureHistory();
            using var image = new Bitmap(640, 480);
            history.Add(image, null, DateTimeOffset.Now);
            using var form = Form(history);
            form.Size = form.MinimumSize;
            _ = form.Handle;
            foreach (Control control in Descendants(form)) _ = control.Handle;
            LayoutTree(form);
            Check(form.ControlBox && !form.MinimizeBox && !form.MaximizeBox, "閉じる以外のボタンがある");
            var list = form.Controls.Find("HistoryEntries", true).Single();
            var preview = form.Controls.Find("HistoryPreview", true).Single();
            Check(list.Width >= 300 && list.Height >= 400, $"一覧の寸法不足 {list.Size}");
            Check(preview.Width >= 500 && preview.Height >= 330, $"画像領域の寸法不足 {preview.Size}");
            foreach (string name in new[] { "CopyHistoryImage", "EditHistoryImage", "ClearHistory", "CloseHistory", "HistoryDetails" })
            {
                var control = form.Controls.Find(name, true).Single();
                Check(control.Parent!.ClientRectangle.Contains(control.Bounds), name + " が親領域外");
                Check(control.Height >= 32 && control.Width >= 90, name + " が小さすぎる");
            }
            preview.Dock = DockStyle.None; preview.Size = new Size(100, 80); LayoutTree(form);
            Check(preview.Width < 500 && preview.Height < 330, "Dock切断を検出できない");
        });

        Run("空の履歴は操作不可で画像プレビューは元画像と独立", () =>
        {
            using var history = new CaptureHistory();
            using var form = Form(history);
            foreach (string name in new[] { "CopyHistoryImage", "EditHistoryImage", "ClearHistory" })
                Check(!form.Controls.Find(name, true).Single().Enabled, name + " が空履歴で有効");
            using var image = new Bitmap(4, 4);
            image.SetPixel(0, 0, Color.Red);
            history.Add(image, null);
            form.RefreshEntries();
            var preview = (PictureBox)form.Controls.Find("HistoryPreview", true).Single();
            Equal(Color.Red.ToArgb(), ((Bitmap)preview.Image!).GetPixel(0, 0).ToArgb());
            image.SetPixel(0, 0, Color.Blue);
            ((Bitmap)preview.Image).SetPixel(0, 0, Color.Green);
            using var stored = history.CloneImage(history.Entries[0].Id);
            Equal(Color.Red.ToArgb(), stored!.GetPixel(0, 0).ToArgb());
            Check(form.Controls.Find("HistoryDetails", true).Single().Text.Contains("メモリー内のみ"), "非保存が表示されない");
            form.Dispose();
            Equal(1, history.Entries.Count);
        });

        Run("履歴消去はメモリーだけを解放し保存済みファイルを残す", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "SSCapture-HistoryUi-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            string saved = Path.Combine(root, "saved-image.png");
            try
            {
                File.WriteAllText(saved, "keep-existing-file");
                using var image = new Bitmap(4, 4);
                using var history = new CaptureHistory();
                history.Add(image, saved);
                using var form = Form(history);
                Check(form.Controls.Find("HistoryDetails", true).Single().Text.Contains(saved), "保存パスが表示されない");
                typeof(HistoryForm).GetMethod("ClearHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
                Equal(0, history.Entries.Count);
                Equal("keep-existing-file", File.ReadAllText(saved));
                Equal(0, ((ListBox)form.Controls.Find("HistoryEntries", true).Single()).Items.Count);
                Check(((PictureBox)form.Controls.Find("HistoryPreview", true).Single()).Image is null, "プレビューが残っている");
            }
            finally { File.Delete(saved); Directory.Delete(root); }
        });

        Run("履歴からの送信中は二重実行と消去を拒否し完了結果を表示", () =>
        {
            using var history = new CaptureHistory();
            using var image = new Bitmap(4, 4);
            history.Add(image, null);
            using var form = Form(history);
            int calls = 0;
            var completion = new TaskCompletionSource<string>();
            Task<string> Send(Guid id) { Equal(history.Entries[0].Id, id); calls++; return completion.Task; }
            Task first = SendHistory(form, Send);
            Check(!form.Controls.Find("CopyHistoryImage", true).Single().Enabled, "処理中もコピー可能");
            Check(!form.Controls.Find("EditHistoryImage", true).Single().Enabled, "処理中も外部送信可能");
            SendHistory(form, Send).GetAwaiter().GetResult();
            typeof(HistoryForm).GetMethod("ClearHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            Equal(1, calls); Equal(1, history.Entries.Count);
            form.RefreshEntries();
            completion.SetResult("再コピーしました。");
            WaitHistory(first);
            Equal("再コピーしました。", form.Controls.Find("HistoryStatus", true).Single().Text);
            Check(form.Controls.Find("CopyHistoryImage", true).Single().Enabled, "完了後も操作不可");
        });

        Run("解放された履歴と送信エラーは自作ステータスで扱う", () =>
        {
            using var history = new CaptureHistory(capacity: 1);
            using var image = new Bitmap(4, 4);
            history.Add(image, null);
            using var form = Form(history);
            history.Add(image, null);
            int calls = 0;
            SendHistory(form, _ => { calls++; return Task.FromResult("送信"); }).GetAwaiter().GetResult();
            Equal(0, calls);
            Check(form.Controls.Find("HistoryStatus", true).Single().Text.Contains("解放"), "失効が表示されない");
            SendHistory(form, _ => Task.FromException<string>(new IOException("送信できません"))).GetAwaiter().GetResult();
            Equal("送信できません", form.Controls.Find("HistoryStatus", true).Single().Text);
            Check(form.Controls.Find("CopyHistoryImage", true).Single().Enabled, "エラー後も操作不可");
        });

        static HistoryForm Form(CaptureHistory history) => new(history, _ => Task.FromResult("コピー完了"), _ => Task.FromResult("編集完了"));
    }

    private static Task SendHistory(HistoryForm form, Func<Guid, Task<string>> action) => (Task)typeof(HistoryForm)
        .GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [action])!;

    private static void WaitHistory(Task task)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.ElapsedMilliseconds < 3000) Application.DoEvents();
        Check(task.IsCompleted, "履歴操作の完了がタイムアウト");
        task.GetAwaiter().GetResult();
    }
}
