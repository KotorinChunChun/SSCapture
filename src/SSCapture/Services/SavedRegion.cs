namespace SSCapture.Services;

internal sealed record DisplayLayout(string Identity, string Name, Rectangle Bounds, int DpiX, int DpiY);

internal sealed record SavedRegion
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "範囲";
    public string Shortcut { get; init; } = "";
    public bool IsDefault { get; init; }
    public int ProfileVersion { get; init; }
    public bool UseFixedCoordinates { get; init; } = true;
    public CaptureMode Mode { get; init; } = CaptureMode.FixedRegion;
    public CaptureAnchor FixedAnchor { get; init; } = CaptureAnchor.TopLeft;
    public bool FreezeFrame { get; init; } = true;
    public bool IncludeCursor { get; init; }
    public int PaddingPixels { get; init; }
    public bool RecordSystemAudio { get; init; }
    public bool RecordMicrophone { get; init; }
    public string MicrophoneDeviceId { get; init; } = "";
    public Rectangle Bounds { get; init; }
    public string? AnchorIdentity { get; init; }
    public Rectangle RelativeBounds { get; init; }
    public DisplayLayout[] Layout { get; init; } = [];

    internal void Validate()
    {
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(FixedAnchor) || PaddingPixels is < 0 or > ScreenshotPadding.MaximumPixels)
            throw new ArgumentException("入力プリセットの取得条件が正しくありません。");
        if (MicrophoneDeviceId is null || MicrophoneDeviceId.Length > 4096 || MicrophoneDeviceId.Any(char.IsControl))
            throw new ArgumentException("入力プリセットのマイク識別子が正しくありません。");
        if (UseFixedCoordinates) RegionCoordinates.ValidateSaved(this);
        else
        {
            if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128 || Id.Any(char.IsControl) || string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || Name.Any(char.IsControl))
                throw new ArgumentException("入力プリセットの名前またはIDが正しくありません。");
            _ = HotkeyBinding.Parse(Shortcut);
            Capture.CaptureGeometry.ValidateFixedSize(Bounds.Size);
        }
    }
    internal AppSettings Apply(AppSettings settings) => settings with
    { DefaultMode = UseFixedCoordinates ? CaptureMode.FixedRegion : Mode, FixedWidth = Bounds.Width, FixedHeight = Bounds.Height,
        FixedCaptureAnchor = FixedAnchor, FreezeFrame = FreezeFrame, IncludeCursor = IncludeCursor, ScreenshotPaddingPixels = PaddingPixels,
        RecordSystemAudio = RecordSystemAudio, RecordMicrophone = RecordMicrophone, MicrophoneDeviceId = MicrophoneDeviceId };
    internal static SavedRegion FromSettings(string name, AppSettings settings) => new()
    { Name = name, ProfileVersion = 2, UseFixedCoordinates = false, Mode = settings.DefaultMode,
        Bounds = new(0, 0, settings.FixedWidth, settings.FixedHeight), FixedAnchor = settings.FixedCaptureAnchor,
        FreezeFrame = settings.FreezeFrame, IncludeCursor = settings.IncludeCursor, PaddingPixels = settings.ScreenshotPaddingPixels,
        RecordSystemAudio = settings.RecordSystemAudio, RecordMicrophone = settings.RecordMicrophone, MicrophoneDeviceId = settings.MicrophoneDeviceId };
}
