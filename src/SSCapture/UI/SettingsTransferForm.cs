using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class SettingsTransferForm : Form
{
    private readonly TextBox path = new() { Name = "TransferPath", Dock = DockStyle.Fill, AccessibleName = "ファイルのパス" };
    private readonly TextBox preview = new()
    {
        Name = "TransferPreview", AccessibleName = "確認結果", Dock = DockStyle.Fill,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical
    };
    private readonly Button apply = SettingsForm.MakeButton("適用", "ApplySettingsImport", ButtonRole.Primary);

    internal AppSettings? Candidate { get; private set; }

    internal SettingsTransferForm(Func<string, AppSettings> read, Func<AppSettings, string> describe)
        : this("設定をインポート", "読込元のJSON", "事前確認", 440)
    {
        apply.AutoSize = true;
        apply.Enabled = false;
        path.TextChanged += (_, _) => ClearPreview("");
        command.Click += (_, _) =>
        {
            ClearPreview("");
            try
            {
                AppSettings candidate = read(FullPath());
                preview.Text = "適用すると現在の設定と設定画面での未保存の入力を置き換えます。"
                    + Environment.NewLine + Environment.NewLine + describe(candidate);
                Candidate = candidate;
                apply.Enabled = true;
            }
            catch (Exception ex) { ClearPreview("設定を読み込めません: " + ex.Message); }
        };
        apply.Click += (_, _) => { if (Candidate is not null) DialogResult = DialogResult.OK; };
        actions.Controls.Add(apply);
    }

    internal SettingsTransferForm(Action<string> export)
        : this("設定をエクスポート", "新規JSONの保存先", "書き出す", 200)
    {
        command.Click += (_, _) =>
        {
            try
            {
                export(FullPath());
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { preview.Text = "設定を書き出せません: " + ex.Message; }
        };
    }

    private readonly Button command;
    private readonly FlowLayoutPanel actions = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };

    private SettingsTransferForm(string title, string pathLabel, string commandText, int height)
    {
        Text = WindowTitle.Create(title);
        Font = new Font("Yu Gothic UI", 12F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(780, height);
        MinimumSize = SizeFromClientSize(new Size(560, 180));
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var label = new Label { Text = pathLabel, AutoSize = true, Anchor = AnchorStyles.Left };
        var cancel = SettingsForm.MakeButton("取消", "CancelSettingsTransfer", ButtonRole.Neutral);
        cancel.DialogResult = DialogResult.Cancel;
        command = SettingsForm.MakeButton(commandText, "PreviewOrExportSettings");
        command.AutoSize = true;
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(path, 1, 0);
        layout.Controls.Add(preview, 0, 1);
        layout.SetColumnSpan(preview, 2);
        layout.Controls.Add(actions, 0, 2);
        layout.SetColumnSpan(actions, 2);
        actions.Controls.AddRange([cancel, command]);
        Controls.Add(layout);
        AcceptButton = command;
        CancelButton = cancel;
        ActiveControl = path;
    }

    private string FullPath()
    {
        string value = path.Text.Trim();
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("JSONファイルの絶対パスを入力してください。");
        return Path.GetFullPath(value);
    }

    private void ClearPreview(string message)
    {
        Candidate = null;
        apply.Enabled = false;
        preview.Text = message;
    }
}
