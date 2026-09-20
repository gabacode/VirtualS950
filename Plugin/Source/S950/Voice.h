#pragma once

#include "Cal.h"
#include "Filter.h"
#include "Patch.h"

namespace s950
{
    /*
     * One sounding note.
     *
     * Everything a voice does happens in render(), a block at a time, and nothing in here
     * allocates once the voice exists. That is the whole reason this half is separate from
     * the editor: an allocation in the middle of an audio callback is a click, and a click
     * in a plugin is someone else's ruined take.
     *
     * The signal path follows the machine: the sample is read out at whatever rate the pitch
     * asks for, and the filter runs AFTER that, at the rate the audio leaves at. So the
     * cutoff is a fixed number of hertz whichever key is held, rather than being dragged up
     * and down by the varispeed.
     *
     * A straight port of AkaiS950Engine/Voice.cs.
     */
    class Voice
    {
    public:
        bool isActive()  const { return stage != Stage::idle; }
        bool isHeld()    const { return stage != Stage::idle && stage != Stage::release; }
        int  getNote()     const { return note; }
        int  getVelocity() const { return velocity; }
        long long getStartedAt() const { return startedAt; }

        /// Which keygroup of the programme this voice came from, or -1.
        int getKeygroupIndex() const { return keygroupIndex; }

        /// The audio this voice is reading, so a caller can ask what is really sounding.
        const SoundPtr& getSound() const { return sound; }

        /// Start this voice. Nothing here allocates.
        void start (const KeygroupPatch& kg, int note, int velocity,
                    double sampleRate, double wheelCents, long long sequence);

        /*
         * Take up new settings without restarting the note.
         *
         * For a value changed while the note is sounding - a filter dragged, an envelope
         * reshaped, a programme edited under a held loop. Everything that describes the note
         * is recomputed; everything that says where the note has got to is left exactly as
         * it is. So the sample goes on from where it was, the envelope stays in its stage,
         * the LFO keeps its phase and the filter keeps its state - the last of those
         * matters, because resetting a filter mid-note is a click.
         *
         * A keygroup now naming a different sample is not a change to this note, it is a
         * different note; the position we are at would not mean the same thing in other
         * audio. That waits for the next trigger.
         */
        void adopt (const KeygroupPatch& kg);

        /// Let go of the key. The note falls at its own release rate.
        void release();

        void kill() { stage = Stage::idle; }

        /*
         * Add this voice into the buffer.
         *
         * `sharedPhase` is the programme's own LFO, which voices with the desync bit CLEAR
         * ride instead of their own. That was measured rather than assumed: with the bit
         * clear two voices held their phase to within 3 degrees over six seconds, and with
         * it set they ran at rates 3.5% apart and drifted a whole turn in the same six.
         */
        void render (float* buffer, int count, double sharedPhase);

    private:
        /// How often the modulators are recomputed. 32 at 48 kHz is 0.67 ms.
        static constexpr int ControlBlock = 32;

        enum class Stage { idle, attack, decay, sustain, release };

        double cutoffNow (double t) const;
        double envelopeAfter (double ahead) const;
        void   advanceStage();

        /// A straight line in decibels from one gain to another.
        static double fall (double from, double to, double u)
        {
            if (from <= 1e-6) return 0.0;
            const double lo = std::max (to, 1e-6);
            return from * std::pow (lo / from, u);
        }

        static double clamp01 (double v) { return cal::clamp (v, 0.0, 1.0); }

        Butterworth filter;

        /*
         * The keygroup this voice is playing.
         *
         * A pointer into the patch, which the engine keeps alive for as long as any voice
         * refers to it - see Engine::setPatch. The sound is held by value as a shared_ptr
         * instead, because that is the one thing a voice reads every single sample.
         */
        const KeygroupPatch* kg = nullptr;
        SoundPtr             sound;

        int       note = 0, velocity = 0;
        int       keygroupIndex = -1;
        long long startedAt = 0;

        double sampleRate = 48000.0;
        double pos        = 0.0;     // where we are in the sample, in frames
        double step       = 1.0;     // frames per output sample, before the LFO
        double leaveRate  = 40000.0; // the rate the audio leaves at, for the filter ceiling

        // amplitude envelope, all in gain except the times
        Stage  stage = Stage::idle;
        double t = 0.0;              // seconds since the stage began
        double attack = 0.0, decay = 0.0, releaseTime = 0.0;
        double peak = 1.0, sustain = 1.0;
        double gain = 0.0, releaseFrom = 0.0;

        // filter envelope
        double vcfAttack = 0.0, vcfDecay = 0.0, vcfSustain = 1.0, vcfDepth = 0.0;
        double baseCutoff = 1000.0, cutoffShift = 0.0, ceiling = 16000.0, floorHz = 311.0;

        // the LFO
        double lfoCents = 0.0, lfoPhase = 0.0, lfoStep = 0.0;
        double fadeSeconds = 0.0, fadeT = 0.0;
        double wheelCents  = 0.0;    // kept so adopt() can add it back to a new depth
        bool   ownLfo = true;
    };
}
