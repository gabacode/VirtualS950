using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AkaiS950Engine;
using AkaiS950List;
using AkaiS950Studio;

/// <summary>
/// An edit to a programme has to reach the engine.
///
/// Instrument keeps what it decodes and what it builds, because a key click asks for the
/// same programme on every single press and rebuilding it each time would be waste. The
/// hazard in that is silent: edit a filter, a decay, a loop point, and the patch already
/// built has no reason to notice. Nothing sounds wrong - it just goes on sounding as it
/// did before the edit, which is worse, because it looks like the edit did nothing.
///
/// So this pokes the bytes the editor pokes and checks the difference arrives. The disk's
/// revision counter is what carries it; this is the test that the counter is actually
/// wired to the things derived from it.
/// </summary>
static class EditHeardCheck
{
    const double Rate = 44100;

    static int _failed;

    static void Ok(string what, string detail)
    {
        Console.WriteLine("  ok   " + what + (detail == "" ? "" : "   " + detail));
    }

    static void Bad(string what, string detail)
    {
        Console.WriteLine("  FAIL " + what + (detail == "" ? "" : "   " + detail));
        _failed++;
    }

    static void Check(bool good, string what, string detail)
    {
        if (good) Ok(what, detail); else Bad(what, detail);
    }

    static int Main(string[] args)
    {
        string folder = args.Length > 0 ? args[0] : "";
        if (folder == "")
        {
            Console.WriteLine("  (no image folder given - nothing to edit)");
            return 0;
        }

        AkaiDisk disk;
        AkaiEntry program;
        if (!FindProgram(folder, out disk, out program))
        {
            Console.WriteLine("  (no programme with a keygroup found under " + folder + ")");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("  " + Path.GetFileName(disk.Source) + ", programme " +
                          program.Name.Trim());
        Console.WriteLine();

        var inst = new Instrument();
        FieldInfo patchField = typeof(Instrument)
            .GetField("_patch", BindingFlags.NonPublic | BindingFlags.Instance);

        // ------------------------------------------------- the revision moves at all

        // Zone 1 begins 24 bytes into the keygroup record and its filter is 20 bytes into
        // the zone, so 44. 45 is that zone's loudness and 4 is the VCA decay.
        const int Zone1Filter = 44, VcaDecay = 4;

        int before = disk.Revision;
        disk.SetKeygroupByte(program, 0, Zone1Filter, 40);
        Check(disk.Revision != before, "editing a byte moves the disk's revision",
              before + " -> " + disk.Revision);

        // ------------------------------------------------- and the patch follows it

        inst.SetProgram(disk, program);
        Patch first = (Patch)patchField.GetValue(inst);
        int filterWas = FilterOf(first);

        inst.SetProgram(disk, program);
        Check(ReferenceEquals(patchField.GetValue(inst), first),
              "asking for the same programme twice does not rebuild it", "same patch back");

        // The editor writes one byte at a time through PokeFile, exactly like this.
        disk.SetKeygroupByte(program, 0, Zone1Filter, (byte)(filterWas >= 50 ? 10 : 90));

        inst.SetProgram(disk, program);
        Patch second = (Patch)patchField.GetValue(inst);
        int filterNow = FilterOf(second);

        Check(!ReferenceEquals(second, first), "editing it does rebuild it", "new patch");
        Check(filterNow != filterWas, "the filter change reached the patch",
              filterWas + " -> " + filterNow);

        // ------------------------------------------------- and is audible

        double brightWas = Brightness(second, program, disk, filterWas);
        double brightNow = Brightness(second, program, disk, filterNow);
        Check(Math.Abs(brightWas - brightNow) > 1.0,
              "and the rendered note changes with it",
              brightWas.ToString("F0") + " Hz -> " + brightNow.ToString("F0") + " Hz");

        // ------------------------------------------------- envelopes too

        int decayWas = DecayOf(second);
        disk.SetKeygroupByte(program, 0, VcaDecay, (byte)(decayWas >= 50 ? 5 : 95));
        inst.SetProgram(disk, program);
        int decayNow = DecayOf((Patch)patchField.GetValue(inst));
        Check(decayNow != decayWas, "an envelope change reaches it as well",
              decayWas + " -> " + decayNow);

        // ------------------------------------- and a note already sounding takes it up

        Check(SoundingNoteFollows(inst, disk, program, patchField, Zone1Filter),
              "a note already sounding takes up the change", "without being retriggered");

        // ------------------------------------------------- undo is a change as well

        int r = disk.Revision;
        disk.Modified = false;                            // what a save does
        Check(disk.Revision != r, "clearing the modified flag counts as a change",
              r + " -> " + disk.Revision);

        Console.WriteLine();
        if (_failed == 0) { Console.WriteLine("all good"); return 0; }
        Console.WriteLine(_failed + " failed");
        return 1;
    }

    /// <summary>
    /// Start a note, edit the filter while it sounds, and see whether the sound changes.
    ///
    /// The note is never retriggered. Everything before this establishes that an edit
    /// reaches the patch; this is the one that says it reaches the air, which is the only
    /// version of the question a player asks. Rendered in two halves either side of the
    /// edit, and the second half has to be brighter or duller than the first.
    /// </summary>
    static bool SoundingNoteFollows(Instrument inst, AkaiDisk disk, AkaiEntry program,
                                    FieldInfo patchField, int filterOffset)
    {
        inst.SetProgram(disk, program);
        var p = (Patch)patchField.GetValue(inst);
        if (p == null || p.Keygroups.Count == 0) return false;

        KeygroupPatch kg = p.Keygroups[0];

        // Dull to start with, so there is room to open up and hear it.
        disk.SetKeygroupByte(program, 0, filterOffset, 20);
        inst.SetProgram(disk, program);
        p = (Patch)patchField.GetValue(inst);

        var eng = new Engine(Rate);
        eng.Gain = 1f;
        eng.SetPatch(p);
        eng.NoteOn((kg.LowKey + kg.HighKey) / 2, 100);

        var before = new float[(int)(Rate * 0.25)];
        Render(eng, before);

        // The edit, mid-note. Nothing is retriggered and nothing is released.
        disk.SetKeygroupByte(program, 0, filterOffset, 99);
        inst.SetProgram(disk, program);
        eng.SetPatch((Patch)patchField.GetValue(inst));

        var after = new float[(int)(Rate * 0.25)];
        Render(eng, after);

        double a = Centroid(before), b = Centroid(after);
        Console.WriteLine("         " + a.ToString("F0") + " Hz before the edit, " +
                          b.ToString("F0") + " Hz after");
        return Math.Abs(a - b) > 2.0;
    }

    static void Render(Engine eng, float[] buf)
    {
        for (int at = 0; at < buf.Length; at += 441)
            eng.Render(buf, at, Math.Min(441, buf.Length - at));
    }

    /// <summary>The first keygroup's zone 1 filter, as the patch holds it.</summary>
    static int FilterOf(Patch p)
    {
        return p == null || p.Keygroups.Count == 0 ? -1 : p.Keygroups[0].ZoneFilter;
    }

    static int DecayOf(Patch p)
    {
        return p == null || p.Keygroups.Count == 0 ? -1 : p.Keygroups[0].VcaDecay;
    }

    /// <summary>Render the first keygroup at one filter setting and say how bright it is.</summary>
    static double Brightness(Patch p, AkaiEntry program, AkaiDisk disk, int filter)
    {
        if (p == null || p.Keygroups.Count == 0) return 0;

        KeygroupPatch kg = p.Keygroups[0];
        int was = kg.ZoneFilter, wasTrack = kg.KeyToFilter;
        kg.ZoneFilter = filter;
        kg.KeyToFilter = 0;

        var eng = new Engine(Rate);
        eng.Gain = 1f;
        eng.SetPatch(p);
        eng.NoteOn((kg.LowKey + kg.HighKey) / 2, 100);

        var buf = new float[(int)(Rate * 0.5)];
        for (int at = 0; at < buf.Length; at += 441)
            eng.Render(buf, at, Math.Min(441, buf.Length - at));

        kg.ZoneFilter = was;
        kg.KeyToFilter = wasTrack;
        return Centroid(buf);
    }

    /// <summary>Spectral centroid - one number for how bright a stretch of audio is.</summary>
    static double Centroid(float[] x)
    {
        int len = Math.Min(1 << 14, x.Length);
        double num = 0, den = 0;

        for (double f = 50; f < 9000; f *= 1.1)
        {
            double re = 0, im = 0, w = 2 * Math.PI * f / Rate;
            for (int i = 0; i < len; i += 4)
            {
                re += x[i] * Math.Cos(w * i);
                im -= x[i] * Math.Sin(w * i);
            }
            double pw = re * re + im * im;
            num += f * pw;
            den += pw;
        }
        return den > 0 ? num / den : 0;
    }

    /// <summary>The first programme, on the first disk, that has a keygroup to edit.</summary>
    static bool FindProgram(string folder, out AkaiDisk disk, out AkaiEntry program)
    {
        disk = null;
        program = null;

        var files = new List<string>();
        try
        {
            files.AddRange(Directory.GetFiles(folder, "*.hfe"));
            files.AddRange(Directory.GetFiles(folder, "*.img"));
        }
        catch { return false; }

        files.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string f in files)
        {
            AkaiDisk d;
            try { d = AkaiDisk.Load(f); }
            catch { continue; }

            foreach (AkaiEntry e in d.Entries)
            {
                if (e.Type != 'P') continue;
                if (d.Keygroups(e).Count == 0) continue;

                disk = d;
                program = e;
                return true;
            }
        }
        return false;
    }
}
