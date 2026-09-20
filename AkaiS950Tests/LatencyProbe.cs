using System;
using System.Runtime.InteropServices;

/// <summary>
/// What latency is actually available on this machine, and at what cost.
///
/// There are three ways to get audio out of Windows and they differ in what they take
/// away from everyone else:
///
///   shared, default period    what the engine is doing now. Mixes with every other
///                             application. Whatever period the audio engine feels like.
///
///   shared, minimum period    IAudioClient3, Windows 10 and later. Still mixes with
///                             everything else - nothing is taken away - but runs at the
///                             smallest period the driver will admit to.
///
///   exclusive                 the endpoint is yours and nobody else's. Every other
///                             application on that device stops.
///
/// This asks the driver which periods it supports rather than assuming, because the answer
/// is a property of the hardware and varies enormously between a motherboard codec and an
/// interface with a written-for-purpose driver.
/// </summary>
static class LatencyProbe
{
    static int Main()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

            IMMDevice deflt;
            enumerator.GetDefaultAudioEndpoint(0, 0, out deflt);
            string defaultId;
            deflt.GetId(out defaultId);

            IMMDeviceCollection all;
            enumerator.EnumAudioEndpoints(0, 1, out all);   // render, active only
            int count;
            all.GetCount(out count);

            for (int n = 0; n < count; n++)
            {
                IMMDevice device;
                all.Item(n, out device);
                string id;
                device.GetId(out id);
                Report(device, Name(device), id == defaultId);
            }
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("could not ask: " + e.Message);
            return 1;
        }
    }

    /// <summary>The device's friendly name, out of its property store.</summary>
    static string Name(IMMDevice device)
    {
        try
        {
            IPropertyStore store;
            device.OpenPropertyStore(0, out store);

            var key = new PropertyKey
            {
                fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                pid = 14
            };

            var v = new PropVariant();
            store.GetValue(ref key, out v);
            return v.vt == 31 && v.p != IntPtr.Zero ? Marshal.PtrToStringUni(v.p) : "(unnamed)";
        }
        catch { return "(unnamed)"; }
    }

    static void Report(IMMDevice device, string name, bool isDefault)
    {
        try
        {

            object obj;
            var iid = typeof(IAudioClient).GUID;
            device.Activate(ref iid, 23, IntPtr.Zero, out obj);
            var client = (IAudioClient)obj;

            IntPtr fmt;
            client.GetMixFormat(out fmt);
            var wf = (WaveFormatEx)Marshal.PtrToStructure(fmt, typeof(WaveFormatEx));

            Console.WriteLine();
            Console.WriteLine("=== " + name + (isDefault ? "   [default]" : ""));
            Console.WriteLine("    " + wf.nSamplesPerSec + " Hz, " + wf.nChannels +
                              " channels, " + wf.wBitsPerSample + "-bit");

            long dflt, min;
            client.GetDevicePeriod(out dflt, out min);
            Console.WriteLine("what the audio engine reports:");
            Console.WriteLine("  default period   " + Ms(dflt, wf.nSamplesPerSec));
            Console.WriteLine("  minimum period   " + Ms(min, wf.nSamplesPerSec) +
                              "   (this is the EXCLUSIVE-mode floor)");
            Console.WriteLine();

            // IAudioClient3 - shared mode, small periods, Windows 10 1607 and later
            var c3 = obj as IAudioClient3;
            if (c3 == null)
            {
                Console.WriteLine("IAudioClient3 is not available: no low-latency shared mode here,");
                Console.WriteLine("so exclusive would be the only way down from the default.");
            }
            else
            {
                uint def, fundamental, lo, hi;
                c3.GetSharedModeEnginePeriod(fmt, out def, out fundamental, out lo, out hi);

                Console.WriteLine("what SHARED mode will do (IAudioClient3), in frames:");
                Console.WriteLine("  default      " + Frames(def, wf.nSamplesPerSec));
                Console.WriteLine("  fundamental  " + Frames(fundamental, wf.nSamplesPerSec) +
                                  "   (periods must be a multiple of this)");
                Console.WriteLine("  minimum      " + Frames(lo, wf.nSamplesPerSec));
                Console.WriteLine("  maximum      " + Frames(hi, wf.nSamplesPerSec));
                Console.WriteLine();

                double gain = def / (double)lo;
                Console.WriteLine("  so shared mode alone can go " + gain.ToString("F1") +
                                  "x lower than it is running now,");
                Console.WriteLine("  without taking the device away from anything.");
            }

            Marshal.FreeCoTaskMem(fmt);
        }
        catch (Exception e)
        {
            Console.WriteLine("  could not ask: " + e.Message);
        }
    }

    static string Ms(long hundredNanos, int rate)
    {
        double ms = hundredNanos / 10000.0;
        return ms.ToString("F2") + " ms  (" + Math.Round(ms * rate / 1000.0) + " frames)";
    }

    static string Frames(uint frames, int rate)
    {
        return frames + " frames  (" + (frames * 1000.0 / rate).ToString("F2") + " ms)";
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct WaveFormatEx
    {
        public short wFormatTag; public short nChannels; public int nSamplesPerSec;
        public int nAvgBytesPerSec; public short nBlockAlign; public short wBitsPerSample;
        public short cbSize;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        void GetDevice(string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant { public short vt; public short r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out int count);
        void Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                      [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, uint flags, long bufferDuration, long periodicity,
                        IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out int frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out int frames);
        void IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start(); void Stop(); void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    /// <summary>
    /// IAudioClient3, which is IAudioClient plus IAudioClient2's three then its own three.
    /// The whole vtable has to be redeclared in order: COM has no notion of inheriting
    /// only the part you care about.
    /// </summary>
    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient3
    {
        void Initialize(int shareMode, uint flags, long bufferDuration, long periodicity,
                        IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out int frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out int frames);
        void IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start(); void Stop(); void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);

        // IAudioClient2
        void IsOffloadCapable(int category, out bool capable);
        void SetClientProperties(IntPtr properties);
        void GetBufferSizeLimits(IntPtr format, bool eventDriven,
                                 out long minDuration, out long maxDuration);

        // IAudioClient3
        void GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriod,
                                       out uint fundamentalPeriod, out uint minPeriod,
                                       out uint maxPeriod);
        void GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriod);
        void InitializeSharedAudioStream(uint flags, uint periodInFrames, IntPtr format,
                                         IntPtr sessionGuid);
    }
}
