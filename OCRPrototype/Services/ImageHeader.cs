using System.Buffers.Binary;

namespace OCRPrototype.Services;

// Inspect dimensions before invoking a native decoder. Only JPEG and PNG are accepted.
public readonly record struct ImageHeader(int Width, int Height, double? Dpi)
{
    public static bool TryRead(ReadOnlySpan<byte> data, out ImageHeader header)
    {
        header = default;
        if (data.Length >= 33 && data[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
        {
            if (!data.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
            uint w = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
            uint h = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return false;
            double? dpi = null;
            for (int p = 8; p <= data.Length - 12;)
            {
                uint size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(p, 4));
                if (size > data.Length - p - 12) return false;
                if (data.Slice(p + 4, 4).SequenceEqual("pHYs"u8) && size == 9 && data[p + 16] == 1)
                    dpi = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(p + 8, 4)) * .0254;
                if (data.Slice(p + 4, 4).SequenceEqual("IDAT"u8)) break;
                p += (int)size + 12;
            }
            header = new((int)w, (int)h, dpi); return true;
        }
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) return false;
        double? jpegDpi = null;
        for (int p = 2; p < data.Length;)
        {
            if (data[p++] != 0xff) return false;
            while (p < data.Length && data[p] == 0xff) p++;
            if (p >= data.Length) return false;
            byte marker = data[p++];
            if (marker is 0xd9 or 0xda) return false;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (p > data.Length - 2) return false;
            int len = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(p, 2));
            if (len < 2 || len > data.Length - p) return false;
            if (marker == 0xe0 && len >= 16 && data.Slice(p + 2, 5).SequenceEqual("JFIF\0"u8))
            {
                int density = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(p + 10, 2));
                jpegDpi = data[p + 9] switch { 1 => density, 2 => density * 2.54, _ => null };
            }
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (len < 8) return false;
                int h = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(p + 3, 2));
                int w = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(p + 5, 2));
                header = new(w, h, jpegDpi); return w > 0 && h > 0;
            }
            p += len;
        }
        return false;
    }
}
