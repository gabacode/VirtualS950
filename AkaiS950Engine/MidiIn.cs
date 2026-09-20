using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AkaiS950Engine
{
    /// <summary>
    /// MIDI in through winmm.
    ///
    /// Short messages only - notes, controllers, pitch bend. No sysex, which a sampler
    /// front panel would want and a keyboard does not send.
    ///
    /// winmm calls back on its own thread, so whatever is handed the messages has to cope
    /// with that. The engine does: its note queue is lock-free precisely so this callback
    /// can post into it without touching the audio thread.
    /// </summary>
    public sealed class MidiIn : IDisposable
    {
        public delegate void MessageCallback(int status, int data1, int data2);

        const int MmSysErrNoError = 0;
        const int CallbackFunction = 0x00030000;
        const int MimData = 0x3C3;

        IntPtr _handle = IntPtr.Zero;
        readonly MidiProc _proc;           // kept alive, or the GC collects the callback
        readonly MessageCallback _onMessage;

        public string Error { get; private set; }
        public bool Open { get { return _handle != IntPtr.Zero; } }

        public MidiIn(MessageCallback onMessage)
        {
            if (onMessage == null) throw new ArgumentNullException("onMessage");
            _onMessage = onMessage;
            _proc = Callback;
        }

        /// <summary>Every input the machine has, in the order winmm numbers them.</summary>
        public static List<string> Ports()
        {
            var list = new List<string>();
            int n = midiInGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var caps = new MidiInCaps();
                if (midiInGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf(typeof(MidiInCaps))) == MmSysErrNoError)
                    list.Add(caps.szPname);
                else
                    list.Add("input " + i);
            }
            return list;
        }

        public bool Start(int port)
        {
            Stop();
            int rc = midiInOpen(out _handle, port, _proc, IntPtr.Zero, CallbackFunction);
            if (rc != MmSysErrNoError)
            {
                _handle = IntPtr.Zero;
                Error = "could not open that MIDI input (winmm " + rc + ")";
                return false;
            }
            midiInStart(_handle);
            return true;
        }

        public void Stop()
        {
            if (_handle == IntPtr.Zero) return;
            try { midiInStop(_handle); midiInReset(_handle); midiInClose(_handle); }
            catch { }
            _handle = IntPtr.Zero;
        }

        public void Dispose() { Stop(); }

        void Callback(IntPtr handle, int msg, IntPtr instance, int param1, int param2)
        {
            if (msg != MimData) return;

            // param1 packs the message: status in the low byte, then the two data bytes
            int status = param1 & 0xFF;
            int d1 = (param1 >> 8) & 0x7F;
            int d2 = (param1 >> 16) & 0x7F;

            try { _onMessage(status, d1, d2); }
            catch { /* never throw back into winmm's thread */ }
        }

        delegate void MidiProc(IntPtr handle, int msg, IntPtr instance, int p1, int p2);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct MidiInCaps
        {
            public short wMid, wPid;
            public int vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public int dwSupport;
        }

        [DllImport("winmm.dll")] static extern int midiInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        static extern int midiInGetDevCaps(IntPtr id, ref MidiInCaps caps, int size);

        [DllImport("winmm.dll")]
        static extern int midiInOpen(out IntPtr handle, int deviceId, MidiProc proc,
                                     IntPtr instance, int flags);

        [DllImport("winmm.dll")] static extern int midiInStart(IntPtr handle);
        [DllImport("winmm.dll")] static extern int midiInStop(IntPtr handle);
        [DllImport("winmm.dll")] static extern int midiInReset(IntPtr handle);
        [DllImport("winmm.dll")] static extern int midiInClose(IntPtr handle);
    }
}
