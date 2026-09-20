// Does an edit made once reach every selected keygroup, and does a flag leave each
// keygroup's other flags alone?
//
// KeygroupEditor is internal to the Studio assembly, so this is compiled together with
// the Studio sources and given its own entry point with /main - see README.md.
using System;
using System.Collections.Generic;
using System.IO;
using AkaiS950List;
using AkaiS950Studio;

static class MultiCheck
{
    static int _fails;

    static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + (detail != null ? "   " + detail : ""));
        if (!ok) _fails++;
    }

    /// <summary>One byte of one keygroup, read straight out of the image.</summary>
    static byte Raw(AkaiDisk d, AkaiEntry p, int index, int offset)
    {
        return d.ReadFile(p)[AkaiDisk.ProgHeaderSize + index * AkaiDisk.KeygroupSize + offset];
    }

    static int Main(string[] args)
    {
        string image = args.Length > 0 ? args[0]
            : @"C:\Users\simon\src\AkaiS950Web\DSKA0000-bench.hfe";
        if (!File.Exists(image)) { Console.WriteLine("no image at " + image); return 2; }

        var disk = AkaiDisk.Load(image);
        AkaiEntry prog = null;
        foreach (var e in disk.Entries)
            if (e.Type == 'P' && AkaiDisk.KeygroupCount(e) >= 4) { prog = e; break; }
        if (prog == null) { Console.WriteLine("no program with four keygroups"); return 2; }

        Console.WriteLine(prog.Name.Trim() + ", " + AkaiDisk.KeygroupCount(prog) + " keygroups");

        // --- a value typed once reaches the whole selection, and nothing else
        var also = new List<int> { 1, 2 };
        var ed = new KeygroupEditor(disk, prog, 0, also);
        Check("the editor says what it reaches", ed.Reaches == 3, ed.Reaches + " keygroups");

        byte before3 = Raw(disk, prog, 3, 11);
        ed.ToLoudness = 42;

        Check("the lead takes the value", Raw(disk, prog, 0, 11) == 42, "kg1 = " + Raw(disk, prog, 0, 11));
        Check("and so do the others", Raw(disk, prog, 1, 11) == 42 && Raw(disk, prog, 2, 11) == 42,
              "kg2 = " + Raw(disk, prog, 1, 11) + ", kg3 = " + Raw(disk, prog, 2, 11));
        Check("a keygroup that was not selected is untouched", Raw(disk, prog, 3, 11) == before3,
              "kg4 = " + Raw(disk, prog, 3, 11) + ", was " + before3);

        // --- a flag is the one bit, not the whole byte
        // give the three different flags to start with: only the bit being set should move.
        disk.PokeFile(prog, AkaiDisk.ProgHeaderSize + 0 * AkaiDisk.KeygroupSize + 18, 0x01);
        disk.PokeFile(prog, AkaiDisk.ProgHeaderSize + 1 * AkaiDisk.KeygroupSize + 18, 0x04);
        disk.PokeFile(prog, AkaiDisk.ProgHeaderSize + 2 * AkaiDisk.KeygroupSize + 18, 0x00);

        var flags = new KeygroupEditor(disk, prog, 0, also);
        flags.OneShot = true;

        Check("the flag is set on every selected keygroup",
              (Raw(disk, prog, 0, 18) & 8) != 0 && (Raw(disk, prog, 1, 18) & 8) != 0 &&
              (Raw(disk, prog, 2, 18) & 8) != 0,
              "0x" + Raw(disk, prog, 0, 18).ToString("x2") + ", 0x" +
              Raw(disk, prog, 1, 18).ToString("x2") + ", 0x" + Raw(disk, prog, 2, 18).ToString("x2"));

        Check("and each keeps its own other flags",
              Raw(disk, prog, 0, 18) == 0x09 && Raw(disk, prog, 1, 18) == 0x0C &&
              Raw(disk, prog, 2, 18) == 0x08,
              "expected 0x09, 0x0c, 0x08");

        flags.OneShot = false;
        Check("clearing it leaves the rest alone too",
              Raw(disk, prog, 0, 18) == 0x01 && Raw(disk, prog, 1, 18) == 0x04 &&
              Raw(disk, prog, 2, 18) == 0x00,
              "0x" + Raw(disk, prog, 0, 18).ToString("x2") + ", 0x" +
              Raw(disk, prog, 1, 18).ToString("x2") + ", 0x" + Raw(disk, prog, 2, 18).ToString("x2"));

        // --- an editor with no siblings still edits exactly one
        byte was1 = Raw(disk, prog, 1, 15);
        new KeygroupEditor(disk, prog, 0).LfoDelay = 63;
        Check("a single selection reaches one keygroup only",
              Raw(disk, prog, 0, 15) == 63 && Raw(disk, prog, 1, 15) == was1,
              "kg1 = " + Raw(disk, prog, 0, 15) + ", kg2 = " + Raw(disk, prog, 1, 15));

        Console.WriteLine(_fails == 0 ? "PASS" : _fails + " FAILED");
        return _fails == 0 ? 0 : 1;
    }
}
