using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SSCapture.Capture;

namespace SSCapture.Recording;

internal sealed record VideoTiming(double FirstPts, double Duration, int Width, int Height, long PacketCount);
internal sealed record MediaInfo(int Width, int Height, int AudioStreams, double Duration, string VideoCodec);
internal sealed record RecordingMediaPart(string Path, VideoTiming Timing, bool HasAudio);

internal static class RecordingMedia
{
    private static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    internal static Size PaddedSize(Rectangle bounds) => new(checked(bounds.Width + bounds.Width % 2), checked(bounds.Height + bounds.Height % 2));

    internal static string[] CaptureArguments(Rectangle bounds, string videoPath, bool includeCursor = true)
    {
        ValidateBounds(bounds);
        return ["-hide_banner", "-y", "-loglevel", "warning", "-copyts", "-f", "gdigrab", "-framerate", "30",
            "-offset_x", bounds.X.ToString(CultureInfo.InvariantCulture), "-offset_y", bounds.Y.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{bounds.Width}x{bounds.Height}", "-draw_mouse", includeCursor ? "1" : "0", "-i", "desktop",
            "-an", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2:0:0:black", "-c:v", "libx264", "-preset", "veryfast",
            "-tune", "zerolatency", "-crf", "23", "-bf", "0", "-pix_fmt", "yuv420p", "-fps_mode", "passthrough",
            "-enc_time_base", "1:1000", "-avoid_negative_ts", "disabled", "-progress", "pipe:1", "-stats_period", "0.1",
            "-f", "matroska", videoPath];
    }

    internal static void ValidateBounds(Rectangle bounds)
    {
        if (bounds.Width < 2 || bounds.Height < 2 || bounds.Width > 32_766 || bounds.Height > 32_766)
            throw new ArgumentException("録画領域は各辺 2～32,766 ピクセルにしてください。");
        _ = checked(bounds.X + bounds.Width);
        _ = checked(bounds.Y + bounds.Height);
    }

    internal static string? CreateMask(CaptureSelection selection, string directory)
    {
        if (selection.Polygon is not { Count: >= 3 } polygon) return null;
        var size = PaddedSize(selection.Bounds);
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Black);
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.FillPolygon(Brushes.White, polygon.Select(point => new Point(point.X - selection.Bounds.X, point.Y - selection.Bounds.Y)).ToArray(), FillMode.Alternate);
        }
        var path = Path.Combine(directory, "selection-mask.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    internal static string AudioFilter(int inputIndex, double videoMinusAudioSeconds, double duration, string label)
    {
        if (!double.IsFinite(videoMinusAudioSeconds) || !double.IsFinite(duration) || duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
        var shift = videoMinusAudioSeconds >= 0
            ? $"atrim=start={Number(videoMinusAudioSeconds)},asetpts=PTS-STARTPTS"
            : $"asetpts=PTS-STARTPTS,adelay={Math.Round(-videoMinusAudioSeconds * 48_000).ToString(CultureInfo.InvariantCulture)}S:all=1";
        return $"[{inputIndex}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,{shift},apad,atrim=duration={Number(duration)}[{label}]";
    }

    internal static string[] MuxArguments(string video, IReadOnlyList<string> audio, string? mask, double videoMinusAudioSeconds, double duration, string output)
    {
        var args = new List<string> { "-hide_banner", "-y", "-loglevel", "warning", "-i", video };
        foreach (var track in audio) args.AddRange(["-i", track]);
        if (mask is not null) args.AddRange(["-loop", "1", "-framerate", "30", "-i", mask]);
        var filters = new List<string>();
        for (var i = 0; i < audio.Count; i++) filters.Add(AudioFilter(i + 1, videoMinusAudioSeconds, duration, $"a{i}"));
        if (audio.Count > 1)
            filters.Add(string.Concat(Enumerable.Range(0, audio.Count).Select(i => $"[a{i}]")) +
                $"amix=inputs={audio.Count}:duration=longest:dropout_transition=0:normalize=1,apad,atrim=duration={Number(duration)}[audio]");
        if (mask is not null)
        {
            // A separate PNG input avoids filter-string escaping of user paths. Both image branches inherit video time.
            // maskedmerge accepts planar RGB, not packed rgb24. Pin every plane to full-range GBR:
            // automatic conversion to limited-range YUV would turn a black mask into luma 16/chroma 128,
            // leaking part of the excluded image instead of applying a zero mask.
            filters.Add("[0:v]setpts=PTS-STARTPTS,format=gbrp,split=2[original][blank]");
            filters.Add("[blank]lutrgb=r=0:g=0:b=0,format=gbrp[black]");
            filters.Add($"[{audio.Count + 1}:v]format=gbrp[mask]");
            filters.Add("[black][original][mask]maskedmerge,format=gbrp,format=yuv420p[video]");
        }
        if (filters.Count != 0) args.AddRange(["-filter_complex", string.Join(';', filters)]);
        args.AddRange(["-map", mask is null ? "0:v:0" : "[video]"]);
        if (mask is null) args.AddRange(["-c:v", "copy"]);
        else args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p"]);
        if (audio.Count == 0) args.Add("-an");
        else args.AddRange(["-map", audio.Count == 1 ? "[a0]" : "[audio]", "-c:a", "aac", "-b:a", "192k"]);
        args.AddRange(["-t", Number(duration), "-movflags", "+faststart", "-f", "mp4", output]);
        return args.ToArray();
    }

    internal static async Task<MediaInfo> ProbeAsync(string ffprobe, string path, CancellationToken cancellationToken = default)
    {
        var json = await MediaProcess.RunAsync(ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height:format=duration", "-of", "json", path], cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(stream => stream.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("映像ストリームがありません。");
        var duration = ReadNumber(document.RootElement.GetProperty("format"), "duration");
        return new(video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(),
            streams.Count(stream => stream.GetProperty("codec_type").GetString() == "audio"), duration,
            video.GetProperty("codec_name").GetString() ?? "");
    }

    internal static async Task<VideoTiming> ProbeVideoTimingAsync(string ffprobe, string path, CancellationToken cancellationToken = default)
    {
        var info = await ProbeAsync(ffprobe, path, cancellationToken).ConfigureAwait(false);
        double first = double.PositiveInfinity, end = double.NegativeInfinity;
        long packets = 0;
        // Epoch PTS makes Matroska format.duration unreliable. Scan packet PTS without buffering the packet list.
        await MediaProcess.RunAsync(ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", path],
            cancellationToken, line =>
            {
                var values = line.Split(',');
                if (values.Length == 0 || !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts) || !double.IsFinite(pts)) return;
                var frameDuration = values.Length > 1 && double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 1d / 30;
                first = Math.Min(first, pts);
                end = Math.Max(end, pts + frameDuration);
                packets++;
            }).ConfigureAwait(false);
        if (packets == 0 || !double.IsFinite(end - first) || end <= first) throw new InvalidDataException("録画ファイルに有効な映像フレームがありません。");
        return new(first, end - first, info.Width, info.Height, packets);
    }

    internal static async Task<string> MuxAsync(string ffmpeg, string ffprobe, string video, IReadOnlyList<string> audio,
        string? mask, double videoMinusAudioSeconds, VideoTiming timing, string output, CancellationToken cancellationToken = default)
    {
        await MediaProcess.RunAsync(ffmpeg, MuxArguments(video, audio, mask, videoMinusAudioSeconds, timing.Duration, output),
            cancellationToken, timeout: TimeSpan.FromSeconds(Math.Max(600, timing.Duration * 2 + 30))).ConfigureAwait(false);
        var info = await ProbeAsync(ffprobe, output, cancellationToken).ConfigureAwait(false);
        if (info.Width != timing.Width || info.Height != timing.Height || info.VideoCodec != "h264" ||
            info.AudioStreams != (audio.Count == 0 ? 0 : 1) || info.Duration <= 0 || Math.Abs(info.Duration - timing.Duration) > 0.25)
            throw new InvalidDataException("完成した MP4 の映像・音声・再生時間が録画内容と一致しません。作業ファイルは保持しています。");
        return output;
    }

    internal static string ConcatList(IReadOnlyList<RecordingMediaPart> videoParts)
    {
        var text = new StringBuilder("ffconcat version 1.0\n");
        foreach (var part in videoParts)
        {
            // ffconcat has its own quoting grammar; filenames never enter a shell or filter expression.
            string path = Path.GetFullPath(part.Path).Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal);
            if (path.Contains('\r') || path.Contains('\n')) throw new ArgumentException("録画区間のパスに改行は使用できません。");
            text.Append("file '").Append(path).Append("'\n");
            text.Append("duration ").Append(Number(part.Timing.Duration)).Append('\n');
        }
        return text.ToString();
    }

    internal static string[] ConcatArguments(string videoList, IReadOnlyList<RecordingMediaPart> parts, string output)
    {
        if (parts.Count == 0) throw new ArgumentException("録画区間がありません。", nameof(parts));
        var args = new List<string> { "-hide_banner", "-y", "-loglevel", "warning", "-f", "concat", "-safe", "0", "-i", videoList };
        bool hasAudio = parts.Any(part => part.HasAudio);
        if (hasAudio)
        {
            foreach (var part in parts.Where(part => part.HasAudio)) args.AddRange(["-i", part.Path]);
            var filters = new List<string>();
            int input = 1;
            for (int i = 0; i < parts.Count; i++)
            {
                string duration = Number(parts[i].Timing.Duration);
                if (parts[i].HasAudio)
                    filters.Add($"[{input++}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS,apad,atrim=duration={duration}[a{i}]");
                else filters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={duration},asetpts=PTS-STARTPTS[a{i}]");
            }
            filters.Add(string.Concat(Enumerable.Range(0, parts.Count).Select(i => $"[a{i}]")) + $"concat=n={parts.Count}:v=0:a=1[audio]");
            args.AddRange(["-filter_complex", string.Join(';', filters)]);
        }
        args.AddRange(["-map", "0:v:0", "-c:v", "copy"]);
        if (hasAudio) args.AddRange(["-map", "[audio]", "-c:a", "aac", "-b:a", "192k"]);
        else args.Add("-an");
        args.AddRange(["-t", Number(parts.Sum(part => part.Timing.Duration)), "-movflags", "+faststart", "-f", "mp4", output]);
        return args.ToArray();
    }

    internal static async Task<string> ConcatAsync(string ffmpeg, string ffprobe, IReadOnlyList<string> paths,
        string output, CancellationToken cancellationToken = default)
    {
        if (paths.Count < 2) throw new ArgumentException("連結には2つ以上の録画区間が必要です。", nameof(paths));
        var outputPath = Path.GetFullPath(output);
        var parts = new List<RecordingMediaPart>();
        foreach (string path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            if (string.Equals(fullPath, outputPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("録画区間と連結先は別のファイルにしてください。");
            var info = await ProbeAsync(ffprobe, fullPath, cancellationToken).ConfigureAwait(false);
            var timing = await ProbeVideoTimingAsync(ffprobe, fullPath, cancellationToken).ConfigureAwait(false);
            if (info.VideoCodec != "h264" || info.AudioStreams > 1 ||
                (parts.Count > 0 && (timing.Width != parts[0].Timing.Width || timing.Height != parts[0].Timing.Height)))
                throw new InvalidDataException("録画区間の映像形式・寸法・音声数が一致しません。作業ファイルは保持しています。");
            parts.Add(new(fullPath, timing, info.AudioStreams != 0));
        }

        var scratch = Path.Combine(Path.GetDirectoryName(outputPath)!, "concat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var videos = new List<RecordingMediaPart>();
        for (int i = 0; i < parts.Count; i++)
        {
            // The concat demuxer requires identical stream layouts. Strip audio and normalize the MP4
            // time base without decoding/re-encoding video, including when only some segments have audio.
            string video = Path.Combine(scratch, $"video-{i:D4}.mp4");
            await MediaProcess.RunAsync(ffmpeg,
                ["-hide_banner", "-y", "-loglevel", "warning", "-i", parts[i].Path, "-map", "0:v:0", "-an", "-c:v", "copy",
                    "-video_track_timescale", "90000", "-f", "mp4", video], cancellationToken,
                timeout: TimeSpan.FromSeconds(Math.Max(600, parts[i].Timing.Duration * 2 + 30))).ConfigureAwait(false);
            videos.Add(parts[i] with { Path = video, HasAudio = false });
        }
        string list = Path.Combine(scratch, "video.ffconcat");
        await File.WriteAllTextAsync(list, ConcatList(videos), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        double duration = parts.Sum(part => part.Timing.Duration);
        await MediaProcess.RunAsync(ffmpeg, ConcatArguments(list, parts, outputPath), cancellationToken,
            timeout: TimeSpan.FromSeconds(Math.Max(600, duration * 2 + 30))).ConfigureAwait(false);
        var result = await ProbeAsync(ffprobe, outputPath, cancellationToken).ConfigureAwait(false);
        var resultTiming = await ProbeVideoTimingAsync(ffprobe, outputPath, cancellationToken).ConfigureAwait(false);
        if (result.VideoCodec != "h264" || result.Width != parts[0].Timing.Width || result.Height != parts[0].Timing.Height ||
            result.AudioStreams != (parts.Any(part => part.HasAudio) ? 1 : 0) || !double.IsFinite(result.Duration) ||
            Math.Abs(result.Duration - duration) > 0.25 || Math.Abs(resultTiming.Duration - duration) > 0.1 ||
            resultTiming.PacketCount != parts.Sum(part => part.Timing.PacketCount))
            throw new InvalidDataException("連結した MP4 の映像・音声・再生時間が録画区間と一致しません。区間ファイルは保持しています。");

        // Leave source MP4s available for recovery. Only known temporary remux files are removed after validation.
        foreach (var temporary in videos.Select(part => part.Path).Append(list))
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        try { Directory.Delete(scratch); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return outputPath;
    }

    private static double ReadNumber(JsonElement element, string name) => element.TryGetProperty(name, out var value) &&
        double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
