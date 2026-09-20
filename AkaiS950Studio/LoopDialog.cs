using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using AkaiS950List;

namespace AkaiS950Studio
{
    /// <summary>
    /// Finding a loop in a sample.
    ///
    /// The S950 holds a loop as an end point and a length and plays end-length .. end over
    /// and over, so the search is for the length - see AkaiDisk.FindLoop, which does the
    /// looking. What this adds is the two things the finder cannot know: how short a loop
    /// is still musical, and whether the loop should run to the end of the sample or to the
    /// end marker the sample already carries.
    ///
    /// The search is quick enough to rerun on every change, so the shaded loop and the
    /// verdict are always live.
    /// </summary>
    internal sealed class LoopDialog : Form
    {
        readonly AkaiEntry _entry;
        readonly short[] _words;

        readonly NumericUpDown _minMs = new NumericUpDown();
        readonly ComboBox _endAt = new ComboBox();
        readonly ComboBox _mode = new ComboBox();
        readonly Label _summary = new Label();
        readonly LoopPreview _preview = new LoopPreview();
        readonly Button _ok = new Button();

        LoopChoice _found;

        /// <summary>The loop the user settled on, or null if there was none to have.</summary>
        public LoopChoice Found { get { return _found; } }

        /// <summary>The loop mode to write with it.</summary>
        public char LoopMode { get { return _mode.SelectedIndex == 1 ? 'A' : 'L'; } }

        public LoopDialog(AkaiEntry entry, short[] words)
        {
            _entry = entry;
            _words = words;

            Text = "Find a loop in " + entry.Name.Trim();
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(700, 400);
            MinimumSize = new Size(640, 400);
            Font = SystemFonts.MessageBoxFont;

            var source = new Label
            {
                Bounds = new Rectangle(12, 10, 676, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = SystemColors.GrayText,
                Text = entry.Name.Trim() + "  -  " + entry.SampleCount.ToString("N0") + " words at " +
                       entry.SampleRate.ToString("N0") + " Hz, " +
                       entry.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s"
            };
            Controls.Add(source);

            _preview.SetBounds(12, 34, 676, 150);
            _preview.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _preview.Words = words;
            Controls.Add(_preview);

            int y = 196;

            Controls.Add(new Label
            {
                Bounds = new Rectangle(12, y + 4, 92, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "Shortest loop"
            });
            _minMs.SetBounds(106, y + 2, 70, 24);
            _minMs.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _minMs.Minimum = 2;
            _minMs.Maximum = 5000;
            _minMs.Value = 40;
            _minMs.Increment = 5;
            _minMs.ValueChanged += delegate { Recalculate(); };
            Controls.Add(_minMs);
            Controls.Add(new Label
            {
                Bounds = new Rectangle(182, y + 4, 90, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                ForeColor = SystemColors.GrayText,
                Text = "milliseconds"
            });

            Controls.Add(new Label
            {
                Bounds = new Rectangle(292, y + 4, 86, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "Loop ends at"
            });
            _endAt.SetBounds(380, y + 2, 200, 24);
            _endAt.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _endAt.DropDownStyle = ComboBoxStyle.DropDownList;
            _endAt.Items.Add("the end of the sample");
            bool hasEnd = entry.LoopEnd > 0 && entry.LoopEnd < entry.SampleCount;
            if (hasEnd) _endAt.Items.Add("the end marker it has now");
            _endAt.SelectedIndex = hasEnd ? 1 : 0;
            _endAt.SelectedIndexChanged += delegate { Recalculate(); };
            Controls.Add(_endAt);

            y += 32;

            Controls.Add(new Label
            {
                Bounds = new Rectangle(12, y + 4, 92, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "Loop mode"
            });
            _mode.SetBounds(106, y + 2, 160, 24);
            _mode.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _mode.DropDownStyle = ComboBoxStyle.DropDownList;
            _mode.Items.Add("Looping");
            _mode.Items.Add("Alternating");
            _mode.SelectedIndex = entry.LoopMode == 'A' ? 1 : 0;
            Controls.Add(_mode);

            y += 36;

            _summary.SetBounds(12, y, 676, 56);
            _summary.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _summary.BorderStyle = BorderStyle.FixedSingle;
            _summary.Padding = new Padding(8, 6, 8, 6);
            Controls.Add(_summary);

            _ok.SetBounds(500, ClientSize.Height - 40, 90, 28);
            _ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _ok.Text = "Set loop";
            _ok.DialogResult = DialogResult.OK;
            Controls.Add(_ok);

            var cancel = new Button
            {
                Bounds = new Rectangle(598, ClientSize.Height - 40, 90, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Text = "Cancel",
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(cancel);

            AcceptButton = _ok;
            CancelButton = cancel;

            Recalculate();
        }

        void Recalculate()
        {
            int end = (int)Math.Min(_words.Length,
                _endAt.SelectedIndex == 1 && _entry.LoopEnd > 0 ? _entry.LoopEnd : _words.Length);
            end -= end % 2;

            int minLength = Math.Max(64, (int)Math.Round(_entry.SampleRate * (double)_minMs.Value / 1000.0));
            _found = AkaiDisk.FindLoop(_words, end, minLength);

            _preview.Choice = _found;
            _preview.Invalidate();

            if (_found == null)
            {
                _summary.ForeColor = Color.FromArgb(160, 40, 40);
                _summary.Text = "No loop fits: the shortest loop asked for is longer than the part " +
                                "of the sample being searched.";
                _ok.Enabled = false;
                return;
            }

            // Where the match lands says more than the number does, so the summary says
            // which it is rather than leaving it to be guessed at.
            string verdict;
            if (_found.Match >= 0.95) verdict = "The join should be inaudible.";
            else if (_found.Match >= 0.8) verdict = "The join should be close, and may tick on a quiet passage.";
            else if (_found.Match >= 0.5) verdict = "The best on offer, but expect to hear it: try a shorter " +
                                                    "minimum, or a sample with a steadier tail.";
            else verdict = "Nothing here loops cleanly - noise and applause have no repeating part to " +
                           "find. It will click.";

            _summary.ForeColor = _found.Match < 0.5 ? Color.FromArgb(160, 40, 40) : SystemColors.ControlText;
            _summary.Text = "Loop " + _found.From.ToString("N0") + " - " + _found.End.ToString("N0") +
                            "  (" + _found.Length.ToString("N0") + " words, " +
                            _found.Seconds(_entry.SampleRate).ToString("0.000", CultureInfo.InvariantCulture) +
                            " s)   -   match " +
                            _found.Match.ToString("0.000", CultureInfo.InvariantCulture) +
                            Environment.NewLine + verdict;
            _ok.Enabled = true;
        }

        /// <summary>The whole sample, with the loop shaded and its two edges marked.</summary>
        sealed class LoopPreview : Control
        {
            public short[] Words;
            public LoopChoice Choice;

            public LoopPreview()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw, true);
                BackColor = Color.FromArgb(24, 24, 28);
                ForeColor = Color.FromArgb(150, 210, 255);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);

                int w = Width, h = Height;
                if (Words == null || Words.Length == 0 || w < 4 || h < 4) return;

                g.SmoothingMode = SmoothingMode.None;
                float mid = h / 2f;

                if (Choice != null)
                {
                    int x0 = (int)((long)Choice.From * w / Words.Length);
                    int x1 = (int)((long)Choice.End * w / Words.Length);
                    using (var shade = new SolidBrush(Color.FromArgb(40, 90, 140, 200)))
                        g.FillRectangle(shade, x0, 0, Math.Max(1, x1 - x0), h);
                }

                int step = Math.Max(1, Words.Length / w);
                using (var pen = new Pen(ForeColor))
                {
                    for (int x = 0; x < w; x++)
                    {
                        int at = (int)((long)x * Words.Length / w);
                        int lo = 0, hi = 0;
                        for (int i = 0; i < step && at + i < Words.Length; i++)
                        {
                            short v = Words[at + i];
                            if (v < lo) lo = v;
                            if (v > hi) hi = v;
                        }
                        g.DrawLine(pen, x, mid - hi / 2048f * mid, x, mid - lo / 2048f * mid);
                    }
                }

                if (Choice == null) return;
                DrawMarker(g, Color.FromArgb(80, 200, 120), Choice.From, w, h);
                DrawMarker(g, Color.FromArgb(220, 110, 70), Choice.End, w, h);
            }

            void DrawMarker(Graphics g, Color c, int at, int w, int h)
            {
                int x = (int)((long)at * w / Words.Length);
                if (x >= w) x = w - 1;
                using (var pen = new Pen(c)) g.DrawLine(pen, x, 0, x, h);
            }
        }
    }
}
