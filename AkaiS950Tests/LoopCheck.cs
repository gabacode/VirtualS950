// Checks the C# loop work against the same corpus the JS version was measured on, so the
// two can be compared number for number:
//   * FindLoop, scored with the same join measure as the loops the library shipped with;
//   * SetLoopMode, which must leave the descriptor chain intact.
using System;
using System.Collections.Generic;
using System.IO;
using AkaiS950List;

static class LoopCheck
{
    static double JoinMatch(short[] words, int end, int len)
    {
        int win = Math.Min(2048, Math.Max(128, len / 2));
        int from = end - len;
        if (from < win || end > words.Length || len < 2) return double.NaN;
        double dot = 0, ea = 0, eb = 0;
        for (int i = 0; i < win; i++)
        {
            double a = words[end - win + i], b = words[from - win + i];
            dot += a * b; ea += a * a; eb += b * b;
        }
        double d = Math.Sqrt(ea * eb);
        return d > 0 ? dot / d : double.NaN;
    }

    static int ChainOk(AkaiDisk disk)
    {
        var ss = new List<AkaiEntry>();
        foreach (var e in disk.Entries) if (e.Type == 'S') ss.Add(e);
        ss.Sort(delegate(AkaiEntry a, AkaiEntry b) { return a.Slot.CompareTo(b.Slot); });
        int ok = 0;
        for (int i = 1; i < ss.Count; i++)
        {
            int want = ss[i - 1].LoopDescriptorPtr +
                       10 * AkaiDisk.LoopRecords(ss[i - 1].SampleCount, ss[i - 1].LoopMode);
            if (ss[i].LoopDescriptorPtr == want) ok++;
        }
        return ok;
    }

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : @"C:\Users\simon\AkaiS950Images";
        int samples = 0, looped = 0, found = 0, better = 0, same = 0, worse = 0;
        double sumOurs = 0, sumTheirs = 0;
        int modeDisks = 0, modeBroke = 0;
        var problems = new List<string>();

        foreach (var file in Directory.GetFiles(dir, "*.hfe"))
        {
            AkaiDisk disk;
            try { disk = AkaiDisk.Load(file); }
            catch (Exception ex) { problems.Add(Path.GetFileName(file) + ": " + ex.Message); continue; }

            foreach (var e in disk.Entries)
            {
                if (e.Type != 'S') continue;
                samples++;
                if (e.LoopMode == 'O') continue;
                looped++;

                short[] words = disk.SampleWords12(e);
                int end = (int)Math.Min(e.LoopEnd > 0 ? e.LoopEnd : words.Length, words.Length);
                double theirs = JoinMatch(words, end, (int)e.LoopLength);

                var got = AkaiDisk.FindLoop(words, end, Math.Max(64, e.SampleRate / 50));
                if (got == null) continue;
                found++;

                if ((got.Length & 1) != 0) problems.Add(e.Name.Trim() + ": odd loop length");
                if (got.From < 0 || got.End > words.Length)
                    problems.Add(e.Name.Trim() + ": loop outside the sample");

                double ours = JoinMatch(words, end, got.Length);
                if (double.IsNaN(ours) || double.IsNaN(theirs)) continue;
                sumOurs += ours; sumTheirs += theirs;
                if (ours > theirs + 0.01) better++;
                else if (ours < theirs - 0.01) worse++;
                else same++;
            }

            // the mode change has to leave every later sample pointing where it should
            var list = new List<AkaiEntry>();
            foreach (var e in disk.Entries) if (e.Type == 'S') list.Add(e);
            if (list.Count >= 3)
            {
                list.Sort(delegate(AkaiEntry a, AkaiEntry b) { return a.Slot.CompareTo(b.Slot); });
                int before = ChainOk(disk);
                var target = list[0];
                disk.SetLoopMode(target, target.LoopMode == 'O' ? 'L' : 'O');
                modeDisks++;
                if (ChainOk(disk) < before)
                {
                    modeBroke++;
                    problems.Add(Path.GetFileName(file) + ": a mode change broke the chain");
                }
            }
        }

        int scored = better + same + worse;
        Console.WriteLine(samples + " samples, " + looped + " looped, " + found + " answered for");
        Console.WriteLine("join match  -  shipped " + (sumTheirs / Math.Max(1, scored)).ToString("0.000") +
                          "   found " + (sumOurs / Math.Max(1, scored)).ToString("0.000"));
        Console.WriteLine("better " + better + "   as good " + same + "   worse " + worse);
        Console.WriteLine("loop mode changed on " + modeDisks + " disks, " + modeBroke +
                          " broke the descriptor chain");

        if (problems.Count > 0)
        {
            Console.WriteLine("PROBLEMS (" + problems.Count + "):");
            for (int i = 0; i < Math.Min(10, problems.Count); i++) Console.WriteLine("  " + problems[i]);
            return 1;
        }
        Console.WriteLine("no problems");
        return 0;
    }
}
