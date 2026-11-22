using System.Diagnostics;
using SSCapture.Capture;

namespace SSCapture.UI;

internal static class CaptureUiPreparation
{
    // Embedded WinForms pages can appear in Application.OpenForms. Their host owns
    // their visibility, and DWM cannot set window transitions on their HWNDs.
    internal static Form[] VisibleTopLevelWindows(IEnumerable<Form?> windows) =>
        windows.OfType<Form>().Where(IsVisibleTopLevelWindow).Distinct().ToArray();

    private static bool IsVisibleTopLevelWindow(Form window) =>
        window is { TopLevel: true, IsDisposed: false, Disposing: false, Visible: true };

    internal static void Restore(Form? previouslyVisible)
    {
        if (previouslyVisible is { TopLevel: true, IsDisposed: false, Disposing: false, Visible: false }) previouslyVisible.Show();
    }

    internal static void RestoreAll(IEnumerable<Form> previouslyVisible)
    {
        foreach (Form window in previouslyVisible) Restore(window);
    }

    internal static void Prepare(IEnumerable<Form?> windows, Action hideExtras, bool syncRequired = false,
        Action<Form>? disableTransitions = null, Action? synchronize = null)
    {
        var visible = VisibleTopLevelWindows(windows);
        foreach (Form window in visible)
        {
            if (!IsVisibleTopLevelWindow(window)) continue;
            try { (disableTransitions ?? CaptureNative.DisableTransitions)(window); }
            catch (IOException ex)
            {
                // Hiding the window and draining the compositor are still required.
                int result = ex.InnerException?.HResult ?? ex.HResult;
                Trace.TraceWarning($"撮影前の画面アニメーション抑止に失敗しました。{window.GetType().Name} HRESULT=0x{result:X8}");
            }
        }

        try
        {
            foreach (Form window in visible)
                if (IsVisibleTopLevelWindow(window)) window.Hide();
            hideExtras();
            if (visible.Any(IsVisibleTopLevelWindow)) throw new IOException("撮影前に自分の画面を非表示にできません。");
            if (syncRequired || visible.Length > 0) (synchronize ?? CaptureNative.SynchronizeDesktop)();
        }
        catch (IOException ex)
        {
            throw new IOException(ex.Message + " 設定画面を閉じてください。その後、もう一度撮影してください。", ex);
        }
    }
}
