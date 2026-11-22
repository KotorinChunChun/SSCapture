using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class StartupSettingsControl : UserControl
{
    readonly IStartupRegistration registration;
    readonly CheckBox enabled = new() { Name = "StartWithWindows", Text = "Windowsサインイン時に自動起動（画面は表示しない）", AutoSize = true, Enabled = false };
    readonly Button apply = new() { Name = "ApplyStartup", Text = "自動起動を適用", AutoSize = true, Enabled = false };
    readonly Label state = new() { Name = "StartupState", Text = "確認中…", AutoSize = true, Margin = new Padding(16, 3, 3, 3), MaximumSize = new Size(850, 0) };
    StartupRegistrationState? current;
    bool working;
    internal event EventHandler? DraftChanged;
    internal bool HasDraft => current?.CanChange == true && enabled.Checked != current.Enabled;

    internal StartupSettingsControl(IStartupRegistration registration, int labelWidth = 177)
    {
        this.registration = registration;
        Name = "StartupSettings"; Dock = DockStyle.Fill; Margin = new Padding(0, 3, 0, 3);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var commands = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
        var title = new Label { Name = "StartupLabel", Text = "自動起動", AutoSize = true, Anchor = AnchorStyles.Left };
        enabled.Anchor = AnchorStyles.Left;
        ButtonTheme.Apply(apply, ButtonRole.StartupApply);
        commands.Controls.AddRange([title, apply, enabled]);
        layout.Controls.Add(commands, 0, 0);
        layout.SetColumnSpan(commands, 2);
        layout.Controls.Add(state, 1, 1);
        Controls.Add(layout);
        SizeChanged += (_, _) => state.MaximumSize = new Size(Math.Max(1, ClientSize.Width - layout.GetColumnWidths().FirstOrDefault() - state.Margin.Horizontal), 0);
        enabled.CheckedChanged += (_, _) => { apply.Enabled = !working && HasDraft; DraftChanged?.Invoke(this, EventArgs.Empty); };
        apply.Click += async (_, _) => await ApplyAsync();
    }
    internal void ResetDraft() { if (current?.CanChange == true) enabled.Checked = false; }
    internal void RevertDraft() { if (current is { } saved) enabled.Checked = saved.Enabled; }

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
        finally { working = false; DraftChanged?.Invoke(this, EventArgs.Empty); }
    }
    async Task ApplyAsync()
    {
        if (working || current?.CanChange != true || enabled.Checked == current.Enabled) return;
        working = true; apply.Enabled = enabled.Enabled = false;
        string? error;
        try { error = await registration.SetAsync(enabled.Checked); }
        catch (Exception ex) { error = ex.Message; }
        finally { working = false; DraftChanged?.Invoke(this, EventArgs.Empty); }
        if (IsDisposed) return;
        await RefreshAsync();
        if (!IsDisposed && error != null) state.Text = "変更できませんでした: " + error;
    }
}
