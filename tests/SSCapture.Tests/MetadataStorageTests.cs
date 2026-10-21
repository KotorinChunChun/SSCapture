using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    private static void MetadataStorageTests()
    {
        Run("XMPはPNGのパレット・透過・IDATを変更せずUnicodeをXMLで保持", () =>
        {
            foreach (var depth in Enum.GetValues<ScreenshotColorDepth>())
            {
                using var source = new Bitmap(40, 40); using (var g = Graphics.FromImage(source)) g.Clear(Color.FromArgb(255, 20, 70, 130));
                using var image = ScreenshotColors.Apply(source, depth);
                using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png); byte[] original = stream.ToArray();
                var metadata = SampleMetadata() with { ForegroundApplication = new("ok", Title: "<検証> & \"情報\"") };
                byte[] changed = PngMetadata.Embed(original, metadata);
                var before = ReadPngChunks(original); var after = ReadPngChunks(changed);
                string beforeHash = Convert.ToHexString(SHA256.HashData(JoinChunks(before.Where(c => c.Type != "iTXt"))));
                string afterHash = Convert.ToHexString(SHA256.HashData(JoinChunks(after.Where(c => c.Type != "iTXt"))));
                Equal(beforeHash, afterHash);
                var xmp = after.Single(c => c.Type == "iTXt").Data;
                Check(xmp.AsSpan().StartsWith("XML:com.adobe.xmp\0\0\0\0\0"u8), "非圧縮iTXt形式が違う");
                var xml = XDocument.Parse(Encoding.UTF8.GetString(xmp, 22, xmp.Length - 22));
                XNamespace ns = PngMetadata.Namespace;
                Equal(metadata.CaptureId, xml.Descendants(ns + "captureId").Single().Value);
                Equal(JsonSerializer.Serialize(metadata, CaptureMetadata.JsonOptions),
                    JsonSerializer.Serialize(JsonSerializer.Deserialize<CaptureMetadata>(xml.Descendants(ns + "contextJson").Single().Value, CaptureMetadata.JsonOptions), CaptureMetadata.JsonOptions));
                using var rendered = Image.FromStream(new MemoryStream(changed)); Equal(image.Size, rendered.Size);
                byte[] repeated = PngMetadata.Embed(changed, metadata); Equal(1, ReadPngChunks(repeated).Count(c => c.Type == "iTXt"));
                changed[20] ^= 1; PaddingRejected(() => { try { PngMetadata.Embed(changed, metadata); } catch (InvalidDataException e) { throw new ArgumentException(e.Message); } });
            }
        });
        Run("XMP・JSON・両方・なしを静止画ファイルだけに保存し同一IDを維持", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = new Bitmap(10, 10); var metadata = SampleMetadata();
            foreach (var mode in Enum.GetValues<CaptureMetadataMode>())
            {
                string folder = Path.Combine(directory, mode.ToString());
                using var router = new OutputRouter(Path.Combine(directory, "work"), _ => Task.CompletedTask);
                await router.ScreenshotAsync(image, OutputDestination.File, new AppSettings { SaveDirectory = folder, MetadataMode = mode }, "image.png", metadata: metadata);
                string path = Path.Combine(folder, "image.png");
                Equal(mode.HasFlag(CaptureMetadataMode.Json), File.Exists(Path.ChangeExtension(path, ".json")));
                Equal(mode.HasFlag(CaptureMetadataMode.PngXmp), ReadPngChunks(File.ReadAllBytes(path)).Any(c => c.Type == "iTXt"));
                if (mode.HasFlag(CaptureMetadataMode.Json))
                {
                    var restored = JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(Path.ChangeExtension(path, ".json")), CaptureMetadata.JsonOptions);
                    Equal(metadata.CaptureId, restored!.CaptureId);
                }
                await router.ScreenshotAsync(image, OutputDestination.Clipboard, new AppSettings { MetadataMode = mode }, metadata: metadata);
            }
        })));
        Run("既存JSONと画像の対を非上書きで命名し並列保存も衝突させない", () => WithOutputDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "image.json"), "既存");
            using var image = new Bitmap(10, 10); var metadata = SampleMetadata();
            var paths = Enumerable.Range(0, 8).AsParallel().Select(_ =>
            {
                using var copy = (Bitmap)image.Clone(); return CaptureStorage.SavePng(copy, directory, DateTimeOffset.Now, "image.png", metadata: metadata, metadataMode: CaptureMetadataMode.Both);
            }).ToArray();
            Equal(8, paths.Distinct().Count()); Check(paths.All(p => Path.GetFileName(p) != "image.png"), "既存JSONと組み合わさった");
            Equal("既存", File.ReadAllText(Path.Combine(directory, "image.json"))); Equal(0, Directory.GetFiles(directory, "*.lock").Length);
        }));
        Run("JSONだけの失敗はPNG・コピー・外部起動・履歴を繰り返さず再実行", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int copies = 0, launches = 0, history = 0, attempts = 0; CaptureDelivery? delivery = null;
            using var image = new Bitmap(10, 10);
            using var router = new OutputRouter(Path.Combine(directory, "work"), _ => { copies++; return Task.CompletedTask; }, launch: _ => launches++,
                captured: (_, _, _) => history++, delivered: d => delivery = d,
                beforeMetadataSave: _ => ++attempts == 1 ? Task.FromException(new IOException("JSON fixture")) : Task.CompletedTask);
            var settings = OutputSettings(directory) with { SaveDirectory = directory, MetadataMode = CaptureMetadataMode.Both, ImageEditorPath = CreateFakeEditor(directory) };
            try { await router.ScreenshotAsync(image, OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp, settings, "image.png", metadata: SampleMetadata()); }
            catch (IOException) { }
            Check(router.HasPendingRetry && delivery!.MetadataFailed && delivery.Completed.HasFlag(OutputDestination.File), "部分成功を保持しない");
            byte[] before = File.ReadAllBytes(Path.Combine(directory, "image.png"));
            await router.RetryFailedAsync();
            Equal(1, copies); Equal(1, launches); Equal(1, history); Equal(1, Directory.GetFiles(directory, "*.png").Length);
            Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "image.png"))), "PNGを再生成");
            Check(File.Exists(Path.Combine(directory, "image.json")) && !router.HasPendingRetry, "JSON再保存に失敗");
        })));
        Run("JSON再試行前の画像改変と同名JSONの割込みを拒否", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            foreach (bool changeImage in new[] { false, true })
            {
                string folder = Path.Combine(directory, changeImage.ToString());
                using var image = new Bitmap(10, 10); int attempts = 0;
                using var router = new OutputRouter(Path.Combine(directory, "work"), beforeMetadataSave: _ => ++attempts == 1 ? Task.FromException(new IOException("fixture")) : Task.CompletedTask);
                try { await router.ScreenshotAsync(image, OutputDestination.File, new AppSettings { SaveDirectory = folder, MetadataMode = CaptureMetadataMode.Json }, "image.png", metadata: SampleMetadata()); } catch (IOException) { }
                string target = Path.Combine(folder, changeImage ? "image.png" : "image.json");
                File.WriteAllText(target, "別データ");
                bool rejected = false; try { await router.RetryFailedAsync(); } catch (IOException) { rejected = true; }
                Check(rejected, "変更先に情報を追記"); Equal("別データ", File.ReadAllText(target)); Equal(1, Directory.GetFiles(folder, "*.png").Length);
            }
        })));
        Run("撮影情報設定はFile依存で保持し出力プリセットにも保存", () =>
        {
            using var form = NewSettings(new AppSettings { MetadataMode = CaptureMetadataMode.Both });
            var combo = (ComboBox)form.Controls.Find("MetadataMode", true).Single();
            Equal(3, combo.SelectedIndex); Check(!combo.Enabled, "File OFFで有効");
            ((CheckBox)form.Controls.Find("FileOutput", true).Single()).Checked = true;
            Check(combo.Enabled, "File ONで無効");
            var settings = new AppSettings { MetadataMode = CaptureMetadataMode.Both };
            Equal(CaptureMetadataMode.Both, CapturePreset.FromSettings("資料", settings).Apply(new AppSettings()).MetadataMode);
            PaddingRejected(() => (settings with { MetadataMode = (CaptureMetadataMode)4 }).Validate());
        });
    }
    private static List<(string Type, byte[] Data)> ReadPngChunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        for (int offset = 8; offset < png.Length;)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            string type = Encoding.ASCII.GetString(png, offset + 4, 4); chunks.Add((type, png.AsSpan(offset + 8, length).ToArray()));
            offset += length + 12;
        }
        return chunks;
    }
    private static byte[] JoinChunks(IEnumerable<(string Type, byte[] Data)> chunks) => chunks.SelectMany(c => Encoding.ASCII.GetBytes(c.Type).Concat(c.Data)).ToArray();
}
