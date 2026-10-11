using System.Text.Json;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static readonly DisplayLayout[] RegionLayout =
    [
        new("monitor-left", "左", new(-1920, -120, 1920, 1080), 120, 120),
        new("monitor-main", "中央", new(0, 0, 2560, 1440), 144, 144)
    ];

    static partial void RegionTests()
    {
        Run("登録範囲は負の座標を画面内相対ピクセルとして保持", () =>
        {
            Rectangle bounds = new(-1880, -80, 500, 300);
            SavedRegion region = RegionCoordinates.FromBounds("進捗", bounds, RegionLayout, shortcut: "Ctrl+F8");
            Equal("monitor-left", region.AnchorIdentity);
            Equal(new Rectangle(40, 40, 500, 300), region.RelativeBounds);
            Equal(bounds, RegionCoordinates.Resolve(region, RegionLayout));
            Equal("Ctrl+F8", region.Shortcut);
            Check(!ReferenceEquals(RegionLayout, region.Layout), "配列の参照を共有している");
        });
        Run("単画面範囲は同じモニターの配置と表示番号の変更に追従", () =>
        {
            SavedRegion region = RegionCoordinates.FromBounds("進捗", new(-1800, 0, 400, 220), RegionLayout);
            DisplayLayout[] moved = [RegionLayout[1], RegionLayout[0] with { Name = "DISPLAY9", Bounds = new(2560, 300, 1920, 1080) }];
            Equal(new Rectangle(2680, 420, 400, 220), RegionCoordinates.Resolve(region, moved));
            Equal(new Rectangle(2680, 420, 400, 220), RegionCoordinates.Resolve(region, [moved[1]]));
        });
        Run("同型別モニターへの置換・未接続・解像度・DPI変更で再撮影を拒否", () =>
        {
            SavedRegion region = RegionCoordinates.FromBounds("進捗", new(-1800, 0, 400, 220), RegionLayout);
            RegionReject(() => RegionCoordinates.Resolve(region, [RegionLayout[1]]));
            RegionReject(() => RegionCoordinates.Resolve(region, [RegionLayout[0] with { Identity = "different-device" }]));
            RegionReject(() => RegionCoordinates.Resolve(region, [RegionLayout[0] with { Bounds = new(-1920, -120, 1920, 1200) }]));
            RegionReject(() => RegionCoordinates.Resolve(region, [RegionLayout[0] with { DpiX = 144, DpiY = 144 }]));
            // Interface paths are case insensitive; friendly labels are not identities.
            Equal(region.Bounds, RegionCoordinates.Resolve(region, [RegionLayout[0] with { Identity = "MONITOR-LEFT", Name = "変更した名前" }]));
        });
        Run("識別情報の欠落・重複・ミラー領域・不正DPIを拒否", () =>
        {
            var bounds = new Rectangle(1, 1, 100, 100);
            RegionReject(() => RegionCoordinates.FromBounds("範囲", bounds, []));
            RegionReject(() => RegionCoordinates.FromBounds("範囲", bounds, [RegionLayout[1] with { Identity = "" }]));
            RegionReject(() => RegionCoordinates.FromBounds("範囲", bounds, [RegionLayout[1], RegionLayout[1] with { Identity = "MONITOR-MAIN" }]));
            RegionReject(() => RegionCoordinates.FromBounds("範囲", bounds, [RegionLayout[1], RegionLayout[1] with { Identity = "mirror" }]));
            RegionReject(() => RegionCoordinates.FromBounds("範囲", bounds, [RegionLayout[1] with { DpiX = 0 }]));
        });
        Run("複数画面をまたぐ範囲は保存構成の並び順以外を厳密に確認", () =>
        {
            var bounds = new Rectangle(-200, 0, 400, 300);
            SavedRegion region = RegionCoordinates.FromBounds("両画面", bounds, RegionLayout);
            Equal<string?>(null, region.AnchorIdentity);
            Equal(bounds, RegionCoordinates.Resolve(region, RegionLayout.Reverse().ToArray()));
            RegionReject(() => RegionCoordinates.Resolve(region, [RegionLayout[0], RegionLayout[1] with { Bounds = new(0, 100, 2560, 1440) }]));
            RegionReject(() => RegionCoordinates.Resolve(region, [RegionLayout[0], RegionLayout[1] with { DpiY = 120 }]));
            RegionReject(() => RegionCoordinates.Resolve(region, [RegionLayout[0]]));
            RegionReject(() => RegionCoordinates.Resolve(region, [..RegionLayout, new("new", "新規", new(2560, 0, 800, 600), 96, 96)]));
        });
        Run("矩形内の隙間・段違い画面外・空領域を含む登録を拒否", () =>
        {
            RegionReject(() => RegionCoordinates.FromBounds("空", Rectangle.Empty, RegionLayout));
            RegionReject(() => RegionCoordinates.FromBounds("空", new(0, 0, -1, 100), RegionLayout));
            RegionReject(() => RegionCoordinates.FromBounds("外", new(-1930, 0, 100, 100), RegionLayout));
            RegionReject(() => RegionCoordinates.FromBounds("段差", new(-100, -100, 200, 200), RegionLayout));
            DisplayLayout[] gap = [new("a", "A", new(0, 0, 400, 400), 96, 96), new("b", "B", new(410, 0, 400, 400), 96, 96)];
            RegionReject(() => RegionCoordinates.FromBounds("隙間", new(300, 100, 200, 100), gap));
            DisplayLayout[] tiled = [new("a", "A", new(0, 0, 400, 400), 96, 96), new("b", "B", new(400, 0, 400, 200), 96, 96), new("c", "C", new(400, 200, 400, 200), 96, 96)];
            Equal(new Rectangle(300, 100, 200, 200), RegionCoordinates.FromBounds("隙間なし", new(300, 100, 200, 200), tiled).Bounds);
        });
        Run("int座標オーバーフロー・過大寸法・過大面積を画像生成前に拒否", () =>
        {
            RegionReject(() => RegionCoordinates.FromBounds("外", new(int.MaxValue - 20, 0, 100, 100), RegionLayout));
            RegionReject(() => RegionCoordinates.FromBounds("外", new(0, int.MaxValue - 20, 100, 100), RegionLayout));
            RegionReject(() => RegionCoordinates.FromBounds("大", new(0, 0, 32769, 1), RegionLayout));
            DisplayLayout[] huge = [new("huge", "大画面", new(0, 0, 20000, 20000), 96, 96)];
            RegionReject(() => RegionCoordinates.FromBounds("大", new(0, 0, 20000, 20000), huge));
            DisplayLayout[] extreme = [new("edge", "端", new(int.MinValue, int.MinValue, 400, 400), 96, 96)];
            var tiny = new Rectangle(int.MinValue + 10, int.MinValue + 10, 1, 1);
            Equal(tiny, RegionCoordinates.Resolve(RegionCoordinates.FromBounds("1px", tiny, extreme), extreme));
        });
        Run("保存済み範囲の相対座標・名前・識別子破損を拒否", () =>
        {
            SavedRegion region = RegionCoordinates.FromBounds("範囲", new(100, 100, 200, 300), RegionLayout);
            RegionReject(() => (region with { RelativeBounds = new(101, 100, 200, 300) }).Validate());
            RegionReject(() => (region with { AnchorIdentity = "missing" }).Validate());
            RegionReject(() => (region with { AnchorIdentity = null }).Validate());
            RegionReject(() => (region with { Name = " " }).Validate());
            RegionReject(() => (region with { Id = "" }).Validate());
            RegionReject(() => (region with { Layout = null! }).Validate());
            RegionReject(() => (region with { Layout = [null!] }).Validate());
        });
        Run("前回範囲をJSON原子保存し再起動後の座標を復元", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "last-region.json");
            var store = new RegionStore(path);
            Equal<SavedRegion?>(null, store.Load(out string? warning)); Equal<string?>(null, warning);
            SavedRegion first = RegionCoordinates.FromBounds("前回", new(-1600, 30, 600, 450), RegionLayout);
            store.Save(first);
            SavedRegion loaded = new RegionStore(path).Load(out warning)!;
            Equal<string?>(null, warning); Equal(first, loaded with { Layout = first.Layout });
            Check(first.Layout.SequenceEqual(loaded.Layout), "ディスプレイ情報の往復不一致");
            SavedRegion second = first with { Name = "更新" }; store.Save(second);
            Equal("更新", store.Load(out warning)!.Name); Equal<string?>(null, warning);
            Equal(1, Directory.GetFiles(directory).Length);
        }));
        Run("破損した範囲ファイルを保持して読込警告・未登録状態にする", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "last-region.json");
            var region = RegionCoordinates.FromBounds("範囲", new(1, 1, 100, 100), RegionLayout);
            foreach (string invalid in new[] { "{bad", "null", "{}", JsonSerializer.Serialize(region with { Layout = null! }),
                JsonSerializer.Serialize(region with { RelativeBounds = Rectangle.Empty }) })
            {
                File.WriteAllText(path, invalid);
                Equal<SavedRegion?>(null, new RegionStore(path).Load(out string? warning));
                Check(!string.IsNullOrWhiteSpace(warning), "破損時の警告なし"); Equal(invalid, File.ReadAllText(path));
            }
        }));
        Run("範囲の保存検証失敗・置換失敗は前回データを保持し一時ファイルだけ除去", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "last-region.json");
            var store = new RegionStore(path);
            SavedRegion region = RegionCoordinates.FromBounds("範囲", new(1, 1, 100, 100), RegionLayout);
            store.Save(region); string initial = File.ReadAllText(path);
            RegionReject(() => store.Save(region with { Name = "" }));
            Equal(initial, File.ReadAllText(path));
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failedSave = false;
                try { store.Save(region with { Name = "置換禁止" }); } catch (IOException) { failedSave = true; }
                Check(failedSave, "置換失敗を検出できない");
            }
            Equal(initial, File.ReadAllText(path)); Equal(1, Directory.GetFiles(directory).Length);
        }));
        if (Environment.GetEnvironmentVariable("SSCAPTURE_REGION_DISPLAY_SMOKE") == "1")
            Run("実機ディスプレイの物理座標・識別情報・DPIを非撮影で列挙", () =>
            {
                IReadOnlyList<DisplayLayout> displays = RegionCoordinates.ReadDisplays();
                Check(displays.Count > 0 && displays.All(display => display.Identity.StartsWith(@"\\?\", StringComparison.Ordinal)), "インターフェイス識別子なし");
                foreach (DisplayLayout display in displays)
                    Console.WriteLine($"DISPLAY: {display.Bounds.X},{display.Bounds.Y} {display.Bounds.Width}x{display.Bounds.Height} DPI={display.DpiX}/{display.DpiY}; stable identity present");
            });
    }

    private static void RegionReject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("不正な撮影範囲を受け入れました。");
    }
}
