namespace SSCapture.UI;

internal class BrandedForm : Form
{
    protected BrandedForm(string name)
    {
        Text = WindowTitle.Create(name);
        Icon = AppIcon.Create();
        Font = new Font("Yu Gothic UI", 12F);
    }
}
