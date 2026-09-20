using System;
using System.Collections.Generic;

namespace AkaiS950Engine
{
    /// <summary>
    /// The instrument: eight voices, a patch, and a buffer to fill.
    ///
    /// Nothing in here knows what is driving it. The editor drives it from a piano keyboard
    /// and a MIDI port; a plugin would drive it from a host's note events; the tests drive
    /// it from a loop and render to a file. That is deliberate - it is the one decision
    /// that keeps a plugin possible later without any of this being rewritten.
    ///
    /// THREADS
    ///
    /// Render runs on the audio thread and must never allocate, never lock and never block.
    /// Notes arrive from somewhere else, so they go into a small lock-free ring and are
    /// picked up at the top of the next Render. A lock here would be a dropout.
    /// </summary>
    public sealed class Engine
    {
        /// <summary>What the machine has. Voices past this steal the oldest.</summary>
        public const int Polyphony = 8;

        readonly Voice[] _voices = new Voice[Polyphony];
        readonly List<KeygroupPatch> _matched = new List<KeygroupPatch>(8);

        // the event ring: written by whoever is playing, read by the audio thread
        struct Event { public byte Kind, A, B; }
        const int RingSize = 256;
        readonly Event[] _ring = new Event[RingSize];
        int _write, _read;

        const byte EvNoteOn = 1, EvNoteOff = 2, EvWheel = 3, EvAllOff = 4;

        Patch _patch;
        long _sequence;

        /*
         * What the last note-on actually started.
         *
         * Not for the engine's benefit - for the caller's, so a report of "every key
         * plays the same sample" can be answered with what the voices really picked up
         * rather than with what the caller meant to ask for. References only: naming
         * them would mean building a string on the audio thread, which is the one thing
         * the render path is not allowed to do.
         */
        readonly Sound[] _started = new Sound[Polyphony];
        int _startedCount, _startedNote = -1;
        double _sharedPhase, _sharedStep;
        int _wheel;

        public double SampleRate { get; private set; }

        /// <summary>The master trim, as a plain gain. Eight voices at once can clip.</summary>
        public float Gain = 0.7f;

        public Engine(double sampleRate)
        {
            SampleRate = sampleRate <= 0 ? 48000 : sampleRate;
            for (int i = 0; i < _voices.Length; i++) _voices[i] = new Voice();
        }

        /// <summary>
        /// What to play. Safe to call while running: the audio thread only ever reads it,
        /// and a reference assignment is atomic.
        /// </summary>
        public void SetPatch(Patch patch)
        {
            _patch = patch;
            _sharedStep = 0;
            if (patch != null)
            {
                // The programme's own LFO runs at the rate its keygroups ask for. They
                // almost always agree; where they do not, the first one wins, since one
                // shared oscillator cannot be at two rates at once.
                for (int i = 0; i < patch.Keygroups.Count; i++)
                {
                    KeygroupPatch k = patch.Keygroups[i];
                    if (k.LfoDesync) continue;
                    _sharedStep = 2.0 * Math.PI *
                        (Cal.LfoRateHzAtZero + k.LfoRate * Cal.LfoRateHzPerUnit) / SampleRate;
                    break;
                }
            }
        }

        /// <summary>The note the last note-on was for, or -1.</summary>
        public int LastNote { get { return _startedNote; } }

        /// <summary>How many voices that note-on started.</summary>
        public int LastStartedCount { get { return _startedCount; } }

        /// <summary>The sound one of them picked up.</summary>
        public Sound LastStarted(int i)
        {
            return i >= 0 && i < _startedCount ? _started[i] : null;
        }

        public int ActiveVoices
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _voices.Length; i++) if (_voices[i].Active) n++;
                return n;
            }
        }

        // ------------------------------------------------------------------ playing

        public void NoteOn(int note, int velocity) { Post(EvNoteOn, note, velocity); }
        public void NoteOff(int note) { Post(EvNoteOff, note, 0); }
        public void Modwheel(int value) { Post(EvWheel, value, 0); }
        public void AllNotesOff() { Post(EvAllOff, 0, 0); }

        void Post(byte kind, int a, int b)
        {
            int w = _write;
            int next = (w + 1) % RingSize;
            if (next == _read) return;            // full: drop it rather than block

            _ring[w].Kind = kind;
            _ring[w].A = (byte)(a < 0 ? 0 : (a > 255 ? 255 : a));
            _ring[w].B = (byte)(b < 0 ? 0 : (b > 255 ? 255 : b));
            System.Threading.Thread.MemoryBarrier();
            _write = next;
        }

        void DrainEvents()
        {
            while (_read != _write)
            {
                Event e = _ring[_read];
                _read = (_read + 1) % RingSize;

                switch (e.Kind)
                {
                    case EvNoteOn: StartNote(e.A, e.B); break;
                    case EvNoteOff: StopNote(e.A); break;
                    case EvWheel: _wheel = e.A; break;
                    case EvAllOff:
                        for (int i = 0; i < _voices.Length; i++) _voices[i].Release();
                        break;
                }
            }
        }

        void StartNote(int note, int velocity)
        {
            Patch p = _patch;
            _startedCount = 0;
            _startedNote = note;
            if (p == null) return;

            p.Matching(note, velocity, _matched);

            for (int m = 0; m < _matched.Count; m++)
            {
                KeygroupPatch kg = _matched[m];
                if (_startedCount < _started.Length) _started[_startedCount++] = kg.Sound;

                // What the wheel adds, in cents. Byte 22 scales it, proportionally - the
                // machine gave 0.509 of full at byte 22 = 50, where proportional is 0.505.
                double wheelCents = Cal.LfoWheelCentsAtFull *
                                    (kg.LfoModwheelDepth / 99.0) * (_wheel / 127.0);

                if (kg.LfoDepth * Cal.LfoDepthCentsPerUnit + wheelCents < 0.5) wheelCents = 0;

                Voice v = Take();
                v.Start(kg, note, velocity, SampleRate, wheelCents, _sequence++);
            }
        }

        void StopNote(int note)
        {
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].Active && _voices[i].Note == note && _voices[i].Held)
                    _voices[i].Release();
        }

        /// <summary>A free voice, or the oldest one if they are all busy.</summary>
        Voice Take()
        {
            for (int i = 0; i < _voices.Length; i++)
                if (!_voices[i].Active) return _voices[i];

            int oldest = 0;
            for (int i = 1; i < _voices.Length; i++)
                if (_voices[i].StartedAt < _voices[oldest].StartedAt) oldest = i;

            _voices[oldest].Kill();
            return _voices[oldest];
        }

        // ------------------------------------------------------------------- render

        /// <summary>
        /// Fill <paramref name="count"/> mono samples. Allocates nothing.
        /// </summary>
        public void Render(float[] buffer, int offset, int count)
        {
            DrainEvents();

            Array.Clear(buffer, offset, count);

            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].Active)
                    _voices[i].Render(buffer, offset, count, _sharedPhase);

            _sharedPhase += _sharedStep * count;
            if (_sharedPhase > 2.0 * Math.PI) _sharedPhase %= 2.0 * Math.PI;

            float g = Gain;
            for (int i = 0; i < count; i++)
            {
                float v = buffer[offset + i] * g;
                buffer[offset + i] = v > 1f ? 1f : (v < -1f ? -1f : v);
            }
        }
    }
}
