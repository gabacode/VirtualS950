using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AkaiS950Studio
{
    /// <summary>
    /// An ADSR envelope drawn as a shape with draggable corners, in place of four
    /// numbers in a property grid. Attack, decay and release are times, sustain is a
    /// level; all four run 0..99 as the S950 stores them.
    /// </summary>
    internal sealed class EnvelopeEditor : Control
    {
        const int Pad = 10;
        const int KnobSize = 9;           // square, as in the reference
        const int LabelBand = 16;

        int _a, _d, _s, _r;
        int _dragging = -1;             // 0 attack, 1 decay/sustain, 2 release

        public string Title = "";

        /// <summary>Raised while a corner is dragged, with the values already updated.</summary>
        public event EventHandler Changed;

        /// <summary>Raised when a corner is grabbed, before anything changes.</summary>
        public event EventHandler DragStarted;

        public int Attack { get { return _a; } }
        public int Decay { get { return _d; } }
        public int Sustain { get { return _s; } }
        public int Release { get { return _r; } }

        public EnvelopeEditor()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(250, 250, 252);   // as the waveform pane
        }

        public void SetValues(int a, int d, int s, int r)
        {
            _a = Clamp(a); _d = Clamp(d); _s = Clamp(s); _r = Clamp(r);
            Invalidate();
        }

        static int Clamp(int v) { return v < 0 ? 0 : v > 99 ? 99 : v; }

        // ------------------------------------------------------------- geometry

        int PlotTop { get { return Pad + LabelBand; } }
        int PlotBottom { get { return Math.Max(PlotTop + 10, Height - Pad); } }
        int PlotLeft { get { return Pad; } }
        int Span { get { return Math.Max(30, Width - 2 * Pad); } }

        /// <summary>Width one time segment takes at its maximum.</summary>
        float Unit { get { return (Span - HoldWidth) / 3f; } }
        float HoldWidth { get { return Span * 0.18f; } }

        PointF P0 { get { return new PointF(PlotLeft, PlotBottom); } }
        PointF P1 { get { return new PointF(PlotLeft + _a / 99f * Unit, PlotTop); } }
        PointF P2 { get { return new PointF(P1.X + _d / 99f * Unit, SustainY); } }
        PointF P3 { get { return new PointF(P2.X + HoldWidth, SustainY); } }
        PointF P4 { get { return new PointF(P3.X + _r / 99f * Unit, PlotBottom); } }

        float SustainY { get { return PlotBottom - _s / 99f * (PlotBottom - PlotTop); } }

        PointF HandleAt(int i) { return i == 0 ? P1 : i == 1 ? P2 : P4; }

        int HitTest(Point p)
        {
            for (int i = 0; i < 3; i++)
            {
                var h = HandleAt(i);
                if (Math.Abs(p.X - h.X) <= KnobSize && Math.Abs(p.Y - h.Y) <= KnobSize) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------- painting

        // The same palette the waveform pane and the keyboard use.
        static readonly Color Line = Color.FromArgb(62, 118, 184);
        static readonly Color Fill = Color.FromArgb(70, 92, 152, 224);
        static readonly Color Knob = Color.FromArgb(28, 78, 140);
        static readonly Color Grid = Color.FromArgb(205, 208, 214);
        static readonly Color Ink = SystemColors.GrayText;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var pen = new Pen(Grid))
            {
                g.DrawRectangle(pen, PlotLeft, PlotTop, Span - 1, PlotBottom - PlotTop);
                int mid = (PlotTop + PlotBottom) / 2;
                g.DrawLine(pen, PlotLeft, mid, PlotLeft + Span - 1, mid);
            }

            var pts = new[] { P0, P1, P2, P3, P4 };
            using (var fill = new SolidBrush(Fill))
                g.FillPolygon(fill, new[] { P0, P1, P2, P3, P4, new PointF(P4.X, PlotBottom) });
            using (var pen = new Pen(Line, 1.8f))
                g.DrawLines(pen, pts);

            for (int i = 0; i < 3; i++) DrawHandle(g, HandleAt(i), i == _dragging);
            DrawLabel(g);
        }

        void DrawHandle(Graphics g, PointF p, bool active)
        {
            var r = new RectangleF(p.X - KnobSize / 2f, p.Y - KnobSize / 2f, KnobSize, KnobSize);
            using (var b = new SolidBrush(active ? Color.FromArgb(232, 140, 40) : Knob))
                g.FillRectangle(b, r);
            using (var pen = new Pen(Color.White))
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }

        void DrawLabel(Graphics g)
        {
            string text = Title + "    A " + _a + "   D " + _d + "   S " + _s + "   R " + _r;
            using (var f = new Font(Font.FontFamily, 7.5f, FontStyle.Bold))
            using (var b = new SolidBrush(Ink))
                g.DrawString(text, f, b, Pad - 2, 2);
        }

        // -------------------------------------------------------------- dragging

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            _dragging = HitTest(e.Location);
            if (_dragging < 0) return;

            Capture = true;
            Invalidate();

            var h = DragStarted;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_dragging < 0)
            {
                Cursor = HitTest(e.Location) >= 0 ? Cursors.SizeAll : Cursors.Default;
                return;
            }

            float unit = Unit;
            if (unit <= 0) return;

            switch (_dragging)
            {
                case 0:
                    _a = Clamp((int)Math.Round((e.X - PlotLeft) / unit * 99));
                    break;

                case 1:
                    _d = Clamp((int)Math.Round((e.X - P1.X) / unit * 99));
                    _s = Clamp((int)Math.Round((PlotBottom - e.Y) / (float)(PlotBottom - PlotTop) * 99));
                    break;

                default:
                    _r = Clamp((int)Math.Round((e.X - P3.X) / unit * 99));
                    break;
            }

            Invalidate();
            var h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging < 0) return;
            _dragging = -1;
            Capture = false;
            Invalidate();
        }
    }
}



