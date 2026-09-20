using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AkaiS950Studio
{
    /// <summary>One keygroup's span on the keyboard, in MIDI note numbers.</summary>
    internal sealed class KeySpan
    {
        public int Index;          // zero-based keygroup number
        public int Low, High;      // inclusive, MIDI note numbers (C3 = 60)
        public string Sample;

        public KeySpan(int index, int low, int high, string sample)
        {
            Index = index;
            Low = Math.Min(low, high);
            High = Math.Max(low, high);
            Sample = sample ?? "";
        }
    }

    /// <summary>
    /// A piano keyboard showing where every keygroup of the current program sits.
    /// Unselected groups get a pale wash, the selected group a solid colour.
    /// </summary>
    internal sealed class PianoKeyboard : Control
    {
        // An 88-key piano unless a keygroup reaches past it, in which case the
        // view widens rather than clipping the range.
        const int DefaultLow = 21, DefaultHigh = 108;

        const int CaptionHeight = 16;
        const int LabelHeight = 13;

        /// <summary>A MIDI note as a name, for anything reporting a range.</summary>
        public static string NameOf(int note) { return NoteName(note); }

        static readonly string[] NoteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        readonly List<KeySpan> _spans = new List<KeySpan>();
        int _selected = -1;
        int _low = DefaultLow, _high = DefaultHigh;
        int _hover = -1;

        // The rest of a multiple selection: shown, but not the one the editor follows.
        List<int> _also;

        // Setting a range by typing two MIDI numbers means knowing them. Armed, the next
        // click is the low key and the one after it the high, and the same key twice is a
        // one-key group.
        bool _arming;
        int _rangeLow = -1;

        /// <summary>Raised with the keygroup that owns the key, and the key's MIDI note.</summary>
        public event Action<int, int> KeyClicked;

        /// <summary>
        /// The key was let go, so the note it started should stop.
        ///
        /// A click used to be one-way - KeyClicked and nothing else - which was fine when
        /// playing a note meant firing a whole sample at SoundPlayer and waiting. With a
        /// real engine behind it, a note that is never released is a note that never ends:
        /// a looped sample rings forever, and after eight of them every voice is held and
        /// the next key steals one.
        /// </summary>
        public event Action<int> KeyReleased;

        // the note the mouse is currently holding down, or -1
        int _sounding = -1;

        /// <summary>Raised once both ends of a range have been clicked, low first.</summary>
        public event Action<int, int> RangePicked;

        /// <summary>The rest of the selection, drawn a shade apart from the lead.</summary>
        public List<int> AlsoSelected
        {
            set { _also = value; Invalidate(); }
        }

        public bool RangeArmed { get { return _arming; } }

        /// <summary>Takes the next two clicks as the ends of a key range.</summary>
        public void ArmRange()
        {
            _arming = true;
            _rangeLow = -1;
            Cursor = Cursors.Cross;
            Invalidate();
        }

        public void CancelRange()
        {
            if (!_arming) return;
            _arming = false;
            _rangeLow = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        /// <summary>The range the two clicks would give, while the second is still to come.</summary>
        bool PendingRange(out int lo, out int hi)
        {
            lo = hi = -1;
            if (!_arming || _rangeLow < 0) return false;
            int other = _hover >= 0 ? _hover : _rangeLow;
            lo = Math.Min(_rangeLow, other);
            hi = Math.Max(_rangeLow, other);
            return true;
        }

        public PianoKeyboard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            Height = CaptionHeight + 60 + LabelHeight;
            BackColor = SystemColors.Control;
        }

        /// <summary>Replaces the keygroup map. The selection survives if it still exists.</summary>
        public void SetSpans(IEnumerable<KeySpan> spans)
        {
            _spans.Clear();
            if (spans != null) _spans.AddRange(spans);

            int lo = DefaultLow, hi = DefaultHigh;
            foreach (var s in _spans)
            {
                lo = Math.Min(lo, s.Low);
                hi = Math.Max(hi, s.High);
            }
            lo = Math.Max(0, Math.Min(127, lo));
            hi = Math.Max(0, Math.Min(127, hi));

            // The view has to begin and end on a white key or the octaves look broken.
            while (lo > 0 && IsBlack(lo)) lo--;
            while (hi < 127 && IsBlack(hi)) hi++;
            _low = lo;
            _high = hi;

            if (_selected >= _spans.Count) _selected = -1;
            Invalidate();
        }

        public void Clear() { SetSpans(null); _selected = -1; Invalidate(); }

        /// <summary>Zero-based keygroup to highlight, or -1 for none.</summary>
        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = (value >= 0 && value < _spans.Count) ? value : -1;
                if (v == _selected) return;
                _selected = v;
                Invalidate();
            }
        }

        // ------------------------------------------------------------ geometry

        static bool IsBlack(int note)
        {
            switch (((note % 12) + 12) % 12)
            {
                case 1: case 3: case 6: case 8: case 10: return true;
                default: return false;
            }
        }

        static string NoteName(int note)
        {
            return NoteNames[((note % 12) + 12) % 12] + (note / 12 - 2);
        }

        int WhiteCount
        {
            get
            {
                int n = 0;
                for (int i = _low; i <= _high; i++) if (!IsBlack(i)) n++;
                return n;
            }
        }

        /// <summary>Position of a white note counted from the left edge of the view.</summary>
        int WhiteIndex(int note)
        {
            int n = 0;
            for (int i = _low; i < note; i++) if (!IsBlack(i)) n++;
            return n;
        }

        float WhiteWidth { get { int w = WhiteCount; return w > 0 ? (float)(Width - 1) / w : 1f; } }

        int KeyboardTop { get { return CaptionHeight; } }
        int KeyboardHeight { get { return Math.Max(12, Height - CaptionHeight - LabelHeight); } }

        RectangleF WhiteRect(int note)
        {
            float ww = WhiteWidth;
            return new RectangleF(WhiteIndex(note) * ww, KeyboardTop, ww, KeyboardHeight);
        }

        RectangleF BlackRect(int note)
        {
            float ww = WhiteWidth;
            float bw = ww * 0.62f;
            float centre = (WhiteIndex(note - 1) + 1) * ww;   // the note below a black key is always white
            return new RectangleF(centre - bw / 2f, KeyboardTop, bw, KeyboardHeight * 0.62f);
        }

        /// <summary>The note under a point, black keys taking priority as they sit on top.</summary>
        int NoteAt(Point p)
        {
            if (p.Y < KeyboardTop || p.Y > KeyboardTop + KeyboardHeight) return -1;
            for (int n = _low; n <= _high; n++)
                if (IsBlack(n) && BlackRect(n).Contains(p)) return n;
            for (int n = _low; n <= _high; n++)
                if (!IsBlack(n) && WhiteRect(n).Contains(p)) return n;
            return -1;
        }

        /// <summary>The keygroup covering a note - the selected one wins a tie, else the first.</summary>
        int GroupAt(int note)
        {
            if (note < 0) return -1;
            if (_selected >= 0 && _selected < _spans.Count
                && note >= _spans[_selected].Low && note <= _spans[_selected].High)
                return _selected;
            for (int i = 0; i < _spans.Count; i++)
                if (note >= _spans[i].Low && note <= _spans[i].High) return i;
            return -1;
        }

        // ------------------------------------------------------------- painting

        static readonly Color WhiteKey = Color.White;
        static readonly Color BlackKey = Color.FromArgb(38, 38, 38);
        static readonly Color WhiteOther = Color.FromArgb(219, 230, 243);
        static readonly Color BlackOther = Color.FromArgb(74, 92, 112);
        static readonly Color WhiteSel = Color.FromArgb(92, 152, 224);
        static readonly Color BlackSel = Color.FromArgb(26, 82, 148);
        static readonly Color Outline = Color.FromArgb(120, 120, 120);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);

            DrawCaption(g);

            float ww = WhiteWidth;
            using (var pen = new Pen(Outline))
            {
                // White keys first, then the blacks on top of them.
                for (int n = _low; n <= _high; n++)
                {
                    if (IsBlack(n)) continue;
                    var r = WhiteRect(n);
                    using (var b = new SolidBrush(FillFor(n, false))) g.FillRectangle(b, r);
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                }

                for (int n = _low; n <= _high; n++)
                {
                    if (!IsBlack(n)) continue;
                    var r = BlackRect(n);
                    using (var b = new SolidBrush(FillFor(n, true))) g.FillRectangle(b, r);
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                }
            }

            DrawOctaveLabels(g, ww);
            DrawHoverTag(g);
        }

        /// <summary>
        /// The hovered key's name and MIDI number, at the pointer. Key range values are
        /// entered as numbers, so the keyboard is the easiest place to read one off.
        /// </summary>
        void DrawHoverTag(Graphics g)
        {
            if (_hover < 0) return;

            string text = NoteName(_hover) + "   " + _hover;
            using (var f = new Font(Font.FontFamily, 7.5f, FontStyle.Bold))
            {
                var size = g.MeasureString(text, f);
                float w = size.Width + 10, h = size.Height + 4;

                // Sit above the key, pulled inside the control at the edges.
                float x = Math.Max(0, Math.Min(Width - w, XOfNote(_hover) - w / 2));
                float y = KeyboardTop + 2;

                using (var b = new SolidBrush(Color.FromArgb(235, 40, 44, 52)))
                    g.FillRectangle(b, x, y, w, h);
                using (var b = new SolidBrush(Color.White))
                    g.DrawString(text, f, b, x + 5, y + 2);
            }
        }

        /// <summary>Centre of a key, black or white.</summary>
        float XOfNote(int note)
        {
            var r = IsBlack(note) ? BlackRect(note) : WhiteRect(note);
            return r.X + r.Width / 2f;
        }

        // What the two clicks will take, marked warm so it cannot be mistaken for the
        // selection it is about to replace.
        static readonly Color WhitePending = Color.FromArgb(243, 205, 178);
        static readonly Color BlackPending = Color.FromArgb(168, 56, 0);

        // The rest of a multiple selection: plainly picked, plainly not the lead.
        static readonly Color WhiteAlso = Color.FromArgb(169, 203, 238);
        static readonly Color BlackAlso = Color.FromArgb(47, 107, 168);

        Color FillFor(int note, bool black)
        {
            int lo, hi;
            if (_arming && ((PendingRange(out lo, out hi) && note >= lo && note <= hi)
                            || note == _rangeLow))
                return black ? BlackPending : WhitePending;

            int grp = GroupAt(note);
            if (grp < 0) return black ? BlackKey : WhiteKey;
            if (grp == _selected) return black ? BlackSel : WhiteSel;
            if (_also != null && _also.Contains(grp)) return black ? BlackAlso : WhiteAlso;
            return black ? BlackOther : WhiteOther;
        }

        void DrawCaption(Graphics g)
        {
            string text;
            if (_arming)
            {
                int lo, hi;
                if (!PendingRange(out lo, out hi))
                    text = "Click the low key" + (_selected >= 0
                           ? " for keygroup " + (_selected + 1) : "");
                else
                    text = "Low " + NoteName(_rangeLow) + "  -  now click the high key    (" +
                           NoteName(lo) + " - " + NoteName(hi) + ",  " + (hi - lo + 1) +
                           " key" + (hi == lo ? "" : "s") + ")";

                using (var af = new Font(Font.FontFamily, 7.5f, FontStyle.Bold))
                using (var ab = new SolidBrush(BlackPending))
                    g.DrawString(text, af, ab, 2, 1);
                return;
            }

            if (_selected >= 0 && _selected < _spans.Count)
            {
                var s = _spans[_selected];
                text = "Keygroup " + (s.Index + 1) + "    " + s.Low + " - " + s.High +
                       "    (" + NoteName(s.Low) + " - " + NoteName(s.High) + ")";
                if (s.Sample.Length > 0) text += "    " + s.Sample;
                if (_also != null && _also.Count > 0)
                    text += "    -  " + (_also.Count + 1) + " selected, edits reach them all";
            }
            else if (_spans.Count > 0)
            {
                text = _spans.Count + " keygroup" + (_spans.Count == 1 ? "" : "s") +
                       "  -  select one to highlight its range";
            }
            else return;

            using (var f = new Font(Font.FontFamily, 7.5f))
            using (var b = new SolidBrush(_selected >= 0 ? BlackSel : SystemColors.GrayText))
                g.DrawString(text, f, b, 2, 1);

            if (_hover >= 0)
            {
                string h = NoteName(_hover) + "  (" + _hover + ")";
                using (var f = new Font(Font.FontFamily, 7.5f))
                using (var b = new SolidBrush(SystemColors.GrayText))
                {
                    var sz = g.MeasureString(h, f);
                    g.DrawString(h, f, b, Width - sz.Width - 2, 1);
                }
            }
        }

        /// <summary>C names under the keyboard, thinned out when the keys get narrow.</summary>
        void DrawOctaveLabels(Graphics g, float ww)
        {
            if (ww < 5f) return;
            int every = ww >= 9f ? 1 : 2;      // every octave, or every other one
            float y = KeyboardTop + KeyboardHeight;

            using (var f = new Font(Font.FontFamily, 6.5f))
            using (var b = new SolidBrush(SystemColors.GrayText))
                for (int n = _low; n <= _high; n++)
                {
                    if (n % 12 != 0) continue;
                    if ((n / 12) % every != 0) continue;
                    var r = WhiteRect(n);
                    var s = NoteName(n);
                    var sz = g.MeasureString(s, f);
                    g.DrawString(s, f, b, r.X + (r.Width - sz.Width) / 2f, y);
                }
        }

        // ------------------------------------------------------------- pointing

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = NoteAt(e.Location);
            if (n == _hover) return;
            _hover = n;
            Cursor = GroupAt(n) >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover < 0) return;
            _hover = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        /// <summary>Clicking a mapped key selects the keygroup that owns it and sounds it.</summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            // right button, or any button while armed, cancels rather than plays
            if (e.Button == MouseButtons.Right) { CancelRange(); return; }
            if (e.Button != MouseButtons.Left) return;

            int note = NoteAt(e.Location);

            if (_arming)
            {
                if (note < 0) return;
                if (_rangeLow < 0) { _rangeLow = note; Invalidate(); return; }

                int lo = Math.Min(_rangeLow, note), hi = Math.Max(_rangeLow, note);
                _arming = false;
                _rangeLow = -1;
                Cursor = Cursors.Default;
                Invalidate();

                var r = RangePicked;
                if (r != null) r(lo, hi);
                return;
            }

            int grp = GroupAt(note);
            if (grp < 0) return;

            _sounding = note;
            Capture = true;            // so the release arrives even off the control

            var h = KeyClicked;
            if (h != null) h(grp, note);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) StopSounding();
        }

        /// <summary>
        /// Let go of whatever is sounding.
        ///
        /// Called from more than one place on purpose. A mouse-up is the ordinary way, but
        /// a drag off the window, another window taking the mouse, or the control being
        /// hidden mid-press all end the press too - and any of them leaving a note held
        /// would be a note held until the program closed.
        /// </summary>
        void StopSounding()
        {
            if (_sounding < 0) return;

            int note = _sounding;
            _sounding = -1;
            Capture = false;

            var r = KeyReleased;
            if (r != null) r(note);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) StopSounding();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) StopSounding();
        }
    }
}

