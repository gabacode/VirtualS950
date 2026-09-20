using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AkaiS950List;

namespace AkaiS950Studio
{
    /// <summary>
    /// A labelled slider with its number beside it: "Filter [=====----] 99".
    ///
    /// The web version puts every keygroup value on one of these, and they are worth
    /// copying rather than leaving the property grid in place. A grid is a list of names
    /// you read; a wall of sliders is a picture of a patch you can take in at a glance and
    /// drag without first finding the right row. It is also the difference between the two
    /// programs that someone moving between them would notice first.
    /// </summary>
    internal sealed class SliderRow : Panel
    {
        readonly Label _name = new Label();
        readonly TrackBar _bar = new TrackBar();
        readonly Label _number = new Label();

        // Set while a value is being loaded in, so filling the pane does not look like
        // three dozen edits.
        bool _loading;

        public event EventHandler Changed;
        public event EventHandler DragStarted;

        public SliderRow(string text, int min, int max, int nameWidth)
        {
            Height = 26;

            _name.Text = text;
            _name.Dock = DockStyle.Left;
            _name.Width = nameWidth;
            _name.TextAlign = ContentAlignment.MiddleLeft;

            _number.Dock = DockStyle.Right;
            _number.Width = 34;
            _number.TextAlign = ContentAlignment.MiddleRight;

            // A TrackBar is 45 pixels tall and covered in ticks by default, which would
            // make a column of them twice the height of the web's for no added meaning.
            _bar.Dock = DockStyle.Fill;
            _bar.AutoSize = false;
            _bar.Height = 24;
            _bar.TickStyle = TickStyle.None;
            _bar.Minimum = min;
            _bar.Maximum = max;
            _bar.SmallChange = 1;
            _bar.LargeChange = Math.Max(1, (max - min) / 10);
            _bar.TabStop = false;

            _bar.ValueChanged += (s, e) =>
            {
                _number.Text = _bar.Value.ToString();
                if (_loading) return;
                var h = Changed;
                if (h != null) h(this, EventArgs.Empty);
            };

            // The "before" for undo has to be taken as the drag begins, not after it.
            _bar.MouseDown += (s, e) => { var h = DragStarted; if (h != null) h(this, EventArgs.Empty); };
            _bar.KeyDown += (s, e) => { var h = DragStarted; if (h != null) h(this, EventArgs.Empty); };

            Controls.Add(_bar);
            Controls.Add(_number);
            Controls.Add(_name);
        }

        public int Value
        {
            get { return _bar.Value; }
            set
            {
                int v = value < _bar.Minimum ? _bar.Minimum
                      : (value > _bar.Maximum ? _bar.Maximum : value);
                _loading = true;
                try { _bar.Value = v; _number.Text = v.ToString(); }
                finally { _loading = false; }
            }
        }
    }

    public sealed partial class MainForm
    {
        // ------------------------------------------------------------- the pane itself

        Panel _kgButtons;                              // Add / Delete under the keygroup list

        readonly Panel _kgPane = new Panel();          // middle column: envelopes, velocity, LFO
        readonly Panel _zonePane = new Panel();        // right column: the two zones and the flags

        readonly Label _kgPaneHeader = new Label();
        readonly Label _keyRangeText = new Label();

        readonly ComboBox _zone1Sample = new ComboBox();
        readonly ComboBox _zone2Sample = new ComboBox();
        readonly Label _zone2Header = new Label();

        readonly CheckBox _flagConstantPitch = new CheckBox();
        readonly CheckBox _flagLfoDesync = new CheckBox();
        readonly CheckBox _flagOneShot = new CheckBox();

        /// <summary>One slider and the keygroup property it stands for.</summary>
        sealed class Bound
        {
            public SliderRow Row;
            public string What;
            public Func<KeygroupEditor, int> Get;
            public Action<KeygroupEditor, int> Set;
        }

        readonly List<Bound> _bound = new List<Bound>();

        // The keygroup the pane is showing. Rebuilt whenever the selection moves, so the
        // rows talk to the current one rather than to whatever was selected when they
        // were made.
        KeygroupEditor _paneKeygroup;
        bool _paneLoading;

        // ---------------------------------------------------------------- construction

        SliderRow Slider(Panel into, string text, int min, int max, string what,
                         Func<KeygroupEditor, int> get, Action<KeygroupEditor, int> set)
        {
            var row = new SliderRow(text, min, max, 128);
            var bound = new Bound { Row = row, What = what, Get = get, Set = set };

            row.DragStarted += (s, e) => CaptureBaseline();
            row.Changed += (s, e) =>
            {
                if (_paneLoading || _paneKeygroup == null) return;
                bound.Set(_paneKeygroup, row.Value);
                AfterPaneEdit(bound.What);
            };

            _bound.Add(bound);
            return row;
        }

        /// <summary>
        /// Stack controls downward in the order given.
        ///
        /// Docking resolves from the last-added control backwards, so a column of
        /// Dock.Top controls comes out upside down unless they are added in reverse. The
        /// height is added up rather than left to AutoSize, because a docked panel that
        /// sizes itself is one more thing to be wrong about.
        /// </summary>
        static Panel Stack(params Control[] children)
        {
            var p = new Panel { Dock = DockStyle.Top };
            int h = 0;

            for (int i = children.Length - 1; i >= 0; i--)
            {
                children[i].Dock = DockStyle.Top;
                p.Controls.Add(children[i]);
                h += children[i].Height;
            }

            p.Height = h;
            return p;
        }

        Label GroupHeader(string text)
        {
            return new Label
            {
                Text = text,
                Height = 26,
                Font = new Font(Font, FontStyle.Bold),
                TextAlign = ContentAlignment.BottomLeft,
                Padding = new Padding(0, 4, 0, 0)
            };
        }

        static CheckBox Flag(string text)
        {
            return new CheckBox { Text = text, Height = 24, AutoSize = false };
        }

        /// <summary>
        /// The middle column: which keygroup, its two envelopes, its key range, and the
        /// velocity and LFO sensitivities side by side as the web has them.
        /// </summary>
        Control BuildKeygroupPane(Control envelopes)
        {
            _kgPaneHeader.Dock = DockStyle.Top;
            _kgPaneHeader.Text = "Keygroup";
            _kgPaneHeader.Height = 30;
            _kgPaneHeader.Font = new Font(Font, FontStyle.Bold);
            _kgPaneHeader.TextAlign = ContentAlignment.MiddleLeft;

            _keyRangeText.Height = 24;
            _keyRangeText.TextAlign = ContentAlignment.MiddleLeft;
            _keyRangeText.Text = "-";

            var fromKeyboard = new Button
            {
                Text = "From keyboard",
                Height = 24,
                Dock = DockStyle.Right,
                Width = 110,
                FlatStyle = FlatStyle.System,
                TabStop = false
            };
            fromKeyboard.Click += (s, e) => _piano.ArmRange();

            var keyRangeRow = new Panel { Height = 24 };
            keyRangeRow.Controls.Add(_keyRangeText);
            keyRangeRow.Controls.Add(fromKeyboard);
            _keyRangeText.Dock = DockStyle.Fill;

            // Left half: the key range and what velocity reaches.
            var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 10, 0) };
            left.Controls.Add(Stack(
                GroupHeader("Key range"),
                keyRangeRow,
                GroupHeader("Velocity"),
                Slider(left, "Velocity switch", 1, 128, "velocity switch",
                       k => k.VelocitySwitch, (k, v) => k.VelocitySwitch = v),
                Slider(left, "To loudness", 0, 99, "velocity to loudness",
                       k => k.ToLoudness, (k, v) => k.ToLoudness = v),
                Slider(left, "To filter", 0, 99, "velocity to filter",
                       k => k.ToFilter, (k, v) => k.ToFilter = v),
                Slider(left, "To attack", 0, 99, "velocity to attack",
                       k => k.ToAttack, (k, v) => k.ToAttack = v),
                Slider(left, "To release", -50, 50, "velocity to release",
                       k => k.ToRelease, (k, v) => k.ToRelease = v),
                Slider(left, "Key to filter", 0, 99, "key to filter",
                       k => k.KeyToFilter, (k, v) => k.KeyToFilter = v)));

            // Right half: the vibrato.
            var right = new Panel { Dock = DockStyle.Right, Width = 300 };
            right.Controls.Add(Stack(
                GroupHeader("LFO"),
                Slider(right, "Delay", 0, 99, "LFO delay",
                       k => k.LfoDelay, (k, v) => k.LfoDelay = v),
                Slider(right, "Rate", 0, 99, "LFO rate",
                       k => k.LfoRate, (k, v) => k.LfoRate = v),
                Slider(right, "Depth", 0, 99, "LFO depth",
                       k => k.LfoDepth, (k, v) => k.LfoDepth = v),
                Slider(right, "From aftertouch", 0, 50, "LFO from aftertouch",
                       k => k.FromAftertouch, (k, v) => k.FromAftertouch = v),
                Slider(right, "From modwheel", 0, 50, "LFO from modwheel",
                       k => k.FromModwheel, (k, v) => k.FromModwheel = v)));

            var below = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 0, 4, 0) };
            below.Controls.Add(left);
            below.Controls.Add(right);

            envelopes.Dock = DockStyle.Top;
            envelopes.Height = 300;

            // Clear of the header: each envelope draws its own caption in a band across
            // its top, and without this the two lines of text share a row.
            envelopes.Padding = new Padding(0, 6, 0, 0);

            _kgPane.Dock = DockStyle.Fill;
            _kgPane.AutoScroll = true;
            _kgPane.Padding = new Padding(4, 0, 0, 0);

            // Fill first, then the pieces that pin to the top, so docking resolves the
            // header above the envelopes above the rest.
            _kgPane.Controls.Add(below);
            _kgPane.Controls.Add(envelopes);
            _kgPane.Controls.Add(_kgPaneHeader);
            return _kgPane;
        }

        /// <summary>The right column: both zones and the three flags.</summary>
        Control BuildZonePane()
        {
            _zone1Sample.Height = 24;
            _zone1Sample.DropDownStyle = ComboBoxStyle.DropDown;
            _zone1Sample.SelectedIndexChanged += (s, e) => ZoneSampleChosen(1, _zone1Sample);
            _zone1Sample.Enter += (s, e) => CaptureBaseline();

            _zone2Sample.Height = 24;
            _zone2Sample.DropDownStyle = ComboBoxStyle.DropDown;
            _zone2Sample.SelectedIndexChanged += (s, e) => ZoneSampleChosen(2, _zone2Sample);
            _zone2Sample.Enter += (s, e) => CaptureBaseline();

            _zone2Header.Text = "Zone 2";
            _zone2Header.Height = 26;
            _zone2Header.Font = new Font(Font, FontStyle.Bold);
            _zone2Header.TextAlign = ContentAlignment.BottomLeft;
            _zone2Header.Padding = new Padding(0, 4, 0, 0);

            _flagConstantPitch.Text = "Constant pitch";
            _flagLfoDesync.Text = "LFO desync";
            _flagOneShot.Text = "One shot";

            foreach (var c in new[] { _flagConstantPitch, _flagLfoDesync, _flagOneShot })
            {
                c.Height = 24;
                c.AutoSize = false;
                var box = c;
                box.Click += (s, e) => CaptureBaseline();
                box.CheckedChanged += (s, e) =>
                {
                    if (_paneLoading || _paneKeygroup == null) return;

                    if (box == _flagConstantPitch) _paneKeygroup.ConstantPitch = box.Checked;
                    else if (box == _flagLfoDesync) _paneKeygroup.LfoDesync = box.Checked;
                    else _paneKeygroup.OneShot = box.Checked;

                    AfterPaneEdit(box.Text.ToLowerInvariant());
                };
            }

            var body = Stack(
                GroupHeader("Zone 1 - sample"),
                _zone1Sample,
                Slider(_zonePane, "Filter", 0, 99, "zone 1 filter",
                       k => k.Filter1, (k, v) => k.Filter1 = v),
                Slider(_zonePane, "Loudness", -50, 50, "zone 1 loudness",
                       k => k.Loudness1, (k, v) => k.Loudness1 = v),
                Slider(_zonePane, "Transpose", -50, 50, "zone 1 transpose",
                       k => k.Transpose1, (k, v) => k.Transpose1 = v),
                Slider(_zonePane, "Fine", 0, 255, "zone 1 fine",
                       k => k.Fine1, (k, v) => k.Fine1 = v),

                _zone2Header,
                _zone2Sample,
                Slider(_zonePane, "Filter", 0, 99, "zone 2 filter",
                       k => k.Filter2, (k, v) => k.Filter2 = v),
                Slider(_zonePane, "Loudness", -50, 50, "zone 2 loudness",
                       k => k.Loudness2, (k, v) => k.Loudness2 = v),
                Slider(_zonePane, "Transpose", -50, 50, "zone 2 transpose",
                       k => k.Transpose2, (k, v) => k.Transpose2 = v),
                Slider(_zonePane, "Fine", 0, 255, "zone 2 fine",
                       k => k.Fine2, (k, v) => k.Fine2 = v),

                GroupHeader("Flags"),
                _flagConstantPitch,
                _flagLfoDesync,
                _flagOneShot);

            _zonePane.Dock = DockStyle.Right;
            _zonePane.Width = 330;
            _zonePane.AutoScroll = true;
            _zonePane.Padding = new Padding(8, 0, 4, 0);
            _zonePane.Controls.Add(body);
            return _zonePane;
        }

        /// <summary>Add a keygroup after the one selected, or delete it.</summary>
        void OnKeygroupButton(bool add)
        {
            var f = SelectedFile;
            if (f == null || f.Entry.Type != (char)80) return;

            var picked = SelectedKeygroups();
            int row = picked.Count > 0 ? picked[0] : AkaiDisk.KeygroupCount(f.Entry) - 1;

            if (add) AddKeygroup(f, row); else DeleteKeygroup(f, row);
        }

        // -------------------------------------------------------------------- loading

        /// <summary>
        /// Put a keygroup's values into the pane.
        ///
        /// Every row is filled with events held down, because a slider being given its
        /// value raises the same event as a slider being dragged, and a pane full of them
        /// would otherwise write the keygroup back over itself on the way in.
        /// </summary>
        void LoadKeygroupPane(KeygroupEditor kg, AkaiDisk disk, int index, int count)
        {
            _paneKeygroup = kg;
            _paneLoading = true;
            try
            {
                bool have = kg != null;
                _kgPane.Enabled = have;
                _zonePane.Enabled = have;

                _kgPaneHeader.Text = have
                    ? "Keygroup  -  " + (index + 1) + " of " + count
                    : "Keygroup";

                if (!have) return;

                foreach (Bound b in _bound) b.Row.Value = b.Get(kg);

                _keyRangeText.Text = PianoKeyboard.NameOf(kg.LowKey) + " - " +
                                     PianoKeyboard.NameOf(kg.HighKey) +
                                     "   (" + kg.LowKey + " - " + kg.HighKey + ")";

                FillSamples(_zone1Sample, disk, kg.Sample1);
                FillSamples(_zone2Sample, disk, kg.Sample2);

                // The panel turns the second zone off with a switch of 128, and saying so
                // is worth more than leaving its controls looking live.
                _zone2Header.Text = kg.VelocitySwitch >= 128
                    ? "Zone 2 - unused (velocity switch off)"
                    : "Zone 2 - sample";

                _flagConstantPitch.Checked = kg.ConstantPitch;
                _flagLfoDesync.Checked = kg.LfoDesync;
                _flagOneShot.Checked = kg.OneShot;
            }
            finally { _paneLoading = false; }
        }

        static void FillSamples(ComboBox box, AkaiDisk disk, string current)
        {
            box.Items.Clear();
            box.Items.Add("(none)");

            if (disk != null)
                foreach (AkaiEntry e in disk.Entries)
                    if (e.Type == 'S') box.Items.Add(e.Name.Trim());

            // An unused zone still has bytes in it, and they are not always blank -
            // the panel leaves whatever was there last. A name that is not a sample on
            // this disk names nothing, so say so rather than showing the leftovers.
            string want = (current ?? "").Trim();
            box.Text = want.Length > 0 && box.Items.IndexOf(want) > 0 ? want : "(none)";
        }

        void ZoneSampleChosen(int zone, ComboBox box)
        {
            if (_paneLoading || _paneKeygroup == null) return;

            string name = box.SelectedItem as string ?? box.Text;
            if (name == "(none)") name = "";

            if (zone == 1) _paneKeygroup.Sample1 = name;
            else _paneKeygroup.Sample2 = name;

            AfterPaneEdit("zone " + zone + " sample");
        }

        /// <summary>
        /// One edit made in the pane, recorded and shown, exactly as the property grid's
        /// own change handler does it - the same bookkeeping, reached a different way.
        /// </summary>
        void AfterPaneEdit(string what)
        {
            if (_editDisk == null) return;

            if (_baselineDisk == _editDisk) PushUndo(_editDisk, what, _baseline, _baselineModified);
            CaptureBaseline();

            _editDisk.Modified = true;
            _editDisk.ParseDirectory();

            var f = SelectedFile;
            if (f != null && f.Entry.Type == 'P')
            {
                int keep = _keygroups.SelectedIndices.Count > 0 ? _keygroups.SelectedIndices[0] : -1;
                ShowKeygroups(f, AkaiDisk.KeygroupCount(f.Entry));
                if (keep >= 0 && keep < _keygroups.Items.Count) SelectKeygroupRow(keep);
            }

            RefreshTreeLabels();
            UpdateCommands();
            SetStatus("Edited " + what + ".  Unsaved changes.");
        }
    }
}
