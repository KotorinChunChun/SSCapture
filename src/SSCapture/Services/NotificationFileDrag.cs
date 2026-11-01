namespace SSCapture.Services;

// Windows Shellと画像取込アプリが共通で受け取れる、実在ファイルのOLEドラッグ。
internal static class NotificationFileDrag
{
    internal const string PreferredEffect = "Preferred DropEffect";
    internal const string PerformedEffect = "Performed DropEffect";
    internal const string LogicalEffect = "Logical Performed DropEffect";

    internal static bool Run(Control source, string path, Func<DataObject, DragDropEffects>? perform = null)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("ドラッグする画像が見つかりません。", path);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, true, new[] { path });
        using var preference = new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Move));
        data.SetData(PreferredEffect, false, preference);
        DragDropEffects result = perform is null ? source.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move) : perform(data);
        return Complete(path, data, result);
    }

    internal static bool Complete(string path, IDataObject data, DragDropEffects result)
    {
        // アプリの「取込み」をMoveと返す実装でも、戻り値だけで保存原本を削除しない。
        // Shellの非最適化移動は戻り値とPerformedの両方がMoveの時だけ削除を要求する。
        if (result == DragDropEffects.Move && ReadEffect(data, PerformedEffect) == DragDropEffects.Move && File.Exists(path))
            File.Delete(path);
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
