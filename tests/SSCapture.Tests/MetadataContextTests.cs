using System.Diagnostics;
using System.Text.Json;
using SSCapture.Services;

namespace SSCapture.Tests;
internal static partial class Program
{
    private static CaptureMetadata SampleMetadata(int padding = 0) => CaptureMetadata.Create(new(-1000, -200, 100, 80), padding,
        DateTimeOffset.Parse("2026-10-21T12:00:00+09:00"), new(DateTimeOffset.Now, new(-1010, -180), true, false),
        new(DateTimeOffset.Now, new("ok", AutomationId: "focus", Name: "入力"), new("unavailable", "elementNotAvailable"),
            new("partial", "accessDenied", 123, Class: "class", Title: "ウィンドウ"), new("unavailable", "windowNotAvailable")));

    private static void MetadataContextTests()
    {
        Run("撮影情報は負座標・範囲外位置を切り詰めず余白分だけ出力座標を変える", () =>
        {
            var data = SampleMetadata(12);
            Equal(new PixelPoint(-10, 20), data.Pointer.RelativeCapture); Equal(new PixelPoint(2, 32), data.Pointer.RelativeOutput);
            Check(!data.Pointer.InsideCapture && data.Pointer.Visible && !data.Pointer.Drawn, "位置記録と描画の独立性");
            Equal(new PixelSize(124, 104), data.OutputSize);
            var changed = data.WithPadding(20); Equal(data.CaptureId, changed.CaptureId); Equal(new PixelPoint(10, 40), changed.Pointer.RelativeOutput);
        });
        Run("撮影情報のDTO往復は未取得理由・null・取得時刻・フォーカスを維持", () =>
        {
            var data = SampleMetadata(); string json = JsonSerializer.Serialize(data, CaptureMetadata.JsonOptions);
            Equal(data, JsonSerializer.Deserialize<CaptureMetadata>(json, CaptureMetadata.JsonOptions));
            using var doc = JsonDocument.Parse(json); Equal("focus", doc.RootElement.GetProperty("focusedElement").GetProperty("automationId").GetString());
            Equal(JsonValueKind.Null, doc.RootElement.GetProperty("pointerElement").GetProperty("name").ValueKind);
            Check(!json.Contains("ValuePattern") && !json.Contains("TextPattern"), "本文を記録");
        });
        Run("記録OFFではUIAワーカーの起動処理を呼ばない", () => RunOutputTask(async () =>
        {
            using var client = new CaptureContextClient(() => throw new InvalidOperationException("起動禁止"));
            var context = await client.ObserveAsync(Point.Empty, false);
            Equal("disabled", context.FocusedElement.Reason); Check(!client.IsStarted, "OFFで子プロセス起動");
        }));
        Run("停止したUIAワーカーは親の期限で打ち切り所有子プロセスを破棄", () => RunOutputTask(async () =>
        {
            string dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
            using var client = new CaptureContextClient(() =>
            {
                var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("--context-stall"); return start;
            });
            var watch = Stopwatch.StartNew(); var context = await client.ObserveAsync(Point.Empty, timeoutMilliseconds: 250);
            Equal("timeout", context.FocusedElement.Reason); Check(watch.ElapsedMilliseconds < 2000, "UIA待機が期限を超えた");
            Check(!client.IsStarted, "停止workerを保持");
        }));
    }

    private static int ContextSmoke(string directory)
    {
        Directory.CreateDirectory(directory);
        using var form = new Form { Text = WindowTitle.Create("撮影情報検証"), StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Right - 700, 100, 650, 360), TopMost = true };
        var focus = new TextBox { Name = "ContextFocus", AccessibleName = "フォーカス確認", Location = new(30, 50), Width = 400 };
        var pointer = new Button { Name = "ContextPointer", AccessibleName = "ポインタ確認", Text = "ポインタ確認", Bounds = new(30, 120, 300, 70) };
        form.Controls.AddRange([focus, pointer]); Exception? failure = null;
        form.Shown += (_, _) => form.BeginInvoke((Action)(async () =>
        {
            try
            {
                form.Activate(); focus.Focus(); form.Update();
                using var client = new CaptureContextClient();
                Point point = pointer.PointToScreen(new Point(20, 20));
                var watch = Stopwatch.StartNew();
                var context = await client.ObserveAsync(point, timeoutMilliseconds: 1500);
                long first = watch.ElapsedMilliseconds; watch.Restart();
                var second = await client.ObserveAsync(point);
                File.WriteAllText(Path.Combine(directory, "context.json"), JsonSerializer.Serialize(context, CaptureMetadata.JsonOptions));
                File.WriteAllText(Path.Combine(directory, "second.json"), JsonSerializer.Serialize(second, CaptureMetadata.JsonOptions));
                File.WriteAllText(Path.Combine(directory, "timing.txt"), $"first={first}ms; second={watch.ElapsedMilliseconds}ms");
                Equal("ok", context.FocusedElement.Status); Equal("フォーカス確認", context.FocusedElement.Name);
                Equal("ポインタ確認", context.PointerElement.Name); Equal(Environment.ProcessId, context.ForegroundApplication.ProcessId);
                Equal("ok", second.FocusedElement.Status);
                Console.WriteLine("CONTEXT PASS " + File.ReadAllText(Path.Combine(directory, "timing.txt")));
            }
            catch (Exception ex) { failure = ex; }
            finally { form.Close(); }
        }));
        Application.Run(form); if (failure is null) return 0; Console.Error.WriteLine(failure); return 1;
    }
}
