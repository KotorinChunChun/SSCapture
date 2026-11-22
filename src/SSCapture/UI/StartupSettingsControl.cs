using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class StartupSettingsControl : UserControl
{
    readonly IStartupRegistration registration;
    readonly CheckBox enabled = new() { Name = "StartWithWindows", Text = "Windowsサインイン時に自動起動（画面は表示しない）", AutoSize = true, Enabled = false, Anchor = AnchorStyles.Left };
    readonly Label state = new() { Name = "StartupState", Text = "確認中…", AutoSize = true, Margin = new Padding(19, 3, 3, 3) };
    StartupRegistrationState? current;
    bool working;
    internal event EventHandler? DraftChanged;
    internal bool HasDraft => !working && current?.CanChange == true && enabled.Checked != current.Enabled;

    internal StartupSettingsControl(IStartupRegistration registration)
    {
        this.registration = registration;
        Name = "StartupSettings";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(enabled, 0, 0);
        layout.Controls.Add(state, 0, 1);
        Controls.Add(layout);
        SizeChanged += (_, _) => state.MaximumSize = new Size(Math.Max(1, ClientSize.Width - state.Margin.Horizontal), 0);
        enabled.CheckedChanged += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ResetDraft() { if (!working && current?.CanChange == true) enabled.Checked = false; }
    internal void RevertDraft() { if (!working && current is { } saved) enabled.Checked = saved.Enabled; }

    internal async Task RefreshAsync()
    {
        if (working || IsDisposed) return;
        working = true;
        enabled.Enabled = false;
        try { await ReadStateAsync(); }
        finally { EndWork(); }
    }

    internal async Task<string?> ApplyDraftAsync()
    {
        if (IsDisposed || !HasDraft) return null;
        bool requested = enabled.Checked;
        working = true;
        enabled.Enabled = false;
        try
        {
            string? error;
            try { error = await registration.SetAsync(requested); }
            catch (Exception ex) { error = ex.Message; }
            if (IsDisposed) return error;

            string? readError = await ReadStateAsync();
            if (IsDisposed) return error ?? readError;
            error ??= readError;
            if (error is null && current?.Enabled != requested)
                error = "指定した自動起動の状態を確認できませんでした。";
            if (error is not null && current?.CanChange == true)
                enabled.Checked = requested;
            return error;
        }
        finally { EndWork(); }
    }

    private async Task<string?> ReadStateAsync()
    {
        try
        {
            var next = await registration.ReadAsync();
            if (!IsDisposed)
            {
                current = next;
                enabled.Checked = next.Enabled;
                state.Text = next.Detail;
            }
            return null;
        }
        catch (Exception ex)
        {
            string message = "自動起動を確認できません: " + ex.Message;
            if (!IsDisposed)
            {
                current = null;
                state.Text = message;
            }
            return message;
        }
    }

    private void EndWork()
    {
        working = false;
        if (IsDisposed) return;
        enabled.Enabled = current?.CanChange == true;
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }
}
