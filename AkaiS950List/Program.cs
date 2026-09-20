using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AkaiS950List
{
    public static class Program
    {
        static void Usage()
        {
            Console.WriteLine("AkaiS950List - list files on Akai S900/S950 floppy images");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  akailist <path> [options]");
            Console.WriteLine();
            Console.WriteLine("  <path>    a .hfe image, a raw 819200-byte .img, or a folder or");
            Console.WriteLine("            drive holding DSKA*.hfe images (for example E:\\)");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --all      list every file type (default: programs only)");
            Console.WriteLine("  --type X   list only type P program, S sample, D drum set, O overall");
            Console.WriteLine("             combine letters to mix, e.g. --type PD");
            Console.WriteLine("  --refs     for each program, list the sample each keygroup references");
            Console.WriteLine("  --empty    also show disks that hold no matching files");
            Console.WriteLine("  --csv      machine-readable output");
            Console.WriteLine("  --help     this text");
        }

        static string LoopName(char m)
        {
            switch (m)
            {
                case 'O': return "one";
                case 'L': return "loop";
                case 'A': return "alt";
                default: return m >= 0x20 && m < 0x7F ? m.ToString() : "?";
            }
        }

        public static int Main(string[] args)
        {
            if (args.Length == 0 || args.Contains("--help")) { Usage(); return args.Length == 0 ? 1 : 0; }

            string path = args[0];
            bool all = args.Contains("--all");
            bool refs = args.Contains("--refs");
            bool csv = args.Contains("--csv");
            bool showEmpty = args.Contains("--empty");

            string only = null;
            int ti = Array.IndexOf(args, "--type");
            if (ti >= 0 && ti + 1 < args.Length) only = args[ti + 1].ToUpperInvariant();
            if (only == null && !all) only = "P";

            var files = new List<string>();
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.GetFiles(path, "*.hfe"));
                files.AddRange(Directory.GetFiles(path, "*.img"));
                files.Sort(StringComparer.OrdinalIgnoreCase);
            }
            else if (File.Exists(path)) files.Add(path);
            else { Console.Error.WriteLine("Not found: " + path); return 2; }

            if (files.Count == 0) { Console.Error.WriteLine("No .hfe or .img images in " + path); return 2; }

            if (csv) Console.WriteLine("disk,slot,type,name,bytes,start_block,blocks,ok,samples,rate_hz,seconds,loop_mode,tuning,loop_start,loop_end,loop_length,keygroups");

            int listed = 0, read = 0, failed = 0;
            foreach (var f in files)
            {
                AkaiDisk d;
                try { d = AkaiDisk.Load(f); }
                catch (Exception ex)
                {
                    failed++;
                    Console.Error.WriteLine("!! " + Path.GetFileName(f) + ": " + ex.Message);
                    continue;
                }
                read++;

                var shown = d.Entries.Where(e => only == null || only.IndexOf(e.Type) >= 0).ToList();
                listed += shown.Count;
                string disk = Path.GetFileNameWithoutExtension(f);

                if (csv)
                {
                    foreach (var e in shown)
                        Console.WriteLine(string.Join(",", disk, e.Slot, e.Type,
                            "\"" + e.Name.Replace("\"", "\"\"") + "\"", e.Length,
                            e.StartBlock, e.ChainBlocks, e.ChainOk ? "1" : "0",
                            e.Type == 'S' ? e.SampleCount.ToString() : "",
                            e.Type == 'S' ? e.SampleRate.ToString() : "",
                            e.Type == 'S' ? e.Seconds.ToString("0.000", CultureInfo.InvariantCulture) : "",
                            e.Type == 'S' ? LoopName(e.LoopMode) : "",
                            e.Type == 'S' ? e.Tuning.ToString() : "",
                            e.Type == 'S' ? e.LoopStart.ToString() : "",
                            e.Type == 'S' ? e.LoopEnd.ToString() : "",
                            e.Type == 'S' ? e.LoopLength.ToString() : "",
                            e.Type == 'P' ? AkaiDisk.KeygroupCount(e).ToString() : ""));
                    continue;
                }

                if (shown.Count == 0 && !showEmpty) continue;

                string warn = "";
                if (d.BadCrcSectors > 0) warn += "  [" + d.BadCrcSectors + " bad-CRC sectors]";
                if (d.MissingSectors > 0) warn += "  [" + d.MissingSectors + " unreadable sectors]";

                Console.WriteLine();
                Console.WriteLine(disk + "  -  " + shown.Count + " of " + d.Entries.Count + " files" + warn);
                if (shown.Count == 0) continue;

                Console.WriteLine(string.Format("  {0,-10}  {1,-8}  {2,9}  {3,6}  {4,7}  {5,6}  {6,4}  {7,6}",
                    "NAME", "TYPE", "BYTES", "BLOCKS", "RATE", "SECS", "LOOP", "KG/TUNE"));
                foreach (var e in shown)
                {
                    bool s = e.Type == 'S';
                    string rate = s ? e.SampleRate.ToString() : "";
                    string secs = s ? e.Seconds.ToString("0.00", CultureInfo.InvariantCulture) : "";
                    string loop = s ? LoopName(e.LoopMode) : "";
                    string last = s ? e.Semitones.ToString("0.0", CultureInfo.InvariantCulture)
                                    : (e.Type == 'P' ? AkaiDisk.KeygroupCount(e) + " kg" : "");

                    Console.WriteLine(string.Format("  {0,-10}  {1,-8}  {2,9}  {3,6}  {4,7}  {5,6}  {6,4}  {7,6}{8}",
                        e.Name, e.TypeName, e.Length, e.ChainBlocks, rate, secs, loop, last,
                        e.ChainOk ? "" : "  <- allocation mismatch"));

                    if (refs && e.Type == 'P')
                        foreach (var kg in d.Keygroups(e))
                            Console.WriteLine(string.Format(
                                "      kg {0,2}  keys {1,3}-{2,3}  VCA {3,-12} VCF {4,-12} filt {5,2} loud {6,3} out {9,-2}  {7,-10}{8}",
                                kg.Index + 1, kg.LowKey, kg.HighKey, kg.Vca, kg.Vcf,
                                kg.Zone1.Filter, kg.Zone1.Loudness, kg.Zone1.Name,
                                kg.HasSecondZone ? "  + " + kg.Zone2.Name : "", kg.OutputName)
                                + (kg.VelocitySwitchOff ? "" : "  vsw " + kg.VelocitySwitch));
                }
            }

            if (!csv)
            {
                Console.WriteLine();
                Console.WriteLine("Read " + read + " disk(s)" +
                    (failed > 0 ? ", " + failed + " failed" : "") +
                    "; listed " + listed + " file(s).");
            }
            return 0;
        }
    }
}
