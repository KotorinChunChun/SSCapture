using System.Globalization;
using System.Buffers.Binary;
using NAudio.Wave;
using SSCapture.Capture;

namespace SSCapture.Recording;

/// <summary>Exercises the production encoder/mux/probe path with generated media; never opens a display or audio endpoint.</summary>
internal static class RecordingMediaSmoke
{
    internal static async Task<string> RunAsync(string ffmpegPath, string ffprobePath, string workRoot, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetFullPath(workRoot), "synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var video = Path.Combine(directory, "synthetic.mkv");
        const double epoch = 1_791_580_000.25;
        var start = epoch.ToString("0.#########", CultureInfo.InvariantCulture);
        await MediaProcess.RunAsync(ffmpegPath,
            ["-hide_banner", "-y", "-loglevel", "warning", "-copyts", "-f", "lavfi", "-i", "testsrc=size=321x241:rate=30:duration=2",
                "-vf", $"pad=ceil(iw/2)*2:ceil(ih/2)*2:0:0:black,settb=1/1000,setpts=PTS+{start}/TB", "-an", "-c:v", "libx264",
                "-preset", "veryfast", "-tune", "zerolatency", "-bf", "0", "-pix_fmt", "yuv420p", "-fps_mode", "passthrough",
                "-enc_time_base", "1:1000", "-avoid_negative_ts", "disabled", "-f", "matroska", video], cancellationToken).ConfigureAwait(false);
        var timing = await RecordingMedia.ProbeVideoTimingAsync(ffprobePath, video, cancellationToken).ConfigureAwait(false);
        if (Math.Abs(timing.FirstPts - epoch) > 0.002 || Math.Abs(timing.Duration - 2) > 0.04 || timing.Width != 322 || timing.Height != 242)
            throw new InvalidDataException($"合成映像の epoch PTS / 奇数寸法検証に失敗しました: {timing}");

        await RecordingMedia.MuxAsync(ffmpegPath, ffprobePath, video, [], null, 0, timing,
            Path.Combine(directory, "silent.mp4"), cancellationToken).ConfigureAwait(false);

        var system = Path.Combine(directory, "stereo-float.wav");
        var mic = Path.Combine(directory, "mono-pcm.wav");
        await MediaProcess.RunAsync(ffmpegPath,
            ["-hide_banner", "-y", "-loglevel", "warning", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1.5",
                "-af", "adelay=750:all=1,apad,atrim=duration=3", "-ac", "2", "-c:a", "pcm_f32le", system], cancellationToken).ConfigureAwait(false);
        await MediaProcess.RunAsync(ffmpegPath,
            ["-hide_banner", "-y", "-loglevel", "warning", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100:duration=1",
                "-af", "adelay=1000:all=1,apad,atrim=duration=3", "-ac", "1", "-c:a", "pcm_s16le", mic], cancellationToken).ConfigureAwait(false);
        var selection = new CaptureSelection(new Rectangle(-321, -241, 321, 241),
            [new(-300, -220), new(-20, -220), new(-160, -20)], OutputDestination.File);
        var mask = RecordingMedia.CreateMask(selection, directory);
        var output = await RecordingMedia.MuxAsync(ffmpegPath, ffprobePath, video, [system, mic], mask, 0.25, timing,
            Path.Combine(directory, "mixed-masked.mp4"), cancellationToken).ConfigureAwait(false);
        await RecordingMedia.MuxAsync(ffmpegPath, ffprobePath, video, [mic], null, -0.1, timing,
            Path.Combine(directory, "delayed-mic.mp4"), cancellationToken).ConfigureAwait(false);

        var decoded = Path.Combine(directory, "decoded-mix.s16");
        await MediaProcess.RunAsync(ffmpegPath,
            ["-hide_banner", "-y", "-loglevel", "error", "-i", output, "-map", "0:a", "-ac", "1", "-ar", "48000", "-f", "s16le", decoded], cancellationToken).ConfigureAwait(false);
        var pcm = await File.ReadAllBytesAsync(decoded, cancellationToken).ConfigureAwait(false);
        if (Rms(pcm, 0.1, 0.3) > 0.001 || Rms(pcm, 0.6, 0.9) < 0.01)
            throw new InvalidDataException("AAC 混合音声の開始前無音または開始後振幅が期待値と異なります。");
        var frame = Path.Combine(directory, "masked-frame.rgb");
        await MediaProcess.RunAsync(ffmpegPath,
            ["-hide_banner", "-y", "-loglevel", "error", "-i", output, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", frame], cancellationToken).ConfigureAwait(false);
        var pixels = await File.ReadAllBytesAsync(frame, cancellationToken).ConfigureAwait(false);
        if (pixels.Length != 322 * 242 * 3 || MaxPixel(pixels, 322, 0, 0, 12, 12) > 12 ||
            MaxPixel(pixels, 322, 300, 220, 12, 12) > 12 || MaxPixel(pixels, 322, 140, 60, 20, 20) < 50)
            throw new InvalidDataException("自由形状外側の黒塗りまたは内側の映像が期待値と異なります。");
        await VerifyConcatenationAsync(ffmpegPath, ffprobePath, directory, video, timing, cancellationToken).ConfigureAwait(false);
        await VerifyAudioGateAsync(ffmpegPath, ffprobePath, directory, video, timing, cancellationToken).ConfigureAwait(false);
        return output;
    }

    private static async Task VerifyAudioGateAsync(string ffmpeg, string ffprobe, string directory,
        string video, VideoTiming timing, CancellationToken cancellationToken)
    {
        const int rate = 48_000, framesPerPacket = 6_000;
        const long origin = 10_000_000, packetDuration = 1_250_000;
        string wave = Path.Combine(directory, "gated-source.wav");
        using (var writer = new WaveFileWriter(wave, new WaveFormat(rate, 16, 1)))
        {
            var gate = new AudioGate(rate, origin, origin + 2_500_000);
            var timeline = new AudioTimeline(rate, 2, origin, writer.Write, gate);
            for (int packetIndex = 0; packetIndex < 16; packetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long packetTime = origin + packetIndex * packetDuration;
                if (packetIndex is 6 or 14) gate.SetEnabled(false, packetTime);
                if (packetIndex == 10) gate.SetEnabled(true, packetTime);
                byte[] pcm = new byte[framesPerPacket * 2];
                for (int frame = 0; frame < framesPerPacket; frame++)
                {
                    double value = Math.Sin((packetIndex * framesPerPacket + frame) * 660d * 2 * Math.PI / rate) * 0.25;
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 2, 2), (short)Math.Round(value * short.MaxValue));
                }
                var packet = gate.CapturePacket(pcm, 0, packetIndex * framesPerPacket, packetTime, packetTime + packetDuration);
                timeline.Append(packet);
            }
            timeline.Finish(origin + 20_000_000);
            if (timeline.FramesWritten != 96_000) throw new InvalidDataException("音声切替で録音時間が伸縮しました。");
        }
        string output = await RecordingMedia.MuxAsync(ffmpeg, ffprobe, video, [wave], null, 0, timing,
            Path.Combine(directory, "gated-audio.mp4"), cancellationToken).ConfigureAwait(false);
        string decoded = Path.Combine(directory, "gated-audio.s16");
        await MediaProcess.RunAsync(ffmpeg,
            ["-hide_banner", "-y", "-loglevel", "error", "-i", output, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "s16le", decoded], cancellationToken).ConfigureAwait(false);
        byte[] decodedPcm = await File.ReadAllBytesAsync(decoded, cancellationToken).ConfigureAwait(false);
        if (Rms(decodedPcm, 0.05, 0.20) > 0.002 || Rms(decodedPcm, 0.85, 1.15) > 0.002 || Rms(decodedPcm, 1.85, 1.95) > 0.002 ||
            Rms(decodedPcm, 0.35, 0.65) < 0.08 || Rms(decodedPcm, 1.35, 1.65) < 0.08)
            throw new InvalidDataException("音声ON/OFFのPCMマスクがAAC出力へ正しく反映されていません。");
    }

    private static async Task VerifyConcatenationAsync(string ffmpeg, string ffprobe, string directory,
        string video, VideoTiming timing, CancellationToken cancellationToken)
    {
        string concatDirectory = Path.Combine(directory, "連結 空白,'確認");
        Directory.CreateDirectory(concatDirectory);
        string wave = Path.Combine(concatDirectory, "連続音.wav");
        await MediaProcess.RunAsync(ffmpeg,
            ["-hide_banner", "-y", "-loglevel", "warning", "-f", "lavfi", "-i", "sine=frequency=660:sample_rate=48000:duration=2",
                "-ac", "2", "-c:a", "pcm_s16le", wave], cancellationToken).ConfigureAwait(false);
        string tone = await RecordingMedia.MuxAsync(ffmpeg, ffprobe, video, [wave], null, 0, timing,
            Path.Combine(concatDirectory, "音あり.mp4"), cancellationToken).ConfigureAwait(false);
        string silent = Path.Combine(directory, "silent.mp4");

        await CheckConcatAsync("all-silent", [silent, silent], [(0.0, 4.0, false)]).ConfigureAwait(false);
        await CheckConcatAsync("silent-then-audio", [silent, tone], [(0.1, 1.9, false), (2.08, 3.9, true)]).ConfigureAwait(false);
        await CheckConcatAsync("audio-then-silent", [tone, silent], [(0.1, 1.92, true), (2.08, 3.9, false)]).ConfigureAwait(false);
        // Five intervals model repeated pause/resume. 6.0s joins two AAC-bearing intervals;
        // the narrow sample window catches repeated encoder priming or a gap at that boundary.
        await CheckConcatAsync("multiple-pauses", [tone, silent, tone, tone, silent],
            [(0.1, 1.9, true), (2.08, 3.92, false), (4.08, 5.9, true), (5.98, 6.02, true), (6.1, 7.9, true), (8.08, 9.9, false)]).ConfigureAwait(false);

        async Task CheckConcatAsync(string name, string[] parts, (double From, double To, bool Audible)[] windows)
        {
            string combined = await RecordingMedia.ConcatAsync(ffmpeg, ffprobe, parts,
                Path.Combine(concatDirectory, name + ".mp4"), cancellationToken).ConfigureAwait(false);
            var info = await RecordingMedia.ProbeAsync(ffprobe, combined, cancellationToken).ConfigureAwait(false);
            int audio = parts.Contains(tone, StringComparer.Ordinal) ? 1 : 0;
            if (info.AudioStreams != audio || Math.Abs(info.Duration - parts.Length * timing.Duration) > 0.1)
                throw new InvalidDataException($"一時停止区間連結の音声数・再生時間が不正です: {name}");
            if (parts.Any(path => !File.Exists(path))) throw new InvalidDataException("連結前の録画区間が失われました。");
            if (audio == 0) return;
            string decoded = Path.Combine(concatDirectory, name + ".s16");
            await MediaProcess.RunAsync(ffmpeg,
                ["-hide_banner", "-y", "-loglevel", "error", "-i", combined, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "s16le", decoded], cancellationToken).ConfigureAwait(false);
            byte[] pcm = await File.ReadAllBytesAsync(decoded, cancellationToken).ConfigureAwait(false);
            foreach (var window in windows)
            {
                double rms = Rms(pcm, window.From, window.To);
                if (window.Audible ? rms < 0.02 : rms > 0.002)
                    throw new InvalidDataException($"連結境界の音声が不正です: {name}, {window.From:F2}～{window.To:F2}s, RMS={rms:F5}");
            }
        }
    }

    private static double Rms(byte[] pcm, double from, double to)
    {
        var start = (int)(from * 48000);
        var end = (int)(to * 48000);
        if (end * 2 > pcm.Length) throw new InvalidDataException("AAC 復号音声が短すぎます。");
        double sum = 0;
        for (var sample = start; sample < end; sample++)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(sample * 2, 2)) / 32768d;
            sum += value * value;
        }
        return Math.Sqrt(sum / (end - start));
    }

    private static byte MaxPixel(byte[] data, int width, int x, int y, int sizeX, int sizeY)
    {
        byte max = 0;
        for (var row = y; row < y + sizeY; row++)
            for (var column = x; column < x + sizeX; column++)
                for (var channel = 0; channel < 3; channel++) max = Math.Max(max, data[(row * width + column) * 3 + channel]);
        return max;
    }
}
