using System;
using AkaiS950Engine;

/// <summary>
/// Does the loop still click?
///
/// A click is a step: one sample to the next, a jump far larger than the waveform itself
/// makes anywhere else. So the measurement is the biggest sample-to-sample difference in
/// the rendered audio, against the biggest one inside a single pass of the loop where
/// there is no join. A clean loop scores about 1; a clicking one scores many times that.
///
/// The test material is a loop built to be as bad as it can reasonably be: a sine whose
/// loop ends a quarter of a cycle away from where it started, so the splice jumps most of
/// the way across the waveform, going the wrong way.
///
///   $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
///   $src = @("...\AkaiS950Tests\LoopClickCheck.cs")
///   $src += (Get-ChildItem "...\AkaiS950Engine\*.cs" | % { $_.FullName })
///   & $csc /nologo /unsafe /target:exe /main:LoopClickCheck /out:"$env:TEMP\LoopClickCheck.exe" `
///       /r:System.dll /r:System.Core.dll $src
/// </summary>
static class LoopClickCheck
{
    const double Rate = 48000;
    static int _fails;

    static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + (detail == null ? "" : "   " + detail));
        if (!ok) _fails++;
    }

    static int Main()
    {
        Console.WriteLine();
        Console.WriteLine("a loop spliced badly on purpose:");

        double plain = Score(0, false);
        Check("a raw splice clicks", plain > 4, "step is " + plain.ToString("F1") +
              "x the worst the waveform makes on its own");

        double snapped = Score(0, true);
        Check("snapping to zero crossings helps", snapped < plain,
              snapped.ToString("F1") + "x, down from " + plain.ToString("F1"));

        double faded = Score(LoopSmoothing.DefaultCrossfadeMs, true);
        Check("and a crossfade takes it away", faded < 1.6,
              faded.ToString("F1") + "x - a clean loop scores about 1");

        // ...without wrecking the sound it is smoothing
        Check("  while the note still sounds", Peak(Render(LoopSmoothing.DefaultCrossfadeMs, true)) > 0.05,
              "peak " + Peak(Render(LoopSmoothing.DefaultCrossfadeMs, true)).ToString("F2"));

        // and a sample whose loop is already perfect must not be disturbed
        Console.WriteLine();
        Console.WriteLine("a loop that was already clean:");

        Sound good = Tone(400, 200, 0);              // whole cycles: joins itself exactly
        int wasFrom = good.LoopFrom, wasTo = good.LoopTo;
        float[] before = (float[])good.Audio.Clone();
        LoopSmoothing.Polish(good, LoopSmoothing.DefaultCrossfadeMs, true);

        // within a sample or two: the point is that it does not go hunting, not that it
        // refuses to move at all
        Check("the loop points are left where they were",
              Math.Abs(good.LoopFrom - wasFrom) <= 1 && Math.Abs(good.LoopTo - wasTo) <= 1,
              good.LoopFrom + ".." + good.LoopTo + ", was " + wasFrom + ".." + wasTo);

        double moved = 0;
        for (int i = 0; i < before.Length; i++)
            moved = Math.Max(moved, Math.Abs(before[i] - good.Audio[i]));
        Check("  and the audio barely moves", moved < 0.02, "largest change " + moved.ToString("F4"));

        // a one-shot has no loop to smooth, and must come back untouched
        Sound shot = Tone(400, 200, 0);
        shot.Loops = false;
        float[] shotBefore = (float[])shot.Audio.Clone();
        LoopSmoothing.Polish(shot, LoopSmoothing.DefaultCrossfadeMs, true);
        bool same = shotBefore.Length == shot.Audio.Length && shot.LoopSmoothed == 0;
        for (int i = 0; same && i < shotBefore.Length; i++)
            if (shotBefore[i] != shot.Audio[i]) same = false;
        Check("  and a one-shot is not touched at all", same,
              same ? "identical" : "the audio changed");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "all good" : _fails + " FAILED");
        return _fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// A sine that loops, with the loop end moved off the cycle boundary by
    /// <paramref name="offBy"/> samples so the splice does not join.
    /// </summary>
    static Sound Tone(double hz, int cycles, int offBy)
    {
        int rate = 48000;
        int period = (int)Math.Round(rate / hz);
        int n = period * cycles;

        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (float)Math.Sin(2 * Math.PI * i / period);

        return new Sound
        {
            Name = "LOOPY", Audio = a, SourceRate = rate, RootPitch = 60,
            Loops = true,
            // start a few cycles in, so there is run-up for a crossfade to use
            LoopFrom = period * 4,
            LoopTo = period * (cycles - 4) + offBy
        };
    }

    static float[] Render(double crossfadeMs, bool snap)
    {
        Sound s = Tone(400, 60, 12);                 // a quarter cycle out at 400 Hz
        LoopSmoothing.Polish(s, crossfadeMs, snap);

        var kg = new KeygroupPatch
        {
            LowKey = 0, HighKey = 127, Sound = s,
            VcaAttack = 0, VcaDecay = 0, VcaSustain = 99, VcaRelease = 0,
            VcfWritten = true, VcfSustain = 99, ZoneFilter = 99, LfoDesync = true
        };

        var p = new Patch();
        p.Keygroups.Add(kg);

        var eng = new Engine(Rate);
        eng.Gain = 1f;
        eng.SetPatch(p);
        eng.NoteOn(60, 100);

        var buf = new float[(int)(Rate * 1.5)];
        for (int at = 0; at < buf.Length; at += 512)
            eng.Render(buf, at, Math.Min(512, buf.Length - at));
        return buf;
    }

    /// <summary>
    /// The worst step in the whole rendered note, against the worst step in one quiet
    /// stretch of it that contains no join.
    /// </summary>
    static double Score(double crossfadeMs, bool snap)
    {
        float[] y = Render(crossfadeMs, snap);

        /*
         * The stretch used as "ordinary" must contain no join, or the click is being
         * compared against itself and everything scores 1. The first wrap cannot happen
         * before the loop end, so anything earlier than that is safely one clean pass.
         */
        int clean = Math.Min((int)(Rate * 0.05), 6000);
        double worst = 0, ordinary = 0;

        for (int i = 1; i < clean; i++)
        {
            double step = Math.Abs(y[i] - y[i - 1]);
            if (step > ordinary) ordinary = step;
        }
        for (int i = (int)(Rate * 0.2); i < y.Length; i++)
        {
            double step = Math.Abs(y[i] - y[i - 1]);
            if (step > worst) worst = step;
        }

        return ordinary > 1e-9 ? worst / ordinary : 0;
    }

    static double Peak(float[] a)
    {
        double p = 0;
        for (int i = 0; i < a.Length; i++) p = Math.Max(p, Math.Abs(a[i]));
        return p;
    }
}
