using System.Runtime.InteropServices;

namespace SSCapture.UI;

internal static class WindowVisibility
{
    // A resident process may have STARTF_USESHOWWINDOW/SW_HIDE on its first window.
    // Explicitly restore user-requested forms after WinForms has created the handle.
    internal static void Show(Form form)
    {
        if (!form.Visible && !form.Modal) form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        Rectangle work = Screen.FromRectangle(form.Bounds).WorkingArea;
        form.Location = new Point(Math.Clamp(form.Left, work.Left, Math.Max(work.Left, work.Right - form.Width)),
            Math.Clamp(form.Top, work.Top, Math.Max(work.Top, work.Bottom - form.Height)));
        ShowWindow(form.Handle, 5);
        form.BringToFront();
        form.Activate();
    }

    internal static bool RestorePending(Form? form, Action<Form>? show = null)
    {
        if (form is null || form.IsDisposed) return false;
        (show ?? Show)(form);
        return true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
