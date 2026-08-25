
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using K4os.Compression.LZ4;

internal static class UnityFsLz4Transcoder
{

    private const uint CompressionTypeNone = 0;
    private const uint CompressionTypeLz4 = 2;
    private const uint CompressionTypeLz4Hc = 3;

    private sealed class StorageBlock
    {
        public uint UncompressedSize;
        public uint CompressedSize;
        public ushort Flags;
    }

    private sealed class NodeEntry
    {
        public long Offset;
        public long Size;
        public uint Flags;
        public string Path = "";
    }

    public static byte[] ForceStandardLz4(byte[] packed)
    {
        int pos = 0;
        ReadSignature(packed, ref pos);
        _ = ReadU32(packed, ref pos);
        string unityVersion = ReadCString(packed, ref pos);
        string unityRevision = ReadCString(packed, ref pos);
        _ = ReadI64(packed, ref pos);
        uint compressedBlocksInfoSize = ReadU32(packed, ref pos);
        uint uncompressedBlocksInfoSize = ReadU32(packed, ref pos);
        uint flags = ReadU32(packed, ref pos);

        bool blocksInfoAtEnd = (flags & 0x80) != 0;
        uint blocksInfoCompressionType = flags & 0x3Fu;

        int headerEnd = AlignUp16(pos);

        int blocksInfoStart = blocksInfoAtEnd
            ? packed.Length - (int)compressedBlocksInfoSize
            : headerEnd;

        if (blocksInfoStart < 0 || (long)blocksInfoStart + compressedBlocksInfoSize > packed.Length)
            throw new InvalidDataException("Pack()'d bundle's blocks-info span is out of range.");

        byte[] compressedBlocksInfoBytes = new byte[compressedBlocksInfoSize];
        Array.Copy(packed, blocksInfoStart, compressedBlocksInfoBytes, 0, (int)compressedBlocksInfoSize);

        byte[] blocksInfo = DecompressGeneric(
            compressedBlocksInfoBytes, blocksInfoCompressionType, (int)uncompressedBlocksInfoSize,
            "blocks-info");

        (List<StorageBlock> sourceBlocks, List<NodeEntry> nodes) = ParseBlocksInfo(blocksInfo);

        int dataStart = blocksInfoAtEnd
            ? headerEnd

            : AlignUp16(blocksInfoStart + (int)compressedBlocksInfoSize);

        int dataRegionEnd = blocksInfoAtEnd ? blocksInfoStart : packed.Length;
        long declaredDataBytes = 0;
        foreach (StorageBlock b in sourceBlocks)
            declaredDataBytes += b.CompressedSize;

        long dataRegionSlack = (dataRegionEnd - dataStart) - declaredDataBytes;

        Console.Error.WriteLine(
            $"[UnityFsLz4Transcoder] blocksInfoAtEnd={blocksInfoAtEnd} dataStart={dataStart} " +
            $"dataRegionEnd={dataRegionEnd} dataRegionSize={dataRegionEnd - dataStart} " +
            $"declaredDataBytes={declaredDataBytes} slack={dataRegionSlack} blockCount={sourceBlocks.Count}");

        if (dataRegionSlack < 0 || dataRegionSlack >= 16)
        {
            throw new InvalidDataException(
                $"Pack()'d bundle's block table doesn't match its data region: " +
                $"blocks declare {declaredDataBytes:N0} bytes total, but the data region " +
                $"(dataStart={dataStart} to {(blocksInfoAtEnd ? "blocksInfoStart" : "EOF")}=" +
                $"{dataRegionEnd}) is {dataRegionEnd - dataStart:N0} bytes (slack={dataRegionSlack}, " +
                "expected 0-15 for alignment padding).");
        }

        var newBlocks = new List<StorageBlock>(sourceBlocks.Count);
        using var newDataStream = new MemoryStream();

        int srcOffset = dataStart;
        int blockIndex = 0;
        foreach (StorageBlock srcBlock in sourceBlocks)
        {
            if (srcOffset + srcBlock.CompressedSize > packed.Length)
                throw new InvalidDataException("Pack()'d bundle's data blocks run past end of file.");

            byte[] compressedSlice = new byte[srcBlock.CompressedSize];
            Array.Copy(packed, srcOffset, compressedSlice, 0, (int)srcBlock.CompressedSize);

            int previewLen = Math.Min(8, compressedSlice.Length);
            string preview = Convert.ToHexString(compressedSlice, 0, previewLen);
            Console.Error.WriteLine(
                $"[UnityFsLz4Transcoder] block {blockIndex}: srcOffset={srcOffset} " +
                $"compType={srcBlock.Flags & 0x3F} uSize={srcBlock.UncompressedSize} " +
                $"cSize={srcBlock.CompressedSize} flags=0x{srcBlock.Flags:X4} first{previewLen}bytes={preview}");

            srcOffset += (int)srcBlock.CompressedSize;

            uint srcBlockCompType = (uint)(srcBlock.Flags & 0x3F);
            byte[] rawBlockBytes;
            try
            {
                rawBlockBytes = DecompressGeneric(
                    compressedSlice, srcBlockCompType, (int)srcBlock.UncompressedSize, "data block");
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Failed decoding block {blockIndex} of {sourceBlocks.Count} " +
                    $"(srcOffset={srcOffset - srcBlock.CompressedSize}, compType={srcBlockCompType}, " +
                    $"uSize={srcBlock.UncompressedSize}, cSize={srcBlock.CompressedSize}, " +
                    $"flags=0x{srcBlock.Flags:X4}, first{previewLen}bytes={preview}): {ex.Message}", ex);
            }

            byte[] fastLz4Bytes = Lz4EncodeBlockFast(rawBlockBytes);

            newDataStream.Write(fastLz4Bytes, 0, fastLz4Bytes.Length);

            newBlocks.Add(new StorageBlock
            {
                UncompressedSize = srcBlock.UncompressedSize,
                CompressedSize = (uint)fastLz4Bytes.Length,

                Flags = (ushort)((srcBlock.Flags & ~0x3F) | CompressionTypeLz4),
            });

            blockIndex++;
        }

        byte[] newData = newDataStream.ToArray();

        return BuildArchive(unityVersion, unityRevision, newBlocks, nodes, newData);
    }

    private static (List<StorageBlock>, List<NodeEntry>) ParseBlocksInfo(byte[] blocksInfo)
    {
        int pos = 16;
        uint blockCount = ReadU32(blocksInfo, ref pos);

        var blocks = new List<StorageBlock>((int)blockCount);
        for (uint i = 0; i < blockCount; i++)
        {
            uint uSize = ReadU32(blocksInfo, ref pos);
            uint cSize = ReadU32(blocksInfo, ref pos);
            ushort blockFlags = ReadU16(blocksInfo, ref pos);
            blocks.Add(new StorageBlock { UncompressedSize = uSize, CompressedSize = cSize, Flags = blockFlags });
        }

        if (blocks.Count == 0)
            throw new InvalidDataException("Pack()'d bundle declares zero data blocks.");

        uint nodeCount = ReadU32(blocksInfo, ref pos);
        var nodes = new List<NodeEntry>((int)nodeCount);
        for (uint i = 0; i < nodeCount; i++)
        {
            long offset = ReadI64(blocksInfo, ref pos);
            long size = ReadI64(blocksInfo, ref pos);
            uint entryFlags = ReadU32(blocksInfo, ref pos);
            string path = ReadCString(blocksInfo, ref pos);
            nodes.Add(new NodeEntry { Offset = offset, Size = size, Flags = entryFlags, Path = path });
        }

        if (nodes.Count == 0)
            throw new InvalidDataException("Pack()'d bundle has no directory nodes.");

        return (blocks, nodes);
    }

    private static byte[] BuildArchive(string unityVersion, string unityRevision,
                                        List<StorageBlock> blocks, List<NodeEntry> nodes, byte[] data)
    {
        using var blocksInfo = new MemoryStream();
        blocksInfo.Write(new byte[16], 0, 16);
        WriteU32(blocksInfo, (uint)blocks.Count);
        foreach (StorageBlock block in blocks)
        {
            WriteU32(blocksInfo, block.UncompressedSize);
            WriteU32(blocksInfo, block.CompressedSize);
            WriteU16(blocksInfo, block.Flags);
        }

        WriteU32(blocksInfo, (uint)nodes.Count);
        foreach (NodeEntry node in nodes)
        {
            WriteI64(blocksInfo, node.Offset);
            WriteI64(blocksInfo, node.Size);
            WriteU32(blocksInfo, node.Flags);
            WriteCString(blocksInfo, node.Path);
        }

        byte[] blocksInfoBytes = blocksInfo.ToArray();
        byte[] compressedBlocksInfo = Lz4EncodeBlockFast(blocksInfoBytes);

        using var outStream = new MemoryStream();
        WriteBytes(outStream, Encoding.ASCII.GetBytes("UnityFS\0"));
        WriteU32(outStream, 8);
        WriteCString(outStream, unityVersion);
        WriteCString(outStream, unityRevision);
        long archiveSizeOffset = outStream.Position;
        WriteI64(outStream, 0);
        WriteU32(outStream, (uint)compressedBlocksInfo.Length);
        WriteU32(outStream, (uint)blocksInfoBytes.Length);

        WriteU32(outStream, 0x40 | 0x200 | CompressionTypeLz4);

        PadTo16(outStream);
        WriteBytes(outStream, compressedBlocksInfo);
        PadTo16(outStream);
        WriteBytes(outStream, data);

        byte[] result = outStream.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan((int)archiveSizeOffset, 8), result.Length);
        return result;
    }

    private static byte[] Lz4EncodeBlockFast(byte[] data)
    {
        if (data.Length == 0) return Array.Empty<byte>();

        int bound = LZ4Codec.MaximumOutputSize(data.Length);
        byte[] dst = new byte[bound];

        int written = LZ4Codec.Encode(data, 0, data.Length, dst, 0, dst.Length, LZ4Level.L00_FAST);
        if (written <= 0)
            throw new InvalidDataException("Standard LZ4 compression failed.");

        if (written == dst.Length) return dst;
        byte[] trimmed = new byte[written];
        Array.Copy(dst, trimmed, written);
        return trimmed;
    }

    private static byte[] DecompressGeneric(byte[] compressed, uint compressionType, int expectedSize, string what)
    {
        if (expectedSize == 0) return Array.Empty<byte>();

        switch (compressionType)
        {
            case CompressionTypeNone:
                if (compressed.Length != expectedSize)
                    throw new InvalidDataException($"Uncompressed {what} size mismatch.");
                return compressed;

            case CompressionTypeLz4:
            case CompressionTypeLz4Hc:

                byte[] outBuf = new byte[expectedSize];
                int written = LZ4Codec.Decode(compressed, 0, compressed.Length, outBuf, 0, outBuf.Length);
                if (written != expectedSize)
                    throw new InvalidDataException(
                        $"LZ4 decode of {what} produced {written} bytes, expected {expectedSize}.");
                return outBuf;

            default:
                throw new NotSupportedException(
                    $"{what} uses unsupported compression type {compressionType} " +
                    "(expected Pack() to emit none/LZ4/LZ4HC only).");
        }
    }

    private static int AlignUp16(int pos) => (pos + 15) & ~15;

    private static void ReadSignature(byte[] buf, ref int pos)
    {
        const string sig = "UnityFS\0";
        if (pos + sig.Length > buf.Length ||
            Encoding.ASCII.GetString(buf, pos, sig.Length) != sig)
        {
            throw new InvalidDataException("Pack()'d bundle is missing the UnityFS signature.");
        }
        pos += sig.Length;
    }

    private static uint ReadU32(byte[] buf, ref int pos)
    {
        if (pos + 4 > buf.Length) throw new InvalidDataException("Truncated bundle (u32).");
        uint v = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos, 4));
        pos += 4;
        return v;
    }

    private static ushort ReadU16(byte[] buf, ref int pos)
    {
        if (pos + 2 > buf.Length) throw new InvalidDataException("Truncated bundle (u16).");
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos, 2));
        pos += 2;
        return v;
    }

    private static long ReadI64(byte[] buf, ref int pos)
    {
        if (pos + 8 > buf.Length) throw new InvalidDataException("Truncated bundle (i64).");
        long v = BinaryPrimitives.ReadInt64BigEndian(buf.AsSpan(pos, 8));
        pos += 8;
        return v;
    }

    private static string ReadCString(byte[] buf, ref int pos)
    {
        int start = pos;
        while (pos < buf.Length && buf[pos] != 0) pos++;
        if (pos >= buf.Length) throw new InvalidDataException("Unterminated string in bundle.");
        string s = Encoding.UTF8.GetString(buf, start, pos - start);
        pos += 1;
        return s;
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteI64(Stream s, long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteCString(Stream s, string v)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(v);
        s.Write(bytes, 0, bytes.Length);
        s.WriteByte(0);
    }

    private static void WriteBytes(Stream s, byte[] v) => s.Write(v, 0, v.Length);

    private static void PadTo16(Stream s)
    {
        while (s.Position % 16 != 0) s.WriteByte(0);
    }
}

