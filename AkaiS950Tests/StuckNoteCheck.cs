using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using AkaiS950Engine;
using AkaiS950Studio;

/// <summary>
/// Notes that never stop, and voices taken from the wrong place.
///
/// Both were reported as "press several keys quickly and some notes end up looping when
/// they shouldn't, and release doesn't always kick in", and they are two separate faults
/// that produce the same complaint.
///
/// The keyboard half is driven through the real control, with its real mouse handlers,
/// because the bug is in the bookkeeping between a press and a release and nothing else
/// would exercise it.
/// </summary>
static class StuckNoteCheck
{
    const double Rate = 48000;
    static int _fails;

    static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + (detail == null ? "" : "   " + detail));
        if (!ok) _fails++;
    }

    [STAThread]
    static int Main()
    {
        Console.WriteLine();
        Console.WriteLine("the keyboard, pressed faster than it is released:");
        KeyboardCheck();

        Console.WriteLine();
        Console.WriteLine("which voice a new note takes:");
        StealCheck();

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "all good" : _fails + " FAILED");
        return _fails == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ keyboard

    static void KeyboardCheck()
    {
        using (var form = new Form())
        {
            var piano = new PianoKeyboard();
            piano.Width = 900;
            piano.Height = 80;
            form.Controls.Add(piano);
            form.CreateControl();
            piano.CreateControl();

            // one keygroup across the whole keyboard, so every key is playable
            piano.SetSpans(new[] { new KeySpan(0, 0, 127, "SAMPLE") });

            var started = new List<int>();
            var stopped = new List<int>();
            piano.KeyClicked += delegate (int g, int n) { started.Add(n); };
            piano.KeyReleased += delegate (int n) { stopped.Add(n); };

            // press three keys with never a mouse-up between them - which is what a
            // swallowed release looks like from in here
            Down(piano, 200);
            Down(piano, 300);
            Down(piano, 400);

            Check("three presses with no release between them started three notes",
                  started.Count == 3, started.Count + " started");
            Check("  and released the first two on the way",
                  stopped.Count == 2, stopped.Count + " released");

            Up(piano, 400);
            Check("  and the last one on the mouse-up",
                  stopped.Count == 3, stopped.Count + " released of " + started.Count);

            // whatever was started must have been stopped, and nothing else
            bool matched = started.Count == stopped.Count;
            for (int i = 0; matched && i < started.Count; i++)
                if (started[i] != stopped[i]) matched = false;
            Check("  every note that started, stopped - and no others", matched,
                  Join(started) + "  ->  " + Join(stopped));
        }
    }

    static string Join(List<int> a)
    {
        var s = new string[a.Count];
        for (int i = 0; i < a.Count; i++) s[i] = a[i].ToString();
        return "[" + string.Join(",", s) + "]";
    }

    /// <summary>The control's own OnMouseDown, reached the way WinForms would reach it.</summary>
    static void Down(PianoKeyboard p, int x)
    {
        Invoke(p, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, x, 20, 0));
    }

    static void Up(PianoKeyboard p, int x)
    {
        Invoke(p, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, x, 20, 0));
    }

    static void Invoke(object target, string method, object arg)
    {
        MethodInfo m = null;
        for (Type t = target.GetType(); t != null && m == null; t = t.BaseType)
            m = t.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance,
                            null, new[] { typeof(MouseEventArgs) }, null);
        if (m == null) throw new InvalidOperationException("no " + method);
        m.Invoke(target, new[] { arg });
    }

    // ------------------------------------------------------------------ stealing

    /// <summary>
    /// With every voice busy, a new note should take one that has been let go before it
    /// takes one the player is still holding.
    /// </summary>
    static void StealCheck()
    {
        var sound = Tone(400);
        var p = new Patch();
        p.Keygroups.Add(new KeygroupPatch
        {
            LowKey = 0, HighKey = 127, VelocityFrom = 0, VelocityTo = 127, Sound = sound,
            VcaAttack = 0, VcaDecay = 0, VcaSustain = 99, VcaRelease = 70,   // a long tail
            VcfWritten = true, VcfSustain = 99, ZoneFilter = 99, LfoDesync = true
        });

        var eng = new Engine(Rate);
        eng.SetPatch(p);
        var buf = new float[256];

        // fill every voice
        for (int i = 0; i < Engine.Polyphony; i++) { eng.NoteOn(40 + i, 100); eng.Render(buf, 0, buf.Length); }
        Check("every voice is busy", eng.ActiveVoices == Engine.Polyphony,
              eng.ActiveVoices + " of " + Engine.Polyphony);

        // let the OLDEST two go: they are now fading, not held
        eng.NoteOff(40);
        eng.NoteOff(41);
        eng.Render(buf, 0, buf.Length);

        int heldBefore = Held(eng);
        Check("  two of them are now releasing", heldBefore == Engine.Polyphony - 2,
              heldBefore + " still held");

        // a new note must land on one of those, leaving every held note alone
        eng.NoteOn(80, 100);
        eng.Render(buf, 0, buf.Length);

        int heldAfter = Held(eng);
        Check("  a new note takes a releasing voice, not a held one",
              heldAfter == heldBefore + 1, heldAfter + " held, was " + heldBefore + " + the new one");

        // and every key still down must still be sounding
        bool allThere = true;
        for (int n = 42; n < 40 + Engine.Polyphony; n++) if (!Sounding(eng, n)) allThere = false;
        Check("  and every key still down is still sounding", allThere, null);
    }

    static int Held(Engine e)
    {
        int n = 0;
        foreach (object v in Voices(e)) if ((bool)Prop(v, "Held")) n++;
        return n;
    }

    static bool Sounding(Engine e, int note)
    {
        foreach (object v in Voices(e))
            if ((bool)Prop(v, "Active") && (int)Prop(v, "Note") == note) return true;
        return false;
    }

    static IEnumerable<object> Voices(Engine e)
    {
        var arr = (Array)typeof(Engine).GetField("_voices",
            BindingFlags.NonPublic | BindingFlags.Instance).GetValue(e);
        foreach (object v in arr) yield return v;
    }

    static object Prop(object o, string name)
    {
        return o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                .GetValue(o, null);
    }

    static Sound Tone(double hz)
    {
        int rate = 48000, period = (int)Math.Round(rate / hz), n = period * 200;
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (float)Math.Sin(2 * Math.PI * (i % period) / period);
        return new Sound
        {
            Name = "TONE", Audio = a, SourceRate = rate, RootPitch = 60,
            Loops = true, LoopFrom = 0, LoopTo = n
        };
    }
}
