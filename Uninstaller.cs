using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly:System.Reflection.AssemblyTitle("WebCoding Uninstall")]
[assembly:System.Reflection.AssemblyProduct("WebCoding")]
[assembly:System.Reflection.AssemblyVersion("2.0.2.0")]
[assembly:System.Reflection.AssemblyFileVersion("2.0.2.0")]

namespace WebCodingUninstall
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            string root = null;
            if (args.Length >= 2 && args[0] == "--root") root = Path.GetFullPath(args[1]);

            if (root == null)
            {
                string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebCoding");
                string ownParent = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                if (File.Exists(Path.Combine(ownParent, ".webcoding-install")))
                    root = ownParent;
                else if (File.Exists(Path.Combine(installed, ".webcoding-install")))
                    root = installed;
                else
                {
                    MessageBox.Show("没有检测到已安装的 WebCoding。\r\n\r\n如果你只是解压了 Release 包，不需要运行卸载器。",
                        "WebCoding", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try
                {
                    string temp = Path.Combine(Path.GetTempPath(), "WebCoding-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                    File.Copy(Application.ExecutablePath, temp, true);
                    Process.Start(new ProcessStartInfo(temp, "--root " + Quote(root)) { UseShellExecute = false });
                    return;
                }
                catch { }
            }

            if (!File.Exists(Path.Combine(root, ".webcoding-install")))
            {
                MessageBox.Show("目标目录缺少 WebCoding 安装标记，已取消卸载。", "WebCoding", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new UninstallForm(root));

            if (!string.Equals(Application.ExecutablePath, Path.Combine(root, "bin", "WebCoding-Uninstall.exe"), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string cmd = "/c ping 127.0.0.1 -n 2 >nul & del /f /q " + Quote(Application.ExecutablePath);
                    Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
                }
                catch { }
            }
        }

        static string Quote(string value)
        {
            var b=new StringBuilder(); b.Append('"'); int slashes=0;
            foreach(char c in value)
            {
                if(c=='\\'){slashes++;continue;}
                b.Append('\\',c=='"'?slashes*2+1:slashes);
                b.Append(c); slashes=0;
            }
            b.Append('\\',slashes*2); b.Append('"'); return b.ToString();
        }
    }

    sealed class UninstallForm : Form
    {
        readonly CheckBox purge = new CheckBox();
        readonly TextBox log = new TextBox();
        readonly ProgressBar progress = new ProgressBar();
        readonly Button uninstall = new Button();
        readonly Button cancel = new Button();
        readonly Label heading = new Label();
        bool busy;
        readonly string root;

        string Root { get { return root; } }

        public UninstallForm(string installRoot)
        {
            root = Path.GetFullPath(installRoot);
            Text = "卸载 WebCoding";
            WebCodingBrand.Branding.Apply(this);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(720, 500);
            MinimumSize = new Size(660, 460);
            MaximizeBox = false;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;

            var top = new Panel { Dock = DockStyle.Top, Height = 112, Padding = new Padding(28, 22, 28, 8) };
            heading.Text = "卸载 WebCoding";
            heading.Dock = DockStyle.Top;
            heading.Height = 44;
            heading.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold);
            heading.ForeColor = Color.FromArgb(28,31,38);
            var sub = new Label {
                Text = "只删除 WebCoding 自己安装的文件，不会删除你的项目。",
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(92,100,112)
            };
            top.Controls.Add(sub);
            top.Controls.Add(heading);
            var logo = WebCodingBrand.Branding.Logo();
            logo.Dock = DockStyle.Right;
            logo.Width = 64;
            top.Controls.Add(logo);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 12, 28, 12) };
            var note = new Label {
                Text = "默认会保留 agent.md、tools.json 和隧道偏好，方便以后重装继续使用。\r\nNode.js、cloudflared、ngrok 如果原本是系统已有版本，也不会被卸载。",
                Dock = DockStyle.Top,
                Height = 72,
                ForeColor = Color.FromArgb(70,76,86)
            };
            purge.Text = "同时删除 WebCoding 的设置与默认隧道偏好";
            purge.Dock = DockStyle.Top;
            purge.Height = 42;

            progress.Dock = DockStyle.Top;
            progress.Height = 14;
            progress.Style = ProgressBarStyle.Blocks;

            log.Dock = DockStyle.Fill;
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Color.FromArgb(250,251,253);
            log.BorderStyle = BorderStyle.FixedSingle;
            log.Font = new Font("Consolas", 9F);

            body.Controls.Add(log);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 16 });
            body.Controls.Add(progress);
            body.Controls.Add(purge);
            body.Controls.Add(note);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 72, Padding = new Padding(20,15,22,15), BackColor = Color.FromArgb(250,251,253) };
            uninstall.Text = "开始卸载";
            uninstall.Width = 120; uninstall.Height = 38;
            cancel.Text = "取消";
            cancel.Width = 96; cancel.Height = 38;
            uninstall.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            footer.Resize += (s,e) => {
                uninstall.Location = new Point(footer.ClientSize.Width - uninstall.Width - 22,17);
                cancel.Location = new Point(uninstall.Left - cancel.Width - 10,17);
            };
            uninstall.Click += (s,e) => StartUninstall();
            cancel.Click += (s,e) => Close();
            footer.Controls.Add(cancel);
            footer.Controls.Add(uninstall);

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(top);

            FormClosing += (s,e) => {
                if (busy) e.Cancel = true;
            };
        }

        void StartUninstall()
        {
            if (busy) return;

            string script = Path.Combine(Root, "Uninstall.ps1");
            if (!File.Exists(script))
            {
                MessageBox.Show("没有找到卸载脚本。请重新安装 WebCoding 后再卸载。", "WebCoding", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            busy = true;
            uninstall.Enabled = false;
            cancel.Enabled = false;
            purge.Enabled = false;
            progress.Style = ProgressBarStyle.Marquee;
            log.Clear();

            ThreadPool.QueueUserWorkItem(_ => {
                int code = Run(script, purge.Checked);
                BeginInvoke((Action)(() => {
                    busy = false;
                    progress.Style = ProgressBarStyle.Blocks;
                    progress.Value = code == 0 ? 100 : 0;
                    if (code == 0)
                    {
                        heading.Text = "卸载完成";
                        uninstall.Visible = false;
                        cancel.Enabled = true;
                        cancel.Text = "关闭";
                    }
                    else
                    {
                        uninstall.Enabled = true;
                        cancel.Enabled = true;
                        purge.Enabled = true;
                        MessageBox.Show("卸载没有完成，请查看窗口中的详细信息。", "WebCoding", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            });
        }

        int Run(string script, bool purgeSettings)
        {
            try
            {
                string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                string args = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + Quote(script) + " -Yes" + (purgeSettings ? " -PurgeSettings" : "");
                var info = new ProcessStartInfo(ps, args) {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Root
                };
                using (var p = new Process { StartInfo = info })
                {
                    p.OutputDataReceived += (s,e) => { if (e.Data != null) BeginInvoke((Action)(() => Append(e.Data))); };
                    p.ErrorDataReceived += (s,e) => { if (e.Data != null) BeginInvoke((Action)(() => Append(e.Data))); };
                    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception e)
            {
                BeginInvoke((Action)(() => Append("启动卸载失败：" + e.Message)));
                return 1;
            }
        }

        void Append(string line)
        {
            log.AppendText(line + Environment.NewLine);
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }

        static string Quote(string value)
        {
            var b=new StringBuilder(); b.Append('"'); int slashes=0;
            foreach(char c in value)
            {
                if(c=='\\'){slashes++;continue;}
                b.Append('\\',c=='"'?slashes*2+1:slashes);
                b.Append(c); slashes=0;
            }
            b.Append('\\',slashes*2); b.Append('"'); return b.ToString();
        }
    }
}
