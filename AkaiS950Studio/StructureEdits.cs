using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using AkaiS950List;

namespace AkaiS950Studio
{
    /// <summary>
    /// The commands that add or remove whole files: deleting a sample, creating and
    /// deleting programs, and slicing a break into one-shots.
    ///
    /// They differ from the parameter edits in that the directory itself changes, so each
    /// one rebuilds the tree and reselects afterwards rather than refreshing the panes in
    /// place. All of them go through PushUndo first, so any of them can be taken back.
    /// </summary>
    public sealed partial class MainForm
    {
        // ------------------------------------------------- the key range, by pointing

        /// <summary>
        /// Escape calls off an armed keyboard wherever the focus happens to be, which is
        /// what anyone expects of a mode they have just entered by accident.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && _piano != null && _piano.RangeArmed)
            {
                _piano.CancelRange();
                SetStatus("Key range left as it was.");
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }


        /// <summary>
        /// Arms the keyboard: the next click on it is the low key of the selected
        /// keygroups, and the one after it the high. Two boxes wanting MIDI numbers are a
        /// poor way to say something the keyboard says better.
        /// </summary>
        void ArmKeyRange()
        {
            var f = SelectedFile;
            if (f == null || f.Entry.Type != 'P') return;
            if (SelectedKeygroups().Count == 0) return;

            _piano.ArmRange();
            SetStatus("Click the low key on the keyboard, then the high key.  " +
                      "The same key twice gives a one-key group; Esc or a right-click cancels.");
        }

        /// <summary>
        /// Both ends, clicked. They go in under one undo - a range is a single edit to
        /// anyone using it - and reach every selected keygroup.
        /// </summary>
        void OnRangePicked(int low, int high)
        {
            var f = SelectedFile;
            if (f == null || f.Entry.Type != 'P') return;

            var picked = SelectedKeygroups();
            if (picked.Count == 0) return;

            int count = AkaiDisk.KeygroupCount(f.Entry);
            PushUndo(f.Disk, picked.Count > 1 ? "key range on " + picked.Count + " keygroups"
                                             : "key range on keygroup " + (picked[0] + 1));
            foreach (int i in picked)
            {
                if (i < 0 || i >= count) continue;
                int at = AkaiDisk.ProgHeaderSize + i * AkaiDisk.KeygroupSize;
                f.Disk.PokeFile(f.Entry, at + 1, (byte)low);     // low key
                f.Disk.PokeFile(f.Entry, at + 0, (byte)high);    // high key
            }

            _editDisk = f.Disk;
            f.Disk.Modified = true;
            ShowFile(f);
            UpdateCommands();

            SetStatus((picked.Count > 1 ? picked.Count + " keygroups cover "
                                        : "Keygroup " + (picked[0] + 1) + " covers ") +
                      PianoKeyboard.NameOf(low) + " - " + PianoKeyboard.NameOf(high) +
                      (low == high ? "  (one key)" : "  (" + (high - low + 1) + " keys)") +
                      ".  Unsaved changes.");
        }

        // ---------------------------------------------------------- finding a loop

        /// <summary>
        /// Finds a loop in a sample and writes it: the end, the length and the mode.
        ///
        /// A loop is a header edit rather than a change to the audio, but setting the
        /// mode moves the loop descriptor pointers of every later sample, so this
        /// rebuilds and reselects the way the structural commands do rather than
        /// refreshing the panes in place.
        /// </summary>
        void FindLoopFor(FileRef f)
        {
            if (f == null || f.Entry.Type != 'S') return;
            var e = f.Entry;

            short[] words;
            try { words = f.Disk.SampleWords12(e); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not read the sample",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (words == null || words.Length < 512)
            {
                MessageBox.Show(this, e.Name.Trim() + " is too short to loop.",
                                "Find loop", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new LoopDialog(e, words))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Found == null) return;
                var got = dlg.Found;

                try
                {
                    _audio.Stop();
                    PushUndo(f.Disk, "loop " + e.Name);
                    f.Disk.SetLoop(e, got.End, got.Length, dlg.LoopMode);

                    _editDisk = f.Disk;
                    int slot = e.Slot;
                    RebuildTree();
                    foreach (var fresh in f.Disk.Entries)
                        if (fresh.Slot == slot) { SelectFileNode(f.Disk, fresh); break; }

                    UpdateCommands();
                    SetStatus("Looped " + e.Name.Trim() + " over the last " +
                              got.Length.ToString("N0") + " words (" +
                              got.Seconds(e.SampleRate).ToString("0.000",
                                  CultureInfo.InvariantCulture) + " s), match " +
                              got.Match.ToString("0.000", CultureInfo.InvariantCulture) +
                              ".  Unsaved changes.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not set that loop",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ------------------------------------------------------- deleting a sample

        /// <summary>
        /// Deletes a sample, after naming every keygroup zone that will be emptied by it.
        /// That list is the whole point of the confirmation: a sample is referenced by
        /// name from programs that may not be on screen, and the S950 gives no hint that
        /// a zone has gone quiet.
        /// </summary>
        void DeleteSampleFile(FileRef f)
        {
            var e = f.Entry;
            var users = f.Disk.SampleUsers(e.Name);

            string where;
            if (users.Count == 0)
                where = "No keygroup on this disk refers to it.";
            else
            {
                var lines = users.Take(8).Select(u => "    " + u.ToString()).ToList();
                if (users.Count > 8) lines.Add("    ... and " + (users.Count - 8) + " more");
                where = "These " + users.Count + " keygroup zone" + (users.Count == 1 ? "" : "s") +
                        " will be emptied:" + Environment.NewLine + string.Join(Environment.NewLine, lines.ToArray());
            }

            int blocks = AkaiDisk.BlocksFor(e.Length);

            var ask = MessageBox.Show(this,
                "Delete " + e.Name.Trim() + "?" + Environment.NewLine + Environment.NewLine +
                "Words     " + e.SampleCount.ToString("N0") + "  (" +
                e.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s at " +
                e.SampleRate.ToString("N0") + " Hz)" + Environment.NewLine +
                "Frees     " + blocks + " block" + (blocks == 1 ? "" : "s") +
                Environment.NewLine + Environment.NewLine +
                where + Environment.NewLine + Environment.NewLine +
                "Samples after it move down in sampler memory and the zones pointing at " +
                "them are pulled back to match." + Environment.NewLine + Environment.NewLine +
                "Go ahead?",
                "Delete sample", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (ask != DialogResult.OK) return;

            try
            {
                _audio.Stop();
                PushUndo(f.Disk, "delete " + e.Name.Trim());

                string gone = e.Name.Trim();
                var r = f.Disk.DeleteSample(e);

                _editDisk = f.Disk;
                RebuildTree();
                SelectDiskNode(f.Disk);
                UpdateCommands();

                SetStatus("Deleted " + gone + "  -  " + r.ZonesCleared + " zone" +
                          (r.ZonesCleared == 1 ? "" : "s") + " emptied, " +
                          r.PointersAdjusted + " pointer" + (r.PointersAdjusted == 1 ? "" : "s") +
                          " adjusted, " + r.BlocksFreed + " block" + (r.BlocksFreed == 1 ? "" : "s") +
                          " freed, " + f.Disk.FreeBlocks + " now free.  Unsaved changes.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not delete the sample",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ------------------------------------------------------ programs

        /// <summary>
        /// Creates a program holding one empty keygroup across the whole keyboard, ready
        /// to have zones pointed at samples.
        /// </summary>
        void CreateProgram(AkaiDisk d)
        {
            if (d == null)
            {
                MessageBox.Show(this, "Select a disk first - a new program has to go somewhere.",
                                "New program", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new NewProgramDialog(d))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    PushUndo(d, "new program");
                    var p = d.AddProgram(dlg.ChosenName, new AkaiDisk.NewProgram
                    {
                        ProgramNumber = dlg.ProgramNumber
                    });

                    _editDisk = d;
                    RebuildTree();
                    if (p != null) SelectFileNode(d, p);
                    UpdateCommands();

                    SetStatus("Created program " + dlg.ChosenName + "  -  one keygroup, MIDI program " +
                              (dlg.ProgramNumber + 1) + ", " + d.FreeBlocks +
                              " blocks free.  Point its zones at a sample to hear it.  Unsaved changes.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not create the program",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// Deletes a program. Nothing on the disk refers to a program, so this is the one
        /// structural delete with no references to chase - only the samples it played are
        /// left behind, untouched.
        /// </summary>
        void DeleteProgramFile(FileRef f)
        {
            var e = f.Entry;
            int keygroups = AkaiDisk.KeygroupCount(e);
            var samples = f.Disk.ReferencedSamples(e);

            var ask = MessageBox.Show(this,
                "Delete the program " + e.Name.Trim() + "?" + Environment.NewLine + Environment.NewLine +
                "Keygroups " + keygroups + Environment.NewLine +
                "Frees     " + AkaiDisk.BlocksFor(e.Length) + " blocks" +
                Environment.NewLine + Environment.NewLine +
                (samples.Count == 0
                    ? "It names no samples."
                    : "The " + samples.Count + " sample" + (samples.Count == 1 ? "" : "s") +
                      " it plays stay on the disk.") +
                Environment.NewLine + Environment.NewLine +
                "Go ahead?",
                "Delete program", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (ask != DialogResult.OK) return;

            try
            {
                _audio.Stop();
                PushUndo(f.Disk, "delete " + e.Name.Trim());

                string gone = e.Name.Trim();
                int freed = f.Disk.DeleteProgram(e);

                _editDisk = f.Disk;
                RebuildTree();
                SelectDiskNode(f.Disk);
                UpdateCommands();

                SetStatus("Deleted the program " + gone + "  -  " + freed + " block" +
                          (freed == 1 ? "" : "s") + " freed, " + f.Disk.FreeBlocks +
                          " now free.  Unsaved changes.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not delete the program",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ------------------------------------------------------------- slicing

        /// <summary>
        /// Cuts a sample into one-shots on its detected onsets, optionally mapping them to
        /// consecutive keys of a program. The dialog costs the whole plan before anything
        /// is written, because this is the operation that can exhaust three limits at once.
        /// </summary>
        void SliceSampleFile(FileRef f)
        {
            var e = f.Entry;
            short[] words;

            try { words = f.Disk.SampleWords12(e); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not read the sample",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (words.Length < 16)
            {
                MessageBox.Show(this, e.Name.Trim() + " is too short to cut up.",
                                "Slice", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new SliceDialog(f.Disk, e, words))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    Cursor = Cursors.WaitCursor;
                    _audio.Stop();
                    PushUndo(f.Disk, "slice " + e.Name.Trim());

                    AkaiDisk.SliceResult r;
                    try { r = f.Disk.SliceSample(e, dlg.Cuts, dlg.Options); }
                    finally { Cursor = Cursors.Default; }

                    _editDisk = f.Disk;
                    RebuildTree();
                    SelectDiskNode(f.Disk);
                    UpdateCommands();

                    SetStatus("Sliced " + e.Name.Trim() + " into " + r.Added.Count + " samples" +
                              (r.Keygroups > 0
                                  ? ", mapped to " + r.Keygroups + " keygroups from " + NoteName(r.RootKey)
                                  : "") +
                              "  -  " + f.Disk.FreeBlocks + " blocks and " + f.Disk.FreeSlots() +
                              " slots left.  Unsaved changes.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not slice the sample",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    /// <summary>
    /// Asks for the name and MIDI program number of a new program. The number defaults to
    /// the lowest the disk is not already using, so two programs do not answer to one
    /// program change by accident.
    /// </summary>
    internal sealed class NewProgramDialog : Form
    {
        readonly TextBox _name = new TextBox();
        readonly NumericUpDown _number = new NumericUpDown();
        readonly Label _note = new Label();
        readonly Button _ok = new Button();
        readonly AkaiDisk _disk;

        public string ChosenName { get { return AkaiDisk.NormaliseName(_name.Text); } }
        public int ProgramNumber { get { return (int)_number.Value - 1; } }

        public NewProgramDialog(AkaiDisk disk)
        {
            _disk = disk;

            Text = "New program";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(400, 188);
            Font = SystemFonts.MessageBoxFont;

            Controls.Add(new Label
            {
                Bounds = new Rectangle(12, 14, 376, 20),
                Text = "Name the new program:"
            });

            _name.SetBounds(12, 38, 200, 24);
            _name.MaxLength = 10;
            _name.CharacterCasing = CharacterCasing.Upper;
            _name.Text = "NEW PROG";
            _name.SelectAll();
            _name.TextChanged += (s, e) => Validate2();
            Controls.Add(_name);

            Controls.Add(new Label
            {
                Bounds = new Rectangle(218, 41, 170, 20),
                Text = "10 characters",
                ForeColor = SystemColors.GrayText
            });

            Controls.Add(new Label
            {
                Bounds = new Rectangle(12, 76, 130, 20),
                Text = "MIDI program"
            });
            _number.SetBounds(146, 74, 66, 24);
            _number.Minimum = 1;
            _number.Maximum = 128;
            _number.Value = disk.FreeProgramNumber() + 1;
            Controls.Add(_number);

            _note.SetBounds(12, 108, 376, 34);
            Controls.Add(_note);

            _ok.SetBounds(192, 150, 96, 26);
            _ok.Text = "Create";
            _ok.DialogResult = DialogResult.OK;
            Controls.Add(_ok);

            var cancel = new Button
            {
                Bounds = new Rectangle(296, 150, 92, 26),
                Text = "Cancel",
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(cancel);

            AcceptButton = _ok;
            CancelButton = cancel;
            Validate2();
        }

        /// <summary>Named to stay clear of Form.Validate.</summary>
        void Validate2()
        {
            string want = ChosenName;
            bool blank = _name.Text.Trim().Length == 0;

            bool taken = _disk.Entries.Any(
                x => x.Type == 'P' && string.Equals(x.Name, want, StringComparison.OrdinalIgnoreCase));

            if (blank) _note.Text = "Give it a name.";
            else if (taken) _note.Text = "'" + want + "' is already used by another program.";
            else if (_disk.FreeSlots() < 1) _note.Text = "The directory is full: 64 files is the limit.";
            else if (_disk.FreeBlocks < 1) _note.Text = "There is no free block for it.";
            else _note.Text = "It arrives with one empty keygroup across the whole keyboard.";

            bool bad = blank || taken || _disk.FreeSlots() < 1 || _disk.FreeBlocks < 1;
            _note.ForeColor = bad ? Color.FromArgb(168, 32, 32) : SystemColors.GrayText;
            _ok.Enabled = !bad;
        }
    }
}
