#include "Voice.h"

namespace s950
{
    void Voice::start (const KeygroupPatch& group, int n, int vel,
                       double rate, double wheel, long long sequence)
    {
        kg            = &group;
        sound         = group.sound;
        note          = n;
        velocity      = vel < 0 ? 0 : (vel > 127 ? 127 : vel);
        keygroupIndex = group.keygroupIndex;
        startedAt     = sequence;
        sampleRate    = rate;

        // --- pitch. Constant pitch means the keygroup ignores which key was struck.
        double semis = group.zoneTranspose;
        if (! group.constantPitch)
            semis += note - sound->rootPitch;

        const double ratio = std::pow (2.0, semis / 12.0);
        step      = sound->sourceRate / sampleRate * ratio;
        leaveRate = sound->sourceRate * ratio;
        pos       = 0.0;

        // --- amplitude envelope
        const double depth     = clamp01 (group.velToLoudness / 99.0);
        const double velDb     = -(127.0 - velocity) * cal::VelDbPerStep * depth;
        const double zoneDb    = group.zoneLoudness * cal::LoudnessDbPerUnit;
        const double sustainDb = -(1.0 - clamp01 (group.vcaSustain / 99.0)) * cal::SustainDb;

        attack      = cal::envSeconds (group.vcaAttack) * cal::AttackScale;
        decay       = cal::envSeconds (group.vcaDecay);
        releaseTime = cal::envSeconds (group.vcaRelease);
        peak        = std::min (cal::dbToGain (velDb + zoneDb), 4.0);
        sustain     = peak * cal::dbToGain (sustainDb);

        stage = attack > 0.0005 ? Stage::attack : Stage::decay;
        t     = 0.0;
        gain  = attack > 0.0005 ? 0.0 : peak;

        /*
         * --- filter. The ceiling is the reconstruction limit, which moves with the rate the
         * audio leaves at - but we are running at the device rate, so it cannot exceed what
         * that can represent either.
         */
        ceiling    = std::min (cal::MaxRatio * leaveRate, sampleRate * 0.45);
        floorHz    = std::min (cal::FloorHz, ceiling);
        baseCutoff = cal::cutoffHz (group.zoneFilter, leaveRate);

        const double track    = cal::clamp (group.keyToFilter, 0, 99) / cal::KeyFull;
        const double keyShift = (note - 60) / 12.0 * track;
        const double velShift = ((velocity - cal::VelPivot) / 127.0)
                                * (cal::clamp (group.velToFilter, 0, 99) / 99.0)
                                * cal::VelOctaves;
        cutoffShift = keyShift + velShift;

        vcfAttack  = group.vcfWritten ? cal::envSeconds (group.vcfAttack) * cal::VcfTimeScale : 0.0;
        vcfDecay   = group.vcfWritten ? cal::envSeconds (group.vcfDecay)  * cal::VcfTimeScale : 0.0;
        vcfSustain = group.vcfWritten ? clamp01 (group.vcfSustain / 99.0) : 1.0;
        vcfDepth   = group.vcfWritten ? (group.vcfAmount / 50.0) * cal::EnvOctaves : 0.0;

        filter.reset();
        filter.setCutoff (cutoffNow (0.0), sampleRate);

        // --- the LFO
        const double own = group.lfoDepth * cal::LfoDepthCentsPerUnit;
        wheelCents  = wheel;
        lfoCents    = own + wheel;
        ownLfo      = group.lfoDesync;
        lfoPhase    = 0.0;
        lfoStep     = 2.0 * 3.14159265358979323846
                      * (cal::LfoRateHzAtZero + group.lfoRate * cal::LfoRateHzPerUnit) / sampleRate;
        fadeSeconds = cal::LfoDelayFadeConstant / std::max (1, 100 - group.lfoDelay);
        fadeT       = 0.0;
    }

    void Voice::adopt (const KeygroupPatch& group)
    {
        if (stage == Stage::idle)     return;
        if (group.sound != sound)     return;   // a different sample is a different note

        kg = &group;

        // --- pitch. Changing transpose moves the playback rate under the position we
        // already hold, which is what transposing a sounding note means.
        double semis = group.zoneTranspose;
        if (! group.constantPitch)
            semis += note - sound->rootPitch;

        const double ratio = std::pow (2.0, semis / 12.0);
        step      = sound->sourceRate / sampleRate * ratio;
        leaveRate = sound->sourceRate * ratio;

        // --- amplitude. The targets move; the gain walks to them from where it is.
        const double depth     = clamp01 (group.velToLoudness / 99.0);
        const double velDb     = -(127.0 - velocity) * cal::VelDbPerStep * depth;
        const double zoneDb    = group.zoneLoudness * cal::LoudnessDbPerUnit;
        const double sustainDb = -(1.0 - clamp01 (group.vcaSustain / 99.0)) * cal::SustainDb;

        attack      = cal::envSeconds (group.vcaAttack) * cal::AttackScale;
        decay       = cal::envSeconds (group.vcaDecay);
        releaseTime = cal::envSeconds (group.vcaRelease);
        peak        = std::min (cal::dbToGain (velDb + zoneDb), 4.0);
        sustain     = peak * cal::dbToGain (sustainDb);

        // --- filter
        ceiling    = std::min (cal::MaxRatio * leaveRate, sampleRate * 0.45);
        floorHz    = std::min (cal::FloorHz, ceiling);
        baseCutoff = cal::cutoffHz (group.zoneFilter, leaveRate);

        const double track    = cal::clamp (group.keyToFilter, 0, 99) / cal::KeyFull;
        const double keyShift = (note - 60) / 12.0 * track;
        const double velShift = ((velocity - cal::VelPivot) / 127.0)
                                * (cal::clamp (group.velToFilter, 0, 99) / 99.0)
                                * cal::VelOctaves;
        cutoffShift = keyShift + velShift;

        vcfAttack  = group.vcfWritten ? cal::envSeconds (group.vcfAttack) * cal::VcfTimeScale : 0.0;
        vcfDecay   = group.vcfWritten ? cal::envSeconds (group.vcfDecay)  * cal::VcfTimeScale : 0.0;
        vcfSustain = group.vcfWritten ? clamp01 (group.vcfSustain / 99.0) : 1.0;
        vcfDepth   = group.vcfWritten ? (group.vcfAmount / 50.0) * cal::EnvOctaves : 0.0;

        // deliberately no filter.reset() - see the note on adopt()

        // --- the LFO keeps its phase and its place in the fade-in
        lfoCents    = group.lfoDepth * cal::LfoDepthCentsPerUnit + wheelCents;
        ownLfo      = group.lfoDesync;
        lfoStep     = 2.0 * 3.14159265358979323846
                      * (cal::LfoRateHzAtZero + group.lfoRate * cal::LfoRateHzPerUnit) / sampleRate;
        fadeSeconds = cal::LfoDelayFadeConstant / std::max (1, 100 - group.lfoDelay);
    }

    void Voice::release()
    {
        if (stage == Stage::idle || stage == Stage::release)
            return;

        // A one-shot keygroup is a drum: it plays through whatever the key does.
        if (kg != nullptr && kg->oneShot && ! sound->loops)
            return;

        releaseFrom = gain;
        stage       = Stage::release;
        t           = 0.0;
    }

    void Voice::render (float* buffer, int count, double sharedPhase)
    {
        if (stage == Stage::idle)
            return;

        const float* audio = sound->audio.data();
        const int    last  = static_cast<int> (sound->audio.size()) - 1;
        const double dt    = 1.0 / sampleRate;

        int done = 0;
        while (done < count && stage != Stage::idle)
        {
            const int n = std::min (ControlBlock, count - done);

            // --- the modulators, once per block
            filter.setCutoff (cutoffNow (t), sampleRate);

            const double fade = fadeSeconds > 0.0005
                              ? (fadeT >= fadeSeconds ? 1.0 : fadeT / fadeSeconds) : 1.0;
            const double cents = lfoCents * fade;
            const double phase = ownLfo ? lfoPhase : sharedPhase;
            const double bend  = cents == 0.0 ? 1.0
                               : std::pow (2.0, cents * std::sin (phase) / 1200.0);
            const double stepNow = step * bend;

            double       gainNow  = gain;
            const double gainEnd  = envelopeAfter (n * dt);
            const double gainStep = (gainEnd - gainNow) / n;

            const bool   loops = sound->loops && sound->loopTo > sound->loopFrom;
            const double end   = loops ? sound->loopTo : static_cast<double> (sound->audio.size());

            for (int j = 0; j < n; ++j)
            {
                /*
                 * Round the loop, or off the end.
                 *
                 * The end to test against is the LOOP end, which for a sample whose loop
                 * runs to its last word is one frame past the last readable one. Testing
                 * against the array instead let the position sit in the gap between them,
                 * where it was neither wrapped nor finished, and the voice simply stopped
                 * after one pass. In the C# that cost three of the four failures the first
                 * run of EngineCheck reported; do not "tidy" it back.
                 */
                if (pos >= end)
                {
                    if (! loops) { stage = Stage::idle; return; }

                    const double len = sound->loopTo - sound->loopFrom;
                    do { pos -= len; } while (pos >= end);
                    if (pos < 0) pos = sound->loopFrom;
                }

                // interpolate towards the next frame, which for the last one is wherever
                // the loop restarts rather than off the end of the array
                int i0 = static_cast<int> (pos);
                if (i0 > last) i0 = last;

                int i1 = i0 + 1;
                if (i1 > last) i1 = loops ? sound->loopFrom : last;

                const double frac = pos - i0;
                const double x    = audio[i0] + (audio[i1] - audio[i0]) * frac;

                buffer[done + j] += static_cast<float> (filter.process (x) * gainNow);
                gainNow += gainStep;

                pos += stepNow;
            }

            gain  = gainEnd;
            t     += n * dt;
            fadeT += n * dt;

            lfoPhase += lfoStep * n;
            if (lfoPhase > 2.0 * 3.14159265358979323846)
                lfoPhase -= 2.0 * 3.14159265358979323846;

            advanceStage();
            done += n;
        }
    }

    double Voice::cutoffNow (double at) const
    {
        double env;

        if (at < vcfAttack)
            env = vcfAttack > 0 ? at / vcfAttack : 1.0;
        else if (at < vcfAttack + vcfDecay)
            env = vcfDecay > 0 ? 1.0 - (1.0 - vcfSustain) * ((at - vcfAttack) / vcfDecay)
                               : vcfSustain;
        else
            env = vcfSustain;

        const double hz = baseCutoff * std::pow (2.0, cutoffShift + env * vcfDepth);
        return hz > ceiling ? ceiling : (hz < floorHz ? floorHz : hz);
    }

    /*
     * Where the amplitude envelope will be in `ahead` seconds.
     *
     * The decay and the release are straight lines in DECIBELS, not in amplitude. That is
     * measured: a stored decay of 80 fell 2.9, 2.7, 3.0, 3.0, 2.7, 2.9 dB per fifth of a
     * second, dead constant for two and a half seconds. A line in amplitude hangs near the
     * peak and then drops off a cliff, which is audibly another instrument.
     */
    double Voice::envelopeAfter (double ahead) const
    {
        const double at = t + ahead;

        switch (stage)
        {
            case Stage::attack:
                return attack > 0 ? peak * std::min (1.0, at / attack) : peak;

            case Stage::decay:
                if (decay <= 0.0005) return sustain;
                return fall (peak, sustain, std::min (1.0, at / decay));

            case Stage::release:
                if (releaseTime <= 0.0005) return 0.0;
                return fall (releaseFrom, 1e-4, std::min (1.0, at / releaseTime));

            default:
                return sustain;
        }
    }

    void Voice::advanceStage()
    {
        switch (stage)
        {
            case Stage::attack:
                if (t >= attack) { stage = Stage::decay; t = 0.0; }
                break;

            case Stage::decay:
                if (t >= decay) { stage = Stage::sustain; t = 0.0; gain = sustain; }
                break;

            case Stage::release:
                if (t >= releaseTime || gain <= 2e-4) stage = Stage::idle;
                break;

            default:
                break;
        }
    }
}
