using System;
using System.Collections.Generic;

namespace AkaiS950List
{
    /// <summary>
    /// In-place MFM patching. A sector's data field is rewritten where it already
    /// sits: sync marks, ID fields, gaps and track timing are never touched, and
    /// the replacement occupies exactly the same span because every Akai sector is
    /// 1024 bytes. That removes the whole problem of re-laying out a track.
    /// </summary>
    public static class HfeWrite
    {
        static byte[] Unpack(byte[] cells)
        {
            var bits = new byte[cells.Length * 8];
            for (int i = 0; i < cells.Length; i++)
                for (int b = 0; b < 8; b++) bits[i * 8 + b] = (byte)((cells[i] >> b) & 1);
            return bits;
        }

        public static byte[] Pack(byte[] bits)
        {
            var cells = new byte[bits.Length / 8];
            for (int i = 0; i < cells.Length; i++)
            {
                int c = 0;
                for (int b = 0; b < 8; b++) c |= bits[i * 8 + b] << b;
                cells[i] = (byte)c;
            }
            return cells;
        }

        static byte ReadByte(byte[] bits, int g)
        {
            int n = bits.Length, v = 0;
            for (int i = 0; i < 8; i++) v = (v << 1) | bits[(g + 2 * i + 1) % n];
            return (byte)v;
        }

        /// <summary>MFM: a clock bit is set only between two zero data bits.</summary>
        static void WriteByte(byte[] bits, int g, byte v, ref int prev)
        {
            int n = bits.Length;
            for (int i = 0; i < 8; i++)
            {
                int d = (v >> (7 - i)) & 1;
                bits[(g + 2 * i) % n] = (byte)((prev == 0 && d == 0) ? 1 : 0);
                bits[(g + 2 * i + 1) % n] = (byte)d;
                prev = d;
            }
        }

        static ushort Crc(byte[] d, int len, ushort crc)
        {
            for (int i = 0; i < len; i++)
            {
                crc ^= (ushort)(d[i] << 8);
                for (int b = 0; b < 8; b++)
                    crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
            return crc;
        }

        static readonly byte[] A1x3 = { 0xA1, 0xA1, 0xA1 };

        public sealed class Field
        {
            public int Cyl, Head, Sec, Size;
            public int DataBitPos;      // bit offset of the data field's first cell group
            public byte Mark;
        }

        /// <summary>Locate every data field on a track, with its bit offset.</summary>
        public static List<Field> Scan(byte[] cells)
        {
            var outp = new List<Field>();
            if (cells.Length == 0) return outp;
            byte[] bits = Unpack(cells);
            int n = bits.Length;
            uint sr = 0; int lastSync = -100, run = 0;
            Field pending = null;

            for (int p = 0; p < n; p++)
            {
                sr = (sr << 1) | bits[p];
                if ((sr & 0xFFFF) != 0x4489) continue;
                run = (p - lastSync == 16) ? run + 1 : 1;
                lastSync = p;
                if (run < 3) continue;

                int g = p + 1;
                byte mark = ReadByte(bits, g); g += 16;

                if (mark == 0xFE)
                {
                    var id = new byte[4];
                    for (int i = 0; i < 4; i++) { id[i] = ReadByte(bits, g); g += 16; }
                    g += 32;
                    pending = new Field { Cyl = id[0], Head = id[1], Sec = id[2], Size = 128 << (id[3] & 7) };
                }
                else if ((mark == 0xFB || mark == 0xF8) && pending != null)
                {
                    pending.DataBitPos = g;
                    pending.Mark = mark;
                    outp.Add(pending);
                    pending = null;
                }
            }
            return outp;
        }

        /// <summary>
        /// Replace one sector's payload in the bit array. Returns the bits so the
        /// caller can repack. The CRC is recomputed over A1 A1 A1 + mark + data.
        /// </summary>
        public static void PatchField(byte[] bits, Field f, byte[] data)
        {
            if (data.Length != f.Size) throw new ArgumentException("sector size mismatch");
            int n = bits.Length;
            int g = f.DataBitPos;
            int prev = bits[(g - 1 + n) % n];       // last data bit of the address mark

            for (int i = 0; i < data.Length; i++) { WriteByte(bits, g, data[i], ref prev); g += 16; }

            ushort c = Crc(A1x3, 3, 0xFFFF);
            c = Crc(new[] { f.Mark }, 1, c);
            c = Crc(data, data.Length, c);
            WriteByte(bits, g, (byte)(c >> 8), ref prev); g += 16;
            WriteByte(bits, g, (byte)(c & 0xFF), ref prev); g += 16;

            // The next cell's clock depends on the last CRC data bit, so fix it.
            int d0 = bits[(g + 1) % n];
            bits[g % n] = (byte)((prev == 0 && d0 == 0) ? 1 : 0);
        }

        // ------------------------------------------------------- building from nothing

        static readonly byte[] HfeHeader = {
            0x48, 0x58, 0x43, 0x50, 0x49, 0x43, 0x46, 0x45,   // HXCPICFE
            0x00,           // format revision
            0x50,           // 80 tracks
            0x02,           // 2 sides
            0x00,           // ISOIBM_MFM
            0xFA, 0x00,     // 250 kbps
            0x00, 0x00,     // rpm, unused
            0x0C,           // interface mode
            0x01,           // unused
            0x01, 0x00      // track list at block 1
        };

        const int SectorsPerTrack = 5, SectorSize = 1024;
        const int TrackBytes = 25000;      // both sides, interleaved
        const int SideBytes = 6250;        // MFM bytes per side
        const int TrackStride = 25088;     // 49 blocks of 512
        const byte Gap = 0x4E, Sync = 0x00, IamMark = 0xFC, Idam = 0xFE, Dam = 0xFB;

        /// <summary>The MFM cell stream for one side of one track, laid out from scratch.</summary>
        static byte[] BuildSide(byte[] image, int cyl, int head, int sides)
        {
            var bits = new byte[SideBytes * 16];
            int at = 0, prev = 0;

            Action<byte, int> put = (value, times) =>
            {
                for (int i = 0; i < times; i++) { WriteByte(bits, at, value, ref prev); at += 16; }
            };

            // A1 and C2 sync bytes carry a deliberately missing clock, so they cannot go
            // through WriteByte - the pattern is written straight into the cells.
            Action<int, int> putSync = (pattern, times) =>
            {
                for (int i = 0; i < times; i++)
                {
                    for (int b = 0; b < 16; b++) bits[at + b] = (byte)((pattern >> (15 - b)) & 1);
                    at += 16;
                    prev = pattern & 1;
                }
            };

            put(Gap, 230);
            put(Sync, 12);
            putSync(0x5224, 3);        // C2 C2 C2
            put(IamMark, 1);
            put(Gap, 50);

            for (int s = 1; s <= SectorsPerTrack; s++)
            {
                int lba = (cyl * sides + head) * SectorsPerTrack + (s - 1);

                // --- ID field
                put(Sync, 12);
                putSync(0x4489, 3);    // A1 A1 A1
                put(Idam, 1);

                var id = new byte[] { (byte)cyl, (byte)head, (byte)s, 3 };
                for (int i = 0; i < id.Length; i++) put(id[i], 1);

                ushort c = Crc(A1x3, 3, 0xFFFF);
                c = Crc(new byte[] { Idam }, 1, c);
                c = Crc(id, id.Length, c);
                put((byte)(c >> 8), 1);
                put((byte)(c & 0xFF), 1);

                put(Gap, 22);

                // --- data field
                put(Sync, 12);
                putSync(0x4489, 3);
                put(Dam, 1);

                var data = new byte[SectorSize];
                int from = lba * SectorSize;
                if (from >= 0 && from + SectorSize <= image.Length)
                    Array.Copy(image, from, data, 0, SectorSize);

                for (int i = 0; i < SectorSize; i++) put(data[i], 1);

                ushort d = Crc(A1x3, 3, 0xFFFF);
                d = Crc(new byte[] { Dam }, 1, d);
                d = Crc(data, data.Length, d);
                put((byte)(d >> 8), 1);
                put((byte)(d & 0xFF), 1);

                put(Gap, 86);
            }

            put(Gap, 94);

            if (at != bits.Length)
                throw new InvalidOperationException(
                    "track came to " + (at / 16) + " bytes, expected " + SideBytes);

            return Pack(bits);
        }

        /// <summary>
        /// An HFE container holding this sector image. The inverse of Hfe.Extract, and the
        /// only way to write a disk that was opened from a raw .img.
        /// </summary>
        public static byte[] BuildHfe(byte[] image, int tracks, int sides)
        {
            if (tracks <= 0) tracks = 80;
            if (sides <= 0) sides = 2;

            int size = 1024 + tracks * TrackStride;
            var outp = new byte[size];

            for (int i = 0; i < 1024; i++) outp[i] = 0xFF;
            Array.Copy(HfeHeader, 0, outp, 0, HfeHeader.Length);
            outp[9] = (byte)tracks;
            outp[10] = (byte)sides;

            // Each side is 12500 cell bytes, which is 48 whole 256-byte chunks and then 212 of
            // one - so 44 bytes of every side's last chunk are padding that WriteSideCells never
            // touches. The drive that wrote the library left its gap fill running through them,
            // and 0x49 0x2A is exactly MFM-encoded 0x4E in this cell packing, so filling the
            // track area with that pattern first reproduces the padding instead of leaving holes.
            for (int i = 1024; i < size; i++) outp[i] = (byte)((i & 1) != 0 ? 0x2A : 0x49);

            // track list: offset in 512-byte blocks, then length in bytes
            for (int t = 0; t < tracks; t++)
            {
                int off = (1024 + t * TrackStride) / 512;
                int at = 512 + t * 4;
                outp[at] = (byte)(off & 0xFF);
                outp[at + 1] = (byte)((off >> 8) & 0xFF);
                outp[at + 2] = (byte)(TrackBytes & 0xFF);
                outp[at + 3] = (byte)((TrackBytes >> 8) & 0xFF);
            }

            for (int t = 0; t < tracks; t++)
                for (int s = 0; s < sides; s++)
                    HfeImage.WriteSideCells(outp, t, s, BuildSide(image, t, s, sides));

            return outp;
        }
        public static byte[] BitsOf(byte[] cells) { return Unpack(cells); }

        public static byte[] ReadField(byte[] bits, Field f)
        {
            var d = new byte[f.Size];
            int g = f.DataBitPos;
            for (int i = 0; i < d.Length; i++) { d[i] = ReadByte(bits, g); g += 16; }
            return d;
        }
    }
}

namespace AkaiS950List
{
    public static class HfeImage
    {
        static ushort U16(byte[] b, int o) { return (ushort)(b[o] | (b[o + 1] << 8)); }

        /// <summary>Inverse of Hfe.SideCells: put a side's cells back into the interleaved blocks.</summary>
        public static void WriteSideCells(byte[] img, int track, int side, byte[] cells)
        {
            int lut = U16(img, 18) * 512;
            int tOff = U16(img, lut + track * 4) * 512;
            int tLen = U16(img, lut + track * 4 + 2);
            int half = tLen / 2;
            int w = 0, pos = tOff;
            while (w < half && w < cells.Length)
            {
                int chunk = System.Math.Min(256, half - w);
                int dst = pos + (side == 0 ? 0 : 256);
                if (dst + chunk > img.Length) break;
                System.Array.Copy(cells, w, img, dst, chunk);
                w += chunk;
                pos += 512;
            }
        }
    }
}
