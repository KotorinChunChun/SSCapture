namespace SSCapture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Services.SettingsCommandLine.Handles(args))
        {
            try
            {
                if (Console.IsOutputRedirected)
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                if (Console.IsErrorRedirected)
                    Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                var store = new Services.SettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "settings.json"));
                Services.SettingsCommandLine.Run(args, store, Console.Out, new Services.StartupRegistration());
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        if (Services.CaptureCommandLine.Handles(args))
        {
            try
            {
                // リダイレクト時の文字コードを固定し、呼出元のコンソール設定は変更しない。
                if (Console.IsOutputRedirected)
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                if (Console.IsErrorRedirected)
                    Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                var request = Services.CaptureCommandLine.Parse(args);
                var settings = new Services.SettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "settings.json")).Load(out string? warning);
                if (warning is not null) throw new InvalidOperationException(warning);
                var command = Services.CaptureCommandLine.Resolve(request, settings);
                ConfigureUi();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                using var capture = new Services.CaptureCommandApplicationContext(command);
                Application.Run(capture);
                Environment.ExitCode = capture.ExitCode;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.FirstOrDefault() == "--capture-context-worker")
        { Environment.ExitCode = Services.CaptureContextWorker.Run(); return; }
        if (args.FirstOrDefault() == "--ocr-worker")
        { Environment.ExitCode = Services.OneOcr.RunWorker(args); return; }
        ConfigureUi();
        if (args.Contains("--ui-preview"))
        {
            using var preview = new UI.SettingsForm(new Services.AppSettings(), _ => null, _ => { });
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

        RunResident(args);
    }

    private static void RunResident(string[] args)
    {
        bool handoff = Services.LaunchBehavior.IsAdministratorHandoff(args);
        if (handoff && !Services.LaunchBehavior.IsAdministrator()) return;
        bool showSettings = Services.LaunchBehavior.ShowSettings(args);
        Mutex mutex;
        try { mutex = new Mutex(false, Services.SettingsCommandLine.SingletonName); }
        catch (UnauthorizedAccessException) { ShowExistingSettings(showSettings); return; }
        using (mutex)
        {
            bool firstInstance;
            try { firstInstance = mutex.WaitOne(handoff ? TimeSpan.FromSeconds(10) : TimeSpan.Zero); }
            catch (AbandonedMutexException) { firstInstance = true; }
            if (!firstInstance)
            {
                ShowExistingSettings(showSettings);
                return;
            }
            try
            {
                var store = new Services.SettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "settings.json"));
                var settings = store.Load(out string? warning);
                if (Services.LaunchBehavior.RequestsAdministrator(args, settings.RunAsAdministrator)
                    && !Services.LaunchBehavior.IsAdministrator())
                {
                    warning = Services.LaunchBehavior.StartAsAdministrator(Path.Combine(AppContext.BaseDirectory, "SSCapture.exe"));
                    // The elevated child waits on the singleton until we release it.
                    if (warning is null) return;
                }
                using var context = new TrayApplicationContext(showSettings, store, settings, warning);
                Application.ThreadException += (_, e) => context.HandleUnexpectedError(e.Exception);
                Application.Run(context);
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private static void ShowExistingSettings(bool showSettings)
    {
        if (!showSettings) return;
        try
        {
            using var signal = EventWaitHandle.OpenExisting(Services.SettingsCommandLine.ShowSettingsSignalName);
            signal.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException) { }
    }

    private static void ConfigureUi()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.SetDefaultFont(new Font("Yu Gothic UI", 12F));
    }
}
