using System.Text.Json;
using System.Text.Json.Serialization;

namespace SSCapture.Capture;

internal enum CaptureActivation { Push, Toggle }

internal sealed record CaptureKeyDefaults(CaptureKeyMap Drag, CaptureKeyMap Before,
    CaptureActivation ControlActivation = CaptureActivation.Push, CaptureActivation WindowActivation = CaptureActivation.Push)
{
    internal const string FileName = "capture-key-defaults.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter<CaptureKeyAction>(allowIntegerValues: false),
            new JsonStringEnumConverter<CaptureActivation>(allowIntegerValues: false) }
    };
    private static readonly Lazy<CaptureKeyDefaults> Defaults = new(() => Load(AppContext.BaseDirectory));
    internal static CaptureKeyDefaults Current => Defaults.Value;

    internal static CaptureKeyDefaults Load(string directory)
    {
        return ReadFile(Path.Combine(directory, "data", FileName))
            ?? ReadFile(Path.Combine(directory, FileName))
            ?? ReadEmbedded();
    }

    private static CaptureKeyDefaults? ReadFile(string path)
    {
        try { using var stream = File.OpenRead(path); return Read(stream); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static CaptureKeyDefaults ReadEmbedded()
    {
        using var stream = typeof(CaptureKeyDefaults).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException("撮影キーの既定設定がありません。");
        return Read(stream) ?? throw new InvalidOperationException("撮影キーの既定設定が無効です。");
    }

    private static CaptureKeyDefaults? Read(Stream stream)
    {
        var defaults = JsonSerializer.Deserialize<CaptureKeyDefaults>(stream, Options);
        if (defaults?.Drag is null || defaults.Before is null || CaptureKeyMap.Combinations.Any(key =>
            defaults.Drag.Resolve(key) == CaptureKeyAction.HighlightWindow || !CaptureKeyMap.IsBeforeAction(defaults.Before.Resolve(key))))
            return null;
        return defaults;
    }
}
