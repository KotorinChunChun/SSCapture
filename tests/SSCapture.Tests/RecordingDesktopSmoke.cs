using System.Diagnostics;
using System.Media;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunRecordingDesktopSmoke(string[] args)
    {
        if (args.Length is not (5 or 6) || !int.TryParse(args[4], out int seconds) || seconds is < 10 or > 600)
        {
            Console.Error.WriteLine("使い方: --recording-desktop-smoke <ffmpeg> <ffprobe> <検証先> <録画秒数 10～600> [音声検証用WAV]");
            return 2;
        }
        bool verifyAudio = args.Length == 6;
        if (Screen.PrimaryScreen is not { WorkingArea.Width: >= 440, WorkingArea.Height: >= 320 } screen)
        {
            Console.Error.WriteLine("録画確認用の画面がありません。");
            return 1;
        }
        var area = screen.WorkingArea;
        using var host = new HitFixture(false)
        {
            Text = "SSCapture 録画確認",
            Bounds = new(area.Right - 440, area.Top + 80, 400, 240),
            BackColor = Color.Teal
        };
        Rectangle bounds = Rectangle.Empty;
        int result = 1;
        host.Shown += async (_, _) =>
        {
            SoundPlayer? tone = null;
            try
            {
                NativeSmokeInput.FocusFixture(host.Handle);
                await Task.Delay(200);
                bounds = host.RectangleToScreen(host.ClientRectangle);
                RequireUnobstructed();
                var selection = new CaptureSelection(bounds, null, OutputDestination.File);
                var settings = new AppSettings { IncludeCursor = false,
                    RecordSystemAudio = verifyAudio, RecordMicrophone = verifyAudio };
                if (verifyAudio)
                {
                    Check(RecordingService.GetMicrophones().Count > 0, "有効なマイクが見つかりません。");
                    tone = new SoundPlayer(Path.GetFullPath(args[5]));
                    tone.Load();
                    tone.PlayLooping();
                }
                await using var recording = new RecordingService(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3]);
                await recording.StartAsync(selection, settings);
                if (verifyAudio)
                {
                    Check(recording.SystemAudioEnabled && recording.MicrophoneEnabled, "PC音・マイクが同時に有効になりません。");
                    await WaitOnFixtureAsync(2);
                    await recording.SetMicrophoneAsync(false);
                    Check(recording.SystemAudioEnabled && !recording.MicrophoneEnabled, "マイクを独立してOFFにできません。");
                    await WaitOnFixtureAsync(1);
                    await recording.SetMicrophoneAsync(true);
                    await WaitOnFixtureAsync(seconds / 2 - 3);
                }
                else await WaitOnFixtureAsync(seconds / 2);
                await recording.PauseAsync();
                await WaitOnFixtureAsync(2);
                await recording.ResumeAsync();
                await WaitOnFixtureAsync(seconds - seconds / 2);
                string output = await recording.StopAsync();

                var media = await RecordingMedia.ProbeAsync(Path.GetFullPath(args[2]), output);
                Equal(bounds.Width, media.Width);
                Equal(bounds.Height, media.Height);
                Equal(verifyAudio ? 1 : 0, media.AudioStreams);
                Check(Math.Abs(media.Duration - seconds) < 5, $"出力の長さが不正です: {media.Duration:F2}秒");
                string frame = Path.Combine(Path.GetDirectoryName(output)!, "fixture-frame.png");
                await MediaProcess.RunAsync(Path.GetFullPath(args[1]),
                    ["-y", "-loglevel", "error", "-i", output, "-frames:v", "1", "-threads", "1", frame]);
                using var image = new Bitmap(frame);
                Color observed = image.GetPixel(image.Width / 2, image.Height / 2);
                Check(Math.Abs(observed.R - host.BackColor.R) < 6 && Math.Abs(observed.G - host.BackColor.G) < 6 &&
                    Math.Abs(observed.B - host.BackColor.B) < 6, "録画画像が専用ウィンドウの表示色と一致しません。");
                Console.WriteLine($"PASS RECORDING {media.Width}x{media.Height} {media.Duration:F2}s {media.VideoCodec} audio={media.AudioStreams} {output}");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL RECORDING " + ex.Message); }
            finally { tone?.Stop(); tone?.Dispose(); host.Close(); }
        };
        Application.Run(host);
        return result;

        async Task WaitOnFixtureAsync(int duration)
        {
            TimeSpan end = TimeSpan.FromSeconds(duration);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < end)
            {
                RequireUnobstructed();
                await Task.Delay(Math.Max(1, Math.Min(250, (int)(end - clock.Elapsed).TotalMilliseconds)));
            }
        }

        void RequireUnobstructed()
        {
            Check(bounds == host.RectangleToScreen(host.ClientRectangle), "録画確認ウィンドウの範囲が変わりました。");
            foreach (Point point in new Point[]
            {
                new(bounds.Left, bounds.Top), new(bounds.Right - 1, bounds.Top),
                new(bounds.Left, bounds.Bottom - 1), new(bounds.Right - 1, bounds.Bottom - 1),
                new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)
            })
                Check(FeedbackNative.WindowFromPoint(point) == host.Handle, "録画確認ウィンドウが隠れたため中断します。");
        }
    }
}
