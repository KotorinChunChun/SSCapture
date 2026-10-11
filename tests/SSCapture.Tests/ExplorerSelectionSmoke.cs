using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    // Explicit opt-in: opens Explorer only for a newly generated synthetic file in the chosen fixture directory.
    private static int RunExplorerSelectionSmoke(string directory)
    {
        try
        {
            directory = Path.GetFullPath(directory);
            if (Directory.Exists(directory)) throw new IOException("検証先は新しい専用フォルダを指定してください。");
            Directory.CreateDirectory(directory);
            var settings = new AppSettings { SaveDirectory = directory, OpenFolderAfterSave = true, KeepHistory = false,
                FileNamePattern = "選択 確認,SSCapture.png" };
            using var image = new Bitmap(120, 80);
            using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.MediumTurquoise);
            CaptureDelivery? result = null;
            var router = new OutputRouter(Path.Combine(directory, "work"), delivered: delivery => result = delivery);
            // No WinForms context is installed here, so this worker does not block a captured UI synchronization context.
            Task.Run(() => router.ScreenshotAsync(image, OutputDestination.File, settings)).GetAwaiter().GetResult();
            Check(result?.SavedPath is not null && result.HasSuccess && !result.HasFailure, "保存後のExplorer選択を完了できない");
            Console.WriteLine("EXPLORER SELECTION READY " + result!.SavedPath);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL EXPLORER SELECTION " + ex.Message); return 1; }
    }
}
