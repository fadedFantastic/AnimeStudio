using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AnimeStudio;

/// <summary>Animation metadata and lossless compaction for binary FBX from our native exporter.</summary>
public static partial class FbxBinaryOptimizer
{
    public sealed record Result(long OriginalBytes, long OptimizedBytes, int ConstantCurves, long RemovedKeys,
        int UnusedEmptyCurves, int CompressedArrays, int UpdatedTimeRanges = 0);

    public static Result Optimize(string path) => CompleteExport(path, null, true);

    internal static Result CompleteExport(string path, IReadOnlyList<ImportedKeyframedAnimation> animations, bool optimize)
    {
        var bytes = File.ReadAllBytes(path);
        var unchanged = new Result(bytes.LongLength, bytes.LongLength, 0, 0, 0, 0);
        if (bytes.Length < 27 || Encoding.ASCII.GetString(bytes, 0, 18) != "Kaydara FBX Binary") return unchanged;
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(23, 4));
        if (version < 7100 || version > 7700) return unchanged;
        var document = new Document(bytes, version);
        document.Read();
        if (!document.HasStandardFooter) return unchanged;
        var objects = document.Nodes.FirstOrDefault(n => n.Name == "Objects");
        if (objects == null) return unchanged;
        if (animations == null && !objects.Children.Any(n => n.Name == "AnimationCurve")) return unchanged;
        var ranges = animations == null ? 0 : WriteAnimationTimeSpans(document, objects, animations);
        var reduced = 0; long removedKeys = 0;
        foreach (var curve in objects.Children.Where(n => optimize && n.Name == "AnimationCurve"))
        {
            var removed = ReduceConstantCurve(curve);
            if (removed > 0) { reduced++; removedKeys += removed; }
        }
        var empty = optimize ? RemoveUnreferencedEmptyCurves(document, objects) : 0;
        var compressed = 0;
        foreach (var node in document.AllNodes())
            foreach (var property in node.Properties)
                if (optimize && property.CompressArray()) compressed++;
        if (reduced == 0 && empty == 0 && compressed == 0 && ranges == 0) return unchanged;
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), ".fbx-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var writer = new BinaryWriter(File.Create(temporary))) document.Write(writer);
            var length = new FileInfo(temporary).Length;
            if (length >= bytes.LongLength && ranges == 0) return unchanged;
            File.Move(temporary, path, true);
            return new(bytes.LongLength, length, reduced, removedKeys, empty, compressed, ranges);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static int ReduceConstantCurve(Node curve)
    {
        var keyVersion = curve.Child("KeyVer")?.Properties.SingleOrDefault();
        if (keyVersion?.Type != 'I' || keyVersion.Integer != 4009) return 0;
        var knownFields = new[] { "Default", "KeyVer", "KeyTime", "KeyValueFloat", "KeyAttrFlags", "KeyAttrDataFloat", "KeyAttrRefCount" };
        if (curve.Children.Any(n => !knownFields.Contains(n.Name))) return 0;
        var times = curve.Array("KeyTime", 'l');
        var values = curve.Array("KeyValueFloat", 'f');
        var flags = curve.Array("KeyAttrFlags", 'i');
        var attributes = curve.Array("KeyAttrDataFloat", 'f');
        var references = curve.Array("KeyAttrRefCount", 'i');
        if (times == null || values == null || flags == null || attributes == null || references == null ||
            values.Count <= 2 || times.Count != values.Count || flags.Count != 1 || attributes.Count != 4 || references.Count != 1) return 0;
        var count = values.Count;
        var timeData = times.DecodeArray(); var valueData = values.DecodeArray();
        var flagData = flags.DecodeArray(); var attrData = attributes.DecodeArray(); var referenceData = references.DecodeArray();
        // Only the SDK's uniform auto-cubic representation with zero outgoing/incoming slopes.
        // Equal values with nonzero tangents can overshoot and MUST NOT be reduced.
        if (BinaryPrimitives.ReadInt32LittleEndian(flagData) != 0x2108 ||
            BinaryPrimitives.ReadInt32LittleEndian(referenceData) != count ||
            BinaryPrimitives.ReadSingleLittleEndian(attrData) != 0 ||
            BinaryPrimitives.ReadSingleLittleEndian(attrData.AsSpan(4)) != 0 ||
            !float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(valueData))) return 0;
        for (var i = 1; i < count; i++)
        {
            if (!valueData.AsSpan(i * 4, 4).SequenceEqual(valueData.AsSpan(0, 4)) ||
                BinaryPrimitives.ReadInt64LittleEndian(timeData.AsSpan(i * 8)) <= BinaryPrimitives.ReadInt64LittleEndian(timeData.AsSpan((i - 1) * 8))) return 0;
        }
        var endpoints = new byte[16];
        timeData.AsSpan(0, 8).CopyTo(endpoints); timeData.AsSpan(timeData.Length - 8, 8).CopyTo(endpoints.AsSpan(8));
        times.SetArray(2, endpoints);
        values.SetArray(2, valueData.AsSpan(0, 8).ToArray());
        BinaryPrimitives.WriteInt32LittleEndian(referenceData, 2); references.SetArray(1, referenceData);
        // Retain endpoint times and the binding, including constant zero/unit values.
        return count - 2;
    }

    private static int RemoveUnreferencedEmptyCurves(Document document, Node objects)
    {
        var connections = document.Nodes.FirstOrDefault(n => n.Name == "Connections");
        var definitions = document.Nodes.FirstOrDefault(n => n.Name == "Definitions");
        var type = definitions?.Children.FirstOrDefault(n => n.Name == "ObjectType" && n.Properties.FirstOrDefault()?.StringValue == "AnimationCurve");
        var totalCount = definitions?.Child("Count")?.Properties.SingleOrDefault();
        var curveCount = type?.Child("Count")?.Properties.SingleOrDefault();
        if (connections == null || totalCount?.Type != 'I' || curveCount?.Type != 'I') return 0;
        // Respect scalar object references outside Connections as well. Unknown references
        // are a reason to keep data, never a reason to assume it is dead.
        var referenced = new HashSet<long>();
        foreach (var node in document.AllNodes())
            for (var i = 0; i < node.Properties.Count; i++)
            {
                var property = node.Properties[i];
                if (property.Type == 'L' && !(node.Name == "AnimationCurve" && i == 0)) referenced.Add(property.Integer);
                if (property.Type == 'l' && node.Name != "KeyTime")
                {
                    var data = property.DecodeArray();
                    for (var at = 0; at < data.Length; at += 8) referenced.Add(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at)));
                }
            }
        var dead = objects.Children.Where(n => n.Name == "AnimationCurve" && n.Properties.FirstOrDefault()?.Type == 'L' &&
            !referenced.Contains(n.Properties[0].Integer) && n.Array("KeyTime", 'l')?.Count == 0 && n.Array("KeyValueFloat", 'f')?.Count == 0).ToArray();
        if (totalCount.Integer < dead.Length || curveCount.Integer < dead.Length) return 0;
        foreach (var curve in dead) objects.Children.Remove(curve);
        if (dead.Length > 0)
        {
            totalCount.SetInt32(checked((int)totalCount.Integer - dead.Length));
            curveCount.SetInt32(checked((int)curveCount.Integer - dead.Length));
        }
        // Referenced empty curves are retained: defaults/binding presence can matter.
        return dead.Length;
    }

    private sealed class Property
    {
        public ReadOnlyMemory<byte> Encoded;
        public char Type => (char)Encoded.Span[0];
        public bool IsArray => "fdlibc".Contains(Type);
        public int Count => BinaryPrimitives.ReadInt32LittleEndian(Encoded.Span.Slice(1, 4));
        public long Integer => Type == 'L' ? BinaryPrimitives.ReadInt64LittleEndian(Encoded.Span.Slice(1)) : BinaryPrimitives.ReadInt32LittleEndian(Encoded.Span.Slice(1));
        public string StringValue => Type == 'S' ? Encoding.UTF8.GetString(Encoded.Span.Slice(5)) : null;
        public byte[] DecodeArray()
        {
            var stride = Type is 'd' or 'l' ? 8 : Type is 'f' or 'i' ? 4 : 1;
            var length = checked(Count * stride);
            if (length < 0) throw new InvalidDataException("Invalid FBX array length.");
            var encoding = BinaryPrimitives.ReadInt32LittleEndian(Encoded.Span.Slice(5, 4));
            var payload = Encoded.Slice(13);
            if (encoding == 0)
            {
                if (payload.Length != length) throw new InvalidDataException("Invalid FBX array size.");
                return payload.ToArray();
            }
            if (encoding != 1) throw new InvalidDataException("Unknown FBX array encoding.");
            var data = new byte[length];
            using var input = new MemoryStream(payload.ToArray());
            using var zip = new ZLibStream(input, CompressionMode.Decompress);
            zip.ReadExactly(data);
            if (zip.ReadByte() != -1) throw new InvalidDataException("FBX array expands beyond its declared size.");
            return data;
        }
        public void SetArray(int count, byte[] data)
        {
            var encoded = new byte[13 + data.Length]; encoded[0] = (byte)Type;
            BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(1), count);
            BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(9), data.Length);
            data.CopyTo(encoded, 13); Encoded = encoded;
        }
        public bool CompressArray()
        {
            if (!IsArray) return false;
            var data = DecodeArray();
            using var buffer = new MemoryStream();
            using (var zip = new ZLibStream(buffer, CompressionLevel.SmallestSize, true)) zip.Write(data);
            var packed = buffer.ToArray();
            var useZip = packed.Length < data.Length;
            var payload = useZip ? packed : data;
            if (payload.Length >= Encoded.Length - 13) return false;
            var encoded = new byte[13 + payload.Length]; Encoded.Span.Slice(0, 5).CopyTo(encoded);
            BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(5), useZip ? 1 : 0);
            BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(9), payload.Length);
            payload.CopyTo(encoded, 13); Encoded = encoded;
            return true;
        }
        public void SetInt32(int value)
        {
            var bytes = new byte[5]; bytes[0] = (byte)'I'; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), value); Encoded = bytes;
        }
    }

    private sealed class Node
    {
        public byte[] NameBytes;
        public string Name => Encoding.UTF8.GetString(NameBytes);
        public List<Property> Properties = new();
        public List<Node> Children = new();
        public bool Terminator;
        public Node Child(string name) => Children.FirstOrDefault(n => n.Name == name);
        public Property Array(string name, char type)
        {
            var property = Child(name)?.Properties.SingleOrDefault();
            return property?.Type == type ? property : null;
        }
    }

    private sealed class Document(byte[] bytes, int version)
    {
        public readonly List<Node> Nodes = new();
        private readonly bool wide = version >= 7500;
        private int HeaderSize => wide ? 25 : 13;
        private byte[] footer;
        public bool HasStandardFooter => footer.Length >= 160 && footer.Length <= 175 &&
            BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(footer.Length - 140, 4)) == version &&
            footer.AsSpan(16, footer.Length - 156).IndexOfAnyExcept((byte)0) < 0 &&
            footer.AsSpan(footer.Length - 136, 120).IndexOfAnyExcept((byte)0) < 0;
        public IEnumerable<Node> AllNodes()
        {
            var work = new Stack<Node>(Nodes);
            while (work.TryPop(out var node)) { yield return node; foreach (var child in node.Children) work.Push(child); }
        }
        public void Read()
        {
            using var reader = new BinaryReader(new MemoryStream(bytes)); reader.BaseStream.Position = 27;
            Node ReadNode(int depth, long limit)
            {
                if (depth > 512 || reader.BaseStream.Position + HeaderSize > limit) throw new InvalidDataException("Invalid FBX node boundary.");
                var end = wide ? reader.ReadInt64() : reader.ReadUInt32();
                var count = wide ? reader.ReadInt64() : reader.ReadUInt32();
                var length = wide ? reader.ReadInt64() : reader.ReadUInt32();
                var nameLength = reader.ReadByte();
                if (end == 0 && count == 0 && length == 0 && nameLength == 0) return null;
                if (end > limit || end < reader.BaseStream.Position + nameLength || count < 0 || length < 0) throw new InvalidDataException("Invalid FBX node.");
                var node = new Node { NameBytes = reader.ReadBytes(nameLength) };
                var propertiesEnd = checked(reader.BaseStream.Position + length);
                if (propertiesEnd > end) throw new InvalidDataException("Invalid FBX properties boundary.");
                for (long i = 0; i < count; i++)
                {
                    var start = checked((int)reader.BaseStream.Position);
                    var type = (char)reader.ReadByte();
                    long size;
                    if (type is 'S' or 'R') size = reader.ReadUInt32();
                    else if ("fdlibc".Contains(type)) { reader.ReadUInt32(); reader.ReadUInt32(); size = reader.ReadUInt32(); }
                    else size = type switch { 'Y' => 2, 'C' => 1, 'I' or 'F' => 4, 'D' or 'L' => 8, _ => throw new InvalidDataException("Unknown FBX property.") };
                    reader.BaseStream.Position = checked(reader.BaseStream.Position + size);
                    if (reader.BaseStream.Position > propertiesEnd) throw new InvalidDataException("Invalid FBX property size.");
                    node.Properties.Add(new Property { Encoded = bytes.AsMemory(start, checked((int)reader.BaseStream.Position - start)) });
                }
                if (reader.BaseStream.Position != propertiesEnd) throw new InvalidDataException("FBX property count mismatch.");
                while (reader.BaseStream.Position < end)
                {
                    var child = ReadNode(depth + 1, end);
                    if (child == null) { node.Terminator = true; break; }
                    node.Children.Add(child);
                }
                if (reader.BaseStream.Position != end) throw new InvalidDataException("Invalid FBX child boundary.");
                return node;
            }
            Node node;
            while ((node = ReadNode(0, bytes.Length)) != null) Nodes.Add(node);
            footer = reader.ReadBytes(checked((int)(bytes.Length - reader.BaseStream.Position)));
        }
        public void Write(BinaryWriter writer)
        {
            writer.Write(bytes, 0, 27);
            void Word(long value) { if (wide) writer.Write(value); else writer.Write(checked((uint)value)); }
            void WriteNode(Node node)
            {
                var start = writer.BaseStream.Position;
                writer.Write(new byte[HeaderSize]); writer.Write(node.NameBytes);
                var propertiesStart = writer.BaseStream.Position;
                foreach (var property in node.Properties) writer.Write(property.Encoded.Span);
                var length = writer.BaseStream.Position - propertiesStart;
                foreach (var child in node.Children) WriteNode(child);
                if (node.Terminator) writer.Write(new byte[HeaderSize]);
                var end = writer.BaseStream.Position;
                writer.BaseStream.Position = start; Word(end); Word(node.Properties.Count); Word(length); writer.Write(checked((byte)node.NameBytes.Length));
                writer.BaseStream.Position = end;
            }
            foreach (var node in Nodes) WriteNode(node);
            writer.Write(new byte[HeaderSize]);
            writer.Write(footer, 0, 16);
            writer.Write(new byte[(int)((16 - writer.BaseStream.Position % 16) % 16) + 4]);
            writer.Write(footer, footer.Length - 140, 140);
        }
    }
}
