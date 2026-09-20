using System;
using System.Threading;
using AkaiS950Engine;

/// <summary>
/// Does it actually come out of the speakers, and how late?
///
/// Separate from EngineCheck because it opens the sound card and makes a noise, which a
/// test suite should not do every time it runs. This is the one that says whether the
/// WASAPI interop is right - the rest of the checking can be done on buffers, but "is the
/// vtable order correct" can only be answered by a working sound.
///
///   $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
///   $src = @("C:\Users\simon\AkaiS950Tests\AudioCheck.cs")
///   $src += (Get-ChildItem "C:\Users\simon\AkaiS950Engine\*.cs" | % { $_.FullName })
///   & $csc /nologo /unsafe /target:exe /main:AudioCheck /out:"$env:TEMP\AudioCheck.exe" `
///       /r:System.dll /r:System.Core.dll $src
///   & "$env:TEMP\AudioCheck.exe"
/// </summary>
static class AudioCheck
{
    static Engine _engine;
    static long _framesRendered;

    static int Main(string[] args)
    {
        bool silent = args.Length > 0 && args[0] == "quiet";

        Console.WriteLine();
        Console.WriteLine("MIDI inputs:");
        var ports = MidiIn.Ports();
        if (ports.Count == 0) Console.WriteLine("  (none)");
        for (int i = 0; i < ports.Count; i++) Console.WriteLine("  " + i + ": " + ports[i]);

        Console.WriteLine();
        Console.WriteLine("opening the default output...");

        // The engine has to exist first: Start primes the buffer through the callback
        // before it returns, so anything built afterwards is built too late.
        var outp = new WasapiOut(Fill);
        _engine = new Engine(48000);
        _engine.Gain = silent ? 0.0f : 0.25f;
        _engine.SetPatch(BuildPatch());

        if (!outp.Start(10))
        {
            Console.WriteLine("  FAILED: " + outp.Error);
            return 1;
        }

        Console.WriteLine("  " + outp.SampleRate + " Hz, buffer " +
                          outp.LatencyMs.ToString("F1") + " ms");

        // and rebuilt at the rate the card actually runs at
        if (outp.SampleRate != 48000)
        {
            _engine = new Engine(outp.SampleRate);
            _engine.Gain = silent ? 0.0f : 0.25f;
            _engine.SetPatch(BuildPatch());
        }

        Console.WriteLine();
        Console.WriteLine(silent ? "rendering silently for two seconds..."
                                 : "playing a chord with the vibrato on, for two seconds...");

        _engine.NoteOn(60, 100);
        Thread.Sleep(150);
        _engine.NoteOn(64, 100);
        Thread.Sleep(150);
        _engine.NoteOn(67, 100);

        Thread.Sleep(1200);
        _engine.AllNotesOff();
        Thread.Sleep(500);

        long frames = Interlocked.Read(ref _framesRendered);
        double seconds = frames / (double)outp.SampleRate;
        outp.Stop();

        Console.WriteLine();
        Console.WriteLine("  rendered " + frames + " frames, " + seconds.ToString("F2") +
                          "s of audio");

        bool ok = seconds > 1.5;
        Console.WriteLine(ok ? "  the card kept asking for audio, so the callback is being served"
                             : "  FAILED: too little audio came out - the render thread is not running");

        Console.WriteLine();
        Console.WriteLine(ok ? "all good" : "1 FAILED");
        return ok ? 0 : 1;
    }

    /// <summary>The audio callback. Allocates nothing, waits for nothing.</summary>
    static void Fill(float[] mono, int frames)
    {
        _engine.Render(mono, 0, frames);
        Interlocked.Add(ref _framesRendered, frames);
    }

    /// <summary>
    /// A patch to play: one keygroup, a sawtooth, with the vibrato at a setting the machine
    /// was measured at - so what comes out should wobble by a semitone and a half.
    /// </summary>
    static Patch BuildPatch()
    {
        const int rate = 48000;
        int period = rate / 200;                 // 200 Hz, exactly periodic
        int n = period * 200;

        var audio = new float[n];
        for (int i = 0; i < n; i++)
        {
            double ph = 2 * Math.PI * (i % period) / period;
            double v = 0;
            for (int k = 1; k <= 12; k++) v += Math.Sin(k * ph) / k;
            audio[i] = (float)(v * 0.4);
        }

        var sound = new Sound
        {
            Name = "SAW", Audio = audio, SourceRate = rate, RootPitch = 60,
            Loops = true, LoopFrom = 0, LoopTo = n
        };

        var kg = new KeygroupPatch
        {
            LowKey = 0, HighKey = 127, Sound = sound,
            VcaAttack = 10, VcaDecay = 60, VcaSustain = 80, VcaRelease = 40,
            VcfWritten = true, VcfAttack = 0, VcfDecay = 50, VcfSustain = 60, VcfRelease = 0,
            VcfAmount = 20, ZoneFilter = 55,
            LfoRate = 60, LfoDepth = 40, LfoDelay = 0, LfoDesync = true
        };

        var p = new Patch { Name = "CHECK" };
        p.Keygroups.Add(kg);
        return p;
    }
}
