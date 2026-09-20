/*
 * Does the port agree with the engine it was ported from?
 *
 * Reference.h holds what the C# computes for a spread of inputs, generated from the working
 * engine by AkaiS950Tests/ReferenceDump.cs. This runs the same inputs through the C++ and
 * insists on the same answers.
 *
 * It exists because of how this port was written: on a machine with no C++ compiler, so not
 * one line of it had ever been run at the point it was committed. Every previous piece of
 * this project was checked by measuring rather than by reading - the LFO against a
 * recording, the engine against the web version, the editor against a rendered screenshot -
 * and a port that was only ever read would be the one thing taken on trust. This is how it
 * stops being taken on trust.
 *
 * Build it with nothing but a compiler:
 *
 *     cl /std:c++17 /EHsc /I..\Source\S950 ConformanceCheck.cpp ..\Source\S950\*.cpp
 *
 * It needs no JUCE, no SDK and no audio device, which is the point - if this fails, nothing
 * built on top of it is worth debugging.
 */

#include "Reference.h"

#include "Cal.h"
#include "Filter.h"
#include "Patch.h"
#include "Engine.h"

#include <cstdio>
#include <cmath>
#include <memory>
#include <vector>

namespace
{
    int failures = 0;
    int checks   = 0;

    bool close (double a, double b, double tolerance)
    {
        const double diff = std::fabs (a - b);
        if (diff <= tolerance) return true;

        // Relative, for the large ones: 16317 Hz does not need to match to a millionth.
        const double scale = std::max (std::fabs (a), std::fabs (b));
        return scale > 0 && diff / scale <= tolerance;
    }

    void check (bool ok, const char* what, double got, double want)
    {
        ++checks;

        if (ok)
            return;

        ++failures;
        std::printf ("  FAIL %-34s got %.6f, want %.6f\n", what, got, want);
    }

    void same (const char* what, double got, double want, double tolerance = 1e-9)
    {
        check (close (got, want, tolerance), what, got, want);
    }

    // ------------------------------------------------------------------ the mappings

    void checkConstants()
    {
        std::printf ("\n  the measured constants\n");

        same ("MaxRatio",             s950::cal::MaxRatio,             reference::MaxRatio);
        same ("FloorHz",              s950::cal::FloorHz,              reference::FloorHz);
        same ("KeyFull",              s950::cal::KeyFull,              reference::KeyFull);
        same ("VelOctaves",           s950::cal::VelOctaves,           reference::VelOctaves);
        same ("VelPivot",             s950::cal::VelPivot,             reference::VelPivot);
        same ("EnvOctaves",           s950::cal::EnvOctaves,           reference::EnvOctaves);
        same ("AttackScale",          s950::cal::AttackScale,          reference::AttackScale);
        same ("VcfTimeScale",         s950::cal::VcfTimeScale,         reference::VcfTimeScale);
        same ("SustainDb",            s950::cal::SustainDb,            reference::SustainDb);
        same ("LoudnessDbPerUnit",    s950::cal::LoudnessDbPerUnit,    reference::LoudnessDbPerUnit);
        same ("VelDbPerStep",         s950::cal::VelDbPerStep,         reference::VelDbPerStep);
        same ("LfoDepthCentsPerUnit", s950::cal::LfoDepthCentsPerUnit, reference::LfoDepthCentsPerUnit);
        same ("LfoWheelCentsAtFull",  s950::cal::LfoWheelCentsAtFull,  reference::LfoWheelCentsAtFull);
    }

    void checkCutoffs()
    {
        std::printf ("\n  the filter's cutoff, %d points\n",
                     static_cast<int> (std::size (reference::cutoffs)));

        double worst = 0.0;

        for (const auto& c : reference::cutoffs)
        {
            const double got = s950::cal::cutoffHz (c.stored, c.rate);
            worst = std::max (worst, std::fabs (got - c.hz) / std::max (1.0, c.hz));

            char what[64];
            std::snprintf (what, sizeof (what), "cutoff %d at %.0f", c.stored, c.rate);
            same (what, got, c.hz, 1e-9);
        }

        std::printf ("    worst relative difference %.3g\n", worst);
    }

    void checkEnvelopes()
    {
        std::printf ("\n  the envelope times, %d points\n",
                     static_cast<int> (std::size (reference::envTimes)));

        for (const auto& e : reference::envTimes)
        {
            char what[64];
            std::snprintf (what, sizeof (what), "envSeconds %d", e.stored);
            same (what, s950::cal::envSeconds (e.stored), e.seconds, 1e-9);
        }
    }

    void checkLfo()
    {
        std::printf ("\n  the LFO\n");

        for (const auto& r : reference::lfoRates)
        {
            const double got = s950::cal::LfoRateHzAtZero
                             + r.stored * s950::cal::LfoRateHzPerUnit;

            char what[64];
            std::snprintf (what, sizeof (what), "rate %d", r.stored);
            same (what, got, r.hz, 1e-9);
        }

        for (const auto& f : reference::lfoFades)
        {
            const double got = s950::cal::LfoDelayFadeConstant
                             / std::max (1, 100 - f.stored);

            char what[64];
            std::snprintf (what, sizeof (what), "delay fade %d", f.stored);
            same (what, got, f.seconds, 1e-9);
        }
    }

    // ------------------------------------------------------- the engine, end to end

    /// A sawtooth, so there is something with harmonics for the filter to work on.
    std::shared_ptr<s950::Sound> makeSaw (int words, int rate)
    {
        auto s = std::make_shared<s950::Sound>();
        s->name       = "SAW";
        s->sourceRate = rate;
        s->rootPitch  = 60.0;
        s->audio.resize (static_cast<size_t> (words));

        const int period = 100;
        for (int i = 0; i < words; ++i)
            s->audio[static_cast<size_t> (i)] =
                static_cast<float> ((i % period) / static_cast<double> (period) * 2.0 - 1.0);

        s->loops    = true;
        s->loopFrom = 0;
        s->loopTo   = words;
        return s;
    }

    double rms (const std::vector<float>& x)
    {
        double sum = 0.0;
        for (float v : x) sum += static_cast<double> (v) * v;
        return x.empty() ? 0.0 : std::sqrt (sum / x.size());
    }

    void checkEngine()
    {
        std::printf ("\n  the engine, driven end to end\n");

        auto patch = std::make_shared<s950::Patch>();
        patch->name = "TEST";

        s950::KeygroupPatch kg;
        kg.lowKey        = 0;
        kg.highKey       = 127;
        kg.keygroupIndex = 0;
        kg.sound         = makeSaw (48000, 48000);
        kg.vcaSustain    = 99;
        kg.zoneFilter    = 99;
        patch->keygroups.push_back (kg);

        s950::Engine engine (48000.0);
        engine.setPatch (patch);

        std::vector<float> buffer (4800);

        // Silence before anything is asked for.
        engine.render (buffer.data(), static_cast<int> (buffer.size()));
        check (rms (buffer) == 0.0, "silent before any note", rms (buffer), 0.0);

        // A note sounds, and the note-on survives the ring.
        engine.noteOn (60, 100);
        engine.render (buffer.data(), static_cast<int> (buffer.size()));

        const double sounding = rms (buffer);
        check (sounding > 0.01, "a note on sounds", sounding, 0.01);
        check (engine.getActiveVoices() == 1, "one voice", engine.getActiveVoices(), 1);

        // Eight at once, and no more.
        for (int n = 0; n < 12; ++n)
            engine.noteOn (48 + n, 100);

        engine.render (buffer.data(), static_cast<int> (buffer.size()));
        check (engine.getActiveVoices() <= s950::Engine::Polyphony,
               "never more than eight voices", engine.getActiveVoices(), s950::Engine::Polyphony);

        // Everything off, and it goes quiet - release is 0, so one block is enough.
        engine.allNotesOff();
        for (int i = 0; i < 20; ++i)
            engine.render (buffer.data(), static_cast<int> (buffer.size()));

        check (rms (buffer) < 1e-4, "all notes off falls silent", rms (buffer), 0.0);

        // The patch hand-off: the audio thread takes it, the message thread frees it.
        engine.setPatch (nullptr);
        engine.render (buffer.data(), static_cast<int> (buffer.size()));
        engine.collectRetiredPatch();

        engine.noteOn (60, 100);
        engine.render (buffer.data(), static_cast<int> (buffer.size()));
        check (rms (buffer) == 0.0, "no patch, no sound", rms (buffer), 0.0);
    }

    void checkFilter()
    {
        std::printf ("\n  the filter\n");

        s950::Butterworth f;
        f.setCutoff (1000.0, 48000.0);

        // A steady input settles to a steady output: the sections are normalised to unity
        // at DC, and if a Q or a coefficient is wrong this is where it shows first.
        double y = 0.0;
        for (int i = 0; i < 20000; ++i)
            y = f.process (1.0);

        same ("unity gain at DC", y, 1.0, 1e-6);

        // Well above the cutoff, very little should come through.
        f.reset();
        f.setCutoff (500.0, 48000.0);

        double peak = 0.0;
        for (int i = 0; i < 48000; ++i)
        {
            const double x = std::sin (2.0 * 3.14159265358979323846 * 8000.0 * i / 48000.0);
            const double out = f.process (x);
            if (i > 24000) peak = std::max (peak, std::fabs (out));
        }

        check (peak < 0.02, "8 kHz is stopped by a 500 Hz cutoff", peak, 0.02);
    }
}

int main()
{
    std::printf ("\n  the C++ port against the engine it came from\n");

    checkConstants();
    checkCutoffs();
    checkEnvelopes();
    checkLfo();
    checkFilter();
    checkEngine();

    std::printf ("\n  %d checks, %d failed\n\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
