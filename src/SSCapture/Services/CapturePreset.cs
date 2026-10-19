namespace SSCapture.Services;

internal sealed record CapturePreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public OutputDestination Destination { get; init; } = OutputDestination.Clipboard;
    public int PaddingPixels { get; init; }
    public string SaveDirectory { get; init; } = new AppSettings().SaveDirectory;
    public string FileNamePattern { get; init; } = Services.FileNamePattern.Default;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            throw new ArgumentException("プリセット名は1～80文字で指定してください。");
        new AppSettings { DefaultDestination = Destination, ScreenshotPaddingPixels = PaddingPixels,
            SaveDirectory = SaveDirectory, FileNamePattern = FileNamePattern }.Validate();
    }

    internal AppSettings Apply(AppSettings settings)
    {
        Validate();
        return settings with { DefaultDestination = Destination, ScreenshotPaddingPixels = PaddingPixels,
            SaveDirectory = SaveDirectory, FileNamePattern = FileNamePattern };
    }

    internal static CapturePreset FromSettings(string name, AppSettings settings) => new()
    {
        Name = name.Trim(), Destination = settings.DefaultDestination, PaddingPixels = settings.ScreenshotPaddingPixels,
        SaveDirectory = settings.SaveDirectory, FileNamePattern = settings.FileNamePattern
    };

    public override string ToString() => Name;
}
