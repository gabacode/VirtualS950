using System;
using System.Drawing;
using System.Windows.Forms;
using AkaiS950List;

namespace AkaiS950Studio
{
    public sealed partial class MainForm
    {
        /*
         * The strip over the work area: what is selected, the handful of fields that
         * belong to it, and its size on the right.
         *
         * The web version puts these here rather than in a pane of their own, and it is
         * the right place for them: a programme has four editable fields and a sample has
         * five, which is a row, not a page. Putting them on the row is also what lets the
         * Summary and Edit tabs go - the tabs only existed because the fields needed
         * somewhere, and the web has no tabs at all.
         */

        readonly Panel _fileBar = new Panel();
        readonly Panel _programRow = new Panel();
        readonly Panel _sampleRow = new Panel();

        readonly Label _fileName = new Label();
        readonly Label _fileStats = new Label();

        // programme
        NumericUpDown _midiProgram, _keyToLoudness;
        CheckBox _crossfade;

        // sample
        NumericUpDown _samplePitch, _sampleFine, _sampleLoudness;
        ComboBox _loopMode, _loopDirection;

        ProgramEditor _headerProgram;
        SampleEditor _headerSample;
        bool _headerLoading;

        // ------------------------------------------------------------------ building

        /// <summary>A label and the control it names, laid out left to right.</summary>
        static int Place(Panel row, ref int x, string caption, Control c, int captionWidth, int width)
        {
            if (caption != null)
            {
                var l = new Label
                {
                    Text = caption,
                    AutoSize = false,
                    Width = captionWidth,
                    Height = 24,
                    Location = new Point(x, 5),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                row.Controls.Add(l);
                x += captionWidth;
            }

            c.Location = new Point(x, 4);
            c.Width = width;
            row.Controls.Add(c);
            x += width + 14;
            return x;
        }

        static NumericUpDown Number(int min, int max)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Height = 24 };
        }

        static Button BarButton(string text, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                Height = 24,
                FlatStyle = FlatStyle.System,
                TabStop = false
            };
            b.Click += onClick;
            return b;
        }

        Control BuildFileHeader()
        {
            _fileName.Font = new Font(Font, FontStyle.Bold);
            _fileName.AutoSize = false;
            _fileName.Width = 150;
            _fileName.Height = 24;
            _fileName.Location = new Point(6, 5);
            _fileName.TextAlign = ContentAlignment.MiddleLeft;

            _fileStats.Dock = DockStyle.Right;
            _fileStats.Width = 420;
            _fileStats.TextAlign = ContentAlignment.MiddleRight;
            _fileStats.Padding = new Padding(0, 0, 8, 0);
            _fileStats.ForeColor = SystemColors.GrayText;

            BuildProgramRow();
            BuildSampleRow();

            _fileBar.Dock = DockStyle.Top;
            _fileBar.Height = 34;

            // Fill first, then the pieces pinned to an edge.
            _fileBar.Controls.Add(_programRow);
            _fileBar.Controls.Add(_sampleRow);
            _fileBar.Controls.Add(_fileStats);
            _fileBar.Controls.Add(_fileName);
            return _fileBar;
        }

        void BuildProgramRow()
        {
            int x = 162;
            Place(_programRow, ref x, null, BarButton("Rename", (s, e) => RenameSelected()), 0, 72);
            Place(_programRow, ref x, null, BarButton("Delete", OnDeleteFile), 0, 66);

            _midiProgram = Number(1, 128);
            _keyToLoudness = Number(-50, 50);
            _crossfade = new CheckBox { Text = "Crossfade", Height = 24, AutoSize = false };

            Place(_programRow, ref x, "MIDI prog", _midiProgram, 74, 58);
            Place(_programRow, ref x, "Key to loudness", _keyToLoudness, 108, 58);
            Place(_programRow, ref x, null, _crossfade, 0, 90);

            _midiProgram.ValueChanged += (s, e) => ProgramEdited("MIDI program",
                p => p.MidiProgram = (int)_midiProgram.Value);
            _keyToLoudness.ValueChanged += (s, e) => ProgramEdited("key to loudness",
                p => p.KeyToLoudness = (int)_keyToLoudness.Value);
            _crossfade.CheckedChanged += (s, e) => ProgramEdited("crossfade",
                p => p.PositionalCrossfade = _crossfade.Checked);

            _programRow.Dock = DockStyle.Fill;
            _programRow.Visible = false;
        }

        void BuildSampleRow()
        {
            int x = 162;
            Place(_sampleRow, ref x, null, BarButton("Rename", (s, e) => RenameSelected()), 0, 72);
            Place(_sampleRow, ref x, null, BarButton("Delete", OnDeleteFile), 0, 66);

            _samplePitch = Number(0, 127);
            _sampleFine = Number(0, 15);
            _sampleLoudness = Number(-50, 50);

            _loopMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Height = 24 };
            _loopMode.Items.AddRange(new object[] { "one-shot", "looping", "alternating" });

            _loopDirection = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Height = 24 };
            _loopDirection.Items.AddRange(new object[] { "normal", "reverse" });

            Place(_sampleRow, ref x, "Pitch", _samplePitch, 44, 58);
            Place(_sampleRow, ref x, "Fine", _sampleFine, 36, 52);
            Place(_sampleRow, ref x, "Loudness", _sampleLoudness, 68, 58);
            Place(_sampleRow, ref x, "Loop", _loopMode, 40, 100);
            Place(_sampleRow, ref x, "Dir", _loopDirection, 30, 90);

            _samplePitch.ValueChanged += (s, e) => SampleEdited("pitch",
                v => v.NominalPitch = (int)_samplePitch.Value);
            _sampleFine.ValueChanged += (s, e) => SampleEdited("fine pitch",
                v => v.FinePitch = (int)_sampleFine.Value);
            _sampleLoudness.ValueChanged += (s, e) => SampleEdited("loudness",
                v => v.Loudness = (int)_sampleLoudness.Value);

            // The three letters the format stores, behind words that say what they do.
            _loopMode.SelectedIndexChanged += (s, e) => SampleEdited("loop mode",
                v => v.LoopMode = "OLA"[Math.Max(0, _loopMode.SelectedIndex)]);
            _loopDirection.SelectedIndexChanged += (s, e) => SampleEdited("loop direction",
                v => v.LoopDirection = "NR"[Math.Max(0, _loopDirection.SelectedIndex)]);

            _sampleRow.Dock = DockStyle.Fill;
            _sampleRow.Visible = false;
        }

        // ------------------------------------------------------------------- showing

        /// <summary>Put the selected file on the header strip.</summary>
        void ShowFileHeader(FileRef f)
        {
            _headerLoading = true;
            try
            {
                _headerProgram = null;
                _headerSample = null;
                _programRow.Visible = false;
                _sampleRow.Visible = false;

                if (f == null)
                {
                    _fileName.Text = "";
                    _fileStats.Text = "";
                    return;
                }

                AkaiEntry e = f.Entry;
                _fileName.Text = e.Name.Trim();

                if (e.Type == 'P')
                {
                    _headerProgram = new ProgramEditor(f.Disk, e);
                    _midiProgram.Value = Clamp(_headerProgram.MidiProgram, 1, 128);
                    _keyToLoudness.Value = Clamp(_headerProgram.KeyToLoudness, -50, 50);
                    _crossfade.Checked = _headerProgram.PositionalCrossfade;

                    _fileStats.Text =
                        AkaiDisk.KeygroupCount(e) + " keygroups  ·  " +
                        e.Length.ToString("N0") + " bytes  ·  " +
                        e.ChainBlocks + (e.ChainBlocks == 1 ? " block" : " blocks") + "  ·  " +
                        "load " + _headerProgram.LoadAddress + "  ·  " +
                        _headerProgram.WrittenBy;

                    _programRow.Visible = true;
                }
                else if (e.Type == 'S')
                {
                    _headerSample = new SampleEditor(f.Disk, e);
                    _samplePitch.Value = Clamp(_headerSample.NominalPitch, 0, 127);
                    _sampleFine.Value = Clamp(_headerSample.FinePitch, 0, 15);
                    _sampleLoudness.Value = Clamp(_headerSample.Loudness, -50, 50);
                    _loopMode.SelectedIndex = Math.Max(0, "OLA".IndexOf(_headerSample.LoopMode));
                    _loopDirection.SelectedIndex = Math.Max(0, "NR".IndexOf(_headerSample.LoopDirection));

                    _fileStats.Text =
                        e.SampleCount.ToString("N0") + " words  ·  " +
                        e.SampleRate.ToString("N0") + " Hz  ·  " +
                        e.Seconds.ToString("0.00") + " s  ·  " +
                        e.Length.ToString("N0") + " bytes";

                    _sampleRow.Visible = true;
                }
                else
                {
                    _fileStats.Text = e.Length.ToString("N0") + " bytes";
                }
            }
            finally { _headerLoading = false; }
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        void ProgramEdited(string what, Action<ProgramEditor> apply)
        {
            if (_headerLoading || _headerProgram == null) return;
            CaptureBaseline();
            apply(_headerProgram);
            AfterPaneEdit(what);
        }

        void SampleEdited(string what, Action<SampleEditor> apply)
        {
            if (_headerLoading || _headerSample == null) return;
            CaptureBaseline();
            apply(_headerSample);
            AfterPaneEdit(what);

            // The waveform draws the loop, so a change to it has to be redrawn.
            var f = SelectedFile;
            if (f != null && f.Entry.Type == 'S') ShowWaveform(f.Disk, f.Entry);
        }

        void RenameSelected()
        {
            var f = SelectedFile;
            if (f != null) RenameFile(f);
        }
    }
}
