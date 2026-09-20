using System;
using System.Collections.Generic;
using AkaiS950Engine;
using AkaiS950List;

namespace AkaiS950Studio
{
    /// <summary>
    /// The instrument: a disk, an engine, a sound card and a MIDI port.
    ///
    /// This is the only place that knows about both halves. AkaiS950Engine knows nothing of
    /// disks, and AkaiDisk knows nothing of audio - which is what lets the same engine be
    /// driven by a plugin host later without either of them being disturbed.
    ///
    /// Everything here is safe to call from the UI thread. The engine's note queue is
    /// lock-free, so a click on the keyboard, a MIDI note from winmm's callback thread and
    /// the audio thread reading the queue never wait on each other.
    /// </summary>
    internal sealed class Instrument : IDisposable
    {
        readonly Engine _engine;
        readonly WasapiOut _out;
        MidiIn _midi;

        // Decoding a sample is slow enough to matter and the same one is played over and
        // over, so it is kept. Keyed on the entry itself, which is a new object each time
        // the directory is re-read - so a reload quietly invalidates the lot.
        readonly Dictionary<AkaiEntry, Sound> _sounds = new Dictionary<AkaiEntry, Sound>();

        /// <summary>
        /// How much of the loop join to crossfade, in milliseconds. Zero is what the S950
        /// itself does - it splices, and clicks when the points are bad.
        /// </summary>
        public double LoopCrossfadeMs = LoopSmoothing.DefaultCrossfadeMs;

        /// <summary>
        /// Pull the loop ends onto zero crossings as well. OFF, and see LoopSmoothing for
        /// why: it changes the loop length, and the loop length is the pitch of the looped
        /// part, so it trades a click for a tone that does not belong to the note.
        /// </summary>
        public bool SnapLoopsToZero = false;

        /// <summary>
        /// Play every keygroup with the filter wide open and not tracking the key.
        ///
        /// For answering one question: are two keygroups playing different samples, or
        /// the same sample through a different filter? Key tracking makes one sample
        /// sound like several, which is what it is for, and which is also exactly what
        /// makes the question hard to settle by ear. With this set, anything still
        /// different is different in the sample.
        /// </summary>
        public bool DefeatFilter = false;

        public bool Running { get; private set; }
        public string Error { get; private set; }
        public double LatencyMs { get { return _out.LatencyMs; } }
        public int SampleRate { get { return _out.SampleRate; } }
        public int ActiveVoices { get { return _engine.ActiveVoices; } }

        public Instrument()
        {
            // The engine is built before the output, because opening the output calls the
            // fill callback once to prime the buffer.
            _engine = new Engine(48000);
            _out = new WasapiOut(Fill);
        }

        public bool Start()
        {
            if (Running) return true;

            if (!_out.Start(10))
            {
                Error = _out.Error;
                return false;
            }

            // The card decides the rate, not us. Rebuilding is cheaper than resampling
            // every voice, and this happens once.
            if (_out.SampleRate != (int)_engine.SampleRate)
            {
                _out.Stop();
                var again = new Engine(_out.SampleRate);
                again.SetPatch(_patch);
                _engineAtRate = again;
                if (!_out.Start(10)) { Error = _out.Error; return false; }
            }

            Running = true;
            return true;
        }

        // The engine actually in use: the one built at the card's rate, if there is one.
        Engine _engineAtRate;
        Engine Live { get { return _engineAtRate ?? _engine; } }

        Patch _patch;

        void Fill(float[] mono, int frames)
        {
            Live.Render(mono, 0, frames);

            // The tap, after the mix and before the card. Null almost always, and a
            // copy into a ring when it is not, so the cost of the feature when it is
            // off is one null test per buffer.
            Recorder r = _recorder;
            if (r != null) r.Write(mono, 0, frames);
        }

        // ----------------------------------------------------------------- recording

        volatile Recorder _recorder;
        readonly List<string> _marks = new List<string>();

        public bool Recording { get { return _recorder != null; } }

        /// <summary>Seconds captured so far, or zero when not recording.</summary>
        public double RecordedSeconds
        {
            get { Recorder r = _recorder; return r == null ? 0 : r.Seconds; }
        }

        /// <summary>
        /// Start capturing what is played to a WAV beside a text file of marks.
        ///
        /// The marks are what makes a recording analysable rather than merely audible:
        /// each one is a position in seconds and what was asked for at that moment, so a
        /// stretch of the waveform can be held against the sample it was supposed to be.
        /// </summary>
        public bool StartRecording(string path)
        {
            if (_recorder != null) return false;
            if (!Running && !Start()) return false;

            _marks.Clear();
            try { _recorder = new Recorder(path, SampleRate, 1.0); }
            catch (Exception ex) { Error = ex.Message; return false; }
            return true;
        }

        /// <summary>Note in the margin of the recording, at wherever it has got to.</summary>
        public void Mark(string what)
        {
            Recorder r = _recorder;
            if (r == null) return;
            lock (_marks) _marks.Add(r.Seconds.ToString("F4") + "	" + what);
        }

        /// <summary>Close the file. Returns its path, or null if nothing was recording.</summary>
        public string StopRecording()
        {
            Recorder r = _recorder;
            if (r == null) return null;
            _recorder = null;

            string path = r.Stop();
            if (r.Overran) Error = "The recording dropped samples - the disk could not keep up.";

            try
            {
                lock (_marks)
                    System.IO.File.WriteAllLines(
                        System.IO.Path.ChangeExtension(path, ".marks.txt"), _marks.ToArray());
            }
            catch { /* the audio is the part that matters */ }

            return path;
        }

        // ------------------------------------------------------------------- playing

        public void NoteOn(int note, int velocity) { Live.NoteOn(note, velocity); }

        /// <summary>
        /// What the last note actually sounded, named.
        ///
        /// Read after the note has been rendered, so it is one buffer behind - which for
        /// a status line is close enough, and it is the truth rather than an intention.
        /// </summary>
        /// <summary>
        /// What the loaded patch says this note and velocity WOULD sound, asked now.
        ///
        /// LastPlayed reports what the audio thread did, so it is one buffer behind. This
        /// asks the same question of the patch synchronously, which is what a diagnostic
        /// wants: it can be set beside what the editor thinks it is playing, at the moment
        /// of the click, with nothing to wait for.
        /// </summary>
        public string WouldPlay(int note, int velocity)
        {
            Patch p = _patch;
            if (p == null) return "(no programme loaded)";

            var hit = new List<KeygroupPatch>();
            p.Matching(note, velocity, hit);
            if (hit.Count == 0) return "(nothing)";

            var names = new List<string>();
            for (int i = 0; i < hit.Count; i++)
                names.Add(hit[i].Sound.Name + " [kg " + (hit[i].KeygroupIndex + 1) + "]");
            return string.Join(" + ", names.ToArray());
        }

        /// <summary>
        /// What every sounding voice is reading, right now.
        ///
        /// The end of the chain. WouldPlay says what the patch intends and LastPlayed says
        /// what the note-on picked; this says what the voices are actually pulling audio
        /// from, which is the only one of the three that cannot be argued with.
        /// </summary>
        public string SoundingNow()
        {
            Engine e = Live;
            var parts = new List<string>();

            for (int i = 0; i < e.VoiceCount; i++)
            {
                Voice v = e.VoiceAt(i);
                if (v == null || !v.Active) continue;
                Sound s = v.Playing;
                parts.Add("note " + v.Note + "=" + (s == null ? "(null)" : s.Name) +
                          "/" + (s == null ? 0 : s.Audio.Length) + "w");
            }
            return parts.Count == 0 ? "(silent)" : string.Join("  ", parts.ToArray());
        }

        public string LastPlayed()
        {
            Engine e = Live;
            int n = e.LastStartedCount;
            if (n == 0) return "nothing";

            var names = new List<string>();
            for (int i = 0; i < n; i++)
            {
                Sound s = e.LastStarted(i);
                if (s != null && !names.Contains(s.Name)) names.Add(s.Name);
            }
            return string.Join(" + ", names.ToArray());
        }
        public void NoteOff(int note) { Live.NoteOff(note); }
        public void AllNotesOff() { Live.AllNotesOff(); }

        public float Gain
        {
            get { return Live.Gain; }
            set { _engine.Gain = value; if (_engineAtRate != null) _engineAtRate.Gain = value; }
        }

        // ---------------------------------------------------------------------- MIDI

        public static List<string> MidiPorts() { return MidiIn.Ports(); }

        public bool OpenMidi(int port)
        {
            CloseMidi();
            _midi = new MidiIn(OnMidi);
            if (_midi.Start(port)) return true;
            Error = _midi.Error;
            _midi = null;
            return false;
        }

        public void CloseMidi()
        {
            if (_midi == null) return;
            _midi.Dispose();
            _midi = null;
        }

        /// <summary>
        /// Called on winmm's own thread. Posts into the engine and does nothing else - no
        /// UI, no locks, nothing that could keep the MIDI driver waiting.
        /// </summary>
        void OnMidi(int status, int d1, int d2)
        {
            int kind = status & 0xF0;

            if (kind == 0x90 && d2 > 0) Live.NoteOn(d1, d2);
            else if (kind == 0x80 || (kind == 0x90 && d2 == 0)) Live.NoteOff(d1);
            else if (kind == 0xB0 && d1 == 1) Live.Modwheel(d2);
            else if (kind == 0xB0 && (d1 == 120 || d1 == 123)) Live.AllNotesOff();
        }

        // ------------------------------------------------------------------- patches

        /// <summary>
        /// Load a programme. Everything the engine needs is copied out now, so playing a
        /// note never touches the disk image or the directory.
        /// </summary>
        public void SetProgram(AkaiDisk disk, AkaiEntry program)
        {
            if (disk == null || program == null || program.Type != 'P')
            {
                _patch = null;
                Live.SetPatch(null);
                return;
            }

            var patch = new Patch { Name = program.Name.Trim() };
            var groups = disk.Keygroups(program);

            for (int i = 0; i < groups.Count; i++)
            {
                AkaiDisk.Keygroup kg = groups[i];

                /*
                 * The two zones are velocity alternatives, so they split the range at the
                 * switch rather than both sounding. A switch of 128 leaves zone 1 the
                 * whole range, which is how the panel turns the second zone off.
                 */
                int split = kg.VelocitySwitch;
                if (split < 1 || split > 128) split = 128;

                if (!kg.HasSecondZone)
                {
                    AddZone(disk, patch, kg, kg.Zone1, 0, 127);
                }
                else
                {
                    AddZone(disk, patch, kg, kg.Zone1, 0, Math.Min(127, split - 1));
                    if (split <= 127) AddZone(disk, patch, kg, kg.Zone2, split, 127);
                }
            }

            _patch = patch;
            Live.SetPatch(patch);
        }

        void AddZone(AkaiDisk disk, Patch patch, AkaiDisk.Keygroup kg, AkaiDisk.Zone zone,
                     int velFrom, int velTo)
        {
            if (velTo < velFrom) return;
            if (zone == null || string.IsNullOrEmpty(zone.Name)) return;

            AkaiEntry sample = FindSample(disk, zone.Name);
            if (sample == null) return;

            Sound sound = SoundFor(disk, sample);
            if (sound == null) return;

            patch.Keygroups.Add(new KeygroupPatch
            {
                LowKey = kg.LowKey,
                HighKey = kg.HighKey,
                KeygroupIndex = kg.Index,
                VelocityFrom = velFrom,
                VelocityTo = velTo,
                Sound = sound,

                VcaAttack = kg.VcaAttack, VcaDecay = kg.VcaDecay,
                VcaSustain = kg.VcaSustain, VcaRelease = kg.VcaRelease,

                VcfWritten = !DefeatFilter &&
                             KeygroupPatch.LooksWritten(kg.VcfAttack, kg.VcfDecay,
                                                        kg.VcfSustain, kg.VcfRelease),
                VcfAttack = kg.VcfAttack, VcfDecay = kg.VcfDecay,
                VcfSustain = kg.VcfSustain, VcfRelease = kg.VcfRelease,
                VcfAmount = kg.VcfAmount,

                VelToFilter = DefeatFilter ? 0 : kg.VelToFilter,
                KeyToFilter = DefeatFilter ? 0 : kg.KeyToFilter,
                VelToLoudness = kg.VelToLoudness,

                LfoDelay = kg.LfoDelay, LfoRate = kg.LfoRate, LfoDepth = kg.LfoDepth,
                LfoModwheelDepth = kg.LfoModwheelDepth, LfoDesync = kg.LfoDesync,

                ZoneFilter = DefeatFilter ? 99 : zone.Filter,
                ZoneLoudness = zone.Loudness,
                ZoneTranspose = zone.PitchOffset,

                ConstantPitch = kg.ConstantPitch,
                OneShot = kg.OneShot
            });
        }

        /// <summary>One sample, decoded once and kept.</summary>
        Sound SoundFor(AkaiDisk disk, AkaiEntry e)
        {
            Sound got;
            if (_sounds.TryGetValue(e, out got)) return got;

            short[] words = disk.SampleWords12(e);
            if (words.Length == 0) return null;

            var audio = new float[words.Length];
            for (int i = 0; i < words.Length; i++) audio[i] = words[i] / 2048f;

            // The machine plays end-length .. end, so the start follows from the length
            // rather than from the stored start - which is simply zero in 250 of the
            // library's 324 looped samples.
            bool loops = e.LoopMode != 'O' && e.LoopLength > 0 && e.LoopEnd > 0;
            int to = (int)Math.Min(e.LoopEnd, words.Length);
            int from = (int)Math.Max(0, e.LoopEnd - e.LoopLength);

            var sound = new Sound
            {
                Name = e.Name.Trim(),
                Audio = audio,
                SourceRate = e.SampleRate < 1000 ? 40000 : e.SampleRate,
                RootPitch = e.NominalPitch + e.FinePitch / 16.0,
                Loops = loops && to > from,
                LoopFrom = from,
                LoopTo = to
            };

            // Join the loop cleanly. The machine splices and clicks if the points are
            // bad; this does not, which is the one place the instrument knowingly sounds
            // better than the hardware. LoopCrossfadeMs = 0 turns it off.
            LoopSmoothing.Polish(sound, LoopCrossfadeMs, SnapLoopsToZero);

            _sounds[e] = sound;
            return sound;
        }

        /// <summary>Forget the decoded audio - after an edit, or a reload.</summary>
        public void Invalidate() { _sounds.Clear(); }

        static AkaiEntry FindSample(AkaiDisk d, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string want = name.Trim();

            for (int i = 0; i < d.Entries.Count; i++)
            {
                AkaiEntry x = d.Entries[i];
                if (x.Type == 'S' &&
                    string.Equals(x.Name.Trim(), want, StringComparison.OrdinalIgnoreCase))
                    return x;
            }
            return null;
        }

        /// <summary>
        /// Audition one sample on its own, with no keygroup around it.
        ///
        /// Built as a patch of one wide-open keygroup so it goes through the same engine as
        /// everything else - a second playback path is a second thing to keep in step.
        /// </summary>
        public void PlaySample(AkaiDisk disk, AkaiEntry sample, int note)
        {
            Sound sound = SoundFor(disk, sample);
            if (sound == null) return;

            var patch = new Patch { Name = sound.Name };
            patch.Keygroups.Add(new KeygroupPatch
            {
                LowKey = 0, HighKey = 127, VelocityFrom = 0, VelocityTo = 127, Sound = sound,
                VcaAttack = 0, VcaDecay = 0, VcaSustain = 99, VcaRelease = 0,
                VcfWritten = true, VcfSustain = 99, ZoneFilter = 99,
                LfoDesync = true
            });

            _patch = patch;
            Live.SetPatch(patch);
            Live.AllNotesOff();
            Live.NoteOn(note, 100);

            /*
             * Nothing will ever let go of this one.
             *
             * A keygroup gets its note-off from the key being released; auditioning a
             * sample has no key, so a looped sample would sound until the program closed.
             * A one-shot ends by itself and needs none of this, but a loop is exactly what
             * loops are for.
             *
             * The whole sample, or ten seconds, whichever is shorter - long enough to hear
             * what the loop does, short enough not to become furniture. Stop() ends it
             * sooner, and so does playing anything else.
             */
            double length = sound.Audio.Length / (double)Math.Max(1, sound.SourceRate);
            double seconds = sound.Loops
                ? Math.Min(10.0, Math.Max(2.0, length * 3))   // long enough to hear the join
                : length + 0.25;                              // it ends itself; this is a backstop
            StopAfter(note, seconds);
        }

        System.Threading.Timer _auditionTimer;

        /// <summary>Release a note after a while, for a preview nobody will release.</summary>
        void StopAfter(int note, double seconds)
        {
            if (_auditionTimer != null) { _auditionTimer.Dispose(); _auditionTimer = null; }

            _auditionTimer = new System.Threading.Timer(delegate
            {
                Live.NoteOff(note);
            }, null, (int)(seconds * 1000), System.Threading.Timeout.Infinite);
        }

        /// <summary>Everything off, now.</summary>
        public void Stop()
        {
            if (_auditionTimer != null) { _auditionTimer.Dispose(); _auditionTimer = null; }
            Live.AllNotesOff();
        }

        public void Dispose()
        {
            if (_auditionTimer != null) { _auditionTimer.Dispose(); _auditionTimer = null; }
            StopRecording();
            CloseMidi();
            _out.Dispose();
            Running = false;
        }
    }
}
