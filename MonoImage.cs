using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace LabelPrinter;

/// <summary>
/// A black-and-white image at printer resolution (one bit per dot, 1 = black),
/// packed most-significant bit first like ZPL's ^GF graphic field.
/// </summary>
internal sealed class MonoImage
{
    public int Width { get; }
    public int Height { get; }
    public int BytesPerRow { get; }
    public byte[] Bits { get; }

    public MonoImage(int width, int height)
    {
        Width = width;
        Height = height;
        BytesPerRow = (width + 7) / 8;
        Bits = new byte[BytesPerRow * height];
    }

    public bool this[int x, int y]
    {
        get => (Bits[y * BytesPerRow + (x >> 3)] & (0x80 >> (x & 7))) != 0;
        set
        {
            int i = y * BytesPerRow + (x >> 3);
            byte mask = (byte)(0x80 >> (x & 7));
            if (value) Bits[i] |= mask; else Bits[i] &= (byte)~mask;
        }
    }

    /// <summary>Thresholds a bitmap: pixels darker than <paramref name="threshold"/> (0-255 luma) become black.</summary>
    public static MonoImage FromBitmap(Bitmap source, int threshold = 170)
    {
        var image = new MonoImage(source.Width, source.Height);
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[source.Width * 4];
            for (int y = 0; y < source.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < source.Width; x++)
                {
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2], a = row[x * 4 + 3];
                    // Transparent counts as white paper.
                    int luma = (r * 299 + g * 587 + b * 114) / 1000;
                    luma = 255 - (255 - luma) * a / 255;
                    if (luma < threshold)
                        image[x, y] = true;
                }
            }
        }
        finally
        {
            source.UnlockBits(data);
        }
        return image;
    }

    /// <summary>Returns this image turned 90° clockwise.</summary>
    public MonoImage RotateClockwise()
    {
        var rotated = new MonoImage(Height, Width);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (this[x, y])
                    rotated[Height - 1 - y, x] = true;
        return rotated;
    }

    /// <summary>Copies <paramref name="image"/> onto this one with its top-left corner at (left, top).</summary>
    public void Draw(MonoImage image, int left, int top)
    {
        for (int y = 0; y < image.Height; y++)
        {
            int ty = top + y;
            if (ty < 0 || ty >= Height) continue;
            for (int x = 0; x < image.Width; x++)
            {
                int tx = left + x;
                if (tx >= 0 && tx < Width && image[x, y])
                    this[tx, ty] = true;
            }
        }
    }

    public Bitmap ToBitmap()
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[Width];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                    row[x] = this[x, y] ? unchecked((int)0xFF000000) : unchecked((int)0xFFFFFFFF);
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, Width);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    /// <summary>
    /// A complete ZPL label that prints this image at the top-left, with the
    /// label size set to the image size. The graphic uses ZPL's ASCII
    /// compression (":" repeats the previous row, "," ends a row with white,
    /// G-Y / g-z are repeat counts), the same encoding printers already accept
    /// in Mercado Libre labels, which keeps a full label at a few KB.
    /// </summary>
    public string ToZplLabel()
    {
        var sb = new StringBuilder();
        sb.Append("^XA^PW").Append(Width).Append("^LL").Append(Height).Append("^LH0,0^FO0,0");
        sb.Append("^GFA,").Append(Bits.Length).Append(',').Append(Bits.Length).Append(',').Append(BytesPerRow).Append(',');

        string? previous = null;
        var hex = new StringBuilder(BytesPerRow * 2);
        for (int y = 0; y < Height; y++)
        {
            hex.Clear();
            for (int i = 0; i < BytesPerRow; i++)
                hex.Append(Bits[y * BytesPerRow + i].ToString("X2"));
            var rowHex = hex.ToString();

            if (rowHex == previous)
            {
                sb.Append(':');
                continue;
            }
            previous = rowHex;

            var trimmed = rowHex.TrimEnd('0');
            if (trimmed.Length == 0)
            {
                sb.Append(',');
                continue;
            }
            AppendRuns(sb, trimmed);
            if (trimmed.Length < rowHex.Length)
                sb.Append(',');
        }

        sb.Append("^FS^XZ\r\n");
        return sb.ToString();
    }

    private static void AppendRuns(StringBuilder sb, string hex)
    {
        int i = 0;
        while (i < hex.Length)
        {
            char c = hex[i];
            int run = 1;
            while (i + run < hex.Length && hex[i + run] == c)
                run++;
            AppendRun(sb, c, run);
            i += run;
        }
    }

    private static void AppendRun(StringBuilder sb, char c, int count)
    {
        while (count > 0)
        {
            int n = Math.Min(count, 419);
            if (n == 1)
            {
                sb.Append(c);
            }
            else
            {
                int twenties = n / 20, ones = n % 20;
                if (twenties > 0) sb.Append((char)('f' + twenties)); // g = 20 ... z = 400
                if (ones > 0) sb.Append((char)('F' + ones));         // G = 1  ... Y = 19
                sb.Append(c);
            }
            count -= n;
        }
    }
}
