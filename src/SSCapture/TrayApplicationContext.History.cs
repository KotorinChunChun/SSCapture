using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private void ConfigureHistory(bool restore = false)
    {
        history.Configure(settings.KeepHistory, settings.HistoryRetentionDays, settings.PersistHistory, restore);
        historyExpiration.Enabled = settings.KeepHistory && settings.HistoryRetentionDays > 0;
    }

    private void ShowHistory()
    {
        if (exiting || busy)
            return;
        if (historyForm is null || historyForm.IsDisposed)
        {
            historyForm = new HistoryForm(history, CopyHistoryAsync, () => settings.ImageEditors, EditHistoryAsync,
                drag: DragHistoryAsync, copyOcr: CopyHistoryOcrAsync);
            historyForm.FormClosed += (_, _) => historyForm = null;
        }

        historyForm.RefreshEntries();
        WindowVisibility.Show(historyForm);
    }

    private async Task<string> DragHistoryAsync(Guid id, Control source, CancellationToken gesture)
    {
        bool dropped = await NotificationFileDrag.RunHistoryAsync(source, history, id, workRoot,
            () => !exiting && source.Visible && !gesture.IsCancellationRequested && (Control.MouseButtons & MouseButtons.Left) != 0,
            SavedFileMoved);
        return dropped ? "画像を渡しました。" : "ドラッグを取り消しました。";
    }

    private void SavedFileMoved(string previous, string? current)
    {
        history.UpdateSavedPath(previous, current);
        feedback.UpdateSavedPath(previous, current);
        historyForm?.RefreshEntries();
    }

    private async Task<string> CopyHistoryOcrAsync(Guid id, bool asTable, CancellationToken token)
    {
        using var image = history.CloneImage(id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        return await CaptureOcrCopy.CopyAsync(image, settings.OneOcrDirectory, asTable, token);
    }

    private async Task<string> CopyHistoryAsync(Guid id)
    {
        var entry = history.Find(id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        using var image = history.CloneImage(id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        try
        {
            await ClipboardWriter.CopyAsync(image);
        }
        catch
        {
            NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.Clipboard,
                CaptureBounds: entry.CaptureBounds, IsHistoryAction: true));
            throw;
        }

        NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None,
            CaptureBounds: entry.CaptureBounds, IsHistoryAction: true));
        return "コピーしました。";
    }

    private async Task<string> EditHistoryAsync(Guid id, string applicationId)
    {
        var application = settings.ImageEditors.FirstOrDefault(app => app.Id == applicationId)
            ?? throw new InvalidOperationException("選んだ画像編集アプリは登録されていません。選び直してください。");
        var editorSettings = (settings with { SelectedImageEditorId = applicationId }).ResolveEditors();
        var entry = history.Find(id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        if (entry.SavedPath is string saved && File.Exists(saved))
        {
            try
            {
                new ExternalEditor().Resolve(application)(saved);
            }
            catch
            {
                NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.ExternalApp,
                    CaptureBounds: entry.CaptureBounds, IsHistoryAction: true));
                throw;
            }

            NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.ExternalApp, OutputDestination.None,
                CaptureBounds: entry.CaptureBounds, IsHistoryAction: true));
            return "保存済みファイルを画像編集アプリへ送りました。";
        }

        using var image = history.CloneImage(id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        // 履歴画像は撮影時に加工済み。現在の設定で余白・減色を重ねない。
        using var router = new OutputRouter(workRoot, delivered: result => DispatchDelivery(result with { IsHistoryAction = true }));
        return await router.ScreenshotAsync(image, OutputDestination.ExternalApp,
            editorSettings with { KeepHistory = false, OpenFolderAfterSave = false, ScreenshotPaddingPixels = 0, ScreenshotColorDepth = ScreenshotColorDepth.Original },
            captureBounds: entry.CaptureBounds);
    }
}
