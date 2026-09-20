using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AkaiS950Studio
{
    /// <summary>
    /// Draws a sample's waveform with its start, end and loop markers. The envelope is
    /// reduced to one min/max pair per pixel column and cached, so a half-million-word
    /// sample repaints as cheaply as a short one.
    /// </summary>
    internal sealed class WaveformView : Control
    {
        const int RulerHeight = 14;

        short[] _pcm;
        int _rate = 40000;
        long _start, _end, _loopLength;
        char _loopMode = 'O';
        string _caption = "";

        short[] _min, _max;          // envelope, one entry per pixel column
        int _cachedWidth = -1;

        public WaveformView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(250, 250, 252);
        }

        public void SetSample(short[] pcm, int rate, long start, long end, long loopLength,
                              char loopMode, string caption)
        {
            _pcm = pcm;
            _rate = rate > 0 ? rate : 40000;
            _start = start;
            _end = end;
            _loopLength = loopLength;
            _loopMode = loopMode;
            _caption = caption ?? "";
            _cachedWidth = -1;
            Invalidate();
        }

        bool Loops { get { return _loopMode == 'L' || _loopMode == 'A'; } }

        /// <summary>
        /// Where the loop begins. Playback runs to the end marker and jumps back by the
        /// loop length, so the loop is the tail of the sample, not the whole marked span.
        /// </summary>
        long LoopFrom
        {
            get
            {
                long from = _end - _loopLength;
                return from < _start ? _start : from;
            }
        }

        public void Clear()
        {
            _pcm = null;
            _min = null;
            _max = null;
            _cachedWidth = -1;
            _caption = "";
            Invalidate();
        }

        // ------------------------------------------------------------- envelope

        int PlotHeight { get { return Math.Max(8, Height - RulerHeight); } }

        void BuildEnvelope()
        {
            int w = Math.Max(1, Width);
            if (_cachedWidth == w && _min != null) return;

            _cachedWidth = w;
            if (_pcm == null || _pcm.Length == 0) { _min = _max = null; return; }

            _min = new short[w];
            _max = new short[w];

            for (int x = 0; x < w; x++)
            {
                long a = (long)x * _pcm.Length / w;
                long b = (long)(x + 1) * _pcm.Length / w;
                if (b <= a) b = a + 1;
                if (b > _pcm.Length) b = _pcm.Length;

                short lo = short.MaxValue, hi = short.MinValue;
                for (long i = a; i < b; i++)
                {
                    short v = _pcm[i];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                _min[x] = lo;
                _max[x] = hi;
            }
        }

        int XOf(long word)
        {
            if (_pcm == null || _pcm.Length == 0) return 0;
            double f = (double)word / _pcm.Length;
            return (int)Math.Round(Math.Max(0, Math.Min(1, f)) * (Width - 1));
        }

        int YOf(int value)
        {
            // 12-bit data runs -2048..2047.
            double f = Math.Max(-1, Math.Min(1, value / 2048.0));
            return (int)Math.Round(PlotHeight / 2.0 - f * (PlotHeight / 2.0 - 2));
        }

        // -------------------------------------------------------------- painting

        static readonly Color Wave = Color.FromArgb(62, 118, 184);
        static readonly Color WaveLoop = Color.FromArgb(28, 78, 140);
        static readonly Color LoopFill = Color.FromArgb(232, 240, 250);
        static readonly Color Axis = Color.FromArgb(205, 208, 214);
        static readonly Color MarkerIn = Color.FromArgb(32, 140, 72);
        static readonly Color MarkerOut = Color.FromArgb(190, 70, 40);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);

            if (_pcm == null || _pcm.Length == 0)
            {
                using (var b = new SolidBrush(SystemColors.GrayText))
                using (var f = new Font(Font.FontFamily, 8f))
                    g.DrawString("Select a sample, or a keygroup, to see its waveform.",
                                 f, b, 8, Height / 2 - 8);
                return;
            }

            BuildEnvelope();
            int mid = PlotHeight / 2;

            // The loop gets a wash behind the waveform, but only when there is one.
            bool loops = Loops && _loopLength > 0 && _end > LoopFrom;
            if (loops)
            {
                int x1 = XOf(LoopFrom), x2 = XOf(_end);
                using (var b = new SolidBrush(LoopFill))
                    g.FillRectangle(b, x1, 0, Math.Max(1, x2 - x1), PlotHeight);
            }

            using (var axis = new Pen(Axis))
                g.DrawLine(axis, 0, mid, Width, mid);

            using (var pen = new Pen(Wave))
            using (var loopPen = new Pen(WaveLoop))
                for (int x = 0; x < _min.Length && x < Width; x++)
                {
                    int y1 = YOf(_max[x]);
                    int y2 = YOf(_min[x]);
                    if (y2 - y1 < 1) y2 = y1 + 1;

                    long word = (long)x * _pcm.Length / Math.Max(1, Width);
                    bool inLoop = loops && word >= LoopFrom && word <= _end;
                    g.DrawLine(inLoop ? loopPen : pen, x, y1, x, y2);
                }

            DrawMarker(g, _start, MarkerIn, "start");
            if (loops) DrawMarker(g, LoopFrom, MarkerIn, "loop");
            if (_end > 0 && _end < _pcm.Length) DrawMarker(g, _end, MarkerOut, "end");

            DrawRuler(g);
            DrawCaption(g);
        }

        void DrawMarker(Graphics g, long word, Color colour, string label)
        {
            if (word <= 0 || word >= _pcm.Length) return;

            int x = XOf(word);
            using (var pen = new Pen(colour))
                g.DrawLine(pen, x, 0, x, PlotHeight);

            // Labels sit at the foot of the marker: the top right belongs to the caption.
            using (var b = new SolidBrush(colour))
            using (var f = new Font(Font.FontFamily, 6.5f))
            {
                var size = g.MeasureString(label, f);
                float lx = x + 2;
                if (lx + size.Width > Width) lx = x - size.Width - 2;
                g.DrawString(label, f, b, lx, PlotHeight - size.Height - 1);
            }
        }

        /// <summary>Seconds along the bottom, at whatever spacing leaves room to read.</summary>
        void DrawRuler(Graphics g)
        {
            double seconds = (double)_pcm.Length / _rate;
            if (seconds <= 0) return;

            double step = NiceStep(seconds, Math.Max(1, Width / 70));
            using (var pen = new Pen(Axis))
            using (var b = new SolidBrush(SystemColors.GrayText))
            using (var f = new Font(Font.FontFamily, 6.5f))
            {
                g.DrawLine(pen, 0, PlotHeight, Width, PlotHeight);
                for (double t = 0; t <= seconds + 1e-9; t += step)
                {
                    int x = (int)Math.Round(t / seconds * (Width - 1));
                    g.DrawLine(pen, x, PlotHeight, x, PlotHeight + 3);

                    string s = t.ToString(step < 0.1 ? "0.00" : step < 1 ? "0.0" : "0",
                                          CultureInfo.InvariantCulture) + " s";
                    var size = g.MeasureString(s, f);
                    float lx = x + 2;
                    if (lx + size.Width > Width) continue;
                    g.DrawString(s, f, b, lx, PlotHeight + 1);
                }
            }
        }

        /// <summary>A round tick interval - 1, 2 or 5 times a power of ten.</summary>
        static double NiceStep(double span, int wanted)
        {
            double raw = span / Math.Max(1, wanted);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-6))));
            double n = raw / mag;
            double step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
            return step * mag;
        }

        void DrawCaption(Graphics g)
        {
            if (_caption.Length == 0) return;
            using (var f = new Font(Font.FontFamily, 7.5f))
            using (var b = new SolidBrush(SystemColors.GrayText))
            {
                var size = g.MeasureString(_caption, f);
                g.DrawString(_caption, f, b, Width - size.Width - 4, 1);
            }
        }
    }
}
