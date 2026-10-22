namespace SSCapture.Services;

internal sealed record CapturePreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public bool IsDefault { get; init; }
    public ScreenshotColorDepth ColorDepth { get; init; }
    public bool OpenFolderAfterSave { get; init; }
    public string OcrSeparator { get; init; } = "_";
    public OutputDestination Destination { get; init; } = OutputDestination.Clipboard;
    public int PaddingPixels { get; init; }
    public string SaveDirectory { get; init; } = new AppSettings().SaveDirectory;
    public string FileNamePattern { get; init; } = Services.FileNamePattern.Default;
    public string? ImageEditorId { get; init; }
    public string? VideoEditorId { get; init; }
    public CaptureMetadataMode MetadataMode { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            throw new ArgumentException("出力プリセット名は1～80文字で指定してください。");
        new AppSettings { DefaultDestination = Destination, ScreenshotPaddingPixels = PaddingPixels,
            SaveDirectory = SaveDirectory, FileNamePattern = FileNamePattern, MetadataMode = MetadataMode,
            ScreenshotColorDepth = ColorDepth, OcrSeparator = OcrSeparator }.Validate();
    }

    internal AppSettings Apply(AppSettings settings)
    {
        Validate();
        return settings with { DefaultDestination = Destination,
            SaveDirectory = SaveDirectory, FileNamePattern = FileNamePattern,
            ScreenshotColorDepth = ColorDepth, OpenFolderAfterSave = OpenFolderAfterSave, OcrSeparator = OcrSeparator,
            SelectedImageEditorId = ImageEditorId ?? "",
            SelectedVideoEditorId = VideoEditorId ?? "", MetadataMode = MetadataMode };
    }

    internal static CapturePreset FromSettings(string name, AppSettings settings) => new()
    {
        Name = name.Trim(), Destination = settings.DefaultDestination,
        ColorDepth = settings.ScreenshotColorDepth, OpenFolderAfterSave = settings.OpenFolderAfterSave, OcrSeparator = settings.OcrSeparator,
        SaveDirectory = settings.SaveDirectory, FileNamePattern = settings.FileNamePattern,
        ImageEditorId = settings.SelectedImageEditorId, VideoEditorId = settings.SelectedVideoEditorId, MetadataMode = settings.MetadataMode
    };

    public override string ToString() => Name;
}
