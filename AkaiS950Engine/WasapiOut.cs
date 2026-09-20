using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AkaiS950Engine
{
    /// <summary>
    /// Audio out through WASAPI, event driven.
    ///
    /// Hand-written COM interop rather than a library, because there is no package manager
    /// here and a sampler that needs a NuGet restore to make a sound is worse than three
    /// hundred lines of declarations. Nothing outside this file knows WASAPI exists.
    ///
    /// WHY NOT SoundPlayer, WHICH IS WHAT WAS HERE
    ///
    /// SoundPlayer hands a whole WAV to winmm and waits. It cannot mix, cannot be
    /// interrupted, and its latency is measured in hundreds of milliseconds - fine for
    /// auditioning a sample, useless for playing one. Shared-mode WASAPI with an event
    /// callback runs at whatever the audio engine's period is, typically ten milliseconds
    /// and often less, which is playable.
    ///
    /// THE CALLBACK RULE
    ///
    /// Fill(float[], int) is called on a thread that must never be kept waiting. No locks,
    /// no allocation, no file access, nothing that can block. The engine is built to that
    /// rule; anything else put in here has to be too.
    /// </summary>
    public sealed class WasapiOut : IDisposable
    {
        public delegate void FillCallback(float[] mono, int frames);

        const int ClsCtxAll = 23;
        const int RenderFlow = 0, ConsoleRole = 0;
        const int ShareModeShared = 0;
        const uint StreamFlagsEventCallback = 0x00040000;
        const uint StreamFlagsAutoConvertPcm = 0x80000000;
        const uint StreamFlagsSrcDefaultQuality = 0x08000000;

        // 100-nanosecond units, which is what WASAPI counts in
        const long Millisecond = 10000;

        IAudioClient _client;
        IAudioRenderClient _render;
        IntPtr _event = IntPtr.Zero;
        Thread _thread;
        volatile bool _running;

        float[] _mono = new float[0];
        int _channels, _bufferFrames;
        readonly FillCallback _fill;

        public int SampleRate { get; private set; }
        public double LatencyMs { get; private set; }
        public string Error { get; private set; }

        public WasapiOut(FillCallback fill)
        {
            if (fill == null) throw new ArgumentNullException("fill");
            _fill = fill;
        }

        /// <summary>
        /// Open the default output. Returns false with Error set rather than throwing, so a
        /// machine with no sound card is a message in the status bar and not a crash.
        ///
        /// NOTE: this calls the fill callback once BEFORE it returns, to prime the buffer so
        /// the first thing the card plays is audio rather than whatever was left in it.
        /// Whatever the callback needs has to exist before Start is called, not after.
        /// </summary>
        public bool Start(double requestedLatencyMs)
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice device;
                enumerator.GetDefaultAudioEndpoint(RenderFlow, ConsoleRole, out device);

                object obj;
                var iid = typeof(IAudioClient).GUID;
                device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out obj);
                _client = (IAudioClient)obj;

                IntPtr formatPtr;
                _client.GetMixFormat(out formatPtr);

                var wf = (WaveFormatEx)Marshal.PtrToStructure(formatPtr, typeof(WaveFormatEx));
                SampleRate = wf.nSamplesPerSec;
                _channels = wf.nChannels;

                if (!IsFloat(wf, formatPtr))
                {
                    Error = "the mix format is " + wf.wBitsPerSample + "-bit, tag " +
                            wf.wFormatTag + ", which this does not handle";
                    Marshal.FreeCoTaskMem(formatPtr);
                    return false;
                }

                // Ask for the buffer we want. The engine is free to give us a bigger one,
                // which is why the size is read back rather than assumed.
                long requested = (long)(requestedLatencyMs * Millisecond);
                _client.Initialize(ShareModeShared,
                                   StreamFlagsEventCallback | StreamFlagsAutoConvertPcm |
                                   StreamFlagsSrcDefaultQuality,
                                   requested, 0, formatPtr, IntPtr.Zero);
                Marshal.FreeCoTaskMem(formatPtr);

                _client.GetBufferSize(out _bufferFrames);
                LatencyMs = _bufferFrames * 1000.0 / SampleRate;

                _event = CreateEvent(IntPtr.Zero, false, false, null);
                if (_event == IntPtr.Zero) { Error = "could not create the render event"; return false; }
                _client.SetEventHandle(_event);

                var renderIid = typeof(IAudioRenderClient).GUID;
                object svc;
                _client.GetService(ref renderIid, out svc);
                _render = (IAudioRenderClient)svc;

                _mono = new float[_bufferFrames];

                // Fill it once before starting, so the first thing the card plays is audio
                // rather than whatever was in the buffer.
                WriteOne(_bufferFrames);

                _running = true;
                _thread = new Thread(Pump);
                _thread.IsBackground = true;
                _thread.Priority = ThreadPriority.Highest;
                _thread.Start();

                _client.Start();
                return true;
            }
            catch (Exception e)
            {
                Error = e.Message;
                Stop();
                return false;
            }
        }

        /// <summary>
        /// Is the engine handing us floats?
        ///
        /// Shared mode always mixes in 32-bit float in practice, but "in practice" is not
        /// the same as "always", and writing floats into a buffer the card reads as 16-bit
        /// integers is a very loud noise.
        /// </summary>
        static bool IsFloat(WaveFormatEx wf, IntPtr ptr)
        {
            const int WaveFormatIeeeFloat = 3, WaveFormatExtensible = unchecked((short)0xFFFE);

            if (wf.wFormatTag == WaveFormatIeeeFloat) return wf.wBitsPerSample == 32;
            if (wf.wFormatTag != WaveFormatExtensible) return false;

            /*
             * The sub-format GUID sits at offset 24.
             *
             * WAVEFORMATEX is 18 bytes, then two for the valid-bits union and four for the
             * channel mask. Reading it from 26 - as though the union were four bytes - finds
             * the middle of the GUID, decides the card is not float, and refuses to open a
             * perfectly ordinary output.
             */
            var sub = new byte[16];
            Marshal.Copy(new IntPtr(ptr.ToInt64() + 24), sub, 0, 16);
            var guid = new Guid(sub);
            return wf.wBitsPerSample == 32 &&
                   guid == new Guid("00000003-0000-0010-8000-00aa00389b71");
        }

        void Pump()
        {
            while (_running)
            {
                if (WaitForSingleObject(_event, 2000) != 0) continue;   // 0 = signalled
                if (!_running) break;

                try
                {
                    int padding;
                    _client.GetCurrentPadding(out padding);
                    int free = _bufferFrames - padding;
                    if (free > 0) WriteOne(free);
                }
                catch
                {
                    // The device went away - a USB interface unplugged mid-note. Stop
                    // quietly rather than throwing on a thread nobody is watching.
                    _running = false;
                }
            }
        }

        void WriteOne(int frames)
        {
            IntPtr buffer;
            _render.GetBuffer(frames, out buffer);

            _fill(_mono, frames);

            // Mono out of the engine, spread across however many channels the card has.
            unsafe
            {
                float* dst = (float*)buffer;
                for (int i = 0; i < frames; i++)
                {
                    float v = _mono[i];
                    for (int c = 0; c < _channels; c++) *dst++ = v;
                }
            }

            _render.ReleaseBuffer(frames, 0);
        }

        public void Stop()
        {
            _running = false;

            if (_thread != null) { _thread.Join(500); _thread = null; }

            if (_client != null)
            {
                try { _client.Stop(); } catch { }
                Marshal.ReleaseComObject(_client);
                _client = null;
            }
            if (_render != null) { Marshal.ReleaseComObject(_render); _render = null; }
            if (_event != IntPtr.Zero) { CloseHandle(_event); _event = IntPtr.Zero; }
        }

        public void Dispose() { Stop(); }

        // ------------------------------------------------------------------ interop

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateEvent(IntPtr attrs, bool manualReset, bool initial, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct WaveFormatEx
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            void GetDevice(string id, out IMMDevice device);
            void RegisterEndpointNotificationCallback(IntPtr client);
            void UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                          [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            void OpenPropertyStore(int access, out IntPtr store);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetState(out int state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            void Initialize(int shareMode, uint streamFlags, long bufferDuration,
                            long periodicity, IntPtr format, IntPtr sessionGuid);
            void GetBufferSize(out int frames);
            void GetStreamLatency(out long latency);
            void GetCurrentPadding(out int frames);
            void IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
            void GetMixFormat(out IntPtr format);
            void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            void Start();
            void Stop();
            void Reset();
            void SetEventHandle(IntPtr handle);
            void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioRenderClient
        {
            void GetBuffer(int frames, out IntPtr buffer);
            void ReleaseBuffer(int frames, int flags);
        }
    }
}
