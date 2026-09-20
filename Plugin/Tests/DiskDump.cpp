/*
 * The same dump as AkaiS950Tests/DiskDump.cs, from the C++ reader.
 *
 * Byte for byte the same text, so the two can be diffed. crosscheck.ps1 converts an image
 * to raw sectors with the C#, dumps it with both, and compares - which checks the port
 * against a real disk without a single image, sample name or byte of audio going into the
 * repository.
 *
 *     DiskDump <image.img>
 */

#include "Disk.h"

#include <cstdio>
#include <cstdint>
#include <string>

namespace
{
    /*
     * FNV-1a over the 16-bit words, a byte at a time, low byte first.
     *
     * A hash rather than the audio itself: the two readers can then be held to each other
     * exactly without any of someone else's recordings being written down. Unsigned
     * throughout, which is what makes it reproduce the C#'s answer.
     */
    std::string hashOf (const std::vector<short>& words)
    {
        std::uint32_t h = 2166136261u;

        for (short w : words)
        {
            const std::uint16_t u = static_cast<std::uint16_t> (w);
            h = (h ^ static_cast<std::uint32_t> (u & 0xFF)) * 16777619u;
            h = (h ^ static_cast<std::uint32_t> (u >> 8))   * 16777619u;
        }

        char buf[16];
        std::snprintf (buf, sizeof (buf), "%08x", h);
        return buf;
    }

    void printZone (const char* label, const s950::Disk::Zone& z)
    {
        std::printf ("     %s '%s' %s ptr %d fine %d trans %d filt %d loud %d\n",
                     label,
                     z.name.c_str(),
                     z.inUse() ? "used" : "unused",
                     z.pointer, z.fine, z.transpose, z.filter, z.loudness);
    }
}

int main (int argc, char** argv)
{
    if (argc < 2)
    {
        std::fprintf (stderr, "usage: DiskDump <image.img>\n");
        return 2;
    }

    s950::Disk disk;
    std::string error;

    if (! disk.loadFile (argv[1], error))
    {
        std::fprintf (stderr, "could not read %s: %s\n", argv[1], error.c_str());
        return 1;
    }

    // How the recovery went, which for an .hfe is part of what is being checked: the two
    // decoders have to agree about which sectors were bad, not just about the good ones.
    std::printf ("recovery badcrc %d missing %d\n",
                 disk.getBadCrcSectors(), disk.getMissingSectors());

    const auto& entries = disk.getEntries();
    std::printf ("entries %d\n", static_cast<int> (entries.size()));

    for (const auto& e : entries)
    {
        std::printf ("entry %d %c '%s' len %d start %d blocks %d %s\n",
                     e.slot, e.type, e.name.c_str(),
                     e.length, e.startBlock, e.chainBlocks,
                     e.chainOk ? "ok" : "SHORT");

        if (e.type == 'S')
        {
            const auto words = disk.sampleWords12 (e);

            std::printf ("  sample count %lld rate %d tuning %d loudness %d loop %c%c %lld/%lld/%lld\n",
                         static_cast<long long> (e.sampleCount), e.sampleRate, e.tuning, e.loudness,
                         e.loopMode, e.loopDirection,
                         static_cast<long long> (e.loopStart),
                         static_cast<long long> (e.loopEnd),
                         static_cast<long long> (e.loopLength));

            std::printf ("  audio words %d hash %s\n",
                         static_cast<int> (words.size()), hashOf (words).c_str());
        }
        else if (e.type == 'P')
        {
            const auto groups = disk.keygroups (e);
            std::printf ("  program keygroups %d\n", static_cast<int> (groups.size()));

            for (const auto& k : groups)
            {
                std::printf ("   kg %d keys %d-%d vsw %d vca %d,%d,%d,%d vcf %d,%d,%d,%d"
                             " amt %d vel %d,%d key %d lfo %d,%d,%d,%d flags %d\n",
                             k.index, k.lowKey, k.highKey, k.velocitySwitch,
                             k.vcaAttack, k.vcaDecay, k.vcaSustain, k.vcaRelease,
                             k.vcfAttack, k.vcfDecay, k.vcfSustain, k.vcfRelease,
                             k.vcfAmount,
                             k.velToFilter, k.velToLoudness,
                             k.keyToFilter,
                             k.lfoDelay, k.lfoRate, k.lfoDepth, k.lfoModwheelDepth,
                             k.flags);

                printZone ("z1", k.zone1);
                printZone ("z2", k.zone2);
            }
        }
    }

    return 0;
}
