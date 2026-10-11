using SSCapture.Recording;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void AudioToggleTests()
    {
        const long origin = 10_000_000;
        Run("音声切替: 初回ON前のバッファを消してoriginからの無音を保持", () =>
        {
            using var output = new MemoryStream();
            var gate = new AudioGate(10, origin, origin + 2_000_000);
            var timeline = new AudioTimeline(10, 4, origin, output.Write, gate);
            timeline.Append(AudioTogglePacket([1, 2, 3, 4, 5], 0, 0, origin + 1_000_000, origin + 6_000_000));
            Check(output.ToArray().SequenceEqual(AudioTogglePcm([0, 0, 2, 3, 4, 5])), "ON直前の音声または初期無音が不正");
            Equal(6L, timeline.FramesWritten); Equal(2L, timeline.SilenceFrames);
        });
        Run("音声切替: ONは切上げOFFは切下げて両チャンネルの部分フレームを消す", () =>
        {
            using var output = new MemoryStream();
            var gate = new AudioGate(10, origin, origin + 1_200_000);
            gate.SetEnabled(false, origin + 4_800_000);
            var timeline = new AudioTimeline(10, 4, origin, output.Write, gate);
            timeline.Append(AudioTogglePacket([1, 2, 3, 4, 5, 6], 0, 0, origin, origin + 6_000_000));
            Check(output.ToArray().SequenceEqual(AudioTogglePcm([0, 0, 3, 4, 0, 0])), "境界をまたぐPCMが残る");
            Equal(4L, timeline.SilenceFrames);
        });
        Run("音声切替: OFF境界をまたぐ遅延パケットは次のON後でもマスク", () =>
        {
            using var output = new MemoryStream();
            var gate = new AudioGate(10, origin, origin);
            var packet = gate.CapturePacket(AudioTogglePcm([1, 2, 3, 4, 5, 6]), 0, 0, origin, origin + 6_000_000);
            gate.SetEnabled(false, origin + 2_000_000);
            gate.SetEnabled(true, origin + 4_000_000);
            new AudioTimeline(10, 4, origin, output.Write, gate).Append(packet);
            Check(output.ToArray().SequenceEqual(AudioTogglePcm([1, 2, 0, 0, 5, 6])), "現在ONなので過去のOFF区間を通した");
        });
        Run("音声切替: 無効中のcallbackは生音をコピーせず同じ長さのsilent packet", () =>
        {
            using var output = new MemoryStream();
            var gate = new AudioGate(10, origin, origin);
            gate.SetEnabled(false, origin + 2_000_000);
            byte[] input = AudioTogglePcm([1, 2, 3, 4, 5]);
            var packet = gate.CapturePacket(input, 1, 0, origin, origin + 5_000_000);
            Check(packet.Data.All(value => value == 0) && (packet.Flags & 2) != 0 && (packet.Flags & 1) != 0, "生音またはフラグを保持した");
            Equal(input.Length, packet.Data.Length);
            gate.SetEnabled(true, origin + 5_000_000);
            var timeline = new AudioTimeline(10, 4, origin, output.Write, gate);
            timeline.Append(packet);
            Equal(5L, timeline.FramesWritten); Equal(5L, timeline.SilenceFrames);
            Check(output.ToArray().All(value => value == 0), "再ONで破棄済みの音が戻った");
        });
        Run("音声切替: 複数回ONOFFと同値設定でも時系列と振幅を維持", () =>
        {
            using var output = new MemoryStream();
            var gate = new AudioGate(10, origin, origin);
            gate.SetEnabled(true, origin + 500_000);
            gate.SetEnabled(false, origin + 1_000_000);
            gate.SetEnabled(false, origin + 1_500_000);
            gate.SetEnabled(true, origin + 2_000_000);
            gate.SetEnabled(false, origin + 3_000_000);
            gate.SetEnabled(true, origin + 4_000_000);
            var timeline = new AudioTimeline(10, 4, origin, output.Write, gate);
            timeline.Append(AudioTogglePacket([1, 2, 3, 4, 5, 6], 0, 0, origin, origin + 6_000_000));
            Check(output.ToArray().SequenceEqual(AudioTogglePcm([1, 0, 3, 0, 5, 6])), "ONOFFが音量または長さを変更した");
            Equal(6L, timeline.FramesWritten);
        });
        Run("音声切替: 無効timestampのarrival fallback確定位置へマスク", () =>
        {
            using var output = new MemoryStream();
            var gate = new AudioGate(10, origin, origin + 2_000_000);
            gate.SetEnabled(false, origin + 4_000_000);
            var timeline = new AudioTimeline(10, 4, origin, output.Write, gate);
            timeline.Append(AudioTogglePacket([1, 2, 3, 4, 5, 6], 4, 0, 0, origin + 6_000_000));
            Equal(1, timeline.TimestampFallbacks);
            Check(output.ToArray().SequenceEqual(AudioTogglePcm([0, 0, 3, 4, 0, 0])), "arrival fallbackでOFF音が残る");
        });
        Run("音声切替: device位置のfallbackにも過去の有効区間を適用", () =>
        {
            using var output = new MemoryStream();
            var gate = new AudioGate(10, origin, origin);
            var timeline = new AudioTimeline(10, 4, origin, output.Write, gate);
            timeline.Append(AudioTogglePacket([1], 0, 0, origin, origin + 1_000_000));
            gate.SetEnabled(false, origin + 2_000_000);
            gate.SetEnabled(true, origin + 4_000_000);
            timeline.Append(AudioTogglePacket([2, 3, 4, 5, 6], 4, 1, 0, origin + 7_000_000));
            Equal(1, timeline.TimestampFallbacks);
            Check(output.ToArray().SequenceEqual(AudioTogglePcm([1, 2, 0, 0, 5, 6])), "device fallbackでOFF音が残る");
        });
        Run("音声切替: 重複packetを除いた確定フレームへマスク", () =>
        {
            using var output = new MemoryStream();
            var gate = new AudioGate(10, origin, origin);
            var timeline = new AudioTimeline(10, 4, origin, output.Write, gate);
            timeline.Append(AudioTogglePacket([1, 2, 3], 0, 0, origin, origin + 3_000_000));
            gate.SetEnabled(false, origin + 3_000_000);
            gate.SetEnabled(true, origin + 5_000_000);
            timeline.Append(AudioTogglePacket([7, 8, 9, 10, 11], 0, 2, origin + 2_000_000, origin + 7_000_000));
            Equal(1L, timeline.OverlapFrames);
            Check(output.ToArray().SequenceEqual(AudioTogglePcm([1, 2, 3, 0, 0, 10, 11])), "重複分だけマスクがずれた");
        });
        Run("音声切替: 停止境界と終了paddingをミュートと併用", () =>
        {
            using var output = new MemoryStream();
            var gate = new AudioGate(10, origin, origin + 2_000_000);
            var timeline = new AudioTimeline(10, 4, origin, output.Write, gate);
            timeline.Append(AudioTogglePacket([1, 2, 3, 4, 5, 6], 0, 0, origin, origin + 6_000_000), origin + 4_000_000);
            timeline.Finish(origin + 6_000_000);
            Equal(6L, timeline.FramesWritten);
            Check(output.ToArray().SequenceEqual(AudioTogglePcm([0, 0, 3, 4, 0, 0])), "停止後の音声を保持した");
        });
        Run("音声切替: 1サンプル未満のON区間と48kHzの厳密境界", () =>
        {
            using var output = new MemoryStream();
            var gate = new AudioGate(48_000, origin, origin + 1);
            gate.SetEnabled(false, origin + 100);
            gate.SetEnabled(true, origin + 10_000); // Exactly 48 samples.
            var frames = Enumerable.Repeat((byte)7, 50).ToArray();
            var timeline = new AudioTimeline(48_000, 4, origin, output.Write, gate);
            timeline.Append(new AudioPacket(AudioTogglePcm(frames), 0, 0, origin, origin + 11_000));
            byte[] expected = new byte[50]; expected[48] = expected[49] = 7;
            Check(output.ToArray().SequenceEqual(AudioTogglePcm(expected)), "短いONまたは整数境界の端数が漏れた");
        });
    }

    private static AudioPacket AudioTogglePacket(byte[] values, int flags, long devicePosition, long qpc100ns, long received100ns)
        => new(AudioTogglePcm(values), flags, devicePosition, qpc100ns, received100ns);

    private static byte[] AudioTogglePcm(byte[] values)
        => values.SelectMany(value => Enumerable.Repeat(value, 4)).ToArray(); // Stereo 16-bit frames.
}
