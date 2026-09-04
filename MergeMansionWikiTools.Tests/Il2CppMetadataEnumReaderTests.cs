using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GameLogic.Il2Cpp;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Tests for the global-metadata.dat enum reader that replaces the hand-maintained
/// <c>HotspotId.cs</c> rebuild (Cpp2IL) after every game update. The layout under test was
/// validated byte-for-byte against the real 26.07.01 metadata (v39): all 10 882 HotspotId
/// members matched the Cpp2IL output. Here we build a minimal synthetic v39 file so the
/// test is self-contained (the real file is ~30 MB and not in the repo).
/// </summary>
public class Il2CppMetadataEnumReaderTests
{
    // ── Compressed integers (Unity il2cpp MetadataLoader encoding) ──

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0)]
    [InlineData(new byte[] { 0x80, 0xC8 }, 100)]          // (100 << 1) = 200 → 2-byte form
    [InlineData(new byte[] { 0x03 }, -2)]                 // odd → negative: -(1 + 1)
    [InlineData(new byte[] { 0xC0, 0x01, 0x00, 0x00 }, 32768)] // 65536 >> 1 → 4-byte form
    public void ReadCompressedInt32_decodes_unity_encoding(byte[] bytes, int expected)
    {
        int pos = 0;
        Assert.Equal(expected, Il2CppMetadataEnumReader.ReadCompressedInt32(bytes, ref pos));
        Assert.Equal(bytes.Length, pos);
    }

    // ── Enum extraction from a synthetic v39 file ──

    [Fact]
    public void ReadEnum_returns_members_with_values_from_synthetic_v39_metadata()
    {
        var members = new List<(string, int)>
        {
            ("None", 0), ("RanchToMansionLocationTravel", 100), ("FirstFloorPantryPrepTableCleanFloor", 18502),
        };
        var bytes = SyntheticMetadata.Build(version: 39, enumName: "HotspotId", members,
            otherEnum: ("ItemTypeConstant", new List<(string, int)> { ("Zero", 0), ("One", 1) }));

        var result = Il2CppMetadataEnumReader.ReadEnum(bytes, "HotspotId");

        Assert.Equal(members, result.Select(m => (m.Name, m.Value)).ToList());
    }

    [Fact]
    public void ReadEnum_throws_when_enum_not_present()
    {
        var bytes = SyntheticMetadata.Build(39, "HotspotId", new List<(string, int)> { ("None", 0) }, null);
        var ex = Assert.Throws<InvalidDataException>(() => Il2CppMetadataEnumReader.ReadEnum(bytes, "Nope"));
        Assert.Contains("Nope", ex.Message);
    }

    [Fact]
    public void ReadEnum_rejects_unsupported_metadata_version()
    {
        var bytes = SyntheticMetadata.Build(39, "HotspotId", new List<(string, int)> { ("None", 0) }, null);
        BitConverter.GetBytes(120).CopyTo(bytes, 4); // pretend v120 (post-108 layout, unsupported)
        var ex = Assert.Throws<NotSupportedException>(() => Il2CppMetadataEnumReader.ReadEnum(bytes, "HotspotId"));
        Assert.Contains("120", ex.Message);
    }

    [Fact]
    public void ReadEnum_rejects_wrong_magic()
    {
        var bytes = new byte[512];
        Assert.Throws<InvalidDataException>(() => Il2CppMetadataEnumReader.ReadEnum(bytes, "HotspotId"));
    }

    // ── Minimal v39 global-metadata.dat builder ──

    /// <summary>
    /// Emits only the sections the reader touches (string, fields, fieldDefaultValues,
    /// fieldAndParameterDefaultValueData, genericContainers, interfaceOffsets, typeDefinitions);
    /// every other section is empty (offset 0, size 0, count 0). Index widths mirror the real
    /// v39 file: Il2CppType indices 4 bytes (interfaceOffsets element = 8), genericContainer
    /// index 2 bytes (count ≤ 65535), everything else 4 bytes.
    /// </summary>
    private static class SyntheticMetadata
    {
        private static readonly string[] SectionOrder =
        {
            "stringLiteral", "stringLiteralData", "string", "events", "properties", "methods",
            "parameterDefaultValues", "fieldDefaultValues", "fieldAndParameterDefaultValueData",
            "fieldMarshaledSizes", "parameters", "fields", "genericParameters",
            "genericParameterConstraints", "genericContainers", "nestedTypes", "interfaces",
            "vtableMethods", "interfaceOffsets", "typeDefinitions", "images", "assemblies",
            "fieldRefs", "referencedAssemblies", "attributeData", "attributeDataRange",
            "unresolvedVirtualCallParameterTypes", "unresolvedVirtualCallParameterRanges",
            "windowsRuntimeTypeNames", "windowsRuntimeStrings", "exportedTypeDefinitions",
        };

        public static byte[] Build(int version, string enumName, List<(string Name, int Value)> members,
            (string Name, List<(string Name, int Value)> Members)? otherEnum)
        {
            var strings = new MemoryStream();
            var stringIndex = new Dictionary<string, int>();
            int Str(string s)
            {
                if (stringIndex.TryGetValue(s, out var i)) return i;
                i = (int)strings.Position;
                var b = Encoding.UTF8.GetBytes(s);
                strings.Write(b, 0, b.Length);
                strings.WriteByte(0);
                stringIndex[s] = i;
                return i;
            }
            Str(""); // index 0 = empty namespace

            var fields = new MemoryStream();      // nameIndex(4) typeIndex(4) token(4)
            var defaults = new MemoryStream();    // fieldIndex(4) typeIndex(4) dataIndex(4)
            var data = new MemoryStream();        // compressed ints
            var typeDefs = new MemoryStream();
            int fieldCount = 0;

            void AddEnum(string name, List<(string Name, int Value)> ms, bool isFirstTypeDef)
            {
                int firstField = fieldCount;
                // value__ (instance field, no default)
                WriteInt(fields, Str("value__")); WriteInt(fields, 7); WriteInt(fields, 0x04000000 + fieldCount);
                fieldCount++;
                foreach (var (n, v) in ms)
                {
                    WriteInt(fields, Str(n)); WriteInt(fields, 7); WriteInt(fields, 0x04000000 + fieldCount);
                    WriteInt(defaults, fieldCount); WriteInt(defaults, 7); WriteInt(defaults, (int)data.Position);
                    WriteCompressedInt32(data, v);
                    fieldCount++;
                }
                // Il2CppTypeDefinition (v35–v103 layout)
                WriteInt(typeDefs, Str(name));          // NameIndex
                WriteInt(typeDefs, Str(""));            // NamespaceIndex
                WriteInt(typeDefs, 7);                  // ByvalTypeIndex (Il2CppType, 4 B)
                WriteInt(typeDefs, -1);                 // DeclaringTypeIndex
                WriteInt(typeDefs, 3);                  // ParentIndex
                WriteUShort(typeDefs, 0xFFFF);          // GenericContainerIndex (2 B, null)
                WriteInt(typeDefs, 0x101);              // Flags
                WriteInt(typeDefs, firstField);         // FirstFieldIdx
                WriteInt(typeDefs, -1);                 // FirstMethodIdx
                WriteInt(typeDefs, -1);                 // FirstEventId
                WriteInt(typeDefs, -1);                 // FirstPropertyId
                WriteInt(typeDefs, -1);                 // NestedTypesStart
                WriteInt(typeDefs, -1);                 // InterfacesStart
                WriteInt(typeDefs, 0);                  // VtableStart
                WriteInt(typeDefs, 0);                  // InterfaceOffsetsStart
                WriteUShort(typeDefs, 0);               // MethodCount
                WriteUShort(typeDefs, 0);               // PropertyCount
                WriteUShort(typeDefs, (ushort)(ms.Count + 1)); // FieldCount
                WriteUShort(typeDefs, 0);               // EventCount
                WriteUShort(typeDefs, 0);               // NestedTypeCount
                WriteUShort(typeDefs, 23);              // VtableCount
                WriteUShort(typeDefs, 0);               // InterfacesCount
                WriteUShort(typeDefs, 3);               // InterfaceOffsetsCount
                WriteInt(typeDefs, 0xC13);              // Bitfield
                WriteInt(typeDefs, 0x02000000 + (isFirstTypeDef ? 1 : 2)); // Token
            }

            // A decoy type first so the reader can't just take typedef #0
            if (otherEnum != null) AddEnum(otherEnum.Value.Name, otherEnum.Value.Members, true);
            AddEnum(enumName, members, otherEnum == null);

            // interfaceOffsets: 1 dummy record of 8 bytes (typeIndex 4 + offset 4) → type width 4
            var ifOffsets = new byte[8];
            // genericContainers: 1 dummy record (count 1 → 2-byte indices? No: GetIndexWidth(1) = 1 byte)
            // → emit 300 dummy records so the real-file width (2 bytes) is exercised.
            var genericContainers = new byte[300 * 16];

            var sections = new Dictionary<string, byte[]>
            {
                ["string"] = strings.ToArray(),
                ["fields"] = fields.ToArray(),
                ["fieldDefaultValues"] = defaults.ToArray(),
                ["fieldAndParameterDefaultValueData"] = data.ToArray(),
                ["genericContainers"] = genericContainers,
                ["interfaceOffsets"] = ifOffsets,
                ["typeDefinitions"] = typeDefs.ToArray(),
            };
            var counts = new Dictionary<string, int>
            {
                ["string"] = strings.ToArray().Length,
                ["fields"] = fieldCount,
                ["fieldDefaultValues"] = (int)defaults.Length / 12,
                ["fieldAndParameterDefaultValueData"] = (int)data.Length,
                ["genericContainers"] = 300,
                ["interfaceOffsets"] = 1,
                ["typeDefinitions"] = (int)typeDefs.Length / 82,
            };

            int headerSize = 8 + SectionOrder.Length * 12;
            var body = new MemoryStream();
            var offsets = new Dictionary<string, int>();
            foreach (var name in SectionOrder)
            {
                if (!sections.TryGetValue(name, out var blob)) continue;
                offsets[name] = headerSize + (int)body.Position;
                body.Write(blob, 0, blob.Length);
                while (body.Position % 4 != 0) body.WriteByte(0);
            }

            var file = new MemoryStream();
            WriteInt(file, unchecked((int)0xFAB11BAF));
            WriteInt(file, version);
            foreach (var name in SectionOrder)
            {
                if (sections.TryGetValue(name, out var blob))
                {
                    WriteInt(file, offsets[name]); WriteInt(file, blob.Length); WriteInt(file, counts[name]);
                }
                else
                {
                    WriteInt(file, 0); WriteInt(file, 0); WriteInt(file, 0);
                }
            }
            body.Position = 0;
            body.CopyTo(file);
            return file.ToArray();
        }

        private static void WriteInt(Stream s, int v) => s.Write(BitConverter.GetBytes(v), 0, 4);
        private static void WriteUShort(Stream s, ushort v) => s.Write(BitConverter.GetBytes(v), 0, 2);

        private static void WriteCompressedInt32(Stream s, int value)
        {
            uint u = value == int.MinValue ? uint.MaxValue
                : value < 0 ? ((uint)(-value - 1) << 1) | 1
                : (uint)value << 1;
            WriteCompressedUInt32(s, u);
        }

        private static void WriteCompressedUInt32(Stream s, uint v)
        {
            if (v < 0x80) { s.WriteByte((byte)v); return; }
            if (v < 0x4000) { s.WriteByte((byte)(0x80 | (v >> 8))); s.WriteByte((byte)v); return; }
            if (v < 0x20000000)
            {
                s.WriteByte((byte)(0xC0 | (v >> 24))); s.WriteByte((byte)(v >> 16));
                s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); return;
            }
            s.WriteByte(0xF0); s.Write(BitConverter.GetBytes(v), 0, 4);
        }
    }
}
