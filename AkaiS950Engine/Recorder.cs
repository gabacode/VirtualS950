using System;
using System.IO;
using System.Threading;

namespace AkaiS950Engine
{
    /// <summary>
    /// Captures what the instrument actually plays, to a WAV file.
    ///
    /// The tap is the same buffer the sound card is handed, so what lands in the file is
    /// what came out of the speakers - every voice, both envelopes, the filter and the
    /// vibrato, mixed by the same code in the same order. That is the point of recording
    /// here rather than re-rendering: a re-render answers "what should it sound like",
    /// and this answers "what did it sound like", and those are only the same question
    /// until something is wrong.
    ///
    /// The audio thread must never block, so it only copies into a ring and moves a
    /// counter. A writer thread drains the ring to disk. If the writer falls behind far
    /// enough for the ring to lap, the recording is marked short rather than silently
    /// losing a hole in the middle - a recording you cannot trust is worse than none.
    /// </summary>
    public sealed class Recorder : IDisposable
    {
        readonly float[] _ring;
        readonly int _mask;
        readonly int _rate;

        long _written;              // frames the audio thread has put in
        long _drained;              // frames the writer has taken out
        volatile bool _stopping;
        volatile bool _lapped;

        Thread _writer;
        BinaryWriter _file;
        long _frames;

        public string Path { get; private set; }

        /// <summary>True if the writer could not keep up and samples were lost.</summary>
        public bool Overran { get { return _lapped; } }

        /// <summary>Frames committed to the file so far.</summary>
        public long Frames { get { return Interlocked.Read(ref _frames); } }

        public double Seconds { get { return Frames / (double)_rate; } }

        /// <param name="seconds">How much audio the ring holds. Only needs to cover the
        /// worst pause the writer thread might take, so a second is generous.</param>
        public Recorder(string path, int sampleRate, double seconds)
        {
            Path = path;
            _rate = sampleRate;

            int want = (int)(sampleRate * Math.Max(0.25, seconds));
            int size = 1;
            while (size < want) size <<= 1;      // power of two, so the wrap is a mask
            _ring = new float[size];
            _mask = size - 1;

            _file = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write,
                                                    FileShare.Read, 1 << 16));
            WriteHeader(0);

            _writer = new Thread(Drain) { IsBackground = true, Name = "VirtualS950 recorder" };
            _writer.Start();
        }

        /// <summary>
        /// Called on the audio thread, from the fill callback. Copies and returns.
        /// </summary>
        public void Write(float[] buffer, int offset, int count)
        {
            long at = Interlocked.Read(ref _written);

            // Would this overwrite audio the writer has not taken yet?
            if (at - Interlocked.Read(ref _drained) + count > _ring.Length)
            {
                _lapped = true;
                return;                          // drop rather than corrupt what is there
            }

            int start = (int)(at & _mask);
            int first = Math.Min(count, _ring.Length - start);
            Array.Copy(buffer, offset, _ring, start, first);
            if (first < count) Array.Copy(buffer, offset + first, _ring, 0, count - first);

            Interlocked.Add(ref _written, count);
        }

        void Drain()
        {
            var scratch = new byte[1 << 16];

            while (true)
            {
                long have = Interlocked.Read(ref _written) - Interlocked.Read(ref _drained);
                if (have == 0)
                {
                    if (_stopping) break;
                    Thread.Sleep(5);
                    continue;
                }

                int take = (int)Math.Min(have, scratch.Length / 2);
                int start = (int)(Interlocked.Read(ref _drained) & _mask);

                int n = 0;
                for (int i = 0; i < take; i++)
                {
                    float v = _ring[(start + i) & _mask];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    int s = (int)Math.Round(v * 32767.0);
                    scratch[n++] = (byte)s;
                    scratch[n++] = (byte)(s >> 8);
                }

                _file.Write(scratch, 0, n);
                Interlocked.Add(ref _drained, take);
                Interlocked.Add(ref _frames, take);
            }
        }

        /// <summary>Finish the file and give back its path.</summary>
        public string Stop()
        {
            if (_file == null) return Path;

            _stopping = true;
            if (_writer != null) { _writer.Join(2000); _writer = null; }

            _file.Flush();
            _file.BaseStream.Seek(0, SeekOrigin.Begin);
            WriteHeader(Interlocked.Read(ref _frames));
            _file.Flush();
            _file.Close();
            _file = null;

            return Path;
        }

        void WriteHeader(long frames)
        {
            int bytes = (int)(frames * 2);
            _file.Write(new[] { 'R', 'I', 'F', 'F' });
            _file.Write(36 + bytes);
            _file.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            _file.Write(16);
            _file.Write((short)1);          // PCM
            _file.Write((short)1);          // mono, as the engine renders
            _file.Write(_rate);
            _file.Write(_rate * 2);
            _file.Write((short)2);
            _file.Write((short)16);
            _file.Write(new[] { 'd', 'a', 't', 'a' });
            _file.Write(bytes);
        }

        public void Dispose() { Stop(); }
    }
}
