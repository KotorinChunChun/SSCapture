using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using FileIdentity = SSCapture.Services.TemporaryHandoff.FileIdentity;

namespace SSCapture.Services;

// Windows Shellと画像取込アプリが共通で受け取れる、実在ファイルのOLEドラッグ。
internal static class NotificationFileDrag
{
    internal const string PreferredEffect = "Preferred DropEffect";
    internal const string PerformedEffect = "Performed DropEffect";
    internal const string LogicalEffect = "Logical Performed DropEffect";

    internal static bool Run(Control source, string path, Func<DataObject, DragDropEffects>? perform = null,
        Action? completeMove = null, Action<string, string?>? savedFileMoved = null)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("ドラッグする画像が見つかりません。", path);
        using var file = FileIdentity.OpenForTracking(path);
        FileIdentity? identity = FileIdentity.Identify(file);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, true, new[] { path });
        using var preference = new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Move));
        data.SetData(PreferredEffect, false, preference);
        DragDropEffects result = perform is null ? source.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move) : perform(data);
        bool removedBySource = false;
        bool dropped = Complete(path, data, result, completeMove ?? DeleteOriginal);
        if (savedFileMoved is not null)
        {
            FileIdentity? remaining = FileIdentity.Read(path);
            if (removedBySource || (remaining is not null ? identity is not null && remaining != identity : !File.Exists(path)))
                savedFileMoved(path, !removedBySource && identity is { } expected ? FindMovedFile(file, expected) : null);
        }
        return dropped;

        void DeleteOriginal()
        {
            if (identity is not { } original || !FileIdentity.DeleteIfMatches(path, original))
                throw new IOException("移動元の画像を削除できませんでした。");
            removedBySource = true;
        }
    }

    internal static async Task<bool> RunHistoryAsync(Control source, CaptureHistory history, Guid id, string workRoot,
        Func<bool> canStart, Action<string, string?> savedFileMoved, Func<DataObject, DragDropEffects>? perform = null)
    {
        if (!canStart()) return false;
        var entry = history.Find(id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        if (entry.SavedPath is { } path && File.Exists(path))
            return Run(source, path, perform, savedFileMoved: savedFileMoved);

        using var image = history.CloneImage(id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        string directory = TemporaryHandoff.DirectoryFor(workRoot);
        using var temporary = await Task.Run(() => TemporaryHandoff.Publish(directory, publish =>
            CaptureStorage.SavePng(image, directory, entry.CapturedAt, beforePublish: publish)));
        return canStart() && history.Find(id) is not null && Run(source, temporary.Path, perform, temporary.CompleteMove);
    }

    internal static bool DragThresholdReached(Point origin, Point current, Size threshold) =>
        !new Rectangle(origin.X - threshold.Width / 2, origin.Y - threshold.Height / 2, threshold.Width, threshold.Height).Contains(current);

    private static string? FindMovedFile(SafeFileHandle file, FileIdentity expected)
    {
        // Shellの通知には移動先パスが含まれない。同じファイルを追跡できたときだけ参照先にする。
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity) return null;
        string target = buffer.ToString();
        if (target.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) target = @"\\" + target[8..];
        else if (target.StartsWith(@"\\?\", StringComparison.Ordinal)) target = target[4..];
        return FileIdentity.Read(target) == expected ? target : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);

    internal static bool Complete(string path, IDataObject data, DragDropEffects result, Action? completeMove = null)
    {
        // アプリの「取込み」をMoveと返す実装でも、戻り値だけで保存原本を削除しない。
        // Shellの非最適化移動は戻り値とPerformedの両方がMoveの時だけ削除を要求する。
        if (result == DragDropEffects.Move && ReadEffect(data, PerformedEffect) == DragDropEffects.Move && File.Exists(path))
        {
            if (completeMove is null) File.Delete(path);
            else completeMove();
        }
        // Explorerの最適化移動はNoneを返す場合もあり、既に元ファイルは移動済み。
        return result != DragDropEffects.None || ReadEffect(data, LogicalEffect) is DragDropEffects.Copy or DragDropEffects.Move
            || !File.Exists(path);
    }

    internal static DragDropEffects ReadEffect(IDataObject data, string format)
    {
        if (data.GetData(format, false) is not Stream stream || !stream.CanRead || !stream.CanSeek || stream.Length < 4)
            return DragDropEffects.None;
        long position = stream.Position;
        try
        {
            stream.Position = 0; Span<byte> bytes = stackalloc byte[4]; stream.ReadExactly(bytes);
            int value = BitConverter.ToInt32(bytes);
            return value is 1 or 2 ? (DragDropEffects)value : DragDropEffects.None;
        }
        finally { stream.Position = position; }
    }
}
