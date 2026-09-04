#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GameLogic.Il2Cpp
{
    /// <summary>
    /// CUSTOM: Reads an enum's members (name + value) straight out of Unity's
    /// <c>global-metadata.dat</c>, so the dumper no longer depends on a hand-rebuilt
    /// <see cref="HotspotId"/> enum after every game update (the enum gains ~300 members per
    /// release; a stale copy makes new-area tasks dump as integer Ids with no description).
    ///
    /// Scope: metadata versions 29–103 (Unity 2021+ up to the v108 layout change). Only the
    /// sections needed for enum extraction are parsed: string table, typeDefinitions, fields,
    /// fieldDefaultValues and the compressed default-value blob. Layout facts were verified
    /// against LibCpp2IL (Cpp2IL) sources and byte-for-byte against the real 26.07.01 file
    /// (v39): all 10 882 HotspotId members matched Cpp2IL's diffable-cs output.
    ///
    /// Variable-width indices (v38+): Il2CppType indices are as wide as the interfaceOffsets
    /// element minus its 4-byte offset field; genericContainer indices are sized by that
    /// section's count (≤255 → 1 B, ≤65535 → 2 B, else 4 B); all other indices stay 4 B until
    /// v104+. Anything the reader can't validate (typedef size mismatch, first field not
    /// <c>value__</c>) throws instead of returning garbage — the enum fallback stays intact.
    /// </summary>
    public static class Il2CppMetadataEnumReader
    {
        public const uint Magic = 0xFAB11BAF;
        public const string MetadataEntryPath = "assets/bin/Data/Managed/Metadata/global-metadata.dat";

        public readonly struct EnumMember
        {
            public EnumMember(string name, int value) { Name = name; Value = value; }
            public string Name { get; }
            public int Value { get; }
        }

        private struct Section { public int Offset, Size, Count; }

        // Header section order for v27.9–v103 (LibCpp2IL Il2CppGlobalMetadataHeader with the
        // [Version] filters applied). v38+ adds a Count int per section.
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

        /// <summary>Returns the metadata version int (offset 4) without parsing anything else.</summary>
        public static int ReadVersion(byte[] data)
        {
            if (data.Length < 8 || BitConverter.ToUInt32(data, 0) != Magic)
                throw new InvalidDataException("Not a global-metadata.dat file (bad magic).");
            return BitConverter.ToInt32(data, 4);
        }

        /// <summary>
        /// Extracts <c>global-metadata.dat</c> from an APK or XAPK (outer zip → base
        /// <c>*.apk</c> → assets/bin/Data/Managed/Metadata/global-metadata.dat). Fully in memory —
        /// the 30 MB blob never touches the disk.
        /// </summary>
        public static byte[] ExtractGlobalMetadata(string apkOrXapkPath)
        {
            using var fs = File.OpenRead(apkOrXapkPath);
            using var outer = new ZipArchive(fs, ZipArchiveMode.Read);

            var direct = outer.GetEntry(MetadataEntryPath);
            if (direct != null) return ReadEntry(direct);

            foreach (var entry in outer.Entries)
            {
                if (!entry.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) continue;
                using var inner = new ZipArchive(entry.Open(), ZipArchiveMode.Read);
                var meta = inner.GetEntry(MetadataEntryPath);
                if (meta != null) return ReadEntry(meta);
            }
            throw new FileNotFoundException($"{MetadataEntryPath} not found in {Path.GetFileName(apkOrXapkPath)} (nor in any inner .apk).");
        }

        private static byte[] ReadEntry(ZipArchiveEntry entry)
        {
            using var s = entry.Open();
            using var ms = new MemoryStream(entry.Length > 0 && entry.Length < int.MaxValue ? (int)entry.Length : 0);
            s.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// Reads all members of <paramref name="enumName"/> (excluding <c>value__</c>) in
        /// declaration order. Throws <see cref="InvalidDataException"/> when the enum is missing
        /// or the layout doesn't validate, <see cref="NotSupportedException"/> for versions
        /// outside 29–103.
        /// </summary>
        public static IReadOnlyList<EnumMember> ReadEnum(byte[] data, string enumName)
        {
            int version = ReadVersion(data);
            if (version < 29 || version > 103)
                throw new NotSupportedException(
                    $"global-metadata.dat version {version} is outside the supported range 29–103 — " +
                    "update Il2CppMetadataEnumReader (see LibCpp2IL Il2CppGlobalMetadataHeader / Il2CppTypeDefinition).");

            bool hasCount = version >= 38;
            var sections = new Dictionary<string, Section>(SectionOrder.Length);
            int pos = 8;
            foreach (var name in SectionOrder)
            {
                var s = new Section { Offset = ReadInt(data, pos), Size = ReadInt(data, pos + 4) };
                pos += 8;
                if (hasCount) { s.Count = ReadInt(data, pos); pos += 4; }
                sections[name] = s;
            }

            var strings = sections["string"];
            var fields = sections["fields"];
            var defaults = sections["fieldDefaultValues"];
            var defaultData = sections["fieldAndParameterDefaultValueData"];
            var typeDefs = sections["typeDefinitions"];

            // Index widths (see class remarks)
            int typeWidth = 4, genericContainerWidth = 4;
            if (hasCount)
            {
                var io = sections["interfaceOffsets"];
                if (io.Count > 0) typeWidth = io.Size / io.Count - 4;
                genericContainerWidth = IndexWidth(sections["genericContainers"].Count);
            }

            // Il2CppTypeDefinition layout (v35–v103; ElementTypeIndex present only ≤ v34)
            int typeDefSize = 4 + 4 + 3 * typeWidth + (version <= 34 ? 4 : 0) + genericContainerWidth
                              + 4 + 6 * 4 + 4 + 4 + 8 * 2 + 4 + 4;
            int typeDefCount = hasCount ? typeDefs.Count : typeDefs.Size / typeDefSize;
            if (hasCount && typeDefs.Count > 0 && typeDefs.Size / typeDefs.Count != typeDefSize)
                throw new InvalidDataException(
                    $"Il2CppTypeDefinition size mismatch: header says {typeDefs.Size / typeDefs.Count} B, " +
                    $"reader expects {typeDefSize} B (metadata v{version}, typeWidth={typeWidth}, gcWidth={genericContainerWidth}).");

            int firstFieldOffsetInTypeDef = 4 + 4 + 3 * typeWidth + (version <= 34 ? 4 : 0) + genericContainerWidth + 4;
            int fieldCountOffsetInTypeDef = typeDefSize - 4 - 4 - 8 * 2 + 2 * 2; // MethodCount, PropertyCount, FieldCount

            int fieldSize = 4 + typeWidth + (version <= 24 ? 4 : 0) + 4;
            int defaultSize = 4 + typeWidth + 4;
            int fieldCountTotal = hasCount ? fields.Count : fields.Size / fieldSize;

            // Locate the typedef by name (namespace is not checked — HotspotId is unique)
            int nameIdx = FindStringIndex(data, strings, enumName);
            if (nameIdx < 0)
                throw new InvalidDataException($"Enum '{enumName}' not found in metadata string table.");

            for (int t = 0; t < typeDefCount; t++)
            {
                int recOff = typeDefs.Offset + t * typeDefSize;
                if (ReadInt(data, recOff) != nameIdx) continue;

                int firstField = ReadInt(data, recOff + firstFieldOffsetInTypeDef);
                int fieldCount = BitConverter.ToUInt16(data, recOff + fieldCountOffsetInTypeDef);
                if (firstField < 0 || fieldCount == 0 || firstField + fieldCount > fieldCountTotal) continue;

                string firstName = ReadString(data, strings, ReadInt(data, fields.Offset + firstField * fieldSize));
                if (firstName != "value__") continue; // not an enum (or layout drift) — keep looking

                // fieldIndex → dataIndex for the enum's constant fields
                var dataIndexByField = new Dictionary<int, int>(fieldCount);
                int defaultCount = hasCount ? defaults.Count : defaults.Size / defaultSize;
                for (int d = 0; d < defaultCount; d++)
                {
                    int dOff = defaults.Offset + d * defaultSize;
                    int fi = ReadInt(data, dOff);
                    if (fi > firstField && fi < firstField + fieldCount)
                        dataIndexByField[fi] = ReadInt(data, dOff + 4 + typeWidth);
                }

                var result = new List<EnumMember>(fieldCount - 1);
                for (int f = firstField + 1; f < firstField + fieldCount; f++)
                {
                    string name = ReadString(data, strings, ReadInt(data, fields.Offset + f * fieldSize));
                    if (!dataIndexByField.TryGetValue(f, out var dataIdx))
                        throw new InvalidDataException($"Enum '{enumName}' member '{name}' has no default value entry.");
                    int p = defaultData.Offset + dataIdx;
                    result.Add(new EnumMember(name, ReadCompressedInt32(data, ref p)));
                }
                return result;
            }

            throw new InvalidDataException($"Enum '{enumName}' has no type definition whose first field is value__ (metadata v{version}).");
        }

        // ── Primitives ──

        private static int IndexWidth(int count) => count <= byte.MaxValue ? 1 : count <= ushort.MaxValue ? 2 : 4;

        private static int ReadInt(byte[] d, int pos) => BitConverter.ToInt32(d, pos);

        private static string ReadString(byte[] d, Section strings, int index)
        {
            int start = strings.Offset + index;
            int end = Array.IndexOf(d, (byte)0, start);
            if (end < 0) end = d.Length;
            return Encoding.UTF8.GetString(d, start, end - start);
        }

        /// <summary>Index of the NUL-terminated string equal to <paramref name="value"/>, or -1.</summary>
        private static int FindStringIndex(byte[] d, Section strings, string value)
        {
            var needle = Encoding.UTF8.GetBytes(value);
            int end = strings.Offset + strings.Size;
            int i = strings.Offset;
            while (i < end)
            {
                int term = Array.IndexOf(d, (byte)0, i, end - i);
                if (term < 0) term = end;
                if (term - i == needle.Length && new ReadOnlySpan<byte>(d, i, needle.Length).SequenceEqual(needle))
                    return i - strings.Offset;
                i = term + 1;
            }
            return -1;
        }

        /// <summary>Unity il2cpp <c>MetadataLoader::ReadCompressedUInt32</c>.</summary>
        public static uint ReadCompressedUInt32(byte[] d, ref int pos)
        {
            byte b = d[pos++];
            if (b == 0xFF) return uint.MaxValue;
            if (b == 0xFE) return uint.MaxValue - 1;
            if ((b & 0x80) == 0) return b;
            if ((b & 0xC0) == 0x80) return (uint)((b & 0x3F) << 8) | d[pos++];
            if ((b & 0xE0) == 0xC0)
            {
                uint v = (uint)((b & 0x1F) << 24) | (uint)(d[pos] << 16) | (uint)(d[pos + 1] << 8) | d[pos + 2];
                pos += 3;
                return v;
            }
            // 0xF0: full 4-byte little-endian value follows
            uint full = BitConverter.ToUInt32(d, pos);
            pos += 4;
            return full;
        }

        /// <summary>Unity il2cpp <c>MetadataLoader::ReadCompressedInt32</c> (zig-zag on top of the uint form).</summary>
        public static int ReadCompressedInt32(byte[] d, ref int pos)
        {
            uint u = ReadCompressedUInt32(d, ref pos);
            if (u == uint.MaxValue) return int.MinValue;
            bool negative = (u & 1) != 0;
            u >>= 1;
            return negative ? -(int)(u + 1) : (int)u;
        }
    }
}
