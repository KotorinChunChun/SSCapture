namespace SSCapture.Services;

internal sealed record ExternalApplication
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Executable { get; init; } = "";
    public string Arguments { get; init; } = EditorArguments.Default;
    public override string ToString() => Name;
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            throw new ArgumentException("外部アプリ名は1～80文字で指定してください。");
        if (!Path.IsPathFullyQualified(Executable) || !Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("外部アプリにはexeファイルの絶対パスを指定してください。");
        EditorArguments.Validate(Arguments);
    }

    internal static void ValidateList(ExternalApplication[] apps, string selected)
    {
        if (apps is null || apps.Length > 50 || apps.Any(a => a is null)) throw new ArgumentException("外部アプリは用途ごとに50件以内です。");
        foreach (var app in apps) app.Validate();
        if (apps.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != apps.Length ||
            apps.Select(a => a.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != apps.Length)
            throw new ArgumentException("外部アプリのIDまたは名前が重複しています。");
        ValidateReference(apps, selected);
    }

    internal static void ValidateReference(ExternalApplication[] apps, string? id)
    {
        if (!string.IsNullOrEmpty(id) && !apps.Any(a => a.Id == id)) throw new ArgumentException("参照中の外部アプリを削除できません。既定・出力プリセットの選択を変更してください。");
    }
}
