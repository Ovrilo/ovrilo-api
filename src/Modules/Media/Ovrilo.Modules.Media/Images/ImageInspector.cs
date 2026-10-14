using System.Buffers.Binary;

namespace Ovrilo.Modules.Media.Images;

/// <summary>Format and pixel size read from an image header.</summary>
internal sealed record ImageInfo(string ContentType, int Width, int Height);

/// <summary>
/// Identifies PNG, JPEG, GIF and WebP from their leading bytes and reads their pixel size, without decoding
/// pixels. Anything else (SVG, HTML, executables, truncated headers) is rejected: the declared MIME type and
/// filename are never trusted (ADR-0014). This is a format check, not a full decode or malware scan.
/// </summary>
internal static class ImageInspector
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string WebP = "image/webp";

    /// <summary>Bytes to read from the start of an object: enough to get past large JPEG EXIF/ICC segments.</summary>
    public const int HeaderBytes = 256 * 1024;

    public static readonly IReadOnlySet<string> SupportedContentTypes = new HashSet<string>(StringComparer.Ordinal) { Png, Jpeg, Gif, WebP };

    public static ImageInfo? Inspect(ReadOnlySpan<byte> data) =>
        TryPng(data) ?? TryGif(data) ?? TryWebP(data) ?? TryJpeg(data);

    private static ImageInfo? TryPng(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        // Signature, then the IHDR chunk: length, type, width, height (big-endian).
        if (data.Length < 24 || !data.StartsWith(signature) || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        return Create(Png, BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadInt32BigEndian(data[20..]));
    }

    private static ImageInfo? TryGif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 || !(data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)))
        {
            return null;
        }

        // Logical screen size (little-endian).
        return Create(Gif, BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
    }

    private static ImageInfo? TryWebP(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30 || !data.StartsWith("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return null;
        }

        var chunk = data.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            // Extended (also animated): 24-bit canvas width-1 and height-1.
            return Create(WebP, ReadUInt24(data[24..]) + 1, ReadUInt24(data[27..]) + 1);
        }

        if (chunk.SequenceEqual("VP8 "u8) && data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
        {
            // Lossy key frame: 14-bit width and height after the start code.
            return Create(WebP, BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF);
        }

        if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2F)
        {
            // Lossless: 14-bit width-1 and height-1 packed into 4 bytes.
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            return Create(WebP, (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        return null;
    }

    private static ImageInfo? TryJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8 || data[2] != 0xFF)
        {
            return null;
        }

        // Walk marker segments until a start-of-frame segment, which holds the size.
        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
            {
                return null;
            }

            var marker = data[offset + 1];
            if (marker == 0xFF)
            {
                // Fill byte.
                offset++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                // Markers without a length.
                offset += 2;
                continue;
            }

            if (marker == 0xD9 || marker == 0xDA)
            {
                // End of image or start of scan before any frame header.
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            if (length < 2)
            {
                return null;
            }

            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isStartOfFrame)
            {
                if (offset + 9 > data.Length)
                {
                    return null;
                }

                return Create(Jpeg, BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..]));
            }

            offset += 2 + length;
        }

        return null;
    }

    private static int ReadUInt24(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);

    private static ImageInfo? Create(string contentType, int width, int height) =>
        width > 0 && height > 0 ? new ImageInfo(contentType, width, height) : null;
}
