namespace SSCapture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--ocr-worker")
        { Environment.ExitCode = Services.OneOcr.RunWorker(args); return; }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.SetDefaultFont(new Font("Yu Gothic UI", 12F));
        if (args.Contains("--ui-preview"))
        {
            using var preview = new UI.SettingsForm(new Services.AppSettings(), _ => null, () => { });
            Application.Run(preview);
            return;
        }
        if (args.Contains("--recording-ui-preview"))
        {
            using var preview = new UI.RecordingControlForm(new Rectangle(0, 0, 640, 480), new Services.AppSettings(), false);
            preview.Text = WindowTitle.Create("録画プレビュー");
            preview.StartRequested += preview.SetRecording;
            preview.StopRequested += preview.Finish;
            preview.CancelRequested += preview.Finish;
            preview.PauseRequested += preview.SetPaused;
            preview.ResumeRequested += preview.SetRecording;
            bool systemAudio = false, microphone = false;
            preview.SystemAudioChangeRequested += enabled => { systemAudio = enabled; preview.SetAudioState(systemAudio, microphone); };
            preview.MicrophoneChangeRequested += enabled => { microphone = enabled; preview.SetAudioState(systemAudio, microphone); };
            Application.Run(preview);
            return;
        }

        using var mutex = new Mutex(true, @"Local\SSCapture.Singleton", out bool firstInstance);
        if (!firstInstance)
        {
            if (args.Contains("--startup")) return;
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\SSCapture.ShowSettings"); signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }
        using var context = new TrayApplicationContext(args.Contains("--settings"));
        Application.ThreadException += (_, e) => context.HandleUnexpectedError(e.Exception);
        Application.Run(context);
    }
}
