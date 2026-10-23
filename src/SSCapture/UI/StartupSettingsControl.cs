using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class StartupSettingsControl : UserControl
{
    readonly IStartupRegistration registration;
    readonly CheckBox enabled = new() { Name = "StartWithWindows", Text = "Windowsサインイン時に自動起動（画面は表示しない）", AutoSize = true, Enabled = false };
    readonly Button apply = new() { Name = "ApplyStartup", Text = "適用", AutoSize = true, Enabled = false };
    readonly Label state = new() { Name = "StartupState", Text = "確認中…", AutoSize = true, MaximumSize = new Size(850, 0) };
    StartupRegistrationState? current;
    bool working;
    internal StartupSettingsControl(IStartupRegistration registration)
    {
        this.registration = registration;
        Name = "StartupSettings"; Dock = DockStyle.Fill;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        var commands = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        commands.Controls.AddRange([enabled, apply]); layout.Controls.AddRange([commands, state]); Controls.Add(layout);
        SizeChanged += (_, _) => state.MaximumSize = new Size(Math.Max(1, ClientSize.Width - 12), 0);
        enabled.CheckedChanged += (_, _) => apply.Enabled = !working && current?.CanChange == true && enabled.Checked != current.Enabled;
        apply.Click += async (_, _) => await ApplyAsync();
    }
    internal async Task RefreshAsync()
    {
        if (working || IsDisposed) return;
        working = true; apply.Enabled = enabled.Enabled = false;
        try
        {
            var next = await registration.ReadAsync();
            if (IsDisposed) return;
            current = next; enabled.Checked = next.Enabled; enabled.Enabled = next.CanChange; state.Text = next.Detail;
        }
        catch (Exception ex)
        {
            if (!IsDisposed) { current = null; state.Text = "自動起動を確認できません: " + ex.Message; }
        }
        finally { working = false; }
    }
    async Task ApplyAsync()
    {
        if (working || current?.CanChange != true) return;
        working = true; apply.Enabled = enabled.Enabled = false;
        string? error;
        try { error = await registration.SetAsync(enabled.Checked); }
        catch (Exception ex) { error = ex.Message; }
        finally { working = false; }
        if (IsDisposed) return;
        await RefreshAsync();
        if (!IsDisposed && error != null) state.Text = "変更できませんでした: " + error;
    }
}
