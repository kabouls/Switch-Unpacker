using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;


namespace SwitchUnpacker
{
    public class Job
    {
        public string File, Keys, OutRoot, Mode, Custom, Result;
        public bool Control, Legal, Data;
    }

    class TypeSpec
    {
        public string Type;
        public string[] Dirs;
        public string[] Args;
    }

    public class Unpacker
    {
        public string Hactool;
        public Action<string> Log = delegate { };
        Process current;

        static readonly Regex KeyName = new Regex(@"^\s*(header_key|master_key_|key_area_key_|titlekek|aes_kek_generation_source|aes_key_generation_source)");
        static readonly Regex KeyVal = new Regex(@"=\s*([0-9a-fA-F]{32}|[0-9a-fA-F]{64})\s*$");

        public void Kill()
        {
            try { if (current != null && !current.HasExited) current.Kill(); } catch { }
        }

        static string Q(string s) { return "\"" + s + "\""; }

        string Run(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Hactool, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            StringBuilder sb = new StringBuilder();
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                current = p;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                current = null;
            }
            return sb.ToString();
        }

        static int CountFiles(string dir)
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length : 0;
        }

        public string Execute(Job j)
        {
            string tmp = null, cleanKeys = null;
            try
            {
                string ext = Path.GetExtension(j.File).ToLowerInvariant();
                Directory.CreateDirectory(j.OutRoot);
                tmp = Path.Combine(j.OutRoot, "_unpack_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string raw = Path.Combine(tmp, "raw");
                string content = Path.Combine(tmp, "content");
                Directory.CreateDirectory(raw);
                Directory.CreateDirectory(content);

                // clean keys
                string[] lines = File.ReadAllLines(j.Keys).Where(l => KeyName.IsMatch(l) && KeyVal.IsMatch(l)).ToArray();
                if (!lines.Any(l => l.TrimStart().StartsWith("header_key")))
                    throw new Exception("header_key not found in the selected prod.keys.");
                cleanKeys = Path.Combine(Path.GetTempPath(), "hactool_keys_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".keys");
                File.WriteAllLines(cleanKeys, lines, Encoding.ASCII);
                string baseArgs = "-k " + Q(cleanKeys) + " --disablekeywarns";

                // unpack container
                Log("Unpacking " + Path.GetFileName(j.File) + " ...");
                if (ext == ".nsp") Run(baseArgs + " -t pfs0 --outdir=" + Q(raw) + " " + Q(j.File));
                else Run(baseArgs + " -t xci --securedir=" + Q(raw) + " " + Q(j.File));

                // titlekey (NSP only)
                string tk = null;
                string tik = Directory.GetFiles(raw, "*.tik", SearchOption.AllDirectories).FirstOrDefault();
                if (tik != null)
                {
                    byte[] b = File.ReadAllBytes(tik);
                    if (b.Length >= 0x190)
                    {
                        StringBuilder h = new StringBuilder();
                        for (int i = 0x180; i < 0x190; i++) h.Append(b[i].ToString("x2"));
                        tk = h.ToString();
                    }
                }

                List<FileInfo> ncas = Directory.GetFiles(raw, "*.nca", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .Where(f => !f.Name.EndsWith(".cnmt.nca", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.Length).ToList();
                if (ncas.Count == 0) throw new Exception("No NCA files found inside the container. Wrong keys, or an unsupported file.");

                bool needControl = j.Control || j.Mode == "title";
                List<TypeSpec> types = new List<TypeSpec>();
                types.Add(new TypeSpec { Type = "program", Dirs = new[] { "romfs", "exefs" },
                    Args = new[] { "--romfsdir=" + Q(Path.Combine(content, "romfs")), "--exefsdir=" + Q(Path.Combine(content, "exefs")) } });
                if (needControl) types.Add(new TypeSpec { Type = "control", Dirs = new[] { "control" }, Args = new[] { "--romfsdir=" + Q(Path.Combine(content, "control")) } });
                if (j.Legal) types.Add(new TypeSpec { Type = "manual", Dirs = new[] { "legal" }, Args = new[] { "--romfsdir=" + Q(Path.Combine(content, "legal")) } });
                if (j.Data) types.Add(new TypeSpec { Type = "data", Dirs = new[] { "data" }, Args = new[] { "--romfsdir=" + Q(Path.Combine(content, "data")) } });

                string lastOut = "";
                foreach (FileInfo nca in ncas)
                {
                    Log("Extracting " + nca.Name + " ...");
                    foreach (TypeSpec t in types)
                    {
                        string a = baseArgs + (tk != null ? " --titlekey=" + tk : "") + " -t nca --xcontenttype=" + t.Type + " " + string.Join(" ", t.Args) + " " + Q(nca.FullName);
                        int before = t.Dirs.Sum(d => CountFiles(Path.Combine(content, d)));
                        lastOut = Run(a);
                        int after = t.Dirs.Sum(d => CountFiles(Path.Combine(content, d)));
                        if (after > before) { Log("   -> " + t.Type); break; }
                    }
                }

                // folder name
                string name = Path.GetFileNameWithoutExtension(j.File);
                if (j.Mode == "custom" && !string.IsNullOrWhiteSpace(j.Custom)) name = j.Custom.Trim();
                else if (j.Mode == "title")
                {
                    string title = null;
                    string cdir = Path.Combine(content, "control");
                    if (Directory.Exists(cdir))
                    {
                        string nacp = Directory.GetFiles(cdir, "control.nacp", SearchOption.AllDirectories).FirstOrDefault();
                        if (nacp != null)
                        {
                            byte[] nb = File.ReadAllBytes(nacp);
                            for (int i = 0; i < 16 && (i + 1) * 0x300 <= nb.Length; i++)
                            {
                                string nm = Encoding.UTF8.GetString(nb, i * 0x300, 0x200).Split('\0')[0].Trim();
                                if (nm.Length > 0) { title = nm; break; }
                            }
                        }
                    }
                    if (title != null) name = title; else Log("Could not read the game title, using the file name.");
                }
                if (!j.Control)
                {
                    string cdir = Path.Combine(content, "control");
                    if (Directory.Exists(cdir)) Directory.Delete(cdir, true);
                }

                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                name = name.Trim().TrimEnd('.');
                if (name.Length == 0) name = "unpacked";

                // drop empty folders; verify something came out
                foreach (string d in Directory.GetDirectories(content))
                    if (CountFiles(d) == 0) Directory.Delete(d, true);
                if (CountFiles(content) == 0)
                    throw new Exception("Nothing was extracted. Check that prod.keys matches the game's firmware.\r\n\r\n" + lastOut.Trim());

                string final = Path.Combine(j.OutRoot, name);
                int n = 2;
                while (Directory.Exists(final) || File.Exists(final)) { final = Path.Combine(j.OutRoot, name + " (" + n + ")"); n++; }
                Directory.Move(content, final);
                j.Result = final;
                return final;
            }
            finally
            {
                try { if (tmp != null && Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                try { if (cleanKeys != null && File.Exists(cleanKeys)) File.Delete(cleanKeys); } catch { }
            }
        }
    }
}

namespace SwitchUnpacker
{
    class MainForm : Form
    {
        TextBox tbFile, tbOut, tbKeys, tbCustom, log;
        RadioButton rbFile, rbTitle, rbCustom;
        CheckBox cbCtl, cbLegal, cbData, cbOpen;
        Button btStart;
        ProgressBar bar;
        BackgroundWorker bw;
        Unpacker unpacker = new Unpacker();
        string settingsPath;
        Dictionary<string, string> settings = new Dictionary<string, string>();

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new MainForm()); }
            catch (Exception ex) { MessageBox.Show(ex.ToString(), "Switch Unpacker - fatal error"); }
        }

        string Get(string k, string def) { string v; return settings.TryGetValue(k, out v) ? v : def; }
        bool GetB(string k, bool def) { return Get(k, def ? "1" : "0") == "1"; }

        void LoadSettings()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwitchUnpacker");
            Directory.CreateDirectory(dir);
            settingsPath = Path.Combine(dir, "settings.ini");
            if (!File.Exists(settingsPath)) return;
            try
            {
                foreach (string l in File.ReadAllLines(settingsPath))
                {
                    int i = l.IndexOf('=');
                    if (i > 0) settings[l.Substring(0, i)] = l.Substring(i + 1);
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                File.WriteAllLines(settingsPath, new[] {
                    "output=" + tbOut.Text, "keys=" + tbKeys.Text,
                    "mode=" + (rbTitle.Checked ? "title" : rbCustom.Checked ? "custom" : "file"),
                    "custom=" + tbCustom.Text,
                    "control=" + (cbCtl.Checked ? "1" : "0"), "legal=" + (cbLegal.Checked ? "1" : "0"),
                    "data=" + (cbData.Checked ? "1" : "0"), "open=" + (cbOpen.Checked ? "1" : "0") });
            }
            catch { }
        }

        string EnsureHactool()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwitchUnpacker");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "hactool.exe");
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("hactool.exe"))
            {
                if (s == null) throw new Exception("Embedded hactool is missing.");
                if (!File.Exists(path) || new FileInfo(path).Length != s.Length)
                {
                    using (FileStream fs = File.Create(path)) s.CopyTo(fs);
                }
            }
            return path;
        }

        Label AddLabel(string text, int y)
        {
            Label l = new Label(); l.Text = text; l.Location = new Point(15, y); l.AutoSize = true;
            Controls.Add(l); return l;
        }

        void AddPathRow(int y, string value, out TextBox tb, out Button bt)
        {
            tb = new TextBox(); tb.Location = new Point(15, y); tb.Size = new Size(490, 24); tb.Text = value;
            bt = new Button(); bt.Text = "Browse..."; bt.Location = new Point(515, y - 1); bt.Size = new Size(90, 26);
            Controls.Add(tb); Controls.Add(bt);
        }

        public MainForm()
        {
            LoadSettings();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "Switch Unpacker";
            ClientSize = new Size(620, 600);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            AllowDrop = true;

            string defKeys = "";
            string[] cands = {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prod.keys"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".switch\prod.keys") };
            foreach (string c in cands) if (File.Exists(c)) { defKeys = c; break; }
            string keys = Get("keys", "");
            if (keys.Length == 0 || !File.Exists(keys)) keys = defKeys;

            Button btFile, btOut, btKeys;
            AddLabel("Game file (.nsp / .xci) - or drag it onto this window", 12);
            AddPathRow(32, "", out tbFile, out btFile);
            AddLabel("Output folder", 72);
            AddPathRow(92, Get("output", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Switch Unpacked")), out tbOut, out btOut);
            AddLabel("prod.keys", 132);
            AddPathRow(152, keys, out tbKeys, out btKeys);

            GroupBox gbName = new GroupBox();
            gbName.Text = "Subfolder name inside the output folder";
            gbName.Location = new Point(15, 195); gbName.Size = new Size(590, 110);
            Controls.Add(gbName);
            rbFile = new RadioButton(); rbFile.Text = "Game file name"; rbFile.Location = new Point(15, 24); rbFile.AutoSize = true;
            rbTitle = new RadioButton(); rbTitle.Text = "Game title (read from the game data)"; rbTitle.Location = new Point(15, 50); rbTitle.AutoSize = true;
            rbCustom = new RadioButton(); rbCustom.Text = "Custom:"; rbCustom.Location = new Point(15, 76); rbCustom.AutoSize = true;
            tbCustom = new TextBox(); tbCustom.Location = new Point(95, 74); tbCustom.Size = new Size(300, 24); tbCustom.Text = Get("custom", "");
            gbName.Controls.AddRange(new Control[] { rbFile, rbTitle, rbCustom, tbCustom });
            string mode = Get("mode", "file");
            if (mode == "title") rbTitle.Checked = true; else if (mode == "custom") rbCustom.Checked = true; else rbFile.Checked = true;

            GroupBox gbEx = new GroupBox();
            gbEx.Text = "Extract";
            gbEx.Location = new Point(15, 315); gbEx.Size = new Size(590, 60);
            Controls.Add(gbEx);
            CheckBox cbProg = new CheckBox(); cbProg.Text = "exefs + romfs"; cbProg.Checked = true; cbProg.Enabled = false; cbProg.Location = new Point(15, 24); cbProg.AutoSize = true;
            cbCtl = new CheckBox(); cbCtl.Text = "Control (title info, icons)"; cbCtl.Checked = GetB("control", true); cbCtl.Location = new Point(125, 24); cbCtl.AutoSize = true;
            cbLegal = new CheckBox(); cbLegal.Text = "Legal"; cbLegal.Checked = GetB("legal", true); cbLegal.Location = new Point(320, 24); cbLegal.AutoSize = true;
            cbData = new CheckBox(); cbData.Text = "Data"; cbData.Checked = GetB("data", true); cbData.Location = new Point(400, 24); cbData.AutoSize = true;
            gbEx.Controls.AddRange(new Control[] { cbProg, cbCtl, cbLegal, cbData });

            cbOpen = new CheckBox(); cbOpen.Text = "Open folder when done"; cbOpen.Checked = GetB("open", true); cbOpen.Location = new Point(15, 392); cbOpen.AutoSize = true;
            Controls.Add(cbOpen);
            btStart = new Button(); btStart.Text = "Unpack"; btStart.Location = new Point(485, 385); btStart.Size = new Size(120, 34);
            Controls.Add(btStart);

            bar = new ProgressBar(); bar.Location = new Point(15, 430); bar.Size = new Size(590, 16); bar.Style = ProgressBarStyle.Continuous;
            Controls.Add(bar);
            log = new TextBox(); log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical;
            log.Location = new Point(15, 455); log.Size = new Size(590, 130); log.Font = new Font("Consolas", 9F);
            Controls.Add(log);

            btFile.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "Switch games (*.nsp;*.xci)|*.nsp;*.xci";
                    if (d.ShowDialog() == DialogResult.OK) tbFile.Text = d.FileName;
                }
            };
            btOut.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "Select output folder";
                    if (tbOut.Text.Length > 0 && Directory.Exists(tbOut.Text)) d.SelectedPath = tbOut.Text;
                    if (d.ShowDialog() == DialogResult.OK) tbOut.Text = d.SelectedPath;
                }
            };
            btKeys.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "Keys (*.keys)|*.keys|All files (*.*)|*.*";
                    if (d.ShowDialog() == DialogResult.OK) tbKeys.Text = d.FileName;
                }
            };
            DragEnter += delegate (object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate (object s, DragEventArgs e)
            {
                string[] f = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (f != null && f.Length > 0 && Regex.IsMatch(f[0], @"\.(nsp|xci)$", RegexOptions.IgnoreCase)) tbFile.Text = f[0];
            };

            unpacker.Log = delegate (string m) { if (bw != null) bw.ReportProgress(0, m); };
            bw = new BackgroundWorker();
            bw.WorkerReportsProgress = true;
            bw.DoWork += delegate (object s, DoWorkEventArgs e) { e.Result = unpacker.Execute((Job)e.Argument); };
            bw.ProgressChanged += delegate (object s, ProgressChangedEventArgs e) { log.AppendText((string)e.UserState + "\r\n"); };
            bw.RunWorkerCompleted += OnDone;

            btStart.Click += OnStart;
            FormClosing += delegate { unpacker.Kill(); };
        }

        void Warn(string msg) { MessageBox.Show(this, msg, "Switch Unpacker", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        void OnStart(object sender, EventArgs e)
        {
            if (!File.Exists(tbFile.Text)) { Warn("Select a valid .nsp or .xci file."); return; }
            if (!Regex.IsMatch(tbFile.Text, @"\.(nsp|xci)$", RegexOptions.IgnoreCase)) { Warn("Only .nsp and .xci files are supported."); return; }
            if (!File.Exists(tbKeys.Text)) { Warn("Select your prod.keys file."); return; }
            if (tbOut.Text.Trim().Length == 0) { Warn("Select an output folder."); return; }
            try { Directory.CreateDirectory(tbOut.Text); }
            catch (Exception ex) { Warn("Cannot create the output folder:\r\n" + ex.Message); return; }
            try { unpacker.Hactool = EnsureHactool(); }
            catch (Exception ex) { Warn("Cannot prepare hactool:\r\n" + ex.Message); return; }

            SaveSettings();
            Job j = new Job();
            j.File = tbFile.Text; j.Keys = tbKeys.Text; j.OutRoot = Path.GetFullPath(tbOut.Text).TrimEnd('\\');
            if (j.OutRoot.Length == 2 && j.OutRoot[1] == ':') j.OutRoot += "\\";
            j.Mode = rbTitle.Checked ? "title" : rbCustom.Checked ? "custom" : "file";
            j.Custom = tbCustom.Text; j.Control = cbCtl.Checked; j.Legal = cbLegal.Checked; j.Data = cbData.Checked;

            log.Clear();
            btStart.Enabled = false;
            bar.Style = ProgressBarStyle.Marquee;
            bar.MarqueeAnimationSpeed = 30;
            bw.RunWorkerAsync(j);
        }

        void OnDone(object sender, RunWorkerCompletedEventArgs e)
        {
            bar.Style = ProgressBarStyle.Continuous;
            btStart.Enabled = true;
            if (e.Error != null)
            {
                bar.Value = 0;
                log.AppendText("ERROR: " + e.Error.Message + "\r\n");
                MessageBox.Show(this, e.Error.Message, "Switch Unpacker - error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string result = (string)e.Result;
            bar.Value = 100;
            log.AppendText("Done: " + result + "\r\n");
            if (cbOpen.Checked) Process.Start("explorer.exe", "\"" + result + "\"");
            MessageBox.Show(this, "Finished.\r\n\r\n" + result, "Switch Unpacker", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
