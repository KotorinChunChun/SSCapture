using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void HotkeyBridgeTests()
    {
        Run("管理者用撮影キー補助は既定の撮影キーだけを順序を保って受け渡す", () =>
        {
            var settings = new AppSettings().ApplyDefaults();
            var allowed = HotkeyBinding.ReadAssignments(settings).Select(item => item.Binding).Where(HotkeyBridgeProtocol.Supports).ToArray();
            Check(allowed.Contains(HotkeyBinding.Parse("Win+Shift+S")!.Value), "通常撮影キーが許可されていない");
            Check(allowed.Contains(HotkeyBinding.Parse("PrintScreen")!.Value), "PrintScreenが許可されていない");
            Check(!HotkeyBridgeProtocol.Supports(HotkeyBinding.Parse("S")!.Value), "単独文字キーが渡る");
            Check(!HotkeyBridgeProtocol.Supports(HotkeyBinding.Parse("Ctrl+C")!.Value), "無関係なキーが渡る");
            using var stream = new MemoryStream();
            HotkeyBridgeProtocol.SendBindingsAsync(stream, allowed, CancellationToken.None).GetAwaiter().GetResult();
            Equal(5L + 2L * allowed.Length, stream.Length);
            stream.Position = 0;
            var received = HotkeyBridgeProtocol.ReadBindingsAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            Check(allowed.SequenceEqual(received), "撮影操作の割り当て順が変わった");
        });

        Run("管理者用撮影キー補助は不正キー・重複・切れた要求を拒否する", () =>
        {
            ReadFails([(byte)'S', (byte)'C', (byte)'H', (byte)'1', 1, (byte)'S', 0]);
            ReadFails([(byte)'S', (byte)'C', (byte)'H', (byte)'1', 2, (byte)'S', 9, (byte)'S', 9]);
            ReadFails([(byte)'S', (byte)'C', (byte)'H', (byte)'1', 1, (byte)'S']);
            ReadFails([(byte)'S', (byte)'C', (byte)'H', (byte)'1', 255]);

            static void ReadFails(byte[] bytes)
            {
                using var stream = new MemoryStream(bytes);
                try { _ = HotkeyBridgeProtocol.ReadBindingsAsync(stream, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (IOException) { return; }
                throw new InvalidOperationException("不正な補助の要求を受理した。");
            }
        });

        Run("管理者用補助の接続中も同じ文字の別キーは従来どおりに撮影できる", () =>
        {
            var bridge = HotkeyBinding.Parse("Win+Shift+S")!.Value;
            var local = HotkeyBinding.Parse("Ctrl+S")!.Value;
            var state = new HotkeyState([new(bridge, CaptureIntent.Screenshot), new(local, CaptureIntent.Recording)],
                KeyboardModifiers.LeftWindows | KeyboardModifiers.LeftShift);
            HashSet<HotkeyBinding> active = [bridge];
            Equal(false, state.Process('S', true, enabled: true, active).Capture);
            Equal(false, state.Process('S', false, enabled: true, active).Suppress);
            state.SynchronizeModifiers(KeyboardModifiers.LeftControl);
            Equal(CaptureIntent.Recording, state.Process('S', true, enabled: true, active).Intent);
            state.Process('S', false, enabled: true, active);
            state.SynchronizeModifiers(KeyboardModifiers.LeftWindows | KeyboardModifiers.LeftShift);
            Equal(true, state.Process('S', true, enabled: true).Capture);
        });
    }
}
