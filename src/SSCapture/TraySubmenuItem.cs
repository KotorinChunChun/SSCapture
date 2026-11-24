namespace SSCapture;

internal sealed class TraySubmenuItem(string text) : ToolStripMenuItem(text)
{
    protected override void OnDropDownShow(EventArgs e)
    {
        base.OnDropDownShow(e);
        if (GetCurrentParent() is { IsHandleCreated: true } parent)
            DropDown.MaximumSize = Screen.FromControl(parent).WorkingArea.Size;
    }

    protected override Point DropDownLocation
    {
        get
        {
            if (GetCurrentParent() is not { IsHandleCreated: true } parent || !HasDropDownItems)
                return base.DropDownLocation;

            Rectangle workArea = Screen.FromControl(parent).WorkingArea;
            Rectangle anchor = parent.RectangleToScreen(Bounds);
            Size size = DropDown.AutoSize ? DropDown.GetPreferredSize(Size.Empty) : DropDown.Size;
            return Place(anchor, size, workArea);
        }
    }

    internal static Point Place(Rectangle anchor, Size size, Rectangle workArea)
    {
        int width = Math.Min(size.Width, workArea.Width);
        int height = Math.Min(size.Height, workArea.Height);
        // Keep one shared edge with the parent so the pointer can enter the submenu.
        int right = anchor.Right - 1;
        int left = anchor.Left + 1;
        int rightSpace = workArea.Right - right;
        int leftSpace = left - workArea.Left;
        bool openRight = rightSpace >= width || (leftSpace < width && rightSpace >= leftSpace);
        int x = openRight ? right : left - width;
        return new(Math.Clamp(x, workArea.Left, workArea.Right - width),
            Math.Clamp(anchor.Top, workArea.Top, workArea.Bottom - height));
    }
}
