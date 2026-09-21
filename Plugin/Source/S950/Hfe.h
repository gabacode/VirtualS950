#pragma once

#include <cstdint>
#include <vector>

namespace s950
{
    /*
     * Decoder for HxC/Gotek .hfe images (HXCPICFE rev 0).
     *
     * An .img is the 800K of sectors a disk holds. An .hfe is a recording of the magnetic
     * flux a floppy controller would see reading it - the raw MFM cells, both sides
     * interleaved in 256-byte chunks - so getting sectors out of one means doing what the
     * controller does: find the sync marks, pull the bits apart from the clock they are
     * woven into, and check the CRCs.
     *
     * That is worth the trouble because it is the format real disks get archived in. The
     * library this was written against is entirely .hfe.
     *
     * A straight port of AkaiS950List/Hfe.cs, which is the one part of this project that
     * was worked out against a specification rather than measured off the machine.
     */
    namespace hfe
    {
        /// True if these bytes start with the HXCPICFE signature.
        bool looksLikeHfe (const std::vector<unsigned char>& raw);

        /// One side's cell bytes, pulled out of the interleaved track blocks.
        std::vector<unsigned char> sideCells (const std::vector<unsigned char>& img,
                                              int track, int side);

        /*
         * Decode every track and lay the sectors out linearly, exactly as a plain image
         * holds them: LBA = (cyl * sides + head) * 5 + (sec - 1), 1024 bytes each.
         *
         * `badCrc` counts sectors that were readable but failed their check, and `missing`
         * counts sectors no good copy was found for. Both are worth showing rather than
         * hiding: an archived floppy is thirty years old, and a disk that reads with three
         * bad sectors is a different thing from one that reads cleanly.
         */
        std::vector<unsigned char> extract (const std::vector<unsigned char>& raw,
                                            int& badCrc, int& missing);
    }
}
