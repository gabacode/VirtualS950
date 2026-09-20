using System;
using System.Collections.Generic;
using System.IO;
using AkaiS950Engine;
using AkaiS950List;
using AkaiS950Studio;

/// <summary>
/// Real programmes off real disks, through the engine.
///
/// EngineCheck proves the maths and AudioCheck proves the sound card. Neither of them
/// plays anything an Akai ever wrote. This does: it builds a patch from every programme on
/// every disk in the library, plays a note in the middle of each keygroup's range, and
/// looks at what comes out.
///
/// What it is looking for is mostly absence of disaster - a NaN, a burst of full-scale
/// noise, a programme that builds a patch and then makes no sound at all. Those are the
/// failures that a made-up test sample will never produce and a twenty-year-old disk will.
///
///   $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
///   $src = @("C:\Users\simon\AkaiS950Tests\PatchCheck.cs")
///   $src += (Get-ChildItem "C:\Users\simon\AkaiS950Engine\*.cs" | % { $_.FullName })
///   $src += @("C:\Users\simon\AkaiS950Studio\Instrument.cs")
///   $src += (Get-ChildItem "C:\Users\simon\AkaiS950List\*.cs" |
///            ? { $_.Name -ne 'Program.cs' } | % { $_.FullName })
///   & $csc /nologo /unsafe /target:exe /main:PatchCheck /out:"$env:TEMP\PatchCheck.exe" `
///       /r:System.dll /r:System.Core.dll /r:System.Drawing.dll $src
///   & "$env:TEMP\PatchCheck.exe" C:\Users\simon\AkaiS950Images
/// </summary>
static class PatchCheck
{
    const double Rate = 48000;

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : @"C:\Users\simon\AkaiS950Images";
        if (!Directory.Exists(dir))
        {
            Console.WriteLine("no disk library at " + dir);
            return 0;
        }

        int disks = 0, programmes = 0, played = 0, silent = 0, bad = 0, keygroups = 0, loud = 0;
        int doubled = 0;
        var complaints = new List<string>();

        foreach (string file in Directory.GetFiles(dir, "*.hfe"))
        {
            AkaiDisk disk;
            try { disk = AkaiDisk.Load(file); }
            catch { continue; }
            disks++;

            foreach (AkaiEntry e in disk.Entries)
            {
                if (e.Type != 'P') continue;
                programmes++;

                var inst = new Instrument();
                inst.SetProgram(disk, e);

                var groups = disk.Keygroups(e);
                keygroups += groups.Count;

                /*
                 * The invariant the "bell over every note" bug broke.
                 *
                 * A keygroup holds up to two VELOCITY zones, which are alternatives. Two
                 * entries from the same keygroup answering one strike means both are
                 * sounding, which laid two copies of one sample over each other on 74 of
                 * the library's two-zone keygroups.
                 */
                Patch built = PatchOf(inst);
                if (built != null)
                {
                    var hit = new List<KeygroupPatch>();
                    for (int note = 0; note < 128 && doubled == 0; note += 3)
                        for (int vel = 1; vel <= 127; vel += 9)
                        {
                            built.Matching(note, vel, hit);
                            for (int x = 0; x < hit.Count; x++)
                                for (int y = x + 1; y < hit.Count; y++)
                                    if (hit[x].KeygroupIndex >= 0 &&
                                        hit[x].KeygroupIndex == hit[y].KeygroupIndex)
                                    {
                                        doubled++;
                                        if (complaints.Count < 8)
                                            complaints.Add(Path.GetFileName(file) + " / " + e.Name.Trim() +
                                                " kg " + (hit[x].KeygroupIndex + 1) +
                                                ": both zones answer note " + note + " velocity " + vel);
                                    }
                        }
                }

                // one note per keygroup, in the middle of its range
                foreach (AkaiDisk.Keygroup kg in groups)
                {
                    int lo = Math.Min(kg.LowKey, kg.HighKey), hi = Math.Max(kg.LowKey, kg.HighKey);
                    int note = (lo + hi) / 2;

                    float[] audio = RenderOne(disk, e, note);
                    if (audio == null) continue;

                    string wrong = Inspect(audio);
                    if (wrong != null)
                    {
                        bad++;
                        if (complaints.Count < 8)
                            complaints.Add(Path.GetFileName(file) + " / " + e.Name.Trim() +
                                           " kg " + (kg.Index + 1) + " note " + note + ": " + wrong);
                    }
                    else if (Peak(audio) < 1e-5) silent++;
                    else
                    {
                        played++;
                        if (Peak(audio) > 0.995f) loud++;
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("  " + disks + " disks, " + programmes + " programmes, " +
                          keygroups + " keygroups");
        Console.WriteLine("  " + played + " notes sounded, " + silent + " were silent, " +
                          bad + " were wrong");
        Console.WriteLine("  " + loud + " reached full scale - a hot sample plus a zone trim, " +
                          "which is what the master gain is for");

        foreach (string c in complaints) Console.WriteLine("    " + c);

        Console.WriteLine();
        Console.WriteLine("  " + doubled + " keygroups sounded both velocity zones at once" +
                          (doubled == 0 ? " - which is as it should be" : ""));

        bool ok = bad == 0 && doubled == 0 && played > keygroups / 2;
        Console.WriteLine(ok ? "all good"
                             : (bad > 0 ? bad + " FAILED" : "too few notes sounded - FAILED"));
        return ok ? 0 : 1;
    }

    /// <summary>Half a second of one note, rendered offline.</summary>
    static float[] RenderOne(AkaiDisk disk, AkaiEntry program, int note)
    {
        var inst = new Instrument();
        inst.SetProgram(disk, program);

        // Instrument renders through its own engine, which needs the sound card. For an
        // offline check the engine is used directly instead, built from the same patch.
        // The engine's own default gain, not unity: that is what the instrument plays at,
        // and it is the headroom eight voices need. Rendering at unity and then calling the
        // result clipped would be testing a setting nothing uses.
        var engine = new Engine(Rate);
        engine.SetPatch(PatchOf(inst));
        engine.NoteOn(note, 100);

        int total = (int)(Rate * 0.5);
        var buf = new float[total];
        for (int at = 0; at < total; at += 512)
            engine.Render(buf, at, Math.Min(512, total - at));

        return buf;
    }

    /// <summary>
    /// The patch Instrument built.
    ///
    /// Reached by reflection rather than by making it public: nothing but a test wants it,
    /// and widening an API for a test is how an API stops meaning anything.
    /// </summary>
    static Patch PatchOf(Instrument inst)
    {
        var f = typeof(Instrument).GetField("_patch",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
        return (Patch)f.GetValue(inst);
    }

    static string Inspect(float[] a)
    {
        for (int i = 0; i < a.Length; i++)
        {
            float v = a[i];
            if (float.IsNaN(v)) return "NaN at sample " + i;
            if (float.IsInfinity(v)) return "infinity at sample " + i;
            if (v > 1.001f || v < -1.001f) return "past full scale (" + v.ToString("F2") + ")";
        }

        /*
         * A filter that has blown up sits on the rail and stays there. A loud note touches
         * it and comes back, many times a cycle. So the test is a run long enough that no
         * audio at any pitch this machine plays could still be inside one half-cycle: a
         * fiftieth of a second is 20 Hz, below the bottom of hearing.
         */
        int pinned = 0, limit = (int)(Rate / 50);
        for (int i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i]) > 0.999f) { if (++pinned > limit) return "stuck at full scale"; }
            else pinned = 0;
        }
        return null;
    }

    static float Peak(float[] a)
    {
        float p = 0;
        for (int i = 0; i < a.Length; i++) { float v = Math.Abs(a[i]); if (v > p) p = v; }
        return p;
    }
}
