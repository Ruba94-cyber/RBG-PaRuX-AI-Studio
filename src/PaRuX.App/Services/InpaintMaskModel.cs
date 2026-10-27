using System;
using System.IO;
using System.IO.Compression;

namespace PaRuX.Services
{
    /// <summary>
    /// The mask the user paints over the Create > Edit image: a low-resolution grid with the source's aspect ratio
    /// (long side at most <see cref="MaxSide"/> cells), 255 = repaint, 0 = keep. Pure, so brush, erase, invert, clear and
    /// the PNG sent to PaRuX Core are all unit-testable without WinUI. The server scales the mask to the working image,
    /// so a coarse grid is enough and keeps the upload small.
    /// </summary>
    public sealed class InpaintMaskModel
    {
        public const int MaxSide = 512;
        public const int MinSide = 8;
        public const byte Painted = 255;

        private readonly byte[] _cells;

        public int Width { get; }
        public int Height { get; }

        public InpaintMaskModel(int width, int height)
        {
            if (width < MinSide || height < MinSide) throw new ArgumentOutOfRangeException(nameof(width), "The mask grid must be at least " + MinSide + " cells per side.");
            if (width > 4096 || height > 4096) throw new ArgumentOutOfRangeException(nameof(width), "The mask grid is too large.");
            Width = width;
            Height = height;
            _cells = new byte[width * height];
        }

        /// <summary>A grid with the source image's proportions, at most <see cref="MaxSide"/> cells on the long side.</summary>
        public static InpaintMaskModel ForSource(int sourceWidth, int sourceHeight)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth), "The source image has no size.");
            double scale = (double)MaxSide / Math.Max(sourceWidth, sourceHeight);
            int w = Math.Clamp((int)Math.Round(sourceWidth * scale), MinSide, MaxSide);
            int h = Math.Clamp((int)Math.Round(sourceHeight * scale), MinSide, MaxSide);
            return new InpaintMaskModel(w, h);
        }

        public byte this[int x, int y] => _cells[y * Width + x];

        /// <summary>Brush radius in cells for a slider value of 1..100 (percent of the short side, at least one cell).</summary>
        public double RadiusFor(double brushSizePercent)
        {
            double fraction = Math.Clamp(brushSizePercent, 1, 100) / 100.0;
            return Math.Max(1.0, fraction * Math.Min(Width, Height) / 2.0);
        }

        /// <summary>Paints (or erases) a filled circle centred on cell coordinates (x, y). Out-of-range centres are clipped.</summary>
        public void Stamp(double x, double y, double radius, bool erase)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(radius)) return;
            radius = Math.Max(0.5, radius);
            int x0 = Math.Max(0, (int)Math.Floor(x - radius)), x1 = Math.Min(Width - 1, (int)Math.Ceiling(x + radius));
            int y0 = Math.Max(0, (int)Math.Floor(y - radius)), y1 = Math.Min(Height - 1, (int)Math.Ceiling(y + radius));
            double r2 = radius * radius;
            byte value = erase ? (byte)0 : Painted;
            for (int cy = y0; cy <= y1; cy++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    double dx = cx + 0.5 - x, dy = cy + 0.5 - y;
                    if (dx * dx + dy * dy <= r2) _cells[cy * Width + cx] = value;
                }
        }

        /// <summary>Stamps along a drag so a fast pointer leaves a continuous stroke, not dots.</summary>
        public void StampLine(double x0, double y0, double x1, double y1, double radius, bool erase)
        {
            double dx = x1 - x0, dy = y1 - y0;
            double length = Math.Sqrt(dx * dx + dy * dy);
            int steps = Math.Max(1, (int)Math.Ceiling(length / Math.Max(0.5, radius / 2.0)));
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                Stamp(x0 + dx * t, y0 + dy * t, radius, erase);
            }
        }

        public void Clear() => Array.Clear(_cells, 0, _cells.Length);

        /// <summary>Swaps repaint and keep: what was painted is now kept, everything else is repainted.</summary>
        public void Invert()
        {
            for (int i = 0; i < _cells.Length; i++) _cells[i] = (byte)(Painted - _cells[i]);
        }

        /// <summary>Share of the image that will be repainted, 0..1.</summary>
        public double PaintedFraction
        {
            get
            {
                int count = 0;
                foreach (byte cell in _cells) if (cell >= 128) count++;
                return (double)count / _cells.Length;
            }
        }

        public bool HasSelection => PaintedFraction > 0;

        /// <summary>An 8-bit grayscale PNG of the grid (white = repaint). Written without any imaging library.</summary>
        public byte[] ToPng()
        {
            using var raw = new MemoryStream();
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int y = 0; y < Height; y++)
                {
                    z.WriteByte(0); // filter: none
                    z.Write(_cells, y * Width, Width);
                }
            }

            using var png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var header = new byte[13];
            WriteBigEndian(header, 0, Width);
            WriteBigEndian(header, 4, Height);
            header[8] = 8;  // bit depth
            header[9] = 0;  // colour type: grayscale
            WriteChunk(png, "IHDR", header);
            WriteChunk(png, "IDAT", raw.ToArray());
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, data.Length);
            stream.Write(length);
            var typeBytes = new byte[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            stream.Write(typeBytes);
            stream.Write(data);
            uint crc = Crc32(typeBytes, data);
            var crcBytes = new byte[4];
            WriteBigEndian(crcBytes, 0, unchecked((int)crc));
            stream.Write(crcBytes);
        }

        private static void WriteBigEndian(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value >> 24);
            target[offset + 1] = (byte)(value >> 16);
            target[offset + 2] = (byte)(value >> 8);
            target[offset + 3] = (byte)value;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] type, byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
