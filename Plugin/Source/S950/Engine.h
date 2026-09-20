#pragma once

#include "Voice.h"

#include <atomic>
#include <vector>

namespace s950
{
    /*
     * The instrument: eight voices, a patch, and a buffer to fill.
     *
     * Nothing in here knows what is driving it. In the C# an editor drives it from a piano
     * keyboard and a MIDI port; here a host drives it from its note events; the checks drive
     * it from a loop and render to a file. That separation is the one decision that made
     * this port a port rather than a rewrite.
     *
     * THREADS
     *
     * render() runs on the audio thread and must never allocate, never lock and never block.
     * Notes arrive from somewhere else, so they go into a small lock-free ring and are picked
     * up at the top of the next render(). A lock here would be a dropout.
     */
    class Engine
    {
    public:
        /// What the machine has. Voices past this steal the oldest.
        static constexpr int Polyphony = 8;

        explicit Engine (double sampleRate);

        double getSampleRate() const { return sampleRate; }

        /// The master trim, as a plain gain. Eight voices at once can clip.
        std::atomic<float> gain { 0.7f };

        /*
         * What to play.
         *
         * Called from the message thread. The patch is handed over rather than shared: see
         * the note in Engine.cpp on why this is a two-slot exchange and not simply an
         * atomic pointer. Call collectRetiredPatch() from the message thread now and then -
         * a timer, or the top of the next setPatch - or the old patch is never released.
         */
        void setPatch (PatchPtr patch);

        /*
         * Release whatever the audio thread has finished with.
         *
         * Message thread only. Freeing a patch is freeing every sample buffer in it, and
         * that must not happen where a late free is a click.
         */
        void collectRetiredPatch();

        // ------------------------------------------------------------------- playing

        void noteOn (int note, int velocity) { post (EvNoteOn,  note, velocity); }
        void noteOff (int note)              { post (EvNoteOff, note, 0); }
        void modwheel (int value)            { post (EvWheel,   value, 0); }
        void allNotesOff()                   { post (EvAllOff,  0, 0); }

        // -------------------------------------------------------------------- render

        /// Fill `count` mono samples. Allocates nothing.
        void render (float* buffer, int count);

        // ------------------------------------------------------------ for the caller

        int getActiveVoices() const;

        const Voice& getVoice (int i) const { return voices[i]; }

    private:
        static constexpr unsigned char EvNoteOn  = 1;
        static constexpr unsigned char EvNoteOff = 2;
        static constexpr unsigned char EvWheel   = 3;
        static constexpr unsigned char EvAllOff  = 4;

        static constexpr int RingSize = 256;

        struct Event { unsigned char kind, a, b; };

        void post (unsigned char kind, int a, int b);
        void drainEvents();
        void takePendingPatch();
        void repatch();
        void startNote (int note, int velocity);
        void stopNote (int note);
        Voice& take();

        double sampleRate = 48000.0;

        Voice voices[Polyphony];
        std::vector<const KeygroupPatch*> matched;   // reused, so starting a note allocates nothing

        Event            ring[RingSize] {};
        std::atomic<int> writeIndex { 0 };
        std::atomic<int> readIndex  { 0 };

        /*
         * The patch, exchanged between threads without either one waiting.
         *
         * audioPatch belongs to the audio thread and nothing else touches it. pending is
         * filled by the message thread and taken by the audio thread; retired goes the other
         * way. Both hand-offs move the shared_ptr rather than copying it, so no reference
         * count is touched on the audio thread and nothing is ever freed there.
         */
        PatchPtr          audioPatch;
        PatchPtr          pending;
        PatchPtr          retired;
        std::atomic<bool> pendingReady { false };
        std::atomic<bool> retiredReady { false };

        long long sequence = 0;
        double    sharedPhase = 0.0, sharedStep = 0.0;
        int       wheel = 0;
    };
}
