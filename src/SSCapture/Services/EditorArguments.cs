using System.Text;
using System.Text.RegularExpressions;

namespace SSCapture.Services;

internal static partial class EditorArguments
{
    public const string Default = "\"{file}\"";
    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Variable();
    internal static string[] Expand(string template, string file)
    {
        string full = Path.GetFullPath(file);
        return Parse(template).Select(argument => Variable().Replace(argument, match => match.Groups[1].Value switch
        {
            "file" => full, "directory" => Path.GetDirectoryName(full)!, "filename" => Path.GetFileName(full),
            _ => throw new ArgumentException($"未知の引数変数: {match.Value}")
        })).ToArray();
    }
    internal static void Validate(string template) => _ = Expand(template, Path.Combine(Path.GetTempPath(), "sample.png"));
    // Windows CRT quoting rules. Expansion happens afterwards, never through a shell.
    internal static string[] Parse(string template)
    {
        if (template is null || template.Contains('\0')) throw new ArgumentException("引数が正しくありません。");
        var result = new List<string>();
        int i = 0;
        while (i < template.Length)
        {
            while (i < template.Length && char.IsWhiteSpace(template[i])) i++;
            if (i == template.Length) break;
            var token = new StringBuilder();
            bool quoted = false;
            while (i < template.Length && (quoted || !char.IsWhiteSpace(template[i])))
            {
                int slashes = 0;
                while (i < template.Length && template[i] == '\\') { slashes++; i++; }
                if (i < template.Length && template[i] == '"')
                {
                    token.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) { token.Append('"'); i++; }
                    else if (quoted && i + 1 < template.Length && template[i + 1] == '"') { token.Append('"'); i += 2; }
                    else { quoted = !quoted; i++; }
                }
                else
                {
                    token.Append('\\', slashes);
                    if (i < template.Length && (quoted || !char.IsWhiteSpace(template[i]))) token.Append(template[i++]);
                }
            }
            if (quoted) throw new ArgumentException("引数のダブルクォートが閉じられていません。");
            result.Add(token.ToString());
        }
        return result.ToArray();
    }
}

internal sealed record EditorPreset(string Name, string Executable, string Arguments)
{
    public override string ToString() => Name;
}
internal static class EditorPresets
{
    public static IReadOnlyList<EditorPreset> Images =>
    [
        new("カスタム", "", EditorArguments.Default),
        new("MS ペイント", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mspaint.exe"), EditorArguments.Default),
        new("Paint.NET", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "paint.net", "paintdotnet.exe"), EditorArguments.Default)
    ];
    public static IReadOnlyList<EditorPreset> Videos =>
    [
        new("カスタム", "", EditorArguments.Default),
        new("Shotcut", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Shotcut", "shotcut.exe"), EditorArguments.Default)
    ];
}
