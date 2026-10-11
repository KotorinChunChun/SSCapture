using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SSCapture.Services;

internal static class RegionCoordinates
{
    internal const int MaximumDimension = 32768;
    internal const long MaximumPixels = 100_000_000;
    private static readonly StringComparer IdentityComparer = StringComparer.OrdinalIgnoreCase;

    internal static SavedRegion FromBounds(string name, Rectangle bounds, IReadOnlyList<DisplayLayout> layout,
        string? id = null, string shortcut = "")
    {
        ValidateLayout(layout);
        ValidateBounds(bounds, "撮影範囲");
        EnsureCovered(bounds, layout);
        DisplayLayout[] anchors = layout.Where(display => Contains(display.Bounds, bounds)).ToArray();
        if (anchors.Length > 1)
            throw new ArgumentException("同じ撮影範囲に複数のディスプレイが重なっています。ディスプレイの構成を確認してください。");
        DisplayLayout? anchor = anchors.SingleOrDefault();
        var region = new SavedRegion
        {
            Id = id ?? Guid.NewGuid().ToString("N"), Name = name, Shortcut = shortcut, Bounds = bounds,
            AnchorIdentity = anchor?.Identity,
            RelativeBounds = anchor is null ? Rectangle.Empty : new Rectangle(
                checked(bounds.X - anchor.Bounds.X), checked(bounds.Y - anchor.Bounds.Y), bounds.Width, bounds.Height),
            Layout = layout.ToArray()
        };
        region.Validate();
        return region;
    }

    internal static Rectangle Resolve(SavedRegion region, IReadOnlyList<DisplayLayout> layout)
    {
        ArgumentNullException.ThrowIfNull(region);
        region.Validate();
        ValidateLayout(layout);
        if (region.AnchorIdentity is string identity)
        {
            DisplayLayout saved = region.Layout.Single(display => IdentityComparer.Equals(display.Identity, identity));
            DisplayLayout? current = layout.SingleOrDefault(display => IdentityComparer.Equals(display.Identity, identity));
            if (current is null)
                throw new InvalidOperationException($"「{region.Name}」のディスプレイが接続されていないか、識別情報が変わりました。範囲を再登録してください。");
            if (current.Bounds.Size != saved.Bounds.Size || current.DpiX != saved.DpiX || current.DpiY != saved.DpiY)
                throw new InvalidOperationException($"「{region.Name}」の解像度または拡大率が変わりました。範囲を再登録してください。");
            long x = (long)current.Bounds.X + region.RelativeBounds.X;
            long y = (long)current.Bounds.Y + region.RelativeBounds.Y;
            if (x is < int.MinValue or > int.MaxValue || y is < int.MinValue or > int.MaxValue)
                throw new InvalidOperationException("移動後の撮影座標が扱える範囲を超えています。範囲を再登録してください。");
            var result = new Rectangle((int)x, (int)y, region.RelativeBounds.Width, region.RelativeBounds.Height);
            ValidateBounds(result, "移動後の撮影範囲");
            if (!Contains(current.Bounds, result))
                throw new InvalidOperationException("保存した撮影範囲がディスプレイからはみ出しています。範囲を再登録してください。");
            return result;
        }
        if (layout.Count != region.Layout.Length || region.Layout.Any(saved =>
                !layout.Any(current => IdentityComparer.Equals(saved.Identity, current.Identity)
                    && current.Bounds == saved.Bounds && current.DpiX == saved.DpiX && current.DpiY == saved.DpiY)))
            throw new InvalidOperationException($"「{region.Name}」は複数画面をまたぐ範囲です。保存時の画面配置・解像度・拡大率に戻すか、範囲を再登録してください。");
        EnsureCovered(region.Bounds, layout);
        return region.Bounds;
    }

    internal static void ValidateSaved(SavedRegion region)
    {
        if (string.IsNullOrWhiteSpace(region.Id) || region.Id.Length > 128 || region.Id.Any(char.IsControl))
            throw new ArgumentException("登録範囲の識別子が正しくありません。");
        if (string.IsNullOrWhiteSpace(region.Name) || region.Name.Length > 120 || region.Name.Any(char.IsControl))
            throw new ArgumentException("範囲名は1～120文字で入力してください。");
        if (region.Shortcut is null) throw new ArgumentException("範囲のショートカット指定が正しくありません。");
        _ = HotkeyBinding.Parse(region.Shortcut);
        ValidateLayout(region.Layout);
        ValidateBounds(region.Bounds, "保存した撮影範囲");
        EnsureCovered(region.Bounds, region.Layout);
        DisplayLayout[] containing = region.Layout.Where(display => Contains(display.Bounds, region.Bounds)).ToArray();
        if (region.AnchorIdentity is string identity)
        {
            DisplayLayout? anchor = region.Layout.SingleOrDefault(display => IdentityComparer.Equals(display.Identity, identity));
            if (anchor is null || containing.Length != 1 || !IdentityComparer.Equals(containing[0].Identity, identity))
                throw new ArgumentException("保存した撮影範囲と対象ディスプレイが一致しません。範囲を再登録してください。");
            Rectangle expected = new(checked(region.Bounds.X - anchor.Bounds.X), checked(region.Bounds.Y - anchor.Bounds.Y),
                region.Bounds.Width, region.Bounds.Height);
            if (region.RelativeBounds != expected)
                throw new ArgumentException("保存した撮影範囲の相対座標が一致しません。範囲を再登録してください。");
        }
        else if (containing.Length != 0 || region.RelativeBounds != Rectangle.Empty)
            throw new ArgumentException("複数画面の撮影範囲の保存情報が正しくありません。範囲を再登録してください。");
    }

    private static void ValidateLayout(IReadOnlyList<DisplayLayout> layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Count is < 1 or > 64)
            throw new ArgumentException("有効なディスプレイ構成がありません。");
        HashSet<string> identities = new(IdentityComparer);
        foreach (DisplayLayout display in layout)
        {
            if (display is null || string.IsNullOrWhiteSpace(display.Identity) || display.Identity.Length > 1024
                || string.IsNullOrWhiteSpace(display.Name) || display.Name.Length > 256)
                throw new ArgumentException("ディスプレイを識別できません。番号で代替せず、範囲の登録を中止します。");
            if (!identities.Add(display.Identity))
                throw new ArgumentException("ディスプレイの識別情報が重複しています。画面構成を確認してください。");
            if (display.DpiX is < 48 or > 960 || display.DpiY is < 48 or > 960)
                throw new ArgumentException("ディスプレイの拡大率を確認できません。");
            ValidateBounds(display.Bounds, "ディスプレイ", enforcePixelLimit: false);
        }
    }

    private static void ValidateBounds(Rectangle bounds, string label, bool enforcePixelLimit = true)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException($"{label}の幅と高さは1ピクセル以上にしてください。");
        if (bounds.Width > MaximumDimension || bounds.Height > MaximumDimension
            || (enforcePixelLimit && (long)bounds.Width * bounds.Height > MaximumPixels))
            throw new ArgumentException($"{label}が大きすぎます。各辺は{MaximumDimension:N0}ピクセル以下、撮影範囲は合計{MaximumPixels:N0}ピクセル以下にしてください。");
        if ((long)bounds.X + bounds.Width > int.MaxValue || (long)bounds.Y + bounds.Height > int.MaxValue)
            throw new ArgumentException($"{label}の座標が扱える範囲を超えています。");
    }

    private static bool Contains(Rectangle outer, Rectangle inner) => inner.Left >= outer.Left && inner.Top >= outer.Top
        && (long)inner.X + inner.Width <= (long)outer.X + outer.Width
        && (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;

    private static void EnsureCovered(Rectangle bounds, IReadOnlyList<DisplayLayout> layout)
    {
        // Sweep vertical strips and union their Y intervals. Bounding boxes alone would
        // incorrectly accept holes between staggered monitors; sums double-count mirrors.
        Rectangle[] pieces = layout.Select(display => Rectangle.Intersect(bounds, display.Bounds))
            .Where(piece => piece.Width > 0 && piece.Height > 0).ToArray();
        int[] edges = pieces.SelectMany(piece => new[] { piece.Left, piece.Right }).Distinct().Order().ToArray();
        long covered = 0;
        for (int index = 0; index + 1 < edges.Length; index++)
        {
            int left = edges[index], right = edges[index + 1];
            var intervals = pieces.Where(piece => piece.Left <= left && piece.Right >= right).OrderBy(piece => piece.Top).ToArray();
            long length = 0;
            int start = 0, end = 0;
            bool first = true;
            foreach (Rectangle piece in intervals)
            {
                if (first) { start = piece.Top; end = piece.Bottom; first = false; }
                else if (piece.Top > end) { length += (long)end - start; start = piece.Top; end = piece.Bottom; }
                else end = Math.Max(end, piece.Bottom);
            }
            if (!first) length += (long)end - start;
            covered += ((long)right - left) * length;
        }
        if (covered != (long)bounds.Width * bounds.Height)
            throw new ArgumentException("撮影範囲に画面外またはディスプレイ間の隙間が含まれています。範囲を指定し直してください。");
    }

    internal static IReadOnlyList<DisplayLayout> ReadDisplays()
    {
        List<DisplayLayout> result = [];
        Exception? error = null;
        MonitorCallback callback = (nint monitor, nint _, ref NativeRect bounds, nint data) =>
        {
            try
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                List<DisplayDevice> devices = [];
                for (uint index = 0; index < 256; index++)
                {
                    var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                    if (!EnumDisplayDevices(info.DeviceName, index, ref device, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */)) break;
                    if ((device.StateFlags & 1 /* DISPLAY_DEVICE_ACTIVE */) != 0) devices.Add(device);
                }
                if (devices.Count != 1 || string.IsNullOrWhiteSpace(devices[0].DeviceId)
                    || !devices[0].DeviceId.StartsWith(@"\\?\", StringComparison.Ordinal))
                    throw new InvalidOperationException($"{info.DeviceName}のモニターを一意に識別できません。ディスプレイの複製設定や接続を確認してください。");
                int status = GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out uint dpiY);
                if (status != 0) Marshal.ThrowExceptionForHR(status);
                // The app is per-monitor DPI aware, so these bounds are physical pixels.
                Rectangle physicalBounds = Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
                result.Add(new DisplayLayout(devices[0].DeviceId, $"{info.DeviceName} ({devices[0].DeviceString})",
                    physicalBounds, checked((int)dpiX), checked((int)dpiY)));
                return true;
            }
            catch (Exception ex) { error = ex; return false; }
        };
        bool success = EnumDisplayMonitors(0, 0, callback, 0);
        if (error is not null) throw new InvalidOperationException($"ディスプレイ情報を読み取れませんでした。{error.Message}", error);
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
        ValidateLayout(result);
        return result;
    }

    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect bounds, nint data);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? deviceName, uint index, ref DisplayDevice device, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
