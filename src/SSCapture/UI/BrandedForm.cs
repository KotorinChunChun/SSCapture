namespace SSCapture.UI;

internal class BrandedForm : Form
{
    private readonly Icon ownedIcon;
    private readonly Font ownedFont;
    private bool resourcesDisposed;
    protected BrandedForm(string name)
    {
        Text = WindowTitle.Create(name);
        Icon = ownedIcon = AppIcon.Create();
        Font = ownedFont = new Font("Yu Gothic UI", 12F);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !resourcesDisposed) { resourcesDisposed = true; ownedIcon.Dispose(); ownedFont.Dispose(); }
    }
}
