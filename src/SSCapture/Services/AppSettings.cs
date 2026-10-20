namespace SSCapture.Services;

internal sealed record AppSettings
{
    public string SaveDirectory { get; init; } = DefaultSaveDirectory();
    public bool OpenFolderAfterSave { get; init; }
    public string FileNamePattern { get; init; } = Services.FileNamePattern.Default;
    public string OcrSeparator { get; init; } = "_";
    public string OneOcrDirectory { get; init; } = "";
    public CaptureMode DefaultMode { get; init; } = CaptureMode.Rectangle;
    public int SchemaVersion { get; init; } = 2;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasShortcuts => HotkeyBinding.ReadAssignments(this).Length > 0;
    public string ScreenshotShortcut1 { get; init; } = "Win+Shift+S";
    public string ScreenshotShortcut2 { get; init; } = "PrintScreen";
    public string RecordingShortcut1 { get; init; } = "Win+Shift+R";
    public string RecordingShortcut2 { get; init; } = "";
    public string[] InstantShortcuts { get; init; } = Enumerable.Range(1, 10).Select(n => $"Win+Shift+{n % 10}").ToArray();
    internal const string LastRegionKeyId = "$last";
    public string LastRegionShortcut { get; init; } = "Win+Ctrl+Shift+S";
    public SavedRegion[] SavedRegions { get; init; } = [];
    public CapturePreset[] CapturePresets { get; init; } = [];
    public int FixedWidth { get; init; } = 1280;
    public int FixedHeight { get; init; } = 720;
    public CaptureAnchor FixedCaptureAnchor { get; init; } = CaptureAnchor.TopLeft;
    public bool IncludeCursor { get; init; }
    public bool KeepHistory { get; init; } = true;
    public NotificationDisplay NotificationDisplay { get; init; } = Services.NotificationDisplay.Captured;
    public int NotificationDurationSeconds { get; init; } = 1;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowCaptureNotification => NotificationDurationSeconds > 0;
    public bool PlayCaptureSound { get; init; } = true;
    public NotificationCorner NotificationCorner { get; init; } = Services.NotificationCorner.BottomRight;
    public bool FreezeFrame { get; init; } = true;
    public bool LimitToStartingMonitor { get; init; } = true;
    public int ScreenshotPaddingPixels { get; init; }
    public ScreenshotColorDepth ScreenshotColorDepth { get; init; }
    public bool Grid20 { get; init; }
    public bool ShowSelectionGuide { get; init; } = true;
    public OutputDestination DefaultDestination { get; init; } = OutputDestination.Clipboard;
    public bool RecordSystemAudio { get; init; }
    public bool RecordMicrophone { get; init; }
    public string MicrophoneDeviceId { get; init; } = "";
    public string ImageEditorPath { get; init; } = "";
    public string VideoEditorPath { get; init; } = "";
    public string ImageEditorArguments { get; init; } = EditorArguments.Default;
    public string VideoEditorArguments { get; init; } = EditorArguments.Default;

    internal void Validate()
    {
        if (CapturePresets is null || CapturePresets.Length > 50 || CapturePresets.Any(p => p is null))
            throw new ArgumentException("プリセットは50件以内で指定してください。");
        foreach (var preset in CapturePresets) preset.Validate();
        if (CapturePresets.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != CapturePresets.Length
            || CapturePresets.Select(p => p.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != CapturePresets.Length)
            throw new ArgumentException("プリセットのIDまたは名前が重複しています。");
        if (SavedRegions is null || SavedRegions.Any(r => r is null)) throw new ArgumentException("登録範囲の設定が不正です。");
        foreach (var region in SavedRegions) region.Validate();
        if (SavedRegions.Any(r => r.Id == LastRegionKeyId)) throw new ArgumentException("登録範囲のIDが予約値です。");
        if (SavedRegions.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != SavedRegions.Length)
            throw new ArgumentException("登録範囲のIDが重複しています。");
        if (FixedWidth is < 1 or > 32768 || FixedHeight is < 1 or > 32768 || (long)FixedWidth * FixedHeight > 100_000_000)
            throw new ArgumentException("固定サイズは1～32768px、合計1億画素以内で指定してください。");
        if (!Enum.IsDefined(FixedCaptureAnchor)) throw new ArgumentException("固定サイズの基点が正しくありません。");
        HotkeyBinding.ValidateSettings(this);
        Services.FileNamePattern.Validate(FileNamePattern, OcrSeparator);
        if (!string.IsNullOrWhiteSpace(OneOcrDirectory) && (!Path.IsPathFullyQualified(OneOcrDirectory) || OneOcrDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0))
            throw new ArgumentException("OneOCRフォルダは絶対パスか空欄（自動検出）を指定してください。");
        if (!Enum.IsDefined(DefaultMode)) throw new ArgumentException("撮影モードが正しくありません。");
        if (!Enum.IsDefined(ScreenshotColorDepth)) throw new ArgumentException("静止画の減色指定が正しくありません。");
        if (ScreenshotPaddingPixels is < 0 or > ScreenshotPadding.MaximumPixels)
            throw new ArgumentException($"画像の余白は0～{ScreenshotPadding.MaximumPixels}pxで指定してください。");
        if (!Enum.IsDefined(NotificationCorner)) throw new ArgumentException("通知の表示位置が正しくありません。");
        if (!Enum.IsDefined(NotificationDisplay)) throw new ArgumentException("通知の表示先が正しくありません。");
        if (NotificationDurationSeconds is < 0 or > 3600) throw new ArgumentException("通知の表示時間は0～3600秒の整数で指定してください。");
        if ((DefaultDestination & ~(OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp)) != 0) throw new ArgumentException("送信先が正しくありません。");
        EditorArguments.Validate(ImageEditorArguments);
        EditorArguments.Validate(VideoEditorArguments);
        if (string.IsNullOrWhiteSpace(SaveDirectory) || !Path.IsPathFullyQualified(SaveDirectory))
            throw new ArgumentException("保存先にはフォルダの絶対パスを入力してください。");
        if (SaveDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || SaveDirectory.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
            throw new ArgumentException("保存先に使用できない文字が含まれています。");
        string fullPath = Path.GetFullPath(SaveDirectory);
        string root = Path.GetPathRoot(fullPath)!;
        if (fullPath[root.Length..].Contains(':')) throw new ArgumentException("保存先のパスが正しくありません。");
        foreach (string editor in new[] { ImageEditorPath, VideoEditorPath })
        {
            if (!string.IsNullOrWhiteSpace(editor) && (!Path.IsPathFullyQualified(editor) || !editor.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("外部アプリにはexeファイルの絶対パスを指定してください。");
        }
    }

    private static string DefaultSaveDirectory()
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures)) pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        return Path.Combine(pictures, "SSCapture");
    }
}
