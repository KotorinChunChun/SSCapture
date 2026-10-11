using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void RecordingTests()
    {
        Run("録画: 負座標・奇数寸法を保持", () =>
        {
            var bounds = new Rectangle(-1921, -241, 321, 241);
            var args = RecordingMedia.CaptureArguments(bounds, "C:\\test's folder\\video.mkv");
            Equal("-1921", args[Array.IndexOf(args, "-offset_x") + 1]);
            Equal("-241", args[Array.IndexOf(args, "-offset_y") + 1]);
            Equal("321x241", args[Array.IndexOf(args, "-video_size") + 1]);
            Equal(new Size(322, 242), RecordingMedia.PaddedSize(bounds));
            Check(args.Contains("-copyts") && !args.Contains("-start_at_zero"), "開始 PTS を保持する");
        });
        Run("録画: 音声 OFF はデバイス初期化対象なし", () =>
        {
            Equal(0, RecordingService.RequestedAudioSources(new AppSettings()).Count);
            Equal(2, RecordingService.RequestedAudioSources(new AppSettings { RecordSystemAudio = true, RecordMicrophone = true }).Count);
            Equal(false, RecordingService.RequestedAudioSources(new AppSettings { RecordMicrophone = true })[0]);
        });
        Run("録画: カーソル設定をgdigrab入力前のdraw_mouseに反映", () =>
        {
            foreach (bool include in new[] { false, true })
            {
                string[] args = RecordingMedia.CaptureArguments(new Rectangle(-100, 20, 320, 240), "video.mkv", include);
                int cursorArgument = Array.IndexOf(args, "-draw_mouse");
                Equal(include ? "1" : "0", args[cursorArgument + 1]);
                Check(cursorArgument < Array.IndexOf(args, "-i"), "gdigrab入力オプションとして指定されていない");
            }
        });
        Run("録画: 音声の前後 offset と path escaping", () =>
        {
            Check(RecordingMedia.AudioFilter(1, 0.25, 2, "a0").Contains("atrim=start=0.25"), "先行音声を trim");
            Check(RecordingMedia.AudioFilter(1, -0.25, 2, "a0").Contains("adelay=12000S:all=1"), "遅延音声を pad");
            var path = "C:\\日本語 ' [test]\\mask.png";
            var args = RecordingMedia.MuxArguments("v.mkv", ["a.wav"], path, 0, 2, "out.mp4");
            Check(args.Contains(path), "mask path は独立 ArgumentList 引数");
            Check(!args[Array.IndexOf(args, "-filter_complex") + 1].Contains(path), "filter 内へ path を埋め込まない");
            Check(args[Array.IndexOf(args, "-filter_complex") + 1].Contains("maskedmerge,format=gbrp"), "mask 合成は full-range planar RGB のまま行う");
        });
        Run("録画: 初期・途中・終了の無音を圧縮しない", () =>
        {
            using var stream = new MemoryStream();
            var timeline = new AudioTimeline(10, 2, 10_000_000, stream.Write);
            timeline.Append(new AudioPacket([1, 2, 3, 4], 0, 0, 12_000_000, 14_000_000));
            timeline.Append(new AudioPacket([5, 6, 7, 8], 1, 5, 17_000_000, 19_000_000));
            timeline.Finish(22_000_000);
            Equal(12L, timeline.FramesWritten);
            Equal(8L, timeline.SilenceFrames);
            Equal(1, timeline.Discontinuities);
            Equal((byte)1, stream.ToArray()[4]);
            Equal((byte)5, stream.ToArray()[14]);
        });
        Run("録画: 無効 timestamp の device/arrival fallback", () =>
        {
            using var stream = new MemoryStream();
            var timeline = new AudioTimeline(10, 2, 10_000_000, stream.Write);
            timeline.Append(new AudioPacket([1, 1], 0, 0, 11_000_000, 12_000_000));
            timeline.Append(new AudioPacket([2, 2], 4, 5, 0, 17_000_000));
            timeline.Append(new AudioPacket([3, 3], 4, 0, 0, 31_000_000));
            Equal(2, timeline.TimestampFallbacks);
            Equal(21L, timeline.FramesWritten);
            Equal((byte)2, stream.ToArray()[12]);
            Equal((byte)3, stream.ToArray()[40]);
        });
        Run("録画: silent flag と重複 packet を扱う", () =>
        {
            using var stream = new MemoryStream();
            var timeline = new AudioTimeline(10, 2, 10_000_000, stream.Write);
            timeline.Append(new AudioPacket([9, 9, 9, 9], 2, 0, 10_000_000, 12_000_000));
            timeline.Append(new AudioPacket([1, 1, 2, 2], 0, 1, 11_000_000, 13_000_000));
            Equal(3L, timeline.FramesWritten);
            Equal(1L, timeline.OverlapFrames);
            Check(stream.ToArray().SequenceEqual(new byte[] { 0, 0, 0, 0, 2, 2 }), "重複フレームを捨て silent はゼロ");
        });
        Run("録画: 停止時刻前の最終 packet 部分だけ保持", () =>
        {
            using var stream = new MemoryStream();
            var timeline = new AudioTimeline(10, 2, 10_000_000, stream.Write);
            timeline.Append(new AudioPacket([1, 1, 2, 2, 3, 3], 0, 0, 10_000_000, 13_000_000), 12_000_000);
            timeline.Append(new AudioPacket([4, 4], 0, 3, 13_000_000, 14_000_000), 12_000_000);
            Equal(2L, timeline.FramesWritten);
            Check(stream.ToArray().SequenceEqual(new byte[] { 1, 1, 2, 2 }), "stop 時刻以降は出力しない");
        });
        Run("録画: 自由形状外側と奇数 pad は黒", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "SSCapture-mask-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string? mask = null;
            try
            {
                mask = RecordingMedia.CreateMask(new CaptureSelection(new Rectangle(-11, -11, 11, 11),
                    [new(-9, -9), new(-2, -9), new(-9, -2)], OutputDestination.File), folder);
                using var image = new Bitmap(mask!);
                Equal(12, image.Width);
                Equal(Color.Black.ToArgb(), image.GetPixel(0, 0).ToArgb());
                Equal(Color.White.ToArgb(), image.GetPixel(3, 3).ToArgb());
                Equal(Color.Black.ToArgb(), image.GetPixel(11, 11).ToArgb());
            }
            finally
            {
                if (mask is not null) File.Delete(mask);
                Directory.Delete(folder);
            }
        });
    }
}
