using System.Reflection;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void HistoryUiTests()
    {
        Run("履歴の期限は撮影時刻から計算し、期限後は取得できない", () =>
        {
            var clock = new HistoryClock();
            using var history = new CaptureHistory(clock: clock);
            using var image = new Bitmap(4, 4);
            history.Configure(true, 1, false);
            history.Add(image, null);
            var id = history.Entries.Single().Id;
            using var clone = history.CloneImage(id)!;
            clock.Now += TimeSpan.FromDays(1) - TimeSpan.FromTicks(1);
            Equal(id, history.Find(id)!.Id);
            clock.Now += TimeSpan.FromTicks(1);
            Equal(true, history.Prune());
            Equal(0, history.Entries.Count);
            Check(history.CloneImage(id) is null, "期限切れの履歴を取得できる");
            Equal(image.Size, clone.Size);
        });

        Run("履歴は明示設定で再起動後に復元し、期限・移動・容量・消去を保持する", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "SSCapture-History-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string original = Path.Combine(root, "保存した画像.png");
                using var image = new Bitmap(4, 4);
                image.SetPixel(0, 0, Color.Blue);
                image.Save(original, System.Drawing.Imaging.ImageFormat.Png);
                var storage = new CaptureHistoryStorage(Path.Combine(root, "history"));
                var clock = new HistoryClock();
                Guid id;
                using (var before = new CaptureHistory(capacity: 3, storage: storage, clock: clock))
                {
                    before.Add(image, original);
                    Check(!Directory.Exists(Path.Combine(root, "history")), "メモリーのみの履歴をディスクへ保存した");
                    before.Configure(true, 2, true);
                    var bounds = new Rectangle(-20, 30, 4, 4);
                    before.Add(image, original, clock.Now.AddDays(-1), bounds);
                    id = before.Entries.Last().Id;
                    before.UpdateSavedPath(original, original + ".moved");
                    before.Add(image, null, clock.Now.AddDays(-1.5));
                }
                using (var after = new CaptureHistory(capacity: 2, storage: storage, clock: clock))
                {
                    after.Configure(true, 2, true, restore: true);
                    Equal(2, after.Entries.Count);
                    Equal(new Rectangle(-20, 30, 4, 4), after.Find(id)!.CaptureBounds!.Value);
                    Equal(original + ".moved", after.Find(id)!.SavedPath);
                    using var copy = after.CloneImage(id);
                    Equal(Color.Blue.ToArgb(), copy!.GetPixel(0, 0).ToArgb());
                    Equal((true, (DateTimeOffset?)clock.Now.AddDays(1)), after.RetentionFor(after.Find(id)!));
                    using (var form = new HistoryForm(after, _ => Task.FromResult("コピー完了"), () => [], (_, _) => Task.FromResult("編集完了")))
                    {
                        var details = form.Controls.Find("HistoryDetails", true).Single();
                        Check(details.Text.Contains("履歴をディスクに保存済み") && details.Text.Contains("保持期限")
                            && details.Text.Contains(original + ".moved"), "保存状態、期限、撮影ファイルが表示されていない");
                    }
                    Equal(2, storage.ReadEntries().Count);
                    clock.Now += TimeSpan.FromDays(1);
                    Equal(true, after.Prune());
                    Equal(1, storage.ReadEntries().Count);
                    after.Configure(true, 2, false);
                    Equal(0, storage.ReadEntries().Count);
                    Equal(1, after.Entries.Count);
                    Equal(false, after.RetentionFor(after.Entries[0]).Stored);
                    after.Configure(true, 2, true);
                    after.Clear();
                    Equal(0, storage.ReadEntries().Count);
                    Check(File.Exists(original), "履歴の消去で利用者の保存済み画像を削除した");
                }
            }
            finally { Directory.Delete(root, true); }
        });

        Run("履歴から選んだアプリと撮影画像だけを編集処理に渡す", () =>
        {
            using var history = new CaptureHistory();
            using var image = new Bitmap(4, 4);
            history.Add(image, null);
            history.Add(image, null);
            var first = new ExternalApplication { Id = "first", Name = "編集 A" };
            var second = new ExternalApplication { Id = "second", Name = "編集 B" };
            ExternalApplication[] apps = [first, second];
            var sent = new List<(Guid, string)>();
            using var form = new HistoryForm(history, _ => Task.FromResult("コピー完了"), () => apps, (id, app) =>
            {
                sent.Add((id, app)); return Task.FromResult("編集完了");
            });
            var menu = form.Controls.Find("EditHistoryImage", true).Single().ContextMenuStrip!;
            Equal(false, OpenMenu());
            Equal(2, menu.Items.Count);
            Equal(0, sent.Count);
            menu.Items["second"]!.PerformClick();
            Equal((history.Entries[0].Id, "second"), sent.Single());
            var list = (ListBox)form.Controls.Find("HistoryEntries", true).Single();
            list.ClearSelected();
            list.SelectedIndex = 1;
            menu.Items["first"]!.PerformClick();
            Equal(1, sent.Count);
            apps = [];
            Equal(true, OpenMenu());
            Equal(0, menu.Items.Count);
            Equal(1, sent.Count);

            bool OpenMenu()
            {
                var args = new System.ComponentModel.CancelEventArgs(true);
                typeof(ContextMenuStrip).GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(menu, [args]);
                return args.Cancel;
            }
        });

        Run("履歴のOCRは選択した画像だけを処理し、終了で取り消す", () =>
        {
            using var history = new CaptureHistory();
            using var image = new Bitmap(4, 4);
            history.Add(image, null);
            history.Add(image, null);
            var pending = new TaskCompletionSource<string>();
            Guid? id = null;
            bool asTable = false;
            CancellationToken token = default;
            using var form = new HistoryForm(history, _ => Task.FromResult("コピー完了"), () => [], (_, _) => Task.FromResult("編集完了"),
                copyOcr: (entry, table, cancellation) =>
                {
                    id = entry; asTable = table; token = cancellation;
                    return pending.Task;
                });
            var list = (ListBox)form.Controls.Find("HistoryEntries", true).Single();
            list.ClearSelected(); list.SetSelected(1, true);
            var menu = form.Controls.Find("HistoryPreview", true).Single().ContextMenuStrip!;
            menu.Items["HistoryCopyTable"]!.PerformClick();
            Equal<Guid?>(history.Entries[1].Id, id);
            Check(asTable && !token.IsCancellationRequested, "表の処理に有効なトークンが渡らなかった");
            form.Dispose();
            Check(token.IsCancellationRequested, "履歴を閉じてもOCRが取り消されなかった");
            pending.SetCanceled(token);
        });

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
            foreach (string name in new[] { "CopyHistoryImage", "CopyHistoryOcr", "EditHistoryImage", "RemoveHistorySelection", "ClearHistory", "CloseHistory", "HistoryDetails" })
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
            foreach (string name in new[] { "CopyHistoryImage", "EditHistoryImage", "RemoveHistorySelection", "ClearHistory" })
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

        Run("複数選択を更新後も維持し選択した履歴だけを削除する", () => WithServiceDirectory(directory =>
        {
            using var history = new CaptureHistory();
            using var image = new Bitmap(4, 4);
            string saved = Path.Combine(directory, "saved.png");
            File.WriteAllText(saved, "keep");
            history.Add(image, saved);
            history.Add(image, null);
            history.Add(image, null);
            var entries = history.Entries;
            using var form = Form(history);
            var list = (ListBox)form.Controls.Find("HistoryEntries", true).Single();
            Equal(SelectionMode.MultiExtended, list.SelectionMode);
            list.SetSelected(2, true);
            form.RefreshEntries();
            Equal(2, list.SelectedItems.Count);
            Check(!form.Controls.Find("CopyHistoryImage", true).Single().Enabled && !form.Controls.Find("EditHistoryImage", true).Single().Enabled,
                "複数選択で単一画像の操作が有効");
            int calls = 0;
            WaitHistory(form.SendAsync(_ => { calls++; return Task.FromResult("処理完了"); }));
            Equal(0, calls);
            typeof(HistoryForm).GetMethod("RemoveSelected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            Equal(entries[1].Id, history.Entries.Single().Id);
            Check(history.CloneImage(entries[0].Id) is null && history.CloneImage(entries[2].Id) is null, "画像が解放されない");
            Equal("keep", File.ReadAllText(saved));
            Equal(1, list.Items.Count);
            Guid? selected = null;
            WaitHistory(form.SendAsync(id => { selected = id; return Task.FromResult("処理完了"); }));
            Equal<Guid?>(entries[1].Id, selected);
        }));

        Run("履歴プレビューのクリックではドラッグせずボタン解放で準備を取り消す", () =>
        {
            using var history = new CaptureHistory();
            using var image = new Bitmap(4, 4);
            history.Add(image, null);
            int drags = 0;
            CancellationToken gesture = default;
            var completion = new TaskCompletionSource<string>();
            using var form = new HistoryForm(history, _ => Task.FromResult("コピー完了"), () => [], (_, _) => Task.FromResult("編集完了"),
                drag: (id, source, token) =>
                {
                    Equal(history.Entries.Single().Id, id);
                    Equal("HistoryPreview", source.Name);
                    drags++;
                    gesture = token;
                    return completion.Task;
                });
            var preview = form.Controls.Find("HistoryPreview", true).Single();
            Mouse("OnMouseDown", 10); Mouse("OnMouseUp", 10); Equal(0, drags);
            Mouse("OnMouseDown", 10); Mouse("OnMouseMove", 10); Equal(0, drags);
            Mouse("OnMouseMove", 10 + SystemInformation.DragSize.Width);
            Mouse("OnMouseMove", 20 + SystemInformation.DragSize.Width);
            Equal(1, drags);
            Check(!gesture.IsCancellationRequested, "ドラッグ開始時点で取り消された");
            Check(!form.Controls.Find("RemoveHistorySelection", true).Single().Enabled, "ドラッグ中に履歴を削除できる");
            Mouse("OnMouseUp", 10);
            Check(gesture.IsCancellationRequested, "マウスを離しても準備が取り消されない");
            completion.SetResult("ドラッグを取り消しました。");

            void Mouse(string method, int x) => typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(preview, [new MouseEventArgs(MouseButtons.Left, 1, x, 10, 0)]);
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

        static HistoryForm Form(CaptureHistory history) => new(history, _ => Task.FromResult("コピー完了"), () => [],
            (_, _) => Task.FromResult("編集完了"), copyOcr: (_, _, _) => Task.FromResult("OCRコピー完了"));
    }

    private sealed class HistoryClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
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
