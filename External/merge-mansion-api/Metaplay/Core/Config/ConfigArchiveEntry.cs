using System;

namespace Metaplay.Core.Config
{
    public class ConfigArchiveEntry
    {
        // 0x10
        public readonly string Name;
        // 0x18
        public readonly ContentHash Hash;
        // 0x28
        public readonly CompressionAlgorithm Compression;
        // 0x30
        public readonly byte[] RawBytes;

        public ConfigArchiveEntry(string name, ContentHash hash, CompressionAlgorithm compression, byte[] bytes)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            RawBytes = bytes ?? throw new ArgumentNullException(nameof(bytes));

            Hash = hash;
            Compression = compression;
        }

        public static ConfigArchiveEntry FromBlob(string name, byte[] bytes)
        {
            var hash = ContentHash.ComputeFromBytes(bytes);
            return new ConfigArchiveEntry(name, hash, CompressionAlgorithm.None, bytes);
        }

        private volatile byte[] _uncompressedCache;

        public byte[] Uncompress()
        {
            if (Compression == CompressionAlgorithm.None)
                return RawBytes;

            return _uncompressedCache ??= CompressUtil.DeflateDecompress(RawBytes);
        }

        public override int GetHashCode()
        {
            return Hash.GetHashCode();
        }

        public override string ToString()
        {
            return $"{Name}#{Hash}";
        }
    }
}
