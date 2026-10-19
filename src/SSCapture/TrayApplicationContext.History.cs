using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private void ShowHistory()
    {
        if (exiting || busy)
            return;
        if (historyForm is null || historyForm.IsDisposed)
        {
            historyForm = new HistoryForm(history, CopyHistoryAsync, EditHistoryAsync);
            historyForm.FormClosed += (_, _) => historyForm = null;
        }

        historyForm.RefreshEntries();
        WindowVisibility.Show(historyForm);
    }

    private async Task<string> CopyHistoryAsync(Guid id)
    {
        var entry = history.Entries.FirstOrDefault(e => e.Id == id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        using var image = history.CloneImage(id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        try
        {
            await ClipboardWriter.CopyAsync(image);
        }
        catch
        {
            NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.Clipboard, CaptureBounds: entry.CaptureBounds));
            throw;
        }

        NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None, CaptureBounds: entry.CaptureBounds));
        return "コピーしました。";
    }

    private async Task<string> EditHistoryAsync(Guid id)
    {
        var entry = history.Entries.FirstOrDefault(e => e.Id == id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        if (entry.SavedPath is string saved && File.Exists(saved))
        {
            try
            {
                using var process = Process.Start(OutputRouter.EditorStartInfo(settings.ImageEditorPath, saved, settings.ImageEditorArguments)) ?? throw new IOException("画像編集アプリを開始できません。");
            }
            catch
            {
                NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.ExternalApp, CaptureBounds: entry.CaptureBounds));
                throw;
            }

            NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.ExternalApp, OutputDestination.None, CaptureBounds: entry.CaptureBounds));
            return "保存済みファイルを画像編集アプリへ送りました。";
        }

        using var image = history.CloneImage(id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        // 履歴画像には撮影時の余白が含まれているため、再送では追加しない。
        return await new OutputRouter(workRoot, delivered: DispatchDelivery).ScreenshotAsync(image, OutputDestination.ExternalApp, settings with { KeepHistory = false, OpenFolderAfterSave = false, ScreenshotPaddingPixels = 0 }, captureBounds: entry.CaptureBounds);
    }
}
