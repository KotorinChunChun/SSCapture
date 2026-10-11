using System.Runtime.InteropServices;

namespace SSCapture.Services;

internal sealed class CompletionSound : ICompletionSound
{
    internal const int SampleRate = 24000;
    internal const int DurationMilliseconds = 120;
    internal const uint PlaybackFlags = 0x0004 /* SND_MEMORY */ | 0x0002 /* SND_NODEFAULT */; // SND_SYNC is zero.
    private readonly object gate = new();
    private readonly byte[] wave = CreateWave();
    private readonly Func<byte[], bool> play;
    private readonly Action<string>? warning;
    private bool playing, disposed;
    private string? lastWarning;
    private Task pending = Task.CompletedTask;

    internal CompletionSound(Func<byte[], bool>? play = null, Action<string>? warning = null)
    { this.play = play ?? PlayWave; this.warning = warning; }

    public string? LastWarning { get { lock (gate) return lastWarning; } }
    internal Task Pending { get { lock (gate) return pending; } }
    internal bool IsPlaying { get { lock (gate) return playing; } }

    public void Request()
    {
        lock (gate)
        {
            if (disposed || playing) return;
            playing = true;
            pending = Task.Run(() =>
            {
                try
                {
                    lock (gate) if (disposed) return;
                    if (!play(wave)) Report("効果音を再生できませんでした。");
                }
                catch (Exception ex) { Report("効果音を再生できません: " + ex.Message); }
                finally { lock (gate) playing = false; }
            });
        }
    }

    private void Report(string value)
    {
        lock (gate) lastWarning = value;
        try { warning?.Invoke(value); }
        catch (Exception) { }
    }

    internal static byte[] CreateWave()
    {
        int samples = SampleRate * DurationMilliseconds / 1000;
        using var stream = new MemoryStream(44 + samples * 2);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(SampleRate); writer.Write(SampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (int index = 0; index < samples; index++)
        {
            double seconds = index / (double)SampleRate;
            double value = Tone(seconds, 0.004, 0.050, 880) + Tone(seconds, 0.062, 0.112, 1174.66);
            writer.Write((short)Math.Round(value * 0.10 * short.MaxValue));
        }
        return stream.ToArray();

        static double Tone(double time, double start, double end, double frequency)
        {
            if (time <= start || time >= end) return 0;
            double envelope = Math.Min(1, Math.Min((time - start) / 0.008, (end - time) / 0.008));
            return Math.Sin((time - start) * frequency * Math.PI * 2) * envelope * envelope;
        }
    }

    private static bool PlayWave(byte[] bytes)
    {
        GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { return PlaySound(pinned.AddrOfPinnedObject(), 0, PlaybackFlags); }
        finally { pinned.Free(); }
    }

    public void Dispose() { lock (gate) disposed = true; }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(nint sound, nint module, uint flags);
}
