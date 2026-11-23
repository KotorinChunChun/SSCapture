using System.Text.Json.Serialization;

namespace SSCapture.Services;

internal enum StillClipboardContent { Image, Text, Tsv, FilePath }

internal sealed record CapturePreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuiltInKey { get; init; }
    public string Name { get; init; } = "";
    public bool IsDefault { get; init; }
    public StillClipboardContent ClipboardContent { get; init; }
    public ScreenshotColorDepth ColorDepth { get; init; }
    public bool OpenFolderAfterSave { get; init; }
    public string OcrSeparator { get; init; } = "_";
    public OutputDestination Destination { get; init; } = OutputDestination.Clipboard;
    // 旧形式の読み込みと移行に使用する。
    public int PaddingPixels { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WhitePaddingPixels { get; init; }

    internal int ResolvePadding(int legacyInputPixels) => WhitePaddingPixels ?? legacyInputPixels;
    public WindowCrop WindowCrop { get; init; }
    public string SaveDirectory { get; init; } = new AppSettings().SaveDirectory;
    public string FileNamePattern { get; init; } = Services.FileNamePattern.Default;
    public string? ImageEditorId { get; init; }
    public string? VideoEditorId { get; init; }
    public CaptureMetadataMode MetadataMode { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            throw new ArgumentException("出力プリセット名は1～80文字で指定してください。");
        if (WhitePaddingPixels is < 0 or > ScreenshotPadding.MaximumPixels)
            throw new ArgumentException("白い余白は0～4096pxで指定してください。");
        new AppSettings { DefaultDestination = Destination, ClipboardContent = ClipboardContent, ScreenshotPaddingPixels = PaddingPixels, WindowCrop = WindowCrop,
            SaveDirectory = SaveDirectory, FileNamePattern = FileNamePattern, MetadataMode = MetadataMode,
            ScreenshotColorDepth = ColorDepth, OcrSeparator = OcrSeparator }.Validate();
    }

    internal AppSettings Apply(AppSettings settings)
    {
        Validate();
        return settings with { DefaultDestination = Destination, ClipboardContent = ClipboardContent, ScreenshotPaddingPixels = ResolvePadding(settings.ScreenshotPaddingPixels),
            SaveDirectory = SaveDirectory, FileNamePattern = FileNamePattern,
            WindowCrop = WindowCrop, ScreenshotColorDepth = ColorDepth, OpenFolderAfterSave = OpenFolderAfterSave, OcrSeparator = OcrSeparator,
            SelectedImageEditorId = ImageEditorId ?? "",
            SelectedVideoEditorId = VideoEditorId ?? "", MetadataMode = MetadataMode };
    }

    internal static CapturePreset FromSettings(string name, AppSettings settings) => new()
    {
        Name = name.Trim(), Destination = settings.DefaultDestination, ClipboardContent = settings.ClipboardContent,
        WindowCrop = settings.WindowCrop, ColorDepth = settings.ScreenshotColorDepth, OpenFolderAfterSave = settings.OpenFolderAfterSave, OcrSeparator = settings.OcrSeparator,
        SaveDirectory = settings.SaveDirectory, FileNamePattern = settings.FileNamePattern,
        ImageEditorId = settings.SelectedImageEditorId, VideoEditorId = settings.SelectedVideoEditorId, MetadataMode = settings.MetadataMode
    };

    public override string ToString() => Name;
}
