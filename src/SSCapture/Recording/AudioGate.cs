namespace SSCapture.Recording;

/// <summary>Session-clock intervals in which complete native PCM frames may be retained.</summary>
internal sealed class AudioGate
{
    private readonly object sync = new();
    private readonly int sampleRate;
    private readonly long origin100ns;
    private readonly List<(long Start, long End)> intervals = [];
    private long lastTransition100ns;
    private int enabled;

    internal AudioGate(int sampleRate, long origin100ns, long enabledFrom100ns)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.sampleRate = sampleRate;
        this.origin100ns = origin100ns;
        lastTransition100ns = Math.Max(origin100ns, enabledFrom100ns);
        intervals.Add((FrameAt(lastTransition100ns, roundUp: true), long.MaxValue));
        enabled = 1;
    }

    internal bool IsEnabled => Volatile.Read(ref enabled) != 0;

    internal void SetEnabled(bool value, long timestamp100ns)
    {
        lock (sync)
        {
            if (IsEnabled == value) return;
            if (timestamp100ns < lastTransition100ns) throw new ArgumentOutOfRangeException(nameof(timestamp100ns));
            if (value) intervals.Add((FrameAt(timestamp100ns, roundUp: true), long.MaxValue));
            else
            {
                var active = intervals[^1];
                intervals[^1] = (active.Start, Math.Max(active.Start, FrameAt(timestamp100ns, roundUp: false)));
            }
            lastTransition100ns = timestamp100ns;
            Volatile.Write(ref enabled, value ? 1 : 0);
        }
    }

    internal AudioPacket CapturePacket(ReadOnlySpan<byte> buffer, int flags, long devicePosition, long qpc100ns, long received100ns)
    {
        // Never copy microphone/loopback bytes while muted. Keep the packet's
        // duration and timestamps so muting cannot compress the recording.
        bool silent = !IsEnabled || (flags & 2) != 0;
        byte[] data = silent ? new byte[buffer.Length] : buffer.ToArray();
        return new(data, silent ? flags | 2 : flags, devicePosition, qpc100ns, received100ns);
    }

    internal void Write(byte[] data, int offset, int frames, long firstFrame, int blockAlign,
        Action<byte[], int, int> write, Action<long> writeSilence)
    {
        (long Start, long End)[] active;
        lock (sync) active = intervals.ToArray();
        // File I/O must not hold the switch lock: a slow disk cannot delay mute
        // becoming visible to the callback and to subsequent queued packets.
        long end = checked(firstFrame + frames), cursor = firstFrame;
        foreach (var interval in active)
        {
            if (interval.End <= cursor) continue;
            if (interval.Start >= end) break;
            long start = Math.Max(cursor, interval.Start);
            if (start > cursor) writeSilence(start - cursor);
            long stop = Math.Min(end, interval.End);
            if (stop > start)
                write(data, checked(offset + (int)(start - firstFrame) * blockAlign), checked((int)(stop - start) * blockAlign));
            cursor = Math.Max(start, stop);
            if (cursor == end) break;
        }
        if (cursor < end) writeSilence(end - cursor);
    }

    // A partially enabled frame is silenced: ON rounds up, OFF rounds down.
    // Decimal arithmetic avoids rounding a precise sample boundary to its neighbour.
    private long FrameAt(long timestamp100ns, bool roundUp)
    {
        decimal frame = ((decimal)timestamp100ns - origin100ns) * sampleRate / 10_000_000m;
        return Math.Max(0, checked((long)(roundUp ? decimal.Ceiling(frame) : decimal.Floor(frame))));
    }
}
