using System.Diagnostics;
using System.Security.Principal;

namespace SSCapture.Services;

internal static class HotkeyBridgeProtocol
{
    internal const byte Ready = 0x52;
    internal const byte Activate = 0x41;
    internal const string Executable = "SSCapture.HotkeyBridge.exe";
    internal static string InstalledPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "KotorinToys", "SSCaptureHotkeyBridge", Executable);

    // The elevated process accepts only these capture chords. Arbitrary keys and
    // application, preset, path, and command data stay in the desktop process.
    private const HotkeyModifiers WinShift = HotkeyModifiers.Windows | HotkeyModifiers.Shift;
    private static readonly HashSet<HotkeyBinding> allowed = new[]
    {
        new HotkeyBinding(0x2C, HotkeyModifiers.None),
        new HotkeyBinding('S', HotkeyModifiers.Windows | HotkeyModifiers.Control | HotkeyModifiers.Alt),
        new HotkeyBinding('S', WinShift | HotkeyModifiers.Control)
    }.Concat("SRQTL0123456789".Select(key => new HotkeyBinding(key, WinShift))).ToHashSet();

    internal static bool Supports(HotkeyBinding binding) => allowed.Contains(binding);
    internal static string PipeName(int session, SecurityIdentifier user) => $"KotorinToys.SSCapture.Hotkeys.v1.{session}.{user.Value}";
    internal static string CurrentPipeName
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            using var identity = WindowsIdentity.GetCurrent();
            return PipeName(process.SessionId, identity.User!);
        }
    }

    internal static async Task SendBindingsAsync(Stream pipe, IReadOnlyList<HotkeyBinding> bindings, CancellationToken token)
    {
        Validate(bindings);
        byte[] bytes = new byte[5 + bindings.Count * 2];
        "SCH1"u8.CopyTo(bytes);
        bytes[4] = (byte)bindings.Count;
        for (int index = 0; index < bindings.Count; index++)
        {
            bytes[5 + index * 2] = (byte)bindings[index].VirtualKey;
            bytes[6 + index * 2] = (byte)bindings[index].Modifiers;
        }
        await pipe.WriteAsync(bytes, token);
    }

    internal static async Task<HotkeyBinding[]> ReadBindingsAsync(Stream pipe, CancellationToken token)
    {
        byte[] header = new byte[5];
        await pipe.ReadExactlyAsync(header, token);
        int count = header[4];
        if (!header.AsSpan(0, 4).SequenceEqual("SCH1"u8) || count == 0 || count > allowed.Count)
            throw new IOException("撮影キー補助の設定形式が一致しません。");
        byte[] bytes = new byte[count * 2];
        await pipe.ReadExactlyAsync(bytes, token);
        var bindings = new HotkeyBinding[count];
        for (int index = 0; index < count; index++)
            bindings[index] = new(bytes[index * 2], (HotkeyModifiers)bytes[index * 2 + 1]);
        Validate(bindings);
        return bindings;
    }

    internal static async Task<byte> ReadByteAsync(Stream pipe, CancellationToken token)
    {
        byte[] bytes = new byte[1];
        await pipe.ReadExactlyAsync(bytes, token);
        return bytes[0];
    }

    private static void Validate(IReadOnlyList<HotkeyBinding> bindings)
    {
        if (bindings.Count == 0 || bindings.Count > allowed.Count || bindings.Distinct().Count() != bindings.Count || bindings.Any(binding => !Supports(binding)))
            throw new IOException("撮影キー補助で許可されていないキー登録です。");
    }
}
