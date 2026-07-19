using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ValheimOne.LiveMap;

internal static class PngEncoder
{
    private static readonly byte[] Signature =
    {
        137, 80, 78, 71, 13, 10, 26, 10,
    };

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void WriteRgba(
        string path,
        byte[] rgba,
        int width,
        int height,
        Func<bool>? shouldStop = null)
    {
        ValidateRgba(rgba, width, height);

        string temporaryPath = path + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                WritePng(output, rgba, width, height, shouldStop);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temporaryPath, path);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static byte[] EncodeRgba(
        byte[] rgba,
        int width,
        int height,
        Func<bool>? shouldStop = null)
    {
        ValidateRgba(rgba, width, height);
        using (var output = new MemoryStream())
        {
            WritePng(output, rgba, width, height, shouldStop);
            return output.ToArray();
        }
    }

    private static void ValidateRgba(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException("RGBA buffer dimensions do not match its length.", nameof(rgba));
        }
    }

    private static void WritePng(
        Stream output,
        byte[] rgba,
        int width,
        int height,
        Func<bool>? shouldStop)
    {
        output.Write(Signature, 0, Signature.Length);

        var ihdr = new byte[13];
        WriteUInt32BigEndian(ihdr, 0, (uint)width);
        WriteUInt32BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR", ihdr);

        byte[] idat = CompressImage(rgba, width, height, shouldStop);
        WriteChunk(output, "IDAT", idat);
        WriteChunk(output, "IEND", Array.Empty<byte>());
    }

    private static byte[] CompressImage(
        byte[] rgba,
        int width,
        int height,
        Func<bool>? shouldStop)
    {
        using (var compressed = new MemoryStream())
        {
            uint adlerA = 1;
            uint adlerB = 0;
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                byte[] filter = { 0 };
                int stride = width * 4;
                for (int row = 0; row < height; row++)
                {
                    if (shouldStop?.Invoke() == true)
                    {
                        throw new OperationCanceledException();
                    }

                    deflate.Write(filter, 0, 1);
                    UpdateAdler(filter, 0, 1, ref adlerA, ref adlerB);

                    int offset = row * stride;
                    deflate.Write(rgba, offset, stride);
                    UpdateAdler(rgba, offset, stride, ref adlerA, ref adlerB);
                }
            }

            byte[] deflated = compressed.ToArray();
            var zlib = new byte[checked(deflated.Length + 6)];
            zlib[0] = 0x78;
            zlib[1] = 0x9c;
            Buffer.BlockCopy(deflated, 0, zlib, 2, deflated.Length);
            uint adler = (adlerB << 16) | adlerA;
            WriteUInt32BigEndian(zlib, zlib.Length - 4, adler);
            return zlib;
        }
    }

    private static void UpdateAdler(
        byte[] data,
        int offset,
        int count,
        ref uint adlerA,
        ref uint adlerB)
    {
        const uint modulus = 65521;
        int end = offset + count;
        while (offset < end)
        {
            int blockEnd = Math.Min(offset + 5552, end);
            while (offset < blockEnd)
            {
                adlerA += data[offset++];
                adlerB += adlerA;
            }

            adlerA %= modulus;
            adlerB %= modulus;
        }
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        WriteUInt32BigEndian(output, (uint)data.Length);
        output.Write(typeBytes, 0, typeBytes.Length);
        output.Write(data, 0, data.Length);

        uint crc = 0xffffffff;
        crc = UpdateCrc(crc, typeBytes, 0, typeBytes.Length);
        crc = UpdateCrc(crc, data, 0, data.Length);
        WriteUInt32BigEndian(output, crc ^ 0xffffffff);
    }

    private static uint UpdateCrc(uint crc, byte[] data, int offset, int count)
    {
        int end = offset + count;
        for (int index = offset; index < end; index++)
        {
            crc = CrcTable[(crc ^ data[index]) & 0xff] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static void WriteUInt32BigEndian(Stream output, uint value)
    {
        var bytes = new byte[4];
        WriteUInt32BigEndian(bytes, 0, value);
        output.Write(bytes, 0, bytes.Length);
    }

    private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup must not hide the original encoding failure.
        }
    }
}
