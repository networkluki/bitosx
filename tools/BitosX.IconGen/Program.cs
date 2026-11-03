using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

// Draws the BitosX icon and writes every file made from it.
//
// The icon is a padlock with a keyhole on a rounded square in the two accent colours of the
// application (App.axaml). It is drawn here from that geometry alone — rounded rectangles, a ring
// and line segments — with nothing but arithmetic and a square root, so that the same pixels come
// out on every machine. Each pixel is the average of a grid of samples inside it, which is what
// smooths the edges.
//
// Usage (from the repository root):
//   dotnet run --project tools/BitosX.IconGen            writes the files
//   dotnet run --project tools/BitosX.IconGen -- --check exits with 1 if a file on disk differs
//
// --check compares pixels, not bytes: the PNG data is deflated, and a later version of the
// compressor may pack the same pixels differently.

bool check = args.Contains("--check", StringComparer.Ordinal);
string root = FindRepositoryRoot(Directory.GetCurrentDirectory());

int differences = 0;
foreach ((string relative, byte[] content) in IconFiles.All())
{
    string path = Path.Combine(root, relative);
    if (check)
    {
        bool same = File.Exists(path) && Canonical.Of(File.ReadAllBytes(path)).AsSpan().SequenceEqual(Canonical.Of(content));
        Console.WriteLine((same ? "same     " : "DIFFERS  ") + relative);
        differences += same ? 0 : 1;
    }
    else
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote    {relative} ({content.Length} bytes)"));
    }
}

if (differences > 0)
{
    Console.Error.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"{differences} file(s) are not what tools/BitosX.IconGen draws. Run it without --check and commit the result."));
    return 1;
}

return 0;

static string FindRepositoryRoot(string start)
{
    for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "BitosX.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new DirectoryNotFoundException("Run this from inside the BitosX repository (no BitosX.slnx found above the current directory).");
}

/// <summary>The geometry of the icon, on a square from (0, 0) at the top left to (1, 1).</summary>
internal static class Design
{
    // The rounded square, and its gradient from the top-left corner to the bottom-right one.
    public const double Inset = 0.03;
    public const double TileRadius = 0.21;
    public static readonly Rgb From = new(0x5B, 0x8D, 0xEF); // BitosXAccent
    public static readonly Rgb To = new(0x22, 0xD3, 0xA8);   // BitosXAccentAlt

    // The padlock.
    public static readonly Rgb Lock = new(0xF5, 0xF7, 0xFA);
    public const double ShackleX = 0.5;
    public const double ShackleY = 0.41;       // the centre of the ring the shackle's arch is part of
    public const double ShackleRadius = 0.15;
    public const double ShackleHalfWidth = 0.055;
    public const double ShackleBottom = 0.52;  // the legs end inside the body
    public const double BodyLeft = 0.25;
    public const double BodyTop = 0.47;
    public const double BodyRight = 0.75;
    public const double BodyBottom = 0.81;
    public const double BodyRadius = 0.07;

    // The keyhole, cut out of the body (it shows the gradient behind it). Too small to read below
    // this many pixels, where the padlock is drawn without it.
    public const int SmallestWithKeyhole = 24;
    public const double KeyholeX = 0.5;
    public const double KeyholeY = 0.6;
    public const double KeyholeRadius = 0.052;
    public const double KeyholeStemBottom = 0.715;
    public const double KeyholeStemHalfWidth = 0.024;

    /// <summary>The colour at a point, or <see langword="null"/> outside the icon.</summary>
    public static Rgb? At(double x, double y, bool keyhole)
    {
        if (!InRoundedRectangle(x, y, Inset, Inset, 1 - Inset, 1 - Inset, TileRadius))
        {
            return null;
        }

        bool inLock = InRoundedRectangle(x, y, BodyLeft, BodyTop, BodyRight, BodyBottom, BodyRadius) || InShackle(x, y);
        if (inLock && !(keyhole && InKeyhole(x, y)))
        {
            return Lock;
        }

        double t = Math.Clamp((x + y - (2 * Inset)) / (2 * (1 - (2 * Inset))), 0, 1);
        return Rgb.Mix(From, To, t);
    }

    private static bool InShackle(double x, double y)
    {
        double w2 = ShackleHalfWidth * ShackleHalfWidth;
        if (y <= ShackleY)
        {
            // The arch: the upper half of a ring.
            double distance = Math.Abs(Math.Sqrt(Square(x - ShackleX) + Square(y - ShackleY)) - ShackleRadius);
            return distance <= ShackleHalfWidth;
        }

        return DistanceSquaredToSegment(x, y, ShackleX - ShackleRadius, ShackleY, ShackleX - ShackleRadius, ShackleBottom) <= w2
            || DistanceSquaredToSegment(x, y, ShackleX + ShackleRadius, ShackleY, ShackleX + ShackleRadius, ShackleBottom) <= w2;
    }

    private static bool InKeyhole(double x, double y)
        => Square(x - KeyholeX) + Square(y - KeyholeY) <= Square(KeyholeRadius)
           || DistanceSquaredToSegment(x, y, KeyholeX, KeyholeY, KeyholeX, KeyholeStemBottom) <= Square(KeyholeStemHalfWidth);

    private static bool InRoundedRectangle(double x, double y, double left, double top, double right, double bottom, double radius)
    {
        if (x < left || x > right || y < top || y > bottom)
        {
            return false;
        }

        double dx = Math.Max(Math.Max(left + radius - x, x - (right - radius)), 0);
        double dy = Math.Max(Math.Max(top + radius - y, y - (bottom - radius)), 0);
        return Square(dx) + Square(dy) <= Square(radius);
    }

    private static double DistanceSquaredToSegment(double x, double y, double x1, double y1, double x2, double y2)
    {
        double vx = x2 - x1;
        double vy = y2 - y1;
        double t = Math.Clamp((((x - x1) * vx) + ((y - y1) * vy)) / (Square(vx) + Square(vy)), 0, 1);
        return Square(x - (x1 + (t * vx))) + Square(y - (y1 + (t * vy)));
    }

    private static double Square(double v) => v * v;

    /// <summary>The same drawing as a scalable vector file, for places that take one.</summary>
    public static string Svg()
    {
        const double S = 100; // the unit square, scaled to 0..100
        static string N(double v) => (v * S).ToString("0.###", CultureInfo.InvariantCulture);
        double legLeft = ShackleX - ShackleRadius;
        double legRight = ShackleX + ShackleRadius;
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\" width=\"512\" height=\"512\">\n");
        svg.Append("  <title>BitosX</title>\n");
        svg.Append("  <!-- Generated by tools/BitosX.IconGen. Do not edit: change the geometry there and run it. -->\n");
        svg.Append(CultureInfo.InvariantCulture, $"  <defs><linearGradient id=\"tile\" gradientUnits=\"userSpaceOnUse\" x1=\"{N(Inset)}\" y1=\"{N(Inset)}\" x2=\"{N(1 - Inset)}\" y2=\"{N(1 - Inset)}\">");
        svg.Append(CultureInfo.InvariantCulture, $"<stop offset=\"0\" stop-color=\"{From}\"/><stop offset=\"1\" stop-color=\"{To}\"/></linearGradient></defs>\n");
        svg.Append(CultureInfo.InvariantCulture, $"  <rect x=\"{N(Inset)}\" y=\"{N(Inset)}\" width=\"{N(1 - (2 * Inset))}\" height=\"{N(1 - (2 * Inset))}\" rx=\"{N(TileRadius)}\" fill=\"url(#tile)\"/>\n");
        svg.Append(CultureInfo.InvariantCulture, $"  <path d=\"M{N(legLeft)} {N(ShackleBottom)} V{N(ShackleY)} A{N(ShackleRadius)} {N(ShackleRadius)} 0 0 1 {N(legRight)} {N(ShackleY)} V{N(ShackleBottom)}\" fill=\"none\" stroke=\"{Lock}\" stroke-width=\"{N(2 * ShackleHalfWidth)}\"/>\n");
        svg.Append(CultureInfo.InvariantCulture, $"  <rect x=\"{N(BodyLeft)}\" y=\"{N(BodyTop)}\" width=\"{N(BodyRight - BodyLeft)}\" height=\"{N(BodyBottom - BodyTop)}\" rx=\"{N(BodyRadius)}\" fill=\"{Lock}\"/>\n");
        svg.Append(CultureInfo.InvariantCulture, $"  <circle cx=\"{N(KeyholeX)}\" cy=\"{N(KeyholeY)}\" r=\"{N(KeyholeRadius)}\" fill=\"url(#tile)\"/>\n");
        svg.Append(CultureInfo.InvariantCulture, $"  <path d=\"M{N(KeyholeX)} {N(KeyholeY)} V{N(KeyholeStemBottom)}\" stroke=\"url(#tile)\" stroke-width=\"{N(2 * KeyholeStemHalfWidth)}\" stroke-linecap=\"round\"/>\n");
        svg.Append("</svg>\n");
        return svg.ToString();
    }
}

internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Mix(Rgb a, Rgb b, double t)
        => new(Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));

    private static byte Lerp(byte a, byte b, double t) => (byte)(int)((a + ((b - a) * t)) + 0.5);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");
}

/// <summary>Turns the geometry into pixels.</summary>
internal static class Raster
{
    /// <summary>The icon at <paramref name="size"/> × <paramref name="size"/> pixels, as RGBA rows from the top, alpha not premultiplied.</summary>
    public static byte[] Draw(int size)
    {
        int grid = size <= 64 ? 16 : 8; // samples per pixel along each axis
        int samples = grid * grid;
        bool keyhole = size >= Design.SmallestWithKeyhole;
        byte[] rgba = new byte[size * size * 4];
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                int covered = 0;
                int r = 0;
                int g = 0;
                int b = 0;
                for (int sy = 0; sy < grid; sy++)
                {
                    double y = (py + ((sy + 0.5) / grid)) / size;
                    for (int sx = 0; sx < grid; sx++)
                    {
                        double x = (px + ((sx + 0.5) / grid)) / size;
                        if (Design.At(x, y, keyhole) is { } c)
                        {
                            covered++;
                            r += c.R;
                            g += c.G;
                            b += c.B;
                        }
                    }
                }

                int i = ((py * size) + px) * 4;
                if (covered > 0)
                {
                    rgba[i] = (byte)((r + (covered / 2)) / covered);
                    rgba[i + 1] = (byte)((g + (covered / 2)) / covered);
                    rgba[i + 2] = (byte)((b + (covered / 2)) / covered);
                    rgba[i + 3] = (byte)(((covered * 255) + (samples / 2)) / samples);
                }
            }
        }

        return rgba;
    }
}

/// <summary>The files, as bytes, and where they go.</summary>
internal static class IconFiles
{
    /// <summary>The sizes in the Windows icon: the small ones for lists and title bars at 100, 125 and 150 %, 256 for Explorer.</summary>
    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    private static readonly int[] PngSizes = [16, 32, 48, 64, 128, 256, 512];

    public static IEnumerable<(string Path, byte[] Content)> All()
    {
        yield return ("src/BitosX.App/Assets/bitosx.ico", Ico());
        foreach (int size in PngSizes)
        {
            yield return (string.Create(CultureInfo.InvariantCulture, $"assets/icon/bitosx-{size}.png"), Png.Encode(size, Raster.Draw(size)));
        }

        yield return ("assets/icon/bitosx.svg", Encoding.UTF8.GetBytes(Design.Svg()));
    }

    /// <summary>
    /// A Windows icon with one image per size: 32-bit bitmaps (with the 1-bit mask Windows still
    /// expects after them) up to 64 pixels, and a PNG for 256, as Windows has read since Vista.
    /// </summary>
    private static byte[] Ico()
    {
        byte[][] images = [.. IcoSizes.Select(size => size >= 256 ? Png.Encode(size, Raster.Draw(size)) : Bitmap(size, Raster.Draw(size)))];
        using var ico = new MemoryStream();
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1); // icon
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)images.Length);
        ico.Write(header);

        int offset = 6 + (16 * images.Length);
        Span<byte> entry = stackalloc byte[16];
        for (int i = 0; i < images.Length; i++)
        {
            entry.Clear();
            entry[0] = (byte)(IcoSizes[i] >= 256 ? 0 : IcoSizes[i]); // 0 means 256
            entry[1] = entry[0];
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);   // planes
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);  // bits per pixel
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)images[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);
            ico.Write(entry);
            offset += images[i].Length;
        }

        foreach (byte[] image in images)
        {
            ico.Write(image);
        }

        return ico.ToArray();
    }

    private static byte[] Bitmap(int size, byte[] rgba)
    {
        int maskStride = ((size + 31) / 32) * 4;
        byte[] dib = new byte[40 + (size * size * 4) + (maskStride * size)];
        Span<byte> span = dib;
        BinaryPrimitives.WriteUInt32LittleEndian(span, 40);                  // BITMAPINFOHEADER
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], size);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], size * 2);        // the colours and the mask
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)(dib.Length - 40));

        int pixels = 40;
        int mask = 40 + (size * size * 4);
        for (int row = 0; row < size; row++)
        {
            int y = size - 1 - row; // bottom-up
            for (int x = 0; x < size; x++)
            {
                int from = ((y * size) + x) * 4;
                int to = pixels + (((row * size) + x) * 4);
                dib[to] = rgba[from + 2];
                dib[to + 1] = rgba[from + 1];
                dib[to + 2] = rgba[from];
                dib[to + 3] = rgba[from + 3];
                if (rgba[from + 3] == 0)
                {
                    dib[mask + (row * maskStride) + (x / 8)] |= (byte)(0x80 >> (x % 8));
                }
            }
        }

        return dib;
    }
}

/// <summary>Writes, and reads back, the PNG files this tool makes (8-bit RGBA, no filtering).</summary>
internal static class Png
{
    public static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(int size, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, size);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), size);
        ihdr[8] = 8; // bits per channel
        ihdr[9] = 6; // RGBA
        Chunk(output, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var deflate = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                for (int y = 0; y < size; y++)
                {
                    deflate.WriteByte(0); // filter: none
                    deflate.Write(rgba, y * size * 4, size * 4);
                }
            }

            Chunk(output, "IDAT", raw.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>The width, the height and the RGBA rows of a PNG file this tool wrote.</summary>
    public static (int Width, int Height, byte[] Rgba) Decode(byte[] file)
    {
        if (!file.AsSpan().StartsWith(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        int width = 0;
        int height = 0;
        using var idat = new MemoryStream();
        for (int at = Signature.Length; at + 12 <= file.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
            string type = Encoding.ASCII.GetString(file, at + 4, 4);
            ReadOnlySpan<byte> data = file.AsSpan(at + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                if (data[8] != 8 || data[9] != 6 || data[12] != 0)
                {
                    throw new InvalidDataException("Not an 8-bit RGBA PNG without interlacing.");
                }
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }

            at += 12 + length;
        }

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        byte[] rows = new byte[height * ((width * 4) + 1)];
        inflate.ReadExactly(rows);
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int row = y * ((width * 4) + 1);
            if (rows[row] != 0)
            {
                throw new InvalidDataException("A filtered PNG row: not a file this tool wrote.");
            }

            Array.Copy(rows, row + 1, rgba, y * width * 4, width * 4);
        }

        return (width, height, rgba);
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        byte[] typeAndData = [.. Encoding.ASCII.GetBytes(type), .. data];
        output.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(typeAndData));
        output.Write(number);
    }

    private static uint Crc(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

/// <summary>What a file shows, rather than how it is packed: what <c>--check</c> compares.</summary>
internal static class Canonical
{
    public static byte[] Of(byte[] file)
    {
        if (file.AsSpan().StartsWith(Png.Signature))
        {
            (int width, int height, byte[] rgba) = Png.Decode(file);
            byte[] size = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(size, width);
            BinaryPrimitives.WriteInt32BigEndian(size.AsSpan(4), height);
            return [.. "PNG"u8, .. size, .. rgba];
        }

        if (file.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2)) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(file) == 0)
        {
            // An icon: each image in its own canonical form (bitmaps are compared as they are).
            int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));
            var parts = new List<byte>("ICO"u8.ToArray());
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> entry = file.AsSpan(6 + (16 * i), 16);
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
                int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
                parts.AddRange(entry[..8].ToArray()); // size, colours, planes, bits per pixel
                parts.AddRange(Of(file.AsSpan(offset, length).ToArray()));
            }

            return [.. parts];
        }

        return file;
    }
}
