using System;
using System.Globalization;
using System.IO;
using System.Text;
using AkaiS950List;

/// <summary>
/// Print everything the reader understands about a disk, in a form another reader can be
/// held to exactly.
///
/// The C++ port in Plugin/Source/S950/Disk.cpp prints the identical text from the identical
/// bytes, and Plugin/crosscheck.ps1 diffs the two. That is how the port gets checked without
/// a single disk image, sample name or byte of audio going into the repository: the
/// reference is produced from whatever image is to hand, at the moment of checking, and
/// thrown away afterwards.
///
///     DiskDump &lt;image.hfe|image.img&gt; [--save-img &lt;path&gt;]
///
/// --save-img writes the raw 800K sectors out first. The C++ side reads only raw images so
/// far, so converting here and dumping THAT is what makes the two comparable - and it
/// exercises the conversion at the same time.
/// </summary>
static class DiskDump
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: DiskDump <image> [--save-img <path>]");
            return 2;
        }

        string path = args[0];
        string saveImg = null;

        for (int i = 1; i < args.Length - 1; i++)
            if (args[i] == "--save-img") saveImg = args[i + 1];

        AkaiDisk disk;
        try { disk = AkaiDisk.Load(path); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not read " + path + ": " + ex.Message);
            return 1;
        }

        if (saveImg != null)
        {
            try { disk.SaveAs(saveImg, "img"); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("could not write " + saveImg + ": " + ex.Message);
                return 1;
            }

            // Dump what the C++ will actually read, not what we started from.
            disk = AkaiDisk.Load(saveImg);
        }

        Console.Write(Dump(disk));
        return 0;
    }

    static string Dump(AkaiDisk d)
    {
        var s = new StringBuilder();

        // How the recovery went, which for an .hfe is part of what is being checked: the
        // two decoders have to agree about which sectors were bad, not just the good ones.
        s.Append("recovery badcrc ").Append(d.BadCrcSectors)
         .Append(" missing ").Append(d.MissingSectors).Append('\n');

        s.Append("entries ").Append(d.Entries.Count).Append('\n');

        foreach (AkaiEntry e in d.Entries)
        {
            s.Append("entry ")
             .Append(e.Slot).Append(' ')
             .Append(e.Type).Append(" '").Append(e.Name).Append("' ")
             .Append("len ").Append(e.Length).Append(' ')
             .Append("start ").Append(e.StartBlock).Append(' ')
             .Append("blocks ").Append(e.ChainBlocks).Append(' ')
             .Append(e.ChainOk ? "ok" : "SHORT").Append('\n');

            if (e.Type == 'S')
            {
                var words = d.SampleWords12(e);

                s.Append("  sample count ").Append(e.SampleCount)
                 .Append(" rate ").Append(e.SampleRate)
                 .Append(" tuning ").Append(e.Tuning)
                 .Append(" loudness ").Append(e.Loudness)
                 .Append(" loop ").Append(e.LoopMode).Append(e.LoopDirection)
                 .Append(' ').Append(e.LoopStart)
                 .Append('/').Append(e.LoopEnd)
                 .Append('/').Append(e.LoopLength)
                 .Append('\n');

                s.Append("  audio words ").Append(words.Length)
                 .Append(" hash ").Append(Hash(words))
                 .Append('\n');
            }
            else if (e.Type == 'P')
            {
                var groups = d.Keygroups(e);
                s.Append("  program keygroups ").Append(groups.Count).Append('\n');

                foreach (AkaiDisk.Keygroup k in groups)
                {
                    s.Append("   kg ").Append(k.Index)
                     .Append(" keys ").Append(k.LowKey).Append('-').Append(k.HighKey)
                     .Append(" vsw ").Append(k.VelocitySwitch)
                     .Append(" vca ").Append(k.VcaAttack).Append(',').Append(k.VcaDecay)
                     .Append(',').Append(k.VcaSustain).Append(',').Append(k.VcaRelease)
                     .Append(" vcf ").Append(k.VcfAttack).Append(',').Append(k.VcfDecay)
                     .Append(',').Append(k.VcfSustain).Append(',').Append(k.VcfRelease)
                     .Append(" amt ").Append(k.VcfAmount)
                     .Append(" vel ").Append(k.VelToFilter).Append(',').Append(k.VelToLoudness)
                     .Append(" key ").Append(k.KeyToFilter)
                     .Append(" lfo ").Append(k.LfoDelay).Append(',').Append(k.LfoRate)
                     .Append(',').Append(k.LfoDepth).Append(',').Append(k.LfoModwheelDepth)
                     .Append(" flags ").Append(k.Flags)
                     .Append('\n');

                    Zone(s, "z1", k.Zone1);
                    Zone(s, "z2", k.Zone2);
                }
            }
        }

        return s.ToString();
    }

    static void Zone(StringBuilder s, string label, AkaiDisk.Zone z)
    {
        s.Append("     ").Append(label);

        if (z == null)
        {
            s.Append(" -\n");
            return;
        }

        s.Append(" '").Append(z.Name).Append("' ")
         .Append(z.InUse ? "used" : "unused")
         .Append(" ptr ").Append(z.Pointer)
         .Append(" fine ").Append(z.Fine)
         .Append(" trans ").Append(z.Transpose)
         .Append(" filt ").Append(z.Filter)
         .Append(" loud ").Append(z.Loudness)
         .Append('\n');
    }

    /// <summary>
    /// FNV-1a over the 16-bit words.
    ///
    /// A hash rather than the audio itself, so the two readers can be compared exactly
    /// without any of someone else's recordings being written down. Unsigned arithmetic
    /// throughout, which is what makes it reproduce in another language.
    /// </summary>
    static string Hash(short[] words)
    {
        uint h = 2166136261;

        foreach (short w in words)
        {
            ushort u = (ushort)w;
            h = (h ^ (uint)(u & 0xFF)) * 16777619;
            h = (h ^ (uint)(u >> 8)) * 16777619;
        }

        return h.ToString("x8", CultureInfo.InvariantCulture);
    }
}
