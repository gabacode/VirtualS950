using System;

namespace AkaiS950Engine
{
    /// <summary>
    /// One sounding note.
    ///
    /// Everything a voice does happens in Render, a block at a time, and nothing in here
    /// allocates once the voice exists - which is the whole reason the engine is separate
    /// from the editor. A garbage collection in the middle of an audio callback is a click,
    /// and a click in a plugin is someone else's ruined take.
    ///
    /// The signal path follows the machine: the sample is read out at whatever rate the
    /// pitch asks for, and the filter runs AFTER that, at the rate the audio leaves at. So
    /// the cutoff is a fixed number of hertz whichever key is held, rather than being
    /// dragged up and down by the varispeed.
    /// </summary>
    public sealed class Voice
    {
        /// <summary>How often the modulators are recomputed. 32 at 48 kHz is 0.67 ms.</summary>
        const int ControlBlock = 32;

        public bool Active { get { return _stage != Stage.Idle; } }
        public int Note { get { return _note; } }
        public long StartedAt { get { return _startedAt; } }
        public bool Held { get { return _stage != Stage.Idle && _stage != Stage.Release; } }

        enum Stage { Idle, Attack, Decay, Sustain, Release }

        readonly Butterworth _filter = new Butterworth();

        KeygroupPatch _kg;
        Sound _sound;
        int _note;
        long _startedAt;

        double _sampleRate;          // the rate we are rendering at
        double _pos;                 // where we are in the sample, in frames
        double _step;                // frames per output sample, before the LFO
        double _leaveRate;           // the rate the audio leaves at, for the filter ceiling

        // amplitude envelope, all in gain except the times
        Stage _stage = Stage.Idle;
        double _t;                   // seconds since the stage began
        double _attack, _decay, _release;
        double _peak, _sustain;
        double _gain, _releaseFrom;

        // filter envelope
        double _vcfAttack, _vcfDecay, _vcfSustain, _vcfDepth;
        double _baseCutoff, _cutoffShift, _ceiling, _floor;

        // the LFO
        double _lfoCents, _lfoPhase, _lfoStep, _fadeSeconds, _fadeT;
        bool _ownLfo;

        /// <summary>Start this voice. Nothing here allocates.</summary>
        public void Start(KeygroupPatch kg, int note, int velocity, double sampleRate,
                          double wheelCents, long sequence)
        {
            _kg = kg;
            _sound = kg.Sound;
            _note = note;
            _startedAt = sequence;
            _sampleRate = sampleRate;

            // --- pitch. Constant pitch means the keygroup ignores which key was struck.
            double semis = kg.ZoneTranspose;
            if (!kg.ConstantPitch) semis += note - _sound.RootPitch;

            double ratio = Math.Pow(2.0, semis / 12.0);
            _step = _sound.SourceRate / sampleRate * ratio;
            _leaveRate = _sound.SourceRate * ratio;
            _pos = 0;

            // --- amplitude envelope
            double vel = velocity < 0 ? 0 : (velocity > 127 ? 127 : velocity);
            double depth = Clamp01(kg.VelToLoudness / 99.0);
            double velDb = -(127.0 - vel) * Cal.VelDbPerStep * depth;
            double zoneDb = kg.ZoneLoudness * Cal.LoudnessDbPerUnit;
            double sustainDb = -(1.0 - Clamp01(kg.VcaSustain / 99.0)) * Cal.SustainDb;

            _attack = Cal.EnvSeconds(kg.VcaAttack) * Cal.AttackScale;
            _decay = Cal.EnvSeconds(kg.VcaDecay);
            _release = Cal.EnvSeconds(kg.VcaRelease);
            _peak = Math.Min(Cal.DbToGain(velDb + zoneDb), 4.0);
            _sustain = _peak * Cal.DbToGain(sustainDb);

            _stage = _attack > 0.0005 ? Stage.Attack : Stage.Decay;
            _t = 0;
            _gain = _attack > 0.0005 ? 0 : _peak;

            // --- filter. The ceiling is the reconstruction limit, which moves with the
            // rate the audio leaves at - but we are running at the device rate, so it
            // cannot exceed what that can represent either.
            _ceiling = Math.Min(Cal.MaxRatio * _leaveRate, sampleRate * 0.45);
            _floor = Math.Min(Cal.FloorHz, _ceiling);
            _baseCutoff = Cal.CutoffHz(kg.ZoneFilter, _leaveRate);

            double track = Clamp(kg.KeyToFilter, 0, 99) / Cal.KeyFull;
            double keyShift = (note - 60) / 12.0 * track;
            double velShift = ((vel - Cal.VelPivot) / 127.0) *
                              (Clamp(kg.VelToFilter, 0, 99) / 99.0) * Cal.VelOctaves;
            _cutoffShift = keyShift + velShift;

            _vcfAttack = kg.VcfWritten ? Cal.EnvSeconds(kg.VcfAttack) * Cal.VcfTimeScale : 0;
            _vcfDecay = kg.VcfWritten ? Cal.EnvSeconds(kg.VcfDecay) * Cal.VcfTimeScale : 0;
            _vcfSustain = kg.VcfWritten ? Clamp01(kg.VcfSustain / 99.0) : 1;
            _vcfDepth = kg.VcfWritten ? (kg.VcfAmount / 50.0) * Cal.EnvOctaves : 0;

            _filter.Reset();
            _filter.SetCutoff(CutoffNow(0), sampleRate);

            // --- the LFO
            double own = kg.LfoDepth * Cal.LfoDepthCentsPerUnit;
            _lfoCents = own + wheelCents;
            _ownLfo = kg.LfoDesync;
            _lfoPhase = 0;
            _lfoStep = 2.0 * Math.PI *
                       (Cal.LfoRateHzAtZero + kg.LfoRate * Cal.LfoRateHzPerUnit) / sampleRate;
            _fadeSeconds = Cal.LfoDelayFadeConstant / Math.Max(1, 100 - kg.LfoDelay);
            _fadeT = 0;
        }

        /// <summary>Let go of the key. The note falls at its own release rate.</summary>
        public void Release()
        {
            if (_stage == Stage.Idle || _stage == Stage.Release) return;

            // A one-shot keygroup is a drum: it plays through whatever the key does.
            if (_kg.OneShot && !_sound.Loops) return;

            _releaseFrom = _gain;
            _stage = Stage.Release;
            _t = 0;
        }

        public void Kill() { _stage = Stage.Idle; }

        /// <summary>
        /// Add this voice into the buffer.
        ///
        /// <paramref name="sharedPhase"/> is the programme's own LFO, which voices with the
        /// desync bit CLEAR ride instead of their own. That was measured rather than
        /// assumed: with the bit clear two voices held their phase to within 3 degrees over
        /// six seconds, and with it set they ran at rates 3.5% apart and drifted a whole
        /// turn in the same six.
        /// </summary>
        public void Render(float[] buffer, int offset, int count, double sharedPhase)
        {
            if (_stage == Stage.Idle) return;

            float[] audio = _sound.Audio;
            int last = audio.Length - 1;
            double dt = 1.0 / _sampleRate;

            int done = 0;
            while (done < count && _stage != Stage.Idle)
            {
                int n = Math.Min(ControlBlock, count - done);

                // --- the modulators, once per block
                _filter.SetCutoff(CutoffNow(_t), _sampleRate);

                double fade = _fadeSeconds > 0.0005
                            ? (_fadeT >= _fadeSeconds ? 1.0 : _fadeT / _fadeSeconds) : 1.0;
                double cents = _lfoCents * fade;
                double phase = _ownLfo ? _lfoPhase : sharedPhase;
                double bend = cents == 0 ? 1.0 : Math.Pow(2.0, cents * Math.Sin(phase) / 1200.0);
                double step = _step * bend;

                double gainStart = _gain;
                double gainEnd = EnvelopeAfter(n * dt);
                double gainStep = (gainEnd - gainStart) / n;

                bool loops = _sound.Loops && _sound.LoopTo > _sound.LoopFrom;
                double end = loops ? _sound.LoopTo : audio.Length;

                for (int j = 0; j < n; j++)
                {
                    /*
                     * Round the loop, or off the end.
                     *
                     * The end to test against is the LOOP end, which for a sample whose
                     * loop runs to its last word is one frame past the last readable
                     * one. Testing against the array instead let the position sit in
                     * the gap between them, where it was neither wrapped nor finished,
                     * and the voice simply stopped after one pass. It cost three of the
                     * four failures the first run of EngineCheck reported.
                     */
                    if (_pos >= end)
                    {
                        if (!loops) { _stage = Stage.Idle; return; }
                        double len = _sound.LoopTo - _sound.LoopFrom;
                        do { _pos -= len; } while (_pos >= end);
                        if (_pos < 0) _pos = _sound.LoopFrom;
                    }

                    // interpolate towards the next frame, which for the last one is
                    // wherever the loop restarts rather than off the end of the array
                    int i0 = (int)_pos;
                    if (i0 > last) i0 = last;
                    int i1 = i0 + 1;
                    if (i1 > last) i1 = loops ? _sound.LoopFrom : last;

                    double frac = _pos - i0;
                    double x = audio[i0] + (audio[i1] - audio[i0]) * frac;

                    buffer[offset + done + j] += (float)(_filter.Process(x) * gainStart);
                    gainStart += gainStep;

                    _pos += step;
                }

                _gain = gainEnd;
                _t += n * dt;
                _fadeT += n * dt;
                _lfoPhase += _lfoStep * n;
                if (_lfoPhase > 2.0 * Math.PI) _lfoPhase -= 2.0 * Math.PI;

                AdvanceStage();
                done += n;
            }
        }


        double CutoffNow(double t)
        {
            double env;
            if (t < _vcfAttack) env = _vcfAttack > 0 ? t / _vcfAttack : 1;
            else if (t < _vcfAttack + _vcfDecay)
                env = _vcfDecay > 0 ? 1 - (1 - _vcfSustain) * ((t - _vcfAttack) / _vcfDecay)
                                    : _vcfSustain;
            else env = _vcfSustain;

            double hz = _baseCutoff * Math.Pow(2.0, _cutoffShift + env * _vcfDepth);
            return hz > _ceiling ? _ceiling : (hz < _floor ? _floor : hz);
        }

        /// <summary>
        /// Where the amplitude envelope will be in <paramref name="ahead"/> seconds.
        ///
        /// The decay and the release are straight lines in DECIBELS, not in amplitude. That
        /// is measured: a stored decay of 80 fell 2.9, 2.7, 3.0, 3.0, 2.7, 2.9 dB per fifth
        /// of a second, dead constant for two and a half seconds. A line in amplitude hangs
        /// near the peak and then drops off a cliff, which is audibly another instrument.
        /// </summary>
        double EnvelopeAfter(double ahead)
        {
            double t = _t + ahead;

            switch (_stage)
            {
                case Stage.Attack:
                    return _attack > 0 ? _peak * Math.Min(1.0, t / _attack) : _peak;

                case Stage.Decay:
                    if (_decay <= 0.0005) return _sustain;
                    return Fall(_peak, _sustain, Math.Min(1.0, t / _decay));

                case Stage.Release:
                    if (_release <= 0.0005) return 0;
                    return Fall(_releaseFrom, 1e-4, Math.Min(1.0, t / _release));

                default:
                    return _sustain;
            }
        }

        /// <summary>A straight line in decibels from one gain to another.</summary>
        static double Fall(double from, double to, double u)
        {
            if (from <= 1e-6) return 0;
            double lo = Math.Max(to, 1e-6);
            return from * Math.Pow(lo / from, u);
        }

        void AdvanceStage()
        {
            switch (_stage)
            {
                case Stage.Attack:
                    if (_t >= _attack) { _stage = Stage.Decay; _t = 0; }
                    break;
                case Stage.Decay:
                    if (_t >= _decay) { _stage = Stage.Sustain; _t = 0; _gain = _sustain; }
                    break;
                case Stage.Release:
                    if (_t >= _release || _gain <= 2e-4) _stage = Stage.Idle;
                    break;
            }
        }

        static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        static double Clamp01(double v) { return Clamp(v, 0, 1); }
    }
}
