using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SSCapture.Services;

internal static class PngMetadata
{
    internal const string Namespace = "https://kotorintoys.dev/sscapture/metadata/1.0/";
    internal static byte[] Embed(byte[] png, CaptureMetadata metadata)
    {
        if (png.Length < 20 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("PNG署名が不正です。");
        XNamespace x = "adobe:ns:meta/", rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#", ssc = Namespace;
        var xml = new XDocument(new XElement(x + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", x),
            new XElement(rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", rdf),
                new XElement(rdf + "Description", new XAttribute(rdf + "about", ""), new XAttribute(XNamespace.Xmlns + "ssc", ssc),
                    new XElement(ssc + "schemaVersion", metadata.SchemaVersion), new XElement(ssc + "captureId", metadata.CaptureId),
                    new XElement(ssc + "imageCapturedAt", metadata.ImageCapturedAt.ToString("O")),
                    new XElement(ssc + "contextJson", JsonSerializer.Serialize(metadata, CaptureMetadata.JsonOptions))))));
        byte[] data = Encoding.UTF8.GetBytes("XML:com.adobe.xmp\0\0\0\0\0" + xml.ToString(SaveOptions.DisableFormatting));
        using var output = new MemoryStream(); output.Write(png.AsSpan(0, 8));
        bool inserted = false;
        for (int offset = 8; offset < png.Length;)
        {
            if (png.Length - offset < 12) throw new InvalidDataException("PNGチャンクが途中です。");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > int.MaxValue || (long)offset + length + 12 > png.Length) throw new InvalidDataException("PNG長が不正です。");
            var typeAndData = png.AsSpan(offset + 4, (int)length + 4);
            if (Crc(typeAndData) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + (int)length, 4))) throw new InvalidDataException("PNGのCRCが不正です。");
            bool end = typeAndData[..4].SequenceEqual("IEND"u8);
            bool old = typeAndData[..4].SequenceEqual("iTXt"u8) && typeAndData[4..].StartsWith("XML:com.adobe.xmp\0"u8);
            if (end) { WriteChunk(output, "iTXt"u8, data); inserted = true; }
            if (!old) output.Write(png.AsSpan(offset, (int)length + 12));
            offset += (int)length + 12;
            if (end) { if (offset != png.Length) throw new InvalidDataException("PNG終端後のデータが不正です。"); break; }
        }
        if (!inserted) throw new InvalidDataException("PNG終端がありません。");
        return output.ToArray();
    }
    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length); output.Write(number);
        output.Write(type); output.Write(data);
        byte[] crcData = new byte[data.Length + 4]; type.CopyTo(crcData); data.CopyTo(crcData, 4);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(crcData)); output.Write(number);
    }
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in data) { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320u; }
        return ~crc;
    }
}

internal sealed class MetadataSidecar(CaptureMetadata metadata)
{
    private byte[]? imageHash;
    internal void RememberImage(string path) { using var file = File.OpenRead(path); imageHash = SHA256.HashData(file); }
    internal void Write(string imagePath)
    {
        using (var image = File.OpenRead(imagePath))
            if (imageHash is null || !SHA256.HashData(image).AsSpan().SequenceEqual(imageHash)) throw new IOException("保存画像が変更されたため撮影情報を再保存できません。");
        string target = Path.ChangeExtension(imagePath, ".json");
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(metadata, CaptureMetadata.JsonOptions);
        if (File.Exists(target))
        {
            if (File.ReadAllBytes(target).AsSpan().SequenceEqual(data)) return;
            throw new IOException("同名のJSONが存在するため上書きしません。");
        }
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data); stream.Flush(true); }
            File.Move(temporary, target, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
