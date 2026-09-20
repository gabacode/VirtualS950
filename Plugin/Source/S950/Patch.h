#pragma once

#include <memory>
#include <string>
#include <vector>
#include <algorithm>

namespace s950
{
    /*
     * One sample's audio, ready to be played.
     *
     * Normalised to +-1 once, when the patch is built, rather than per sample in the render
     * loop. The engine never sees a disk image: whoever builds the patch does the reading,
     * which is what lets the same engine sit behind a WinForms window, a plugin, or a test.
     *
     * Held by shared_ptr, which the C# did not need and this does. A voice reads this audio
     * on the audio thread; the message thread may replace the patch at any moment. Sharing
     * ownership means the buffer a voice is halfway through cannot be freed under it - the
     * last holder releases it, and if that turns out to be a voice, see the note on
     * Engine::setPatch about where that release happens.
     */
    struct Sound
    {
        std::string        name;
        std::vector<float> audio;            // -1 .. +1
        int                sourceRate = 40000;   // hertz, as stored on the disk
        double             rootPitch  = 60.0;    // the MIDI note at which it plays at sourceRate

        /*
         * The loop, in frames. The machine plays end-length .. end round and round, so the
         * start follows from the length rather than from the stored start - which is simply
         * zero in 250 of the library's 324 looped samples.
         */
        int  loopFrom = 0, loopTo = 0;
        bool loops    = false;

        /// How many samples of the loop join were crossfaded, or 0 for a plain splice.
        int loopSmoothed = 0;
    };

    using SoundPtr = std::shared_ptr<const Sound>;

    /*
     * One keygroup, flattened into what a voice needs.
     *
     * The raw 0..99 panel bytes are kept rather than seconds and hertz, because the mappings
     * from one to the other are the measured part and belong in one place - Cal.
     */
    struct KeygroupPatch
    {
        int lowKey = 0, highKey = 127;

        /*
         * Which velocities this entry answers to.
         *
         * A keygroup holds up to two VELOCITY zones - alternatives, not layers. The switch
         * in byte 2 is the boundary: softer than it plays zone 1, at it or harder plays
         * zone 2, and a switch of 128 is how the panel says "no second zone", since no
         * velocity can reach it.
         *
         * Sounding both was the C# engine's worst bug. 74 of the library's 168 two-zone
         * keygroups name the SAME sample in both, so playing them together put two copies of
         * one sample on top of each other - and 121 of the 168 set the switch to 128, so
         * their second zone should never have sounded at all. Together they rang like a bell
         * over every note. Keep the ranges; do not "simplify" this into layering.
         */
        int velocityFrom = 0, velocityTo = 127;

        /// Which keygroup this came from. Only the checks care, and they care a lot: two
        /// entries from the SAME keygroup must never answer one strike.
        int keygroupIndex = -1;

        SoundPtr sound;

        int  vcaAttack = 0, vcaDecay = 0, vcaSustain = 99, vcaRelease = 0;
        int  vcfAttack = 0, vcfDecay = 0, vcfSustain = 99, vcfRelease = 0;
        bool vcfWritten = true;          // an S900 program leaves the four bytes as spaces
        int  vcfAmount  = 0;             // signed, -50..+50

        int velToFilter = 0, keyToFilter = 0, velToLoudness = 0;

        int  lfoDelay = 0, lfoRate = 0, lfoDepth = 0, lfoModwheelDepth = 0;
        bool lfoDesync = true;           // set in 1652 keygroups of 1908

        int    zoneFilter    = 99;       // zone 1's cutoff, 0..99
        int    zoneLoudness  = 0;        // signed trim, in the machine's decibel count
        double zoneTranspose = 0.0;      // semitones, including the fine part

        bool constantPitch = false;
        bool oneShot       = false;

        /*
         * The four VCF bytes read as ASCII spaces means the program was written by an S900,
         * which had no filter envelope at all - not an envelope that happens to be set to
         * 32. In the library this never bites, because none of the 1684 keygroups with blank
         * bytes sets an amount, but it would the moment one did.
         */
        static bool looksWritten (int a, int d, int s, int r)
        {
            return ! (a == 0x20 && d == 0x20 && s == 0x20 && r == 0x20);
        }
    };

    /// A program: the keygroups a note might land in.
    struct Patch
    {
        std::string                name;
        std::vector<KeygroupPatch> keygroups;

        /*
         * Every entry this note and velocity should sound.
         *
         * Plural on purpose, but for one reason only: overlapping KEYGROUPS do layer, and
         * the machine sounds all of them. The two zones INSIDE a keygroup do not - they are
         * velocity alternatives, and only one of them answers any given strike.
         *
         * Fills a caller's vector rather than returning one, because the caller is the audio
         * thread and it keeps one around: allocating here would be a dropout waiting for a
         * busy moment.
         */
        void matching (int note, int velocity, std::vector<const KeygroupPatch*>& into) const
        {
            into.clear();

            for (const auto& k : keygroups)
            {
                if (k.sound == nullptr || k.sound->audio.empty())
                    continue;

                const int lo = std::min (k.lowKey, k.highKey);
                const int hi = std::max (k.lowKey, k.highKey);

                if (note < lo || note > hi)                       continue;
                if (velocity < k.velocityFrom || velocity > k.velocityTo) continue;

                into.push_back (&k);
            }
        }
    };

    using PatchPtr = std::shared_ptr<const Patch>;
}
