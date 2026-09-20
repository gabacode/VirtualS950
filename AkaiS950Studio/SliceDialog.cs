using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using AkaiS950List;

namespace AkaiS950Studio
{
    /// <summary>
    /// Cutting a break into one-shots.
    ///
    /// The detector is cheap enough to rerun on every twitch of the slider, so the markers
    /// and the cost are always live - and the cost matters, because slicing is the one
    /// operation that can run out of directory slots, disk blocks or sampler memory at the
    /// same time, and the sampler's memory is not something the disk can tell us.
    /// </summary>
    internal sealed class SliceDialog : Form
    {
        readonly AkaiDisk _disk;
        readonly AkaiEntry _entry;
        readonly short[] _words;

        readonly TrackBar _sens = new TrackBar();
        readonly Label _sensVal = new Label();
        readonly NumericUpDown _minMs = new NumericUpDown();
        readonly ComboBox _program = new ComboBox();
        readonly NumericUpDown _root = new NumericUpDown();
        readonly Label _rootName = new Label();
        readonly ComboBox _ram = new ComboBox();
        readonly Label _summary = new Label();
        readonly WavePreview _preview = new WavePreview();
        readonly Button _ok = new Button();

        List<int> _cuts = new List<int>();
        AkaiDisk.SlicePlan _plan;

        /// <summary>The cut points the user settled on.</summary>
        public List<int> Cuts { get { return _cuts; } }

        /// <summary>The options to hand to SliceSample.</summary>
        public AkaiDisk.SliceOptions Options
        {
            get
            {
                return new AkaiDisk.SliceOptions
                {
                    Program = SelectedProgram,
                    RootKey = (int)_root.Value,
                    MaxRam = SelectedRam,
                    Name = _entry.Name
                };
            }
        }

        public AkaiDisk.SlicePlan Plan { get { return _plan; } }

        public SliceDialog(AkaiDisk disk, AkaiEntry entry, short[] words)
        {
            _disk = disk;
            _entry = entry;
            _words = words;

            Text = "Slice " + entry.Name.Trim() + " into one-shots";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(700, 460);
            MinimumSize = new Size(640, 460);
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

            Controls.Add(new Label { Bounds = new Rectangle(12, y + 4, 90, 20), Text = "Sensitivity" });
            _sens.SetBounds(104, y, 300, 40);
            _sens.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _sens.Minimum = 0;
            _sens.Maximum = 100;
            _sens.TickFrequency = 10;
            _sens.Value = 50;
            _sens.ValueChanged += (s, e) => Refresh2();
            Controls.Add(_sens);

            _sensVal.SetBounds(410, y + 4, 40, 20);
            _sensVal.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(_sensVal);

            Controls.Add(new Label
            {
                Bounds = new Rectangle(460, y + 4, 110, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "Shortest slice"
            });
            _minMs.SetBounds(572, y + 2, 70, 24);
            _minMs.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _minMs.Minimum = 5;
            _minMs.Maximum = 1000;
            _minMs.Value = 40;
            _minMs.Increment = 5;
            _minMs.ValueChanged += (s, e) => Refresh2();
            Controls.Add(_minMs);
            Controls.Add(new Label
            {
                Bounds = new Rectangle(646, y + 6, 40, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                ForeColor = SystemColors.GrayText,
                Text = "ms"
            });

            y += 44;

            Controls.Add(new Label
            {
                Bounds = new Rectangle(12, y + 4, 90, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "Map into"
            });
            _program.SetBounds(104, y, 220, 24);
            _program.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _program.DropDownStyle = ComboBoxStyle.DropDownList;
            _program.Items.Add(new ProgramChoice(null));
            foreach (var p in disk.ProgramsInOrder()) _program.Items.Add(new ProgramChoice(p));
            _program.SelectedIndex = 0;
            _program.SelectedIndexChanged += (s, e) => Refresh2();
            Controls.Add(_program);

            Controls.Add(new Label
            {
                Bounds = new Rectangle(336, y + 4, 70, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "from key"
            });
            _root.SetBounds(408, y + 2, 60, 24);
            _root.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _root.Minimum = 0;
            _root.Maximum = 127;
            _root.Value = 36;                       // C1, where a sliced kit usually starts
            _root.ValueChanged += (s, e) => Refresh2();
            Controls.Add(_root);

            _rootName.SetBounds(474, y + 6, 90, 20);
            _rootName.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _rootName.ForeColor = SystemColors.GrayText;
            Controls.Add(_rootName);

            y += 32;

            Controls.Add(new Label
            {
                Bounds = new Rectangle(12, y + 4, 90, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "Sampler RAM"
            });
            _ram.SetBounds(104, y, 220, 24);
            _ram.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _ram.DropDownStyle = ComboBoxStyle.DropDownList;
            _ram.Items.Add(new RamChoice("750 KB  -  S900", 750 * 1024));
            _ram.Items.Add(new RamChoice("2,304 KB  -  S950 expanded", AkaiDisk.DefaultSamplerRam));
            _ram.SelectedIndex = 1;
            _ram.SelectedIndexChanged += (s, e) => Refresh2();
            Controls.Add(_ram);

            y += 36;

            _summary.SetBounds(12, y, 676, 40);
            _summary.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_summary);

            _ok.SetBounds(488, 420, 96, 28);
            _ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _ok.Text = "Slice";
            _ok.DialogResult = DialogResult.OK;
            Controls.Add(_ok);

            var cancel = new Button
            {
                Bounds = new Rectangle(592, 420, 96, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Text = "Cancel",
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(cancel);

            AcceptButton = _ok;
            CancelButton = cancel;

            Refresh2();
        }

        AkaiEntry SelectedProgram
        {
            get
            {
                var c = _program.SelectedItem as ProgramChoice;
                return c != null ? c.Entry : null;
            }
        }

        int SelectedRam
        {
            get
            {
                var c = _ram.SelectedItem as RamChoice;
                return c != null ? c.Bytes : AkaiDisk.DefaultSamplerRam;
            }
        }

        /// <summary>Named to stay clear of Control.Refresh.</summary>
        void Refresh2()
        {
            _cuts = AudioImport.DetectSlices(_words, _entry.SampleRate, _sens.Value, (int)_minMs.Value);

            var program = SelectedProgram;
            _plan = _disk.PlanSlices(_entry, _cuts, Options);

            _sensVal.Text = _sens.Value.ToString(CultureInfo.InvariantCulture);
            _rootName.Text = NoteName((int)_root.Value);

            _preview.Cuts = _plan.Slices.Select(s => s.Start).ToList();
            _preview.Invalidate();

            string note = _plan.Slices.Count + " slices  -  " +
                          _plan.Blocks + " of " + _disk.FreeBlocks + " free blocks, " +
                          _plan.Slots + " of " + _disk.FreeSlots() + " free slots, " +
                          (_plan.Ram / 1024) + " KB of " + (_plan.Spare / 1024) +
                          " KB free sampler memory";

            if (program != null)
                note += "  -  keys " + NoteName((int)_root.Value) + " upward in " + program.Name.Trim();

            _summary.Text = _plan.Ok ? note : string.Join("   ", _plan.Problems.ToArray());
            _summary.ForeColor = _plan.Ok ? SystemColors.GrayText : Color.FromArgb(168, 32, 32);
            _ok.Enabled = _plan.Ok;
        }

        static readonly string[] NoteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        static string NoteName(int note)
        {
            return NoteNames[((note % 12) + 12) % 12] + (note / 12 - 2);
        }

        sealed class ProgramChoice
        {
            public readonly AkaiEntry Entry;
            public ProgramChoice(AkaiEntry e) { Entry = e; }
            public override string ToString()
            {
                return Entry == null ? "(nothing - just add the samples)" : Entry.Name.Trim();
            }
        }

        sealed class RamChoice
        {
            readonly string _text;
            public readonly int Bytes;
            public RamChoice(string text, int bytes) { _text = text; Bytes = bytes; }
            public override string ToString() { return _text; }
        }

        /// <summary>The sample drawn as a min/max envelope, with a line at every cut.</summary>
        sealed class WavePreview : Control
        {
            public short[] Words;
            public List<int> Cuts = new List<int>();

            public WavePreview()
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

                using (var pen = new Pen(ForeColor))
                {
                    for (int x = 0; x < w; x++)
                    {
                        int from = (int)((long)x * Words.Length / w);
                        int to = (int)((long)(x + 1) * Words.Length / w);
                        if (to <= from) to = from + 1;
                        if (to > Words.Length) to = Words.Length;

                        int lo = 0, hi = 0;
                        for (int i = from; i < to; i++)
                        {
                            int v = Words[i];
                            if (v < lo) lo = v;
                            if (v > hi) hi = v;
                        }

                        float y0 = mid - hi / 2048f * (mid - 2);
                        float y1 = mid - lo / 2048f * (mid - 2);
                        g.DrawLine(pen, x, y0, x, y1);
                    }
                }

                using (var cut = new Pen(Color.FromArgb(255, 190, 80)))
                    foreach (int c in Cuts)
                    {
                        float x = (float)c * w / Words.Length;
                        g.DrawLine(cut, x, 0, x, h);
                    }
            }
        }
    }
}
