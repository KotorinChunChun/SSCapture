using System.ComponentModel;
using System.Diagnostics;

namespace SSCapture.UI;

internal sealed class RecordingSetupForm : BrandedForm
{
    private const string GuideUrl =
        "https://github.com/KotorinChunChun/SSCapture/blob/main/docs/usage.md#録画の準備";

    internal RecordingSetupForm(string directory, IReadOnlyList<string> missing) : base("録画の準備")
    {
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(600, 180);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var missingLabel = new Label { AutoSize = true, Text = $"不足ファイル　{string.Join("、", missing)}" };
        var destinationLabel = new Label { AutoSize = true, Text = "配置先（なければ作成）" };
        var path = new TextBox { Name = "RecordingToolsDirectory", ReadOnly = true, Dock = DockStyle.Top, Text = directory };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = SettingsForm.MakeButton("閉じる", "CloseRecordingSetup");
        close.DialogResult = DialogResult.Cancel;
        var guide = SettingsForm.MakeButton("導入手順を開く", "OpenRecordingGuide");
        guide.AutoSize = true;
        guide.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(GuideUrl) { UseShellExecute = true })?.Dispose(); }
            catch (Win32Exception) { missingLabel.Text = "ブラウザーを開けません。SSCaptureの利用ガイドを参照してください。"; }
        };
        buttons.Controls.AddRange([close, guide]);
        layout.Controls.Add(missingLabel, 0, 0);
        layout.Controls.Add(destinationLabel, 0, 1);
        layout.Controls.Add(path, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        CancelButton = close;
    }
}
