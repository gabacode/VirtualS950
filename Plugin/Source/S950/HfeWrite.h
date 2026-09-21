#pragma once

#include <cstdint>
#include <vector>

namespace s950::hfe
{
    /*
     * Writing .hfe images: patching one that already exists, or laying out a new one.
     *
     * A port of AkaiS950List/HfeWrite.cs. Patching rewrites a sector's data field where it
     * already sits - sync marks, ID fields, gaps and track timing are never touched, and the
     * replacement is the same length because every Akai sector is 1024 bytes - which keeps
     * the bitstream of the disk it came from.
     */

    /// One data field found on a track, with the bit offset of its first cell group.
    struct Field
    {
        int           cyl = 0, head = 0, sec = 0, size = 0;
        std::size_t   dataBitPos = 0;
        unsigned char mark = 0;
    };

    std::vector<unsigned char> unpackCells (const std::vector<unsigned char>& cells);
    std::vector<unsigned char> packBits   (const std::vector<unsigned char>& bits);

    /// Every data field on a track's cells.
    std::vector<Field> scan (const std::vector<unsigned char>& cells);

    std::vector<unsigned char> readField (const std::vector<unsigned char>& bits, const Field& f);

    /// Replace one sector's payload. False if the data is not the field's size.
    bool patchField (std::vector<unsigned char>& bits, const Field& f,
                     const std::vector<unsigned char>& data);

    /// Inverse of sideCells: put a side's cells back into the interleaved blocks.
    void writeSideCells (std::vector<unsigned char>& img, int track, int side,
                         const std::vector<unsigned char>& cells);

    /// An HFE container holding this sector image, laid out from nothing.
    std::vector<unsigned char> buildHfe (const std::vector<unsigned char>& image,
                                         int tracks, int sides);

    /*
     * `rawHfe` with every sector's data field rewritten from `image`.
     *
     * `written` counts the sectors replaced; none means the template had nothing to patch,
     * and the caller should treat that as a failure rather than write the file back out.
     */
    std::vector<unsigned char> patchHfe (const std::vector<unsigned char>& rawHfe,
                                         const std::vector<unsigned char>& image,
                                         int& written);
}
