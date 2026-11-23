using SSCapture.Services;

namespace SSCapture.Tests;
internal static partial class Program
{
    private static void RunNotificationTask(Func<Task> run) => WaitHistory(run());
    private static void NotificationDragTests()
    {
        Run("通知ドラッグは実在画像のFileDropと既定Moveを提示する", () => WithServiceDirectory(directory =>
        {
            using var control = new Control();
            string path = Path.Combine(directory, "画像.png"); File.WriteAllText(path, "fixture");
            Check(NotificationFileDrag.Run(control, path, data =>
            {
                Check(data.TryGetData<string[]>(DataFormats.FileDrop, out var files), "FileDropがない");
                Equal(path, files!.Single());
                Equal(DragDropEffects.Move, NotificationFileDrag.ReadEffect(data, NotificationFileDrag.PreferredEffect));
                return DragDropEffects.Copy;
            }), "取込を失敗扱い");
            Check(File.Exists(path), "アプリへの取込で原本を消した");
        }));
        Run("Move戻り値だけのアプリとCtrlコピーは原本を消さない", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "image.png"); File.WriteAllText(path, "fixture");
            var data = new DataObject();
            Check(NotificationFileDrag.Complete(path, data, DragDropEffects.Move), "Move取込を拒否");
            Check(File.Exists(path), "Move戻り値だけで原本削除");
            using var performed = new MemoryStream(BitConverter.GetBytes(2));
            data.SetData(NotificationFileDrag.PerformedEffect, false, performed);
            Check(NotificationFileDrag.Complete(path, data, DragDropEffects.Copy) && File.Exists(path), "Copyで原本削除");
            Check(!NotificationFileDrag.Complete(path, data, DragDropEffects.None) && File.Exists(path), "キャンセルで原本削除");
        }));
        Run("Shellの非最適化移動は二重確認して原本を削除し最適化移動も成功扱い", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "image.png"), target = Path.Combine(directory, "target.png");
            File.WriteAllText(path, "fixture"); File.Copy(path, target);
            var data = new DataObject(); using var performed = new MemoryStream(BitConverter.GetBytes(2));
            data.SetData(NotificationFileDrag.PerformedEffect, false, performed);
            Check(NotificationFileDrag.Complete(path, data, DragDropEffects.Move), "移動失敗扱い");
            Check(!File.Exists(path) && File.Exists(target), "非最適化移動の完了処理が不正");
            Check(NotificationFileDrag.Complete(path, new DataObject(), DragDropEffects.None), "最適化移動のNoneを失敗扱い");
        }));
        Run("通知の画像は表示だけでファイル化せず縦横比を保つ", () => WithServiceDirectory(directory =>
        {
            using var image = new Bitmap(400, 200); using var pending = new NotificationImage(image, directory);
            using var thumbnail = pending.CreateThumbnail(); Equal(new Size(88, 44), thumbnail.Size);
            Check(!Directory.EnumerateFiles(directory).Any(), "プレビューで画像を書いた");
            Check(!NotificationFileDrag.DragThresholdReached(new(10, 10), new(11, 11), new(8, 8)), "クリックでドラッグ開始");
            Check(NotificationFileDrag.DragThresholdReached(new(10, 10), new(16, 10), new(8, 8)), "ドラッグが始まらない");
        }));
        Run("保存済み通知の編集はプレビューを持っても原本を一時整理へ渡さない", () => WithServiceDirectory(directory =>
        {
            using var image = new Bitmap(16, 8); string saved = CaptureStorage.SavePng(image, directory, DateTimeOffset.Now, "saved.png");
            var toast = new FakeCaptureToast(); var retained = new List<string>(); int launches = 0;
            var app = new ExternalApplication { Id = "edit", Name = "検証", Executable = Environment.ProcessPath! };
            using var feedback = new CaptureFeedback(createToast: () => toast, sound: new FakeCompletionSound(), timer: new FakeFeedbackTimer(),
                retainDragFile: retained.Add, launch: info => { Equal(saved, info.ArgumentList[0]); launches++; });
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.File, OutputDestination.None, SavedPath: saved, NotificationImage: new(image, directory)),
                new AppSettings { ImageEditors = [app], NotificationApplicationId = app.Id });
            toast.Actions!.Edit!(); Equal(1, launches); Equal(0, retained.Count); Check(File.Exists(saved), "原本を削除した");
        }));
        Run("画像の非同期生成中に通知を解放しても完成画像は有効", () => WithServiceDirectory(directory => RunNotificationTask(async () =>
        {
            using var image = new Bitmap(200, 100); var pending = new NotificationImage(image, directory);
            Task<string> prepare = pending.PrepareAsync(); pending.Dispose();
            using var completed = new Bitmap(await prepare); Equal(new Size(200, 100), completed.Size);
        })));
        Run("未保存通知はドラッグ時だけ生成し期限を止め取込成功後に消える", () => WithServiceDirectory(directory => RunNotificationTask(async () =>
        {
            using var image = new Bitmap(16, 8); using var source = new Control();
            var toast = new FakeCaptureToast { DisplayBounds = new(100, 100, 392, 180) };
            var timer = new FakeFeedbackTimer(); long now = 0; string? received = null;
            var retained = new List<string>();
            using var feedback = new CaptureFeedback(createToast: () => toast, sound: new FakeCompletionSound(), timer: timer,
                now: () => now, pointer: () => Point.Empty, dragButtonDown: () => true, retainDragFile: retained.Add,
                dragFile: (_, path) =>
                {
                    Check(!timer.Running, "ドラッグ中も期限が進む");
                    now = 100_000; timer.Fire(); Check(toast.Visible, "ドラッグ中に消える");
                    using var file = new Bitmap(path); Equal(image.Size, file.Size); received = path; return true;
                });
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None, NotificationImage: new(image, directory)),
                new AppSettings { HoldNotificationWhileApproaching = false, NotificationDurationSeconds = 1 });
            Check(toast.Actions?.Thumbnail is not null && toast.Actions.Drag is not null && !Directory.EnumerateFiles(directory).Any(), "準備時点の副作用");
            await toast.Actions!.Drag!(source, CancellationToken.None);
            Equal(received, retained.Single()); Check(!toast.Visible && !timer.Running && File.Exists(received), "取込後の状態が不正");
        })));
        Run("保存済み画像は原本を渡しキャンセル後は満額で通知を再開する", () => WithServiceDirectory(directory => RunNotificationTask(async () =>
        {
            using var image = new Bitmap(16, 8); string saved = CaptureStorage.SavePng(image, directory, DateTimeOffset.Now, "saved.png");
            using var source = new Control(); var toast = new FakeCaptureToast { DisplayBounds = new(100, 100, 392, 180) }; var timer = new FakeFeedbackTimer();
            long now = 0; int drags = 0, retains = 0;
            using var feedback = new CaptureFeedback(createToast: () => toast, sound: new FakeCompletionSound(), timer: timer, now: () => now,
                pointer: () => Point.Empty, dragButtonDown: () => true, retainDragFile: _ => retains++,
                dragFile: (_, path) => { Equal(saved, path); drags++; now = 10_000; return false; });
            var settings = new AppSettings { HoldNotificationWhileApproaching = false, NotificationDurationSeconds = 1 };
            var result = new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.File, OutputDestination.None, SavedPath: saved);
            feedback.Notify(result, settings); var previous = toast.Actions!.Drag!;
            await previous(source, CancellationToken.None); Equal(1, drags); Equal(0, retains); Check(toast.Visible && timer.Running, "キャンセル後に消える");
            now = 10_999; timer.Fire(); Check(toast.Visible, "再開時に満額でない");
            now = 11_000; timer.Fire(); Check(!toast.Visible, "再開後に消えない");
            feedback.Notify(result, settings); await previous(source, CancellationToken.None); Equal(1, drags);
            Check(toast.Visible && File.Exists(saved) && Directory.GetFiles(directory).Length == 1, "古い操作やプレビューの副作用");
        })));
        Run("ドラッグ中の新通知を古い成功で消さず解放後も操作しない", () => WithServiceDirectory(directory => RunNotificationTask(async () =>
        {
            using var image = new Bitmap(16, 8); string path = CaptureStorage.SavePng(image, directory, DateTimeOffset.Now, "saved.png");
            using var source = new Control(); var toast = new FakeCaptureToast(); var timer = new FakeFeedbackTimer();
            var settings = new AppSettings(); var result = new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.File, OutputDestination.None, SavedPath: path);
            CaptureFeedback? feedback = null;
            using (feedback = new CaptureFeedback(createToast: () => toast, sound: new FakeCompletionSound(), timer: timer, dragButtonDown: () => true,
                dragFile: (_, _) => { feedback!.Notify(result, settings); return true; }))
            {
                feedback.Notify(result, settings); var drag = toast.Actions!.Drag!;
                await drag(source, CancellationToken.None); Check(toast.Visible && timer.Running && toast.Shows == 2, "古い成功が新通知を消した");
                feedback.HideForCapture(); await drag(source, CancellationToken.None); Equal(2, toast.Shows); Check(!toast.Visible, "消去後に再表示");
            }
        })));
        Run("準備中のボタン解放と失敗は通知を残し一時画像だけ保持対象にする", () => WithServiceDirectory(directory => RunNotificationTask(async () =>
        {
            using var image = new Bitmap(16, 8); using var source = new Control(); var toast = new FakeCaptureToast(); int drags = 0; bool down = false;
            var retained = new List<string>(); string? warning = null;
            using var feedback = new CaptureFeedback(w => warning = w, () => toast, new FakeCompletionSound(), new FakeFeedbackTimer(),
                dragButtonDown: () => down, retainDragFile: retained.Add, dragFile: (_, _) => { drags++; throw new IOException("取込失敗"); });
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None, NotificationImage: new(image, directory)), new AppSettings());
            await toast.Actions!.Drag!(source, CancellationToken.None); Equal(0, drags); Equal(1, retained.Count); Check(toast.Visible, "早い解放で通知を消した");
            down = true; await toast.Actions!.Drag!(source, CancellationToken.None); Equal(1, drags); Equal(2, retained.Count);
            Check(toast.Visible && warning?.Contains("取込失敗") == true, "失敗を通知できない");
            using var gesture = new CancellationTokenSource(); gesture.Cancel();
            await toast.Actions!.Drag!(source, gesture.Token); Equal(1, drags);
            Check(toast.Visible, "終了済みジェスチャーで通知を消した");
        })));
        Run("履歴の保存画像は移動先で再ドラッグできコピーと取消では参照を維持する", () => WithServiceDirectory(directory => RunNotificationTask(async () =>
        {
            using var history = new CaptureHistory();
            using var image = new Bitmap(8, 4);
            using var source = new Control();
            string saved = CaptureStorage.SavePng(image, directory, DateTimeOffset.Now, "saved.png");
            string destinationFolder = Directory.CreateDirectory(Path.Combine(directory, "moved")).FullName;
            string moved = Path.Combine(destinationFolder, Path.GetFileName(saved)), copied = Path.Combine(directory, "copied.png");
            history.Add(image, saved);
            Guid id = history.Entries.Single().Id;

            Check(await Drag(saved, path => { File.Move(path, moved); return DragDropEffects.None; }), "最適化移動を失敗扱い");
            Check(string.Equals(moved, history.Find(id)!.SavedPath, StringComparison.OrdinalIgnoreCase), "履歴が移動先を参照していない");
            Check(await Drag(moved, path => { File.Copy(path, copied); return DragDropEffects.Copy; }), "コピーを失敗扱い");
            Check(!await Drag(moved, _ => DragDropEffects.None), "取消を成功扱い");
            Check(string.Equals(moved, history.Find(id)!.SavedPath, StringComparison.OrdinalIgnoreCase), "コピーや取消で履歴の参照が変わった");
            Check(File.Exists(moved) && File.Exists(copied), "コピーや取消でファイルを削除した");

            Task<bool> Drag(string expected, Func<string, DragDropEffects> operation) =>
                NotificationFileDrag.RunHistoryAsync(source, history, id, directory, () => true, history.UpdateSavedPath, data =>
                {
                    Equal(DragDropEffects.Move, NotificationFileDrag.ReadEffect(data, NotificationFileDrag.PreferredEffect));
                    string path = DraggedPath(data);
                    Check(string.Equals(expected, path, StringComparison.OrdinalIgnoreCase), "異なるファイルのパスを渡した");
                    return operation(path);
                });
        })));
        Run("移動先が通知されない履歴はメモリー画像を再ドラッグし一時PNGは参照に残さない", () => WithServiceDirectory(directory => RunNotificationTask(async () =>
        {
            using var history = new CaptureHistory();
            using var image = new Bitmap(8, 4);
            image.SetPixel(1, 1, Color.Orange);
            using var source = new Control();
            string saved = CaptureStorage.SavePng(image, directory, DateTimeOffset.Now, "saved.png");
            string copy = Path.Combine(directory, "untracked.png");
            history.Add(image, saved);
            Guid id = history.Entries.Single().Id;
            using var performed = new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Move));
            Check(await Drag(() => true, data =>
            {
                File.Copy(DraggedPath(data), copy);
                data.SetData(NotificationFileDrag.PerformedEffect, false, performed);
                return DragDropEffects.Move;
            }), "非最適化移動を失敗扱い");
            Check(!File.Exists(saved) && File.Exists(copy), "移動の完了処理が不正");
            Equal<string?>(null, history.Find(id)!.SavedPath);
            Check(!Directory.Exists(TemporaryHandoff.DirectoryFor(directory)), "事前に一時PNGを作った");
            int readinessChecks = 0;
            Check(!await Drag(() => readinessChecks++ == 0, _ => throw new Exception("作成中の取消後にドラッグした")), "取消を成功扱い");
            var paths = new List<string>();
            foreach (DragDropEffects result in new[] { DragDropEffects.None, DragDropEffects.Copy })
            {
                Equal(result != DragDropEffects.None, await Drag(() => true, data =>
                {
                    string path = DraggedPath(data);
                    paths.Add(path);
                    using var file = new Bitmap(path);
                    Equal(image.GetPixel(1, 1), file.GetPixel(1, 1));
                    return result;
                }));
            }
            Equal(2, paths.Distinct().Count());
            Equal<string?>(null, history.Find(id)!.SavedPath);
            Check(File.Exists(copy), "再ドラッグでユーザーの移動先ファイルを消した");

            Task<bool> Drag(Func<bool> canStart, Func<DataObject, DragDropEffects> perform) =>
                NotificationFileDrag.RunHistoryAsync(source, history, id, directory, canStart, history.UpdateSavedPath, perform);
        })));
    }

    private static string DraggedPath(DataObject data)
    {
        Check(data.TryGetData<string[]>(DataFormats.FileDrop, out var files), "FileDropがない");
        return files!.Single();
    }
}
