/*
 * Does the HFE writer undo exactly what the reader does?
 *
 * The reader had a reference to be held to - the numbers the C# prints. The writer has a
 * better one: itself. An .hfe is a recording of magnetic flux, and decoding it is the
 * inverse of laying it out, so an image that survives a round trip through both has been
 * checked against the decoder that already agrees with the C#. A CRC that came out wrong,
 * a clock bit in the wrong cell, a track laid out a byte short - none of them survive it.
 *
 * Both write paths are covered, because they fail differently. buildHfe synthesises a
 * container from nothing, which is what a disk opened from a raw .img needs. patchHfe
 * rewrites data fields inside a container that already exists, keeping its bitstream, and
 * that is what a disk opened from .hfe gets so its gaps and sync marks survive being saved.
 *
 * No JUCE, no audio device, and no disk image on hand - the image it uses is generated.
 */

#include "Disk.h"
#include "Hfe.h"
#include "HfeWrite.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

namespace
{
    int failures = 0;
    int checks   = 0;

    void check (bool ok, const char* what)
    {
        ++checks;
        if (ok) return;

        ++failures;
        std::printf ("    FAIL  %s\n", what);
    }

    /// An 800K image with no two sectors alike, so a sector written to the wrong place shows.
    std::vector<unsigned char> makeImage()
    {
        std::vector<unsigned char> image (80 * 2 * 5 * 1024);

        std::uint32_t x = 0x950A1E5Bu;
        for (auto& b : image)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            b = static_cast<unsigned char> (x);
        }

        return image;
    }

    void checkCells()
    {
        std::printf ("\n  cells and bits\n");

        std::vector<unsigned char> cells (4096);
        std::uint32_t x = 0x13579BDFu;
        for (auto& c : cells)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            c = static_cast<unsigned char> (x);
        }

        const auto bits = s950::hfe::unpackCells (cells);
        check (bits.size() == cells.size() * 8, "unpacking gives eight cells a byte");
        check (s950::hfe::packBits (bits) == cells, "packing is the inverse of unpacking");
    }

    void checkBuild (const std::vector<unsigned char>& image)
    {
        std::printf ("\n  an .hfe laid out from nothing\n");

        const auto hfe = s950::hfe::buildHfe (image, 80, 2);

        check (hfe.size() == 1024 + 80 * 25088, "the container is the size the format fixes");
        check (s950::hfe::looksLikeHfe (hfe),   "it carries the HXCPICFE signature");

        int bad = -1, missing = -1;
        const auto out = s950::hfe::extract (hfe, bad, missing);

        check (bad == 0,           "every sector's CRC checks out");
        check (missing == 0,       "no sector is missing");
        check (out.size() == image.size(), "it decodes to 800K of sectors");
        check (out == image,       "and to the same 800K that went in");
    }

    void checkPatch (const std::vector<unsigned char>& image)
    {
        std::printf ("\n  an .hfe patched in place\n");

        const auto original = s950::hfe::buildHfe (image, 80, 2);

        // Something to write: the same disk with four scattered sectors replaced, including
        // the first and the last, which are the ones a layout error reaches for.
        std::vector<unsigned char> edited = image;
        for (int lba : { 0, 1, 399, 799 })
            for (int i = 0; i < 1024; ++i)
                edited[static_cast<std::size_t> (lba) * 1024 + static_cast<std::size_t> (i)]
                    = static_cast<unsigned char> (lba + i);

        int written = -1;
        const auto patched = s950::hfe::patchHfe (original, edited, written);

        check (written == 80 * 2 * 5,        "every sector on the disk was rewritten");
        check (patched.size() == original.size(), "the container did not change size");
        check (std::equal (original.begin(), original.begin() + 1024, patched.begin()),
               "the header and track list are untouched");

        int bad = -1, missing = -1;
        const auto out = s950::hfe::extract (patched, bad, missing);

        check (bad == 0,      "the recomputed CRCs check out");
        check (missing == 0,  "no sector was lost in the patching");
        check (out == edited, "the sectors read back as edited");
        check (out != image,  "and the edit actually reached the disk");
    }

    void checkDiskReads (const std::vector<unsigned char>& image)
    {
        std::printf ("\n  what Disk makes of them\n");

        // Not an Akai filesystem, so there is nothing to find in the directory - but a Disk
        // must still take both containers and come back with the same sectors.
        s950::Disk fromImg, fromHfe;
        std::string why;

        const bool okImg = fromImg.loadBytes ("generated.img", image, why);
        const bool okHfe = fromHfe.loadBytes ("generated.hfe", s950::hfe::buildHfe (image, 80, 2), why);

        check (okImg, "a raw image loads");
        check (okHfe, "the same disk as .hfe loads");
        check (! fromImg.wasHfe(), "the raw one knows it was raw");
        check (fromHfe.wasHfe(),   "the .hfe one knows it was .hfe");
        check (fromHfe.getBadCrcSectors() == 0 && fromHfe.getMissingSectors() == 0,
               "and it read cleanly");
        check (fromImg.getImage() == fromHfe.getImage(),
               "both arrive at the same sectors");
    }
}

int main()
{
    std::printf ("\n  the HFE writer against the reader it has to undo\n");

    const auto image = makeImage();

    checkCells();
    checkBuild (image);
    checkPatch (image);
    checkDiskReads (image);

    std::printf ("\n  %d checks, %d failed\n\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
