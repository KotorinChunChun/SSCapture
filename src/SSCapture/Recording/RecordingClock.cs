using System.Diagnostics;

namespace SSCapture.Recording;

internal sealed record RecordingClock(long Origin100ns, double OriginUnixSeconds)
{
    internal static long Now100ns => (long)(Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency));

    internal static RecordingClock Start()
    {
        // Pair the wall clock used by gdigrab with the WASAPI QPC clock, bracketing the UTC read.
        var before = Now100ns;
        var utc = DateTimeOffset.UtcNow;
        var after = Now100ns;
        return new(before + (after - before) / 2, (utc - DateTimeOffset.UnixEpoch).Ticks / 10_000_000d);
    }

    internal double UnixSeconds(long qpc100ns) => OriginUnixSeconds + (qpc100ns - Origin100ns) / 10_000_000d;
}

internal sealed record AudioPacket(byte[] Data, int Flags, long DevicePosition, long Qpc100ns, long Received100ns);

/// <summary>Places native-format packets on a session-wide timeline, including initial and intermittent silence.</summary>
internal sealed class AudioTimeline(int sampleRate, int blockAlign, long origin100ns, Action<byte[], int, int> write, AudioGate? audioGate = null)
{
    private readonly byte[] zeros = new byte[Math.Max(blockAlign, 16_384 / blockAlign * blockAlign)];
    private long? deviceAnchor;
    private long anchorQpc;
    private long previousTimestamp;
    internal long FramesWritten { get; private set; }
    internal int TimestampFallbacks { get; private set; }
    internal int Discontinuities { get; private set; }
    internal long SilenceFrames { get; private set; }
    internal long OverlapFrames { get; private set; }

    internal void Append(AudioPacket packet, long? end100ns = null)
    {
        if (packet.Data.Length % blockAlign != 0) throw new InvalidDataException("音声パケットのフレーム境界が不正です。");
        var frames = packet.Data.Length / blockAlign;
        if (frames == 0) return;
        if ((packet.Flags & 1) != 0) Discontinuities++;
        var valid = (packet.Flags & 4) == 0 && packet.Qpc100ns > 0 &&
                    packet.Qpc100ns >= origin100ns - 10_000_000L &&
                    packet.Qpc100ns <= packet.Received100ns + 1_000_000L &&
                    packet.Qpc100ns >= packet.Received100ns - 50_000_000L &&
                    packet.Qpc100ns > previousTimestamp;
        long timestamp;
        if (valid)
        {
            timestamp = packet.Qpc100ns;
            deviceAnchor = packet.DevicePosition;
            anchorQpc = timestamp;
        }
        else
        {
            TimestampFallbacks++;
            timestamp = packet.Received100ns - FramesToTicks(frames);
            if (deviceAnchor is { } anchor && packet.DevicePosition >= anchor)
            {
                var deviceTime = anchorQpc + FramesToTicks(packet.DevicePosition - anchor);
                if (Math.Abs(deviceTime - timestamp) <= 2_500_000L) timestamp = deviceTime;
            }
            // Do not anchor an invalid device position: an arrival-clock fallback keeps long gaps intact.
        }
        var target = (long)Math.Round((timestamp - origin100ns) * (sampleRate / 10_000_000d));
        previousTimestamp = timestamp;
        if (end100ns is { } end)
        {
            var lastFrame = Math.Max(0, (long)Math.Floor((end - origin100ns) * (sampleRate / 10_000_000d)));
            if (target >= lastFrame) return;
            frames = (int)Math.Min(frames, Math.Max(0, lastFrame - target));
        }
        if (target > FramesWritten) PadTo(target);
        var skip = Math.Min((long)frames, Math.Max(0, FramesWritten - target));
        OverlapFrames += skip;
        var count = checked((int)((frames - skip) * blockAlign));
        if (count == 0) return;
        if ((packet.Flags & 2) != 0) WriteSilence(frames - skip);
        else if (audioGate is not null)
            audioGate.Write(packet.Data, checked((int)skip * blockAlign), checked((int)(frames - skip)), FramesWritten, blockAlign, write, WriteSilence);
        else write(packet.Data, checked((int)skip * blockAlign), count);
        FramesWritten += frames - skip;
    }

    internal void Finish(long end100ns) => PadTo(Math.Max(0, (long)Math.Ceiling((end100ns - origin100ns) * (sampleRate / 10_000_000d))));

    private long FramesToTicks(long frames) => (long)Math.Round(frames * (10_000_000d / sampleRate));
    private void PadTo(long target)
    {
        if (target <= FramesWritten) return;
        var gap = target - FramesWritten;
        WriteSilence(gap);
        FramesWritten = target;
    }
    private void WriteSilence(long frames)
    {
        SilenceFrames += frames;
        var bytes = checked(frames * blockAlign);
        while (bytes > 0)
        {
            var size = (int)Math.Min(bytes, zeros.Length);
            write(zeros, 0, size);
            bytes -= size;
        }
    }
}
