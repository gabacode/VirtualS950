#include "HfeWrite.h"

#include "Hfe.h"

#include <algorithm>
#include <cstring>

namespace s950::hfe
{
    namespace
    {
        constexpr int  SectorsPerTrack = 5;
        constexpr int  SectorSize      = 1024;
        constexpr int  TrackBytes      = 25000;     // both sides, interleaved
        constexpr int  SideBytes       = 6250;      // MFM bytes per side
        constexpr int  TrackStride     = 25088;     // 49 blocks of 512

        constexpr unsigned char Gap = 0x4E, Sync = 0x00, IamMark = 0xFC, Idam = 0xFE, Dam = 0xFB;

        const unsigned char A1x3[3] = { 0xA1, 0xA1, 0xA1 };

        const unsigned char HfeHeader[] = {
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

        std::uint16_t u16 (const std::vector<unsigned char>& b, std::size_t at)
        {
            if (at + 1 >= b.size()) return 0;
            return static_cast<std::uint16_t> (b[at] | (b[at + 1] << 8));
        }

        unsigned char readByteAt (const std::vector<unsigned char>& bits, std::size_t g)
        {
            const std::size_t n = bits.size();
            if (n == 0) return 0;

            int v = 0;
            for (std::size_t i = 0; i < 8; ++i)
                v = (v << 1) | bits[(g + 2 * i + 1) % n];

            return static_cast<unsigned char> (v);
        }

        /// MFM: a clock bit is set only between two zero data bits.
        void writeByteAt (std::vector<unsigned char>& bits, std::size_t g, unsigned char v, int& prev)
        {
            const std::size_t n = bits.size();
            if (n == 0) return;

            for (std::size_t i = 0; i < 8; ++i)
            {
                const int d = (v >> (7 - i)) & 1;
                bits[(g + 2 * i) % n]     = static_cast<unsigned char> ((prev == 0 && d == 0) ? 1 : 0);
                bits[(g + 2 * i + 1) % n] = static_cast<unsigned char> (d);
                prev = d;
            }
        }

        std::uint16_t crc (const unsigned char* d, std::size_t len, std::uint16_t c)
        {
            for (std::size_t i = 0; i < len; ++i)
            {
                c ^= static_cast<std::uint16_t> (d[i] << 8);
                for (int b = 0; b < 8; ++b)
                    c = static_cast<std::uint16_t> ((c & 0x8000) != 0 ? (c << 1) ^ 0x1021 : c << 1);
            }
            return c;
        }

        /// The MFM cell stream for one side of one track, laid out from scratch.
        std::vector<unsigned char> buildSide (const std::vector<unsigned char>& image,
                                              int cyl, int head, int sides)
        {
            std::vector<unsigned char> bits (static_cast<std::size_t> (SideBytes) * 16);

            std::size_t at   = 0;
            int         prev = 0;

            auto put = [&] (unsigned char value, int times)
            {
                for (int i = 0; i < times; ++i) { writeByteAt (bits, at, value, prev); at += 16; }
            };

            // A1 and C2 carry a deliberately missing clock, so their cells are written straight
            // in rather than through writeByteAt.
            auto putSync = [&] (int pattern, int times)
            {
                for (int i = 0; i < times; ++i)
                {
                    for (int b = 0; b < 16; ++b)
                        bits[at + static_cast<std::size_t> (b)] = static_cast<unsigned char> ((pattern >> (15 - b)) & 1);

                    at  += 16;
                    prev = pattern & 1;
                }
            };

            put (Gap, 230);
            put (Sync, 12);
            putSync (0x5224, 3);        // C2 C2 C2
            put (IamMark, 1);
            put (Gap, 50);

            for (int s = 1; s <= SectorsPerTrack; ++s)
            {
                const int lba = (cyl * sides + head) * SectorsPerTrack + (s - 1);

                put (Sync, 12);
                putSync (0x4489, 3);    // A1 A1 A1
                put (Idam, 1);

                const unsigned char id[4] = { static_cast<unsigned char> (cyl),
                                              static_cast<unsigned char> (head),
                                              static_cast<unsigned char> (s),
                                              3 };
                for (unsigned char b : id) put (b, 1);

                std::uint16_t c = crc (A1x3, 3, 0xFFFF);
                c = crc (&Idam, 1, c);
                c = crc (id, 4, c);
                put (static_cast<unsigned char> (c >> 8), 1);
                put (static_cast<unsigned char> (c & 0xFF), 1);

                put (Gap, 22);

                put (Sync, 12);
                putSync (0x4489, 3);
                put (Dam, 1);

                std::vector<unsigned char> data (SectorSize, 0);
                const std::size_t from = static_cast<std::size_t> (lba) * SectorSize;
                if (from + SectorSize <= image.size())
                    std::memcpy (data.data(), image.data() + from, SectorSize);

                for (unsigned char b : data) put (b, 1);

                std::uint16_t d = crc (A1x3, 3, 0xFFFF);
                d = crc (&Dam, 1, d);
                d = crc (data.data(), data.size(), d);
                put (static_cast<unsigned char> (d >> 8), 1);
                put (static_cast<unsigned char> (d & 0xFF), 1);

                put (Gap, 86);
            }

            put (Gap, 94);

            if (at != bits.size()) return {};   // the layout above no longer fills a track

            return packBits (bits);
        }
    }

    std::vector<unsigned char> unpackCells (const std::vector<unsigned char>& cells)
    {
        std::vector<unsigned char> bits (cells.size() * 8);

        for (std::size_t i = 0; i < cells.size(); ++i)
            for (int b = 0; b < 8; ++b)
                bits[i * 8 + static_cast<std::size_t> (b)] = static_cast<unsigned char> ((cells[i] >> b) & 1);

        return bits;
    }

    std::vector<unsigned char> packBits (const std::vector<unsigned char>& bits)
    {
        std::vector<unsigned char> cells (bits.size() / 8);

        for (std::size_t i = 0; i < cells.size(); ++i)
        {
            int c = 0;
            for (int b = 0; b < 8; ++b) c |= bits[i * 8 + static_cast<std::size_t> (b)] << b;
            cells[i] = static_cast<unsigned char> (c);
        }

        return cells;
    }

    std::vector<Field> scan (const std::vector<unsigned char>& cells)
    {
        std::vector<Field> out;
        if (cells.empty()) return out;

        const std::vector<unsigned char> bits = unpackCells (cells);
        const std::size_t n = bits.size();

        std::uint32_t sr = 0;
        long long     lastSync = -100;
        int           run = 0;
        bool          havePending = false;
        Field         pending;

        for (std::size_t p = 0; p < n; ++p)
        {
            sr = (sr << 1) | bits[p];
            if ((sr & 0xFFFF) != 0x4489) continue;

            run = (static_cast<long long> (p) - lastSync == 16) ? run + 1 : 1;
            lastSync = static_cast<long long> (p);
            if (run < 3) continue;

            std::size_t         g    = p + 1;
            const unsigned char mark = readByteAt (bits, g);
            g += 16;

            if (mark == 0xFE)
            {
                unsigned char id[4];
                for (auto& b : id) { b = readByteAt (bits, g); g += 16; }
                g += 32;

                pending = Field { id[0], id[1], id[2], 128 << (id[3] & 7), 0, 0 };
                havePending = true;
            }
            else if ((mark == 0xFB || mark == 0xF8) && havePending)
            {
                pending.dataBitPos = g;
                pending.mark       = mark;
                out.push_back (pending);
                havePending = false;
            }
        }

        return out;
    }

    std::vector<unsigned char> readField (const std::vector<unsigned char>& bits, const Field& f)
    {
        std::vector<unsigned char> d (static_cast<std::size_t> (f.size));

        std::size_t g = f.dataBitPos;
        for (auto& b : d) { b = readByteAt (bits, g); g += 16; }

        return d;
    }

    bool patchField (std::vector<unsigned char>& bits, const Field& f,
                     const std::vector<unsigned char>& data)
    {
        if (static_cast<int> (data.size()) != f.size || bits.empty()) return false;

        const std::size_t n = bits.size();
        std::size_t       g = f.dataBitPos;
        int               prev = bits[(g + n - 1) % n];   // last data bit of the address mark

        for (unsigned char b : data) { writeByteAt (bits, g, b, prev); g += 16; }

        std::uint16_t c = crc (A1x3, 3, 0xFFFF);
        c = crc (&f.mark, 1, c);
        c = crc (data.data(), data.size(), c);

        writeByteAt (bits, g, static_cast<unsigned char> (c >> 8), prev);   g += 16;
        writeByteAt (bits, g, static_cast<unsigned char> (c & 0xFF), prev); g += 16;

        // The next cell's clock depends on the last CRC data bit, so fix it.
        const int d0 = bits[(g + 1) % n];
        bits[g % n] = static_cast<unsigned char> ((prev == 0 && d0 == 0) ? 1 : 0);

        return true;
    }

    void writeSideCells (std::vector<unsigned char>& img, int track, int side,
                         const std::vector<unsigned char>& cells)
    {
        const std::size_t lut = static_cast<std::size_t> (u16 (img, 18)) * 512;
        if (lut + static_cast<std::size_t> (track) * 4 + 3 >= img.size()) return;

        const std::size_t tOff = static_cast<std::size_t> (u16 (img, lut + static_cast<std::size_t> (track) * 4)) * 512;
        const int         tLen = u16 (img, lut + static_cast<std::size_t> (track) * 4 + 2);
        const int         half = tLen / 2;

        int         w   = 0;
        std::size_t pos = tOff;

        while (w < half && static_cast<std::size_t> (w) < cells.size())
        {
            const int         chunk = std::min (256, half - w);
            const std::size_t dst   = pos + (side == 0 ? 0 : 256);

            if (dst + static_cast<std::size_t> (chunk) > img.size()) break;

            std::memcpy (img.data() + dst, cells.data() + w, static_cast<std::size_t> (chunk));
            w   += chunk;
            pos += 512;
        }
    }

    std::vector<unsigned char> buildHfe (const std::vector<unsigned char>& image,
                                         int tracks, int sides)
    {
        if (tracks <= 0) tracks = 80;
        if (sides  <= 0) sides  = 2;

        const std::size_t size = 1024 + static_cast<std::size_t> (tracks) * TrackStride;
        std::vector<unsigned char> out (size, 0xFF);

        std::memcpy (out.data(), HfeHeader, sizeof (HfeHeader));
        out[9]  = static_cast<unsigned char> (tracks);
        out[10] = static_cast<unsigned char> (sides);

        // Each side is 12500 cell bytes, which is 48 whole 256-byte chunks and then 212 of one,
        // so 44 bytes of every side's last chunk are padding the layout never touches. 0x49 0x2A
        // is MFM-encoded 0x4E in this cell packing, which is what the drive that wrote the
        // library left running through them.
        for (std::size_t i = 1024; i < size; ++i)
            out[i] = static_cast<unsigned char> ((i & 1) != 0 ? 0x2A : 0x49);

        for (int t = 0; t < tracks; ++t)
        {
            const int         off = static_cast<int> ((1024 + static_cast<std::size_t> (t) * TrackStride) / 512);
            const std::size_t at  = 512 + static_cast<std::size_t> (t) * 4;

            out[at]     = static_cast<unsigned char> (off & 0xFF);
            out[at + 1] = static_cast<unsigned char> ((off >> 8) & 0xFF);
            out[at + 2] = static_cast<unsigned char> (TrackBytes & 0xFF);
            out[at + 3] = static_cast<unsigned char> ((TrackBytes >> 8) & 0xFF);
        }

        for (int t = 0; t < tracks; ++t)
            for (int s = 0; s < sides; ++s)
                writeSideCells (out, t, s, buildSide (image, t, s, sides));

        return out;
    }

    std::vector<unsigned char> patchHfe (const std::vector<unsigned char>& rawHfe,
                                         const std::vector<unsigned char>& image,
                                         int& written)
    {
        std::vector<unsigned char> img = rawHfe;
        written = 0;

        for (int track = 0; track < 80; ++track)
        {
            for (int side = 0; side < 2; ++side)
            {
                const std::vector<unsigned char> cells = sideCells (img, track, side);
                if (cells.empty()) continue;

                const std::vector<Field> fields = scan (cells);
                if (fields.empty()) continue;

                std::vector<unsigned char> bits = unpackCells (cells);
                bool touched = false;

                for (const Field& f : fields)
                {
                    if (f.size != SectorSize) continue;

                    const int         lba = (f.cyl * 2 + f.head) * SectorsPerTrack + (f.sec - 1);
                    const std::size_t off = static_cast<std::size_t> (lba) * SectorSize;

                    if (lba < 0 || off + SectorSize > image.size()) continue;

                    const std::vector<unsigned char> data (image.begin() + static_cast<std::ptrdiff_t> (off),
                                                           image.begin() + static_cast<std::ptrdiff_t> (off + SectorSize));

                    if (patchField (bits, f, data)) { touched = true; ++written; }
                }

                if (touched) writeSideCells (img, track, side, packBits (bits));
            }
        }

        return img;
    }
}
