using System;
using System.Drawing;
using System.Windows.Forms;
using AkaiS950List;

namespace AkaiS950Studio
{
    /// <summary>
    /// Asks for a new filename, refusing the ones the disk cannot hold: blank, and
    /// anything already in use. Names are 10 characters of upper-case ASCII, which is
    /// all the S900/S950 directory has room for.
    /// </summary>
    internal sealed class RenameDialog : Form
    {
        readonly TextBox _name = new TextBox();
        readonly Label _note = new Label();
        readonly Button _ok = new Button();
        readonly AkaiDisk _disk;
        readonly AkaiEntry _entry;

        public string ChosenName { get { return AkaiDisk.NormaliseName(_name.Text); } }

        public RenameDialog(AkaiDisk disk, AkaiEntry entry)
        {
            _disk = disk;
            _entry = entry;

            Text = "Rename " + entry.TypeName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 148);
            Font = SystemFonts.MessageBoxFont;

            Controls.Add(new Label
            {
                Bounds = new Rectangle(12, 14, 356, 20),
                Text = "Rename '" + entry.Name + "' to:"
            });

            _name.SetBounds(12, 38, 200, 24);
            _name.MaxLength = 10;
            _name.CharacterCasing = CharacterCasing.Upper;
            _name.Text = entry.Name;
            _name.SelectAll();
            _name.TextChanged += (s, e) => Validate2();
            Controls.Add(_name);

            Controls.Add(new Label
            {
                Bounds = new Rectangle(218, 41, 150, 20),
                Text = "10 characters",
                ForeColor = SystemColors.GrayText
            });

            _note.SetBounds(12, 68, 356, 34);
            Controls.Add(_note);

            _ok.SetBounds(172, 110, 90, 26);
            _ok.Text = "Rename";
            _ok.DialogResult = DialogResult.OK;
            Controls.Add(_ok);

            var cancel = new Button
            {
                Bounds = new Rectangle(270, 110, 90, 26),
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

            // A name clashes only with another file of the same type.
            bool taken = false;
            foreach (var x in _disk.Entries)
                if (x.Slot != _entry.Slot && x.Type == _entry.Type &&
                    string.Equals(x.Name, want, StringComparison.OrdinalIgnoreCase))
                    taken = true;

            if (blank) _note.Text = "Give it a name.";
            else if (taken) _note.Text = "'" + want + "' is already used by another " + _entry.TypeName + ".";
            else if (_entry.Type == 'S')
                _note.Text = "Keygroups referring to this sample will be updated to match.";
            else _note.Text = "";

            _note.ForeColor = (blank || taken) ? Color.FromArgb(168, 32, 32) : SystemColors.GrayText;
            _ok.Enabled = !blank && !taken;
        }
    }
}
