using System.Globalization;

namespace SSCapture.Services;

internal static class CaptureTimerArguments
{
    internal static bool Handles(string option) => option is "--delay-seconds" or "--at" or "--interval-seconds" or "--count";

    internal static void Read(Dictionary<string, string> options, string option, IReadOnlyList<string> args, ref int index)
    {
        if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException(option + " の値を指定してください。");
        if (!options.TryAdd(option, args[index]))
            throw new ArgumentException(option + " が重複しています。");
    }

    internal static CaptureSchedule? Build(IReadOnlyDictionary<string, string> options)
    {
        if (options.Count == 0) return null;

        int Number(string option)
        {
            if (!int.TryParse(options[option], NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0)
                throw new ArgumentException(option + " には1以上の整数を指定してください。");
            return value;
        }

        CaptureSchedule schedule;
        if (options.Count == 2 && options.ContainsKey("--interval-seconds") && options.ContainsKey("--count"))
            schedule = new CaptureSchedule.Periodic(Number("--interval-seconds"), Number("--count"));
        else if (options.Count == 1 && options.ContainsKey("--delay-seconds"))
            schedule = new CaptureSchedule.Delay(Number("--delay-seconds"));
        else if (options.Count == 1 && options.TryGetValue("--at", out string? value))
            schedule = new CaptureSchedule.At(ParseTime(value));
        else
            throw new ArgumentException("時限には --delay-seconds または --at を1つ、定期には --interval-seconds と --count を組み合わせて指定してください。");
        schedule.Validate(DateTimeOffset.UtcNow);
        return schedule;
    }

    private static DateTimeOffset ParseTime(string value)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        if (value.EndsWith('Z')) value = value[..^1] + "+00:00";
        if (!DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
            throw new ArgumentException("--at に ISO 8601 形式のオフセット付き日時を指定してください。");
        return parsed;
    }

    internal static void Append(List<string> args, CaptureSchedule? schedule)
    {
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        switch (schedule)
        {
            case CaptureSchedule.Delay delay:
                args.AddRange(["--delay-seconds", Number(delay.Seconds)]);
                break;
            case CaptureSchedule.At at:
                args.AddRange(["--at", at.When.ToString("O", CultureInfo.InvariantCulture)]);
                break;
            case CaptureSchedule.Periodic { Count: int count } periodic:
                args.AddRange(["--interval-seconds", Number(periodic.IntervalSeconds), "--count", Number(count)]);
                break;
            case CaptureSchedule.Periodic:
                throw new ArgumentException("定期撮影のコマンドには撮影枚数を指定してください。");
        }
    }

    internal static CaptureSchedule.At LocalTime(DateTime value, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(value) || zone.IsAmbiguousTime(value))
            throw new ArgumentException("時刻の切り替わりで一意に指定できない日時です。別の時刻を選んでください。");
        return new(new DateTimeOffset(value, zone.GetUtcOffset(value)));
    }
}
