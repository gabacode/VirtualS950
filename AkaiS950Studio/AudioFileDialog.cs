using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AkaiS950Studio
{
    /// <summary>
    /// Choosing an audio file to add to a disk, with a preview.
    ///
    /// The Windows common dialog cannot do this: it offers no managed hook for "the
    /// selection changed", so hearing a file before committing to it means browsing the
    /// folder ourselves. What that buys is worth the dialog - clicking a file decodes it,
    /// draws it and plays it, so a folder of takes can be auditioned in the place where
    /// you are choosing between them rather than one import at a time.
    ///
    /// Decoding happens off the UI thread, because an ffmpeg decode of a long file is a
    /// subprocess and a temp file, not an instant. Clicking down a list faster than the
    /// decodes finish is the normal case rather than the exception, so each one carries a
    /// generation number and a result that arrives after the selection has moved on is
    /// dropped.
    /// </summary>
    internal sealed class AudioFileDialog : Form
    {
        readonly TextBox _path = new TextBox();
        readonly Button _up = new Button();
        readonly Button _browse = new Button();
        readonly ListView _files = new ListView();
        readonly WaveformView _wave = new WaveformView();
        readonly Label _info = new Label();
        readonly Button _play = new Button();
        readonly Button _stop = new Button();
        readonly CheckBox _auto = new CheckBox();
        readonly Button _ok = new Button();
        readonly SamplePlayer _audio = new SamplePlayer();

        /// <summary>Where the last one of these was pointed, so the next opens there.</summary>
        static string _lastFolder;

        /// <summary>Whether the preview auto-plays, remembered across invocations.</summary>
        static bool _autoPlay = true;

        string _folder;
        int _generation;

        /// <summary>The file that was chosen.</summary>
        public string ChosenPath { get; private set; }

        /// <summary>
        /// Its audio, already decoded for the preview. Handing this back means the file is
        /// not read and decoded a second time on the way into the import dialog.
        /// </summary>
        public AudioClip ChosenClip { get; private set; }

        // A decode this big is not worth starting on a single click; the Play button will
        // still do it on request. Roughly twenty minutes of 44.1 kHz stereo.
        const long AutoPreviewLimit = 220L * 1024 * 1024;

        AudioClip _clip;                 // the decoded selection, or null
        string _clipPath;                // which file _clip came from

        public AudioFileDialog(string title, string startFolder)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(820, 560);
            MinimumSize = new Size(700, 480);
            Font = SystemFonts.MessageBoxFont;

            // ---- the folder bar
            _path.SetBounds(12, 12, 660, 24);
            _path.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _path.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                Navigate(_path.Text);
            };
            Controls.Add(_path);

            _up.SetBounds(680, 11, 52, 26);
            _up.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _up.Text = "Up";
            _up.Click += (s, e) =>
            {
                var parent = Directory.GetParent(_folder ?? "");
                if (parent != null) Navigate(parent.FullName);
            };
            Controls.Add(_up);

            _browse.SetBounds(738, 11, 70, 26);
            _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browse.Text = "Folder...";
            _browse.Click += OnBrowse;
            Controls.Add(_browse);

            // ---- the listing
            _files.SetBounds(12, 46, 796, 268);
            _files.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _files.View = View.Details;
            _files.FullRowSelect = true;
            _files.MultiSelect = false;
            _files.HideSelection = false;
            _files.Columns.Add("Name", 380);
            _files.Columns.Add("Size", 100, HorizontalAlignment.Right);
            _files.Columns.Add("Kind", 130);
            _files.Columns.Add("Modified", 160);
            _files.SelectedIndexChanged += OnSelectionChanged;
            _files.DoubleClick += OnDoubleClick;
            _files.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = true; OnDoubleClick(s, EventArgs.Empty); }
                if (e.KeyCode == Keys.Back) { e.Handled = true; _up.PerformClick(); }
            };
            Controls.Add(_files);

            // ---- the preview
            _wave.SetBounds(12, 326, 796, 116);
            _wave.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(_wave);

            _info.SetBounds(12, 448, 560, 38);
            _info.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _info.ForeColor = SystemColors.GrayText;
            Controls.Add(_info);

            _play.SetBounds(12, 490, 74, 28);
            _play.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _play.Text = "Play";
            _play.Click += (s, e) => PreviewSelected(true, true);
            Controls.Add(_play);

            _stop.SetBounds(92, 490, 74, 28);
            _stop.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _stop.Text = "Stop";
            _stop.Click += (s, e) => _audio.Stop();
            Controls.Add(_stop);

            _auto.SetBounds(178, 495, 170, 20);
            _auto.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _auto.Text = "Play on selection";
            _auto.Checked = _autoPlay;
            _auto.CheckedChanged += (s, e) => _autoPlay = _auto.Checked;
            Controls.Add(_auto);

            _ok.SetBounds(608, 490, 96, 28);
            _ok.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            _ok.Text = "Add";
            _ok.Enabled = false;
            _ok.Click += (s, e) => Accept();
            Controls.Add(_ok);

            var cancel = new Button
            {
                Bounds = new Rectangle(712, 490, 96, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Text = "Cancel",
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(cancel);

            CancelButton = cancel;

            Navigate(FirstFolder(startFolder));
            ShowFfmpegNote();
        }

        static string FirstFolder(string startFolder)
        {
            foreach (var candidate in new[] { _lastFolder, startFolder,
                                              Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                                              Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
            {
                try { if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate)) return candidate; }
                catch (Exception) { /* an unreachable drive */ }
            }
            return Directory.GetCurrentDirectory();
        }

        void ShowFfmpegNote()
        {
            if (AudioImport.FfmpegAvailable) return;
            _info.Text = "WAV and AIFF are read directly. MP3, FLAC, Ogg and the rest need " +
                         "ffmpeg on the PATH, which was not found - those will not open.";
        }

        void OnBrowse(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Which folder are the samples in?";
                try { dlg.SelectedPath = _folder; }
                catch (Exception) { /* it will just open at the default */ }
                if (dlg.ShowDialog(this) == DialogResult.OK) Navigate(dlg.SelectedPath);
            }
        }

        // ------------------------------------------------------------ the listing

        void Navigate(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;

            try { folder = Path.GetFullPath(folder); }
            catch (Exception) { return; }

            if (!Directory.Exists(folder))
            {
                // Typing the path of a file rather than its folder is a reasonable thing
                // to do, so follow it to the file instead of refusing.
                if (File.Exists(folder))
                {
                    string parent = Path.GetDirectoryName(folder);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Navigate(parent);
                        SelectByName(Path.GetFileName(folder));
                    }
                    return;
                }
                MessageBox.Show(this, "There is no folder at" + Environment.NewLine + folder,
                                "Browse", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _folder = folder;
            _lastFolder = folder;
            _path.Text = folder;
            Fill();
        }

        void Fill()
        {
            _audio.Stop();
            _generation++;                 // anything still decoding is now stale
            _clip = null;
            _clipPath = null;
            _ok.Enabled = false;
            _wave.Clear();

            _files.BeginUpdate();
            _files.Items.Clear();

            try
            {
                foreach (var dir in Directory.GetDirectories(_folder).OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (Hidden(dir)) continue;
                    var item = new ListViewItem(new[] { NameOf(dir), "", "Folder", Modified(dir) })
                    {
                        Tag = new Row(dir, true),
                        ForeColor = Color.FromArgb(70, 90, 130)
                    };
                    _files.Items.Add(item);
                }

                foreach (var f in Directory.GetFiles(_folder).OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (!AudioImport.LooksLikeAudio(f) || Hidden(f)) continue;

                    string kind = (Path.GetExtension(f) ?? "").TrimStart('.').ToUpperInvariant();
                    if (!AudioImport.ReadNatively(f) && !AudioImport.FfmpegAvailable) kind += "  (needs ffmpeg)";

                    long size = 0;
                    try { size = new FileInfo(f).Length; } catch (Exception) { }

                    _files.Items.Add(new ListViewItem(new[] { NameOf(f), Bytes(size), kind, Modified(f) })
                    {
                        Tag = new Row(f, false)
                    });
                }
            }
            catch (UnauthorizedAccessException)
            {
                _info.Text = "That folder cannot be read.";
            }
            catch (IOException ex)
            {
                _info.Text = ex.Message;
            }
            finally
            {
                _files.EndUpdate();
            }

            if (_files.Items.Count == 0 && string.IsNullOrEmpty(_info.Text))
                _info.Text = "No audio files here.";
        }

        void SelectByName(string name)
        {
            foreach (ListViewItem item in _files.Items)
            {
                var row = (Row)item.Tag;
                if (!row.IsFolder && string.Equals(NameOf(row.Path), name, StringComparison.OrdinalIgnoreCase))
                {
                    item.Selected = true;
                    item.EnsureVisible();
                    _files.Focus();
                    return;
                }
            }
        }

        static string NameOf(string p) { return Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)); }

        static bool Hidden(string p)
        {
            try
            {
                var a = File.GetAttributes(p);
                return (a & FileAttributes.Hidden) != 0 || (a & FileAttributes.System) != 0;
            }
            catch (Exception) { return true; }
        }

        static string Modified(string p)
        {
            try { return File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
            catch (Exception) { return ""; }
        }

        static string Bytes(long n)
        {
            if (n >= 1024L * 1024 * 1024) return (n / 1024.0 / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            if (n >= 1024 * 1024) return (n / 1024.0 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (n >= 1024) return (n / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            return n + " B";
        }

        // ------------------------------------------------------------- previewing

        Row Selected
        {
            get
            {
                return _files.SelectedItems.Count == 1 ? (Row)_files.SelectedItems[0].Tag : null;
            }
        }

        void OnSelectionChanged(object sender, EventArgs e)
        {
            PreviewSelected(_auto.Checked, false);
        }

        void OnDoubleClick(object sender, EventArgs e)
        {
            var row = Selected;
            if (row == null) return;

            if (row.IsFolder) Navigate(row.Path);
            else if (_ok.Enabled) Accept();
        }

        /// <summary>
        /// Decode the selection if it is not already decoded, then draw it and - if asked -
        /// play it. <paramref name="force"/> overrides the size guard, for the Play button.
        /// </summary>
        async void PreviewSelected(bool play, bool force)
        {
            _audio.Stop();

            var row = Selected;
            int gen = ++_generation;

            if (row == null || row.IsFolder)
            {
                _clip = null;
                _clipPath = null;
                _ok.Enabled = false;
                _wave.Clear();
                _info.Text = row != null ? "Folder" : "";
                return;
            }

            // Already in hand - no need to read it twice.
            if (_clip != null && _clipPath == row.Path)
            {
                if (play) PlayClip(_clip);
                return;
            }

            _clip = null;
            _clipPath = null;
            _ok.Enabled = false;
            _wave.Clear();

            long size = 0;
            try { size = new FileInfo(row.Path).Length; } catch (Exception) { }

            if (size > AutoPreviewLimit && !force)
            {
                _info.Text = NameOf(row.Path) + "  -  " + Bytes(size) +
                             ", too big to decode on a click. Press Play to hear it.";
                return;
            }

            _info.Text = "Reading " + NameOf(row.Path) + "...";

            AudioClip clip = null;
            string error = null;
            string path = row.Path;

            try { clip = await Task.Run(() => AudioImport.Load(path)); }
            catch (Exception ex) { error = ex.Message; }

            if (gen != _generation) return;          // the selection moved on; drop this
            if (IsDisposed || Disposing) return;

            if (error != null)
            {
                _info.Text = NameOf(path) + "  -  " + error;
                return;
            }

            if (clip == null || clip.Mono.Length == 0)
            {
                _info.Text = NameOf(path) + " contains no audio.";
                return;
            }

            _clip = clip;
            _clipPath = path;
            _ok.Enabled = true;

            // No caption: the name is already on the selected row and in the line below,
            // and a third copy only lands on top of the waveform.
            var pcm = ToPcm16(clip.Mono);
            _wave.SetSample(pcm, clip.SampleRate, 0, pcm.Length, 0, 'O', "");

            _info.Text = clip.Describe() + "   -   peak " +
                         AudioImport.Peak(clip.Mono).ToString("0.00", CultureInfo.InvariantCulture) +
                         ".  It is converted to the S950's format on the next screen.";

            if (play) PlayClip(clip);
        }

        void PlayClip(AudioClip clip)
        {
            try { _audio.Play(ToPcm16(clip.Mono), clip.SampleRate); }
            catch (Exception ex) { _info.Text = "Could not play it: " + ex.Message; }
        }

        /// <summary>The preview is the source audio as it is, before any S950 conversion.</summary>
        static short[] ToPcm16(float[] mono)
        {
            var pcm = new short[mono.Length];
            for (int i = 0; i < mono.Length; i++)
            {
                int v = (int)Math.Round(mono[i] * 32767f);
                if (v > 32767) v = 32767;
                if (v < -32768) v = -32768;
                pcm[i] = (short)v;
            }
            return pcm;
        }

        void Accept()
        {
            if (_clip == null || _clipPath == null) return;

            _audio.Stop();
            ChosenPath = _clipPath;
            ChosenClip = _clip;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _generation++;
            _audio.Dispose();
            base.OnFormClosed(e);
        }

        sealed class Row
        {
            public readonly string Path;
            public readonly bool IsFolder;
            public Row(string path, bool isFolder) { Path = path; IsFolder = isFolder; }
        }
    }
}
