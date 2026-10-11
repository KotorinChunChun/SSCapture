using System.Diagnostics;
using System.Runtime.InteropServices;
using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class RecordingControlForm : Form
{
    private readonly Label state;
    private readonly Button start, stop, pause, cancel;
    private readonly CheckBox systemAudio, microphone;
    private readonly Size recordingSize;
    private readonly Stopwatch elapsed = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private bool active, paused, processing, allowClose, settingAudio, resourcesDisposed, fittingHeight;
    private readonly bool excludeFromCapture;
    public event Action? StartRequested;
    public event Action? StopRequested;
    public event Action? CancelRequested;
    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action<bool>? SystemAudioChangeRequested;
    public event Action<bool>? MicrophoneChangeRequested;

    public RecordingControlForm(Rectangle region, AppSettings settings, bool excludeFromCapture = true)
    {
        this.excludeFromCapture = excludeFromCapture;
        recordingSize = region.Size;
        Name = "RecordingControl";
        Text = WindowTitle.Create("録画");
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 12F);
        BackColor = Color.FromArgb(247, 249, 251);
        ClientSize = new Size(480, 178);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        var area = Screen.FromRectangle(region).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - 16), area.Top + 16);
        TopMost = true;
        var layout = new TableLayoutPanel { Name = "RecordingLayout", Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 3, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        state = new Label { Name = "RecordingState", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        layout.Controls.Add(state, 0, 0);
        var audio = new TableLayoutPanel { Name = "RecordingAudio", Dock = DockStyle.Fill,
            RowCount = 1, ColumnCount = 2, Margin = Padding.Empty };
        audio.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        audio.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        audio.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        systemAudio = AudioToggle("SystemAudioToggle");
        microphone = AudioToggle("MicrophoneToggle");
        audio.Controls.Add(systemAudio, 0, 0); audio.Controls.Add(microphone, 1, 0);
        layout.Controls.Add(audio, 0, 1);
        systemAudio.CheckedChanged += (_, _) => AudioChanged(systemAudio, "PC音", SystemAudioChangeRequested);
        microphone.CheckedChanged += (_, _) => AudioChanged(microphone, "マイク", MicrophoneChangeRequested);
        SetAudioState(settings.RecordSystemAudio, settings.RecordMicrophone);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        start = RecordingButton("開始", "StartRecording", ButtonRole.Primary);
        stop = RecordingButton("停止", "StopRecording", ButtonRole.Danger);
        pause = RecordingButton("一時停止", "PauseRecording", ButtonRole.Pause);
        cancel = RecordingButton("キャンセル", "CancelRecording", ButtonRole.Neutral);
        start.Click += (_, _) => { if (!processing && !active) StartRequested?.Invoke(); };
        stop.Click += (_, _) => { if (!processing && active) StopRequested?.Invoke(); };
        pause.Click += (_, _) =>
        {
            if (processing || !active) return;
            if (paused) ResumeRequested?.Invoke(); else PauseRequested?.Invoke();
        };
        cancel.Click += (_, _) => { if (!processing && !active) CancelRequested?.Invoke(); };
        buttons.Controls.AddRange([start, stop, pause, cancel]);
        layout.Controls.Add(buttons, 0, 2);
        buttons.Layout += (_, _) => FitButtonRow(layout, buttons);
        timer.Tick += (_, _) => { if (!processing) RefreshState(); };
        SetControlsReady();
        FormClosing += (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (processing) return;
            if (active) StopRequested?.Invoke(); else CancelRequested?.Invoke();
        };
        FitButtonRow(layout, buttons);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (excludeFromCapture) _ = SetWindowDisplayAffinity(Handle, 0x11); // WDA_EXCLUDEFROMCAPTURE, Windows 11.
    }
    public void SetProcessing(string text, bool pauseElapsed = true)
    {
        processing = true;
        timer.Stop();
        if (pauseElapsed) elapsed.Stop();
        state.Text = text;
        ApplyControls();
    }
    public void SetRecording()
    {
        if (!active) elapsed.Restart(); else elapsed.Start();
        active = true;
        paused = false;
        SetControlsReady();
    }
    public void SetPaused()
    {
        if (!active) return;
        paused = true;
        SetControlsReady();
    }
    public void SetControlsReady()
    {
        processing = false;
        if (active && !paused) { elapsed.Start(); timer.Start(); }
        else { elapsed.Stop(); timer.Stop(); }
        ApplyControls();
        RefreshState();
    }
    public void SetAudioState(bool systemEnabled, bool micEnabled)
    {
        settingAudio = true;
        try
        {
            systemAudio.Checked = systemEnabled; microphone.Checked = micEnabled;
            systemAudio.Text = $"PC音: {(systemEnabled ? "ON" : "OFF")}";
            microphone.Text = $"マイク: {(micEnabled ? "ON" : "OFF")}";
        }
        finally { settingAudio = false; }
    }
    private void ApplyControls()
    {
        start.Visible = cancel.Visible = !active;
        stop.Visible = pause.Visible = active;
        pause.Text = paused ? "再開" : "一時停止";
        start.Enabled = cancel.Enabled = !processing && !active;
        stop.Enabled = pause.Enabled = !processing && active;
        systemAudio.Enabled = microphone.Enabled = !processing;
        ButtonTheme.Apply(pause, paused ? ButtonRole.Primary : ButtonRole.Pause);
        AcceptButton = !processing && !active ? start : null;
        CancelButton = !processing && !active ? cancel : null;
    }
    private void RefreshState() => state.Text = !active ? $"録画準備  {recordingSize.Width} × {recordingSize.Height}"
        : $"{(paused ? "Ⅱ 一時停止" : "● 録画中")}  {elapsed.Elapsed:hh\\:mm\\:ss}";
    private void AudioChanged(CheckBox control, string label, Action<bool>? requested)
    {
        control.Text = $"{label}: {(control.Checked ? "ON" : "OFF")}";
        if (!settingAudio && !processing && control.Enabled) requested?.Invoke(control.Checked);
    }
    private static CheckBox AudioToggle(string name)
    {
        var toggle = new CheckBox { Name = name, Appearance = Appearance.Button, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(3) };
        ButtonTheme.ApplyRecordingToggle(toggle);
        return toggle;
    }
    private static Button RecordingButton(string text, string name, ButtonRole role)
    {
        var button = new Button { Name = name, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(108, 40), Padding = new Padding(10, 4, 10, 4), Margin = new Padding(4) };
        ButtonTheme.Apply(button, role);
        return button;
    }
    private void FitButtonRow(TableLayoutPanel layout, FlowLayoutPanel buttons)
    {
        if (fittingHeight || Disposing || IsDisposed) return;
        // AutoSize buttons grow with their font. Reserve their actual preferred height,
        // including margins, while retaining the compact default size at 12pt.
        int buttonHeight = buttons.Controls.Cast<Button>().Max(button =>
            button.GetPreferredSize(Size.Empty).Height + button.Margin.Vertical);
        int requiredHeight = layout.Padding.Vertical + (int)Math.Ceiling(
            layout.RowStyles[0].Height + layout.RowStyles[1].Height)
            + buttons.Margin.Vertical + buttons.Padding.Vertical + buttonHeight;
        int height = Math.Max(178, requiredHeight);
        if (ClientSize.Height == height) return;
        fittingHeight = true;
        try { ClientSize = new Size(ClientSize.Width, height); }
        finally { fittingHeight = false; }
    }
    public void Finish() { allowClose = true; elapsed.Stop(); timer.Stop(); Close(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        { resourcesDisposed = true; elapsed.Stop(); timer.Stop(); timer.Dispose(); }
        base.Dispose(disposing);
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
