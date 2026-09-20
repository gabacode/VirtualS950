using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AkaiS950List;

namespace AkaiS950Studio
{
    /// <summary>
    /// Fits a sample to a tempo. Either by stretching it in time, which keeps the pitch
    /// and changes the length, or by varispeed, which changes the rate it plays back at
    /// and moves the pitch with it but costs nothing and touches no audio.
    /// </summary>
    internal sealed class StretchDialog : Form
    {
        readonly AkaiDisk _disk;
        readonly AkaiEntry _entry;
        readonly short[] _words;
        readonly SamplePlayer _audio = new SamplePlayer();

        readonly NumericUpDown _from = new NumericUpDown();
        readonly NumericUpDown _to = new NumericUpDown();
        readonly NumericUpDown _beats = new NumericUpDown();
        readonly CheckBox _varispeed = new CheckBox();
        readonly Label _summary = new Label();
        readonly Button _ok = new Button();
        readonly Button _preview = new Button();

        short[] _result;                 // the stretched audio, once computed

        public bool UseVarispeed { get { return _varispeed.Checked; } }
        public short[] Words { get { return _result; } }
        public int NewRate { get { return (int)Math.Round(_entry.SampleRate * Ratio); } }

        /// <summary>Output length over input length. A faster target means a shorter sample.</summary>
        public double Ratio
        {
            get
            {
                double f = (double)_from.Value, t = (double)_to.Value;
                return (t <= 0 || f <= 0) ? 1 : f / t;
            }
        }

        public StretchDialog(AkaiDisk disk, AkaiEntry entry)
        {
            _disk = disk;
            _entry = entry;
            _words = disk.SampleWords12(entry);

            Text = "Fit " + entry.Name + " to a Tempo";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(500, 330);
            Font = SystemFonts.MessageBoxFont;

            Build();
            Recalculate();
        }

        void Build()
        {
            int y = 12;

            Controls.Add(new Label
            {
                Bounds = new Rectangle(12, y, 476, 34),
                Text = _entry.Name + ":  " + _entry.SampleCount.ToString("N0") + " words at " +
                       _entry.SampleRate.ToString("N0") + " Hz,  " +
                       _entry.Seconds.ToString("0.000", CultureInfo.InvariantCulture) + " s"
            });
            y += 40;

            Label(y + 3, "Beats in the sample");
            _beats.SetBounds(160, y, 70, 24);
            _beats.Minimum = 1;
            _beats.Maximum = 256;
            _beats.Value = 4;
            Controls.Add(_beats);

            var calc = new Button { Bounds = new Rectangle(238, y - 1, 110, 26), Text = "&Work out BPM" };
            calc.Click += (s, e) => WorkOutBpm();
            Controls.Add(calc);
            y += 32;

            Label(y + 3, "Sample is at");
            _from.SetBounds(160, y, 90, 24);
            Setup(_from);
            Controls.Add(_from);
            Controls.Add(new Label
            {
                Bounds = new Rectangle(256, y + 3, 60, 20),
                Text = "BPM",
                ForeColor = SystemColors.GrayText
            });
            y += 32;

            Label(y + 3, "Play it at");
            _to.SetBounds(160, y, 90, 24);
            Setup(_to);
            Controls.Add(_to);
            Controls.Add(new Label
            {
                Bounds = new Rectangle(256, y + 3, 60, 20),
                Text = "BPM",
                ForeColor = SystemColors.GrayText
            });
            y += 34;

            _varispeed.SetBounds(160, y, 330, 22);
            _varispeed.Text = "Change pitch instead (varispeed)";
            _varispeed.CheckedChanged += (s, e) => Recalculate();
            Controls.Add(_varispeed);
            y += 24;

            Controls.Add(new Label
            {
                Bounds = new Rectangle(178, y, 310, 32),
                Text = "Rewrites the playback rate only. No audio is changed and the\r\n" +
                       "file stays exactly the same size.",
                ForeColor = SystemColors.GrayText
            });
            y += 38;

            _summary.SetBounds(12, y, 476, 52);
            _summary.BorderStyle = BorderStyle.FixedSingle;
            _summary.Padding = new Padding(6, 5, 6, 5);
            Controls.Add(_summary);
            y += 60;

            _preview.SetBounds(12, y, 110, 28);
            _preview.Text = "&Preview";
            _preview.Click += (s, e) => Preview();
            Controls.Add(_preview);

            _ok.SetBounds(280, y, 100, 28);
            _ok.Text = "Apply";
            _ok.DialogResult = DialogResult.OK;
            _ok.Click += (s, e) => { if (!_varispeed.Checked) Compute(); };
            Controls.Add(_ok);

            var cancel = new Button
            {
                Bounds = new Rectangle(388, y, 100, 28),
                Text = "Cancel",
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(cancel);

            AcceptButton = _ok;
            CancelButton = cancel;
        }

        void Setup(NumericUpDown n)
        {
            n.DecimalPlaces = 2;
            n.Minimum = 20;
            n.Maximum = 400;
            n.Value = 120;
            n.ValueChanged += (s, e) => Recalculate();
        }

        void Label(int y, string text)
        {
            Controls.Add(new Label { Bounds = new Rectangle(12, y, 144, 20), Text = text });
        }

        /// <summary>A sample that is a whole number of beats states its own tempo.</summary>
        void WorkOutBpm()
        {
            double seconds = _entry.Seconds;
            if (seconds <= 0) return;

            decimal bpm = (decimal)((double)_beats.Value * 60.0 / seconds);
            _from.Value = Math.Max(_from.Minimum, Math.Min(_from.Maximum, Math.Round(bpm, 2)));
            Recalculate();
        }

        // ------------------------------------------------------------ arithmetic

        int NewWords { get { return (int)(_words.Length * Ratio) & ~1; } }

        void Recalculate()
        {
            var text = new System.Text.StringBuilder();
            bool ok;

            if (_varispeed.Checked)
            {
                int rate = NewRate;
                ok = rate >= 1000 && rate <= 48000;

                text.Append("Plays at ").Append(rate.ToString("N0")).Append(" Hz instead of ")
                    .Append(_entry.SampleRate.ToString("N0")).Append(" Hz,  ")
                    .Append((_words.Length / (double)Math.Max(1, rate)).ToString("0.000", CultureInfo.InvariantCulture))
                    .Append(" s");
                text.AppendLine();
                text.Append("Pitch moves by ")
                    .Append((12 * Math.Log(1 / Ratio, 2)).ToString("+0.00;-0.00;0", CultureInfo.InvariantCulture))
                    .Append(" semitones.  Same size, no blocks needed.");

                if (!ok)
                {
                    text.AppendLine();
                    text.Append(rate.ToString("N0") + " Hz is outside what the sampler uses.");
                }
            }
            else
            {
                int words = NewWords;
                int length = AkaiDisk.HeaderSize + words * 3 / 2;
                int have = AkaiDisk.BlocksFor(_entry.Length);
                int need = AkaiDisk.BlocksFor(length);
                int extra = need - have;                     // negative when it shrinks
                ok = words >= 2 && extra <= _disk.FreeBlocks;

                text.Append(_entry.SampleCount.ToString("N0")).Append(" words  ->  ")
                    .Append(words.ToString("N0")).Append(",  ")
                    .Append((words / (double)Math.Max(1, _entry.SampleRate)).ToString("0.000", CultureInfo.InvariantCulture))
                    .Append(" s at the same pitch");
                text.AppendLine();
                text.Append(extra > 0
                        ? "Needs " + extra + " more block" + (extra == 1 ? "" : "s") + ", " +
                          _disk.FreeBlocks.ToString("N0") + " free"
                        : extra < 0
                            ? "Frees " + (-extra) + " block" + (extra == -1 ? "" : "s")
                            : "Same number of blocks");

                if (!ok)
                {
                    text.AppendLine();
                    text.Append(words < 2
                        ? "Nothing would be left."
                        : "Too big for this disk - raise the target tempo, or free some space.");
                }
            }

            _summary.Text = text.ToString();
            _summary.ForeColor = ok ? SystemColors.ControlText : Color.FromArgb(168, 32, 32);
            _ok.Enabled = ok;
            _preview.Enabled = ok;
        }

        // ------------------------------------------------------------ conversion

        void Compute()
        {
            Cursor = Cursors.WaitCursor;
            try { _result = AudioImport.TimeStretch(_words, Ratio); }
            finally { Cursor = Cursors.Default; }
        }

        void Preview()
        {
            try
            {
                if (_varispeed.Checked)
                {
                    var pcm = new short[_words.Length];
                    for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(_words[i] * 16);
                    _audio.Play(pcm, NewRate);
                    return;
                }

                Compute();
                var outp = new short[_result.Length];
                for (int i = 0; i < outp.Length; i++) outp[i] = (short)(_result[i] * 16);
                _audio.Play(outp, _entry.SampleRate);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Preview failed",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _audio.Dispose();
            base.OnFormClosed(e);
        }
    }
}
