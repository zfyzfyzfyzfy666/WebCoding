using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("WebCoding Setup")]
[assembly: AssemblyProduct("WebCoding")]
[assembly: AssemblyDescription("WebCoding setup wizard")]
[assembly: AssemblyVersion("2.0.2.0")]
[assembly: AssemblyFileVersion("2.0.2.0")]

namespace WebCodingSetup
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = false, checkOnly = false;
            foreach (string arg in args)
            {
                if (arg == "--yes") silent = true;
                else if (arg == "--check") { checkOnly = true; silent = true; }
                else if (arg == "--help") return 0;
                else return 1;
            }

            if (silent)
                return RunHeadless(checkOnly);

            Application.Run(new SetupWizard());
            return SetupWizard.ExitCode;
        }

        static int RunHeadless(bool checkOnly)
        {
            string stage = null;
            try
            {
                stage = Payload.Extract();
                string ps = Payload.PowerShell();
                string arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " +
                    Payload.Quote(Path.Combine(stage, "Install.ps1")) +
                    " -PayloadDirectory " + Payload.Quote(stage) +
                    (checkOnly ? " -CheckOnly" : "");
                var start = new ProcessStartInfo(ps, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = stage
                };
                using (var p = Process.Start(start))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch { return 1; }
            finally { Payload.Cleanup(stage); }
        }
    }

    internal static class Payload
    {
        internal static readonly string[] Files = {
            "Install.ps1", "Uninstall.ps1", "README.md", "webcoding.exe",
            "WebCoding.Native.exe", "WebCoding.Config.exe", "WebCoding-Uninstall.exe",
            "agent.md", "tools.json"
        };

        internal static string Extract()
        {
            string stage = Path.Combine(Path.GetTempPath(), "WebCoding-Setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            foreach (string file in Files)
            {
                using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("WebCoding.Setup." + file))
                using (FileStream target = File.Create(Path.Combine(stage, file)))
                {
                    if (source == null) throw new IOException("安装器内置文件缺失：" + file);
                    source.CopyTo(target);
                }
            }
            return stage;
        }

        internal static void Cleanup(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return;
            try { Directory.Delete(stage, true); } catch { }
        }

        internal static string PowerShell()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
        }

        internal static string Quote(string value)
        {
            var b = new StringBuilder(); b.Append('"'); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                b.Append(c); slashes = 0;
            }
            b.Append('\\', slashes * 2); b.Append('"');
            return b.ToString();
        }
    }

    internal sealed class SetupWizard : Form
    {
        internal static int ExitCode = 0;

        readonly string[] stepNames = { "欢迎", "环境", "ngrok", "安装", "完成" };
        readonly Label[] stepLabels = new Label[5];
        readonly Panel host = new Panel();
        readonly Label title = new Label();
        readonly Label subtitle = new Label();
        readonly Label notice = new Label();
        readonly Button back = new Button();
        readonly Button next = new Button();
        readonly Button cancel = new Button();
        readonly ComboBox language = new ComboBox();
        readonly List<Panel> pages = new List<Panel>();
        readonly Dictionary<Panel, bool> pageLayoutBusy = new Dictionary<Panel, bool>();
        readonly Panel sidebar = new Panel();
        readonly Label brand = new Label();
        readonly Label version = new Label();
        readonly PictureBox logo = WebCodingBrand.Branding.Logo();
        readonly Panel top = new Panel();
        double dpiScale = 1;
        bool shellLayoutBusy;

        int U(int value) { return (int)Math.Ceiling(value * dpiScale); }

        readonly Label[] envRows = new Label[5];
        readonly TextBox checkLog = new TextBox();
        readonly TextBox token = new TextBox();
        readonly Label ngrokStatus = new Label();
        readonly TextBox installLog = new TextBox();
        readonly ProgressBar progress = new ProgressBar();
        readonly Label finishTitle = new Label();
        readonly Label finishText = new Label();

        string stage;
        int pageIndex;
        bool busy, installed;
        int installExit;
        bool english;

        Color accent = Color.FromArgb(40, 103, 245);
        Color ink = Color.FromArgb(28, 31, 38);
        Color muted = Color.FromArgb(92, 100, 112);
        Color panelBg = Color.FromArgb(246, 248, 251);

        internal SetupWizard()
        {
            Text = "WebCoding 2.0.2 安装";
            WebCodingBrand.Branding.Apply(this);
            StartPosition = FormStartPosition.CenterScreen;
            using (var graphics = CreateGraphics()) dpiScale = graphics.DpiX / 96.0;
            var area = Screen.FromControl(this).WorkingArea;
            // Cap the window in physical pixels; the content can scroll at higher DPI.
            ClientSize = new Size(Math.Min(1100, area.Width - U(32)), Math.Min(Math.Max(760, U(560)), area.Height - U(64)));
            MinimumSize = new Size(Math.Min(960, Width), Math.Min(680, Height));
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            // Every pixel length comes from U(); text is measured at the process DPI.
            // Avoid mixing WinForms automatic scaling with dynamically built pages.
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;

            BuildShell();
            pages.Add(BuildWelcomePage());
            pages.Add(BuildEnvironmentPage());
            pages.Add(BuildNgrokPage());
            pages.Add(BuildInstallPage());
            pages.Add(BuildFinishPage());

            try { stage = Payload.Extract(); }
            catch (Exception e)
            {
                ShowNotice(T("无法展开安装文件：", "Unable to extract setup files: ") + e.Message, true);
                next.Enabled = false;
            }

            FormClosing += OnClosing;
            ShowPage(0);
        }

        void BuildShell()
        {
            sidebar.Dock = DockStyle.Left;
            sidebar.BackColor = panelBg;
            sidebar.Padding = new Padding(U(20), U(24), U(20), U(16));
            brand.Text = "WebCoding";
            brand.Font = new Font("Segoe UI Semibold", 16F);
            brand.ForeColor = ink;
            brand.Dock = DockStyle.Fill;
            version.Text = "2.0.2  ·  MCP 本地桥接";
            version.Font = new Font("Microsoft YaHei UI", 9F);
            version.ForeColor = muted;
            version.Dock = DockStyle.Fill;
            var sideRows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            sideRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sideRows.RowStyles.Add(new RowStyle(SizeType.Absolute, U(92)));
            sideRows.RowStyles.Add(new RowStyle(SizeType.Absolute, brand.PreferredHeight + U(8)));
            sideRows.RowStyles.Add(new RowStyle(SizeType.Absolute, version.PreferredHeight + U(20)));
            sideRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logo.Size = new Size(U(80), U(80));
            logo.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            logo.Margin = new Padding(3, 0, 0, U(12));
            sideRows.Controls.Add(logo, 0, 0);
            sideRows.Controls.Add(brand, 0, 1);
            sideRows.Controls.Add(version, 0, 2);
            sidebar.Controls.Add(sideRows);

            var stepBox = new Panel { Dock = DockStyle.Fill };
            for (int i = stepNames.Length - 1; i >= 0; i--)
            {
                int n = i;
                var row = new Label {
                    Text = (i + 1) + "   " + stepNames[i],
                    Height = U(44),
                    Dock = DockStyle.Top,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular),
                    ForeColor = muted,
                    Padding = new Padding(U(10), 0, 0, 0)
                };
                stepLabels[i] = row;
                stepBox.Controls.Add(row);
            }
            sideRows.Controls.Add(stepBox, 0, 3);

            top.Dock = DockStyle.Top;
            top.Padding = new Padding(U(24), U(20), U(24), U(12));
            title.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold);
            title.ForeColor = ink;
            title.Dock = DockStyle.Fill;
            subtitle.Font = new Font("Microsoft YaHei UI", 10F);
            subtitle.ForeColor = muted;
            subtitle.Dock = DockStyle.Fill;
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, U(112)));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, subtitle.PreferredHeight + U(6)));
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(subtitle, 0, 1);
            header.SetColumnSpan(subtitle, 2);
            top.Controls.Add(header);
            language.DropDownStyle = ComboBoxStyle.DropDownList;
            language.Items.AddRange(new object[] { "中文", "English" });
            language.SelectedIndex = 0;
            language.Width = U(100);
            language.Dock = DockStyle.Top;
            language.Margin = new Padding(U(12), U(3), 0, 0);
            language.SelectedIndexChanged += (s, e) => {
                bool value = language.SelectedIndex == 1;
                if (value != english) { english = value; RebuildPages(); }
            };
            header.Controls.Add(language, 1, 0);

            host.Dock = DockStyle.Fill;
            host.Padding = new Padding(U(24), U(4), U(24), U(10));

            notice.Dock = DockStyle.Bottom;
            notice.Height = U(36);
            notice.TextAlign = ContentAlignment.MiddleLeft;
            notice.Padding = new Padding(U(18), 0, U(10), 0);
            notice.Visible = false;

            var footer = new Panel { Dock = DockStyle.Bottom, Height = U(64), BackColor = Color.FromArgb(250, 251, 253) };
            cancel.Text = T("取消", "Cancel");
            cancel.Width = U(96); cancel.Height = U(36);
            cancel.FlatStyle = FlatStyle.System;
            cancel.Click += (s, e) => Close();

            back.Text = T("上一步", "Back");
            back.Width = U(96); back.Height = U(36);
            back.FlatStyle = FlatStyle.System;
            back.Click += (s, e) => { if (!busy && pageIndex > 0) ShowPage(pageIndex - 1); };

            next.Text = T("下一步", "Next");
            next.Width = U(118); next.Height = U(36);
            next.FlatStyle = FlatStyle.System;
            next.Click += NextClicked;

            footer.Controls.Add(cancel);
            footer.Controls.Add(back);
            footer.Controls.Add(next);
            cancel.Location = new Point(U(20), U(14));
            next.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            back.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            footer.Resize += (s, e) => {
                next.Location = new Point(footer.ClientSize.Width - next.Width - U(20), U(14));
                back.Location = new Point(next.Left - back.Width - U(10), U(14));
            };

            Controls.Add(host);
            Controls.Add(notice);
            Controls.Add(footer);
            Controls.Add(top);
            Controls.Add(sidebar);
            top.SizeChanged += (s, e) => LayoutShell();
            title.TextChanged += (s, e) => LayoutShell();
            subtitle.TextChanged += (s, e) => LayoutShell();
            LayoutShell();
        }

        void LayoutShell()
        {
            if (shellLayoutBusy) return;
            shellLayoutBusy = true;
            try
            {
                int sideWidth = Math.Max(TextRenderer.MeasureText(brand.Text, brand.Font).Width, TextRenderer.MeasureText(version.Text, version.Font).Width);
                foreach (var row in stepLabels) if (row != null) sideWidth = Math.Max(sideWidth, TextRenderer.MeasureText(row.Text, row.Font).Width + row.Padding.Horizontal);
                sidebar.Width = sideWidth + sidebar.Padding.Horizontal + U(14);
                var sideRows = (TableLayoutPanel)sidebar.Controls[0];
                sideRows.RowStyles[1].Height = brand.PreferredHeight + U(8);
                sideRows.RowStyles[2].Height = version.PreferredHeight + U(20);
                var header = (TableLayoutPanel)top.Controls[0];
                header.ColumnStyles[1].Width = Math.Max(U(112), TextRenderer.MeasureText("English", language.Font).Width + U(38));
                int textWidth = Math.Max(U(120), top.ClientSize.Width - top.Padding.Horizontal - (int)header.ColumnStyles[1].Width - U(12));
                int titleHeight = TextRenderer.MeasureText(title.Text, title.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + U(6);
                int subtitleHeight = TextRenderer.MeasureText(subtitle.Text, subtitle.Font, new Size(Math.Max(U(120), top.ClientSize.Width - top.Padding.Horizontal - U(6)), int.MaxValue), TextFormatFlags.WordBreak).Height + U(6);
                header.RowStyles[1].Height = subtitleHeight;
                top.Height = top.Padding.Vertical + Math.Max(titleHeight, language.PreferredHeight + U(6)) + subtitleHeight;
                if (notice.Visible) notice.Height = TextRenderer.MeasureText(notice.Text, notice.Font, new Size(Math.Max(U(120), notice.Width - notice.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak).Height + U(12);
            }
            finally { shellLayoutBusy = false; }
        }

        Panel Page()
        {
            return new Panel { Dock = DockStyle.Fill, BackColor = Color.White, AutoScroll = true };
        }

        void PreparePageLayout(Panel page)
        {
            if (pageLayoutBusy.ContainsKey(page)) return;
            pageLayoutBusy[page] = false;
            var items = new List<Control>();
            foreach (Control child in page.Controls) items.Add(child);
            items.Sort((a, b) => a.Top.CompareTo(b.Top));
            page.Tag = items;
            page.SizeChanged += (s, e) => LayoutPage(page);
            foreach (Control child in items)
            {
                if (child is Label) child.TextChanged += (s, e) => LayoutPage(page);
                else if (child is TextBox || child is ProgressBar || child is Button) child.Height = U(child.Height);
                var card = child as Panel;
                if (card != null) card.Padding = new Padding(U(card.Padding.Left), U(card.Padding.Top), U(card.Padding.Right), U(card.Padding.Bottom));
            }
        }

        void LayoutPage(Panel page)
        {
            if (pageLayoutBusy[page]) return;
            pageLayoutBusy[page] = true;
            page.SuspendLayout();
            try
            {
                int width = Math.Max(U(100), page.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - U(12));
                int y = U(8) - page.VerticalScroll.Value;
                foreach (Control child in (List<Control>)page.Tag)
                {
                    child.Left = U(4);
                    child.Top = y;
                    if (child is Button)
                        child.Width = Math.Min(width, Math.Max(U(100), TextRenderer.MeasureText(child.Text, child.Font).Width + U(28)));
                    else child.Width = width;
                    var label = child as Label;
                    if (label != null)
                        child.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + U(8);
                    var card = child as Panel;
                    if (card != null)
                    {
                        int height = card.Padding.Vertical;
                        foreach (Control text in card.Controls)
                        {
                            int lines = TextRenderer.MeasureText(text.Text, text.Font, new Size(Math.Max(U(40), width - card.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak).Height + U(6);
                            if (text.Dock == DockStyle.Top) text.Height = lines;
                            height += lines;
                        }
                        card.Height = height;
                    }
                    y += child.Height + U(16);
                }
                page.AutoScrollMinSize = new Size(0, y + page.VerticalScroll.Value);
            }
            finally { page.ResumeLayout(); pageLayoutBusy[page] = false; }
        }

        string T(string zh, string en) { return english ? en : zh; }

        string Localize(string text)
        {
            if (!english || string.IsNullOrEmpty(text)) return text;
            var map = new Dictionary<string, string>(StringComparer.Ordinal) {
                {"欢迎", "Welcome"}, {"环境", "Environment"}, {"安装", "Install"}, {"完成", "Finish"},
                {"WebCoding 2.0.2 安装", "WebCoding 2.0.2 Setup"}, {"MCP 本地桥接", "Local MCP bridge"},
                {"取消", "Cancel"}, {"上一步", "Back"}, {"下一步", "Next"}, {"开始", "Start"}, {"开始安装", "Install"},
                {"欢迎使用 WebCoding", "Welcome to WebCoding"}, {"检查运行环境", "Check your environment"}, {"连接 ngrok（可选）", "Connect ngrok (optional)"},
                {"安装 WebCoding", "Install WebCoding"}, {"可以开始使用了", "You are ready to go"},
                {"一个轻量的本地 MCP 桥接工具", "A lightweight local MCP bridge"}, {"确认系统状态，并说明安装器会自动补齐什么", "Review system status and missing components"},
                {"只需打开官方页面并粘贴 token；也可以现在跳过", "Paste a token from the official page, or skip for now"},
                {"所有进度都留在这一个窗口里", "All progress stays in this window"}, {"正常使用只需要记住一个命令：webcoding", "You only need one command: webcoding"},
                {"重新检查", "Check again"}, {"打开 ngrok 官方 token 页面", "Open the ngrok token page"}, {"authtoken", "authtoken"},
                {"正在检测是否已有 ngrok 配置…", "Checking for an existing ngrok configuration…"}, {"安装完成", "Installation complete"},
                {"以后需要时：打开工具与规则设置", "Open tools and rules settings"}
            };
            string value; return map.TryGetValue(text, out value) ? value : text;
        }

        void LocalizeControls(Control root)
        {
            foreach (Control child in root.Controls)
            {
                if (!(child is TextBox) && !(child is ComboBox)) child.Text = Localize(child.Text);
                LocalizeControls(child);
            }
        }

        void RebuildPages()
        {
            int current = pageIndex;
            pages.Clear();
            pages.Add(BuildWelcomePage()); pages.Add(BuildEnvironmentPage()); pages.Add(BuildNgrokPage());
            pages.Add(BuildInstallPage()); pages.Add(BuildFinishPage());
            ShowPage(Math.Min(current, pages.Count - 1));
        }
        Label Paragraph(string text, int top, int height, float size = 10F, bool bold = false)
        {
            return new Label {
                Text = text,
                Left = 4,
                Top = top,
                Width = 760,
                Height = Math.Max(height, TextRenderer.MeasureText(text, new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), new Size(760, 1000), TextFormatFlags.WordBreak).Height + 4),
                AutoSize = false,
                ForeColor = bold ? ink : muted,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
            };
        }

        Panel Card(string heading, string body, int top)
        {
            var card = new Panel {
                Left = 4, Top = top, Width = 760, Height = 108,
                BackColor = panelBg,
                Padding = new Padding(18, 14, 18, 10)
            };
            var h = new Label {
                Text = heading, Dock = DockStyle.Top, Height = 28,
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), ForeColor = ink
            };
            var b = new Label {
                Text = body, Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 9.5F), ForeColor = muted
            };
            b.AutoEllipsis = false;
            card.Controls.Add(b); card.Controls.Add(h);
            return card;
        }

        Panel BuildWelcomePage()
        {
            var p = Page();
            p.Controls.Add(Paragraph(T("把当前项目安全地映射成一个 Remote MCP 地址，让支持 MCP 的 Web Agent 读写文件、运行命令。", "Securely expose the current project as a Remote MCP address so MCP-capable web agents can read files and run commands."), 8, 56, 11F));
            p.Controls.Add(Card(T("安装内容", "Installation"), T("WebCoding 主程序、Node.js（需要时）、Cloudflare、ngrok，以及独立卸载程序。已有可用组件会直接复用。", "WebCoding, Node.js when needed, Cloudflare, ngrok, and the uninstaller. Existing components are reused."), 84));
            p.Controls.Add(Card(T("不会做什么", "What it will not do"), T("不会删除或移动你的项目；不会要求把 ngrok token 发给 WebCoding 服务端；默认按当前 Windows 用户权限运行。", "It will not delete or move your project, send your ngrok token to a WebCoding server, or run outside your Windows user account."), 204));
            p.Controls.Add(Card(T("安装位置", "Install location"), T("%LOCALAPPDATA%\\WebCoding，并为当前用户添加 webcoding 命令。", "%LOCALAPPDATA%\\WebCoding; the webcoding command is added for the current user."), 324));
            return p;
        }

        Panel BuildEnvironmentPage()
        {
            var p = Page();
            p.Controls.Add(Paragraph("安装前先检查电脑环境。缺失的组件会在真正安装时从官方来源补齐。", 4, 44));
            string[] names = { "Windows / .NET", "Node.js", "Cloudflare", "ngrok", "安装范围" };
            for (int i = 0; i < names.Length; i++)
            {
                var row = new Label {
                    Left = 4, Top = 58 + i * 42, Width = 760, Height = 34,
                    Text = "○  " + names[i] + "：等待检查",
                    Font = new Font("Microsoft YaHei UI", 10F),
                    ForeColor = muted,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                envRows[i] = row;
                p.Controls.Add(row);
            }
            var refresh = new Button { Text = "重新检查", Left = 4, Top = 280, Width = 110, Height = 34 };
            refresh.Click += (s, e) => StartEnvironmentCheck();
            p.Controls.Add(refresh);
            checkLog.Left = 4; checkLog.Top = 328; checkLog.Width = 760; checkLog.Height = 115;
            checkLog.Multiline = true; checkLog.ReadOnly = true; checkLog.ScrollBars = ScrollBars.Vertical;
            checkLog.BackColor = Color.White; checkLog.BorderStyle = BorderStyle.FixedSingle;
            checkLog.Font = new Font("Consolas", 9F);
            p.Controls.Add(checkLog);
            return p;
        }

        Panel BuildNgrokPage()
        {
            var p = Page();
            p.Controls.Add(Paragraph("ngrok 是可选项。Cloudflare 不需要账号；如果你想使用 ngrok，只需在官方页面注册并把 authtoken 粘贴回来。", 4, 58, 10.5F));
            var open = new Button { Text = "打开 ngrok 官方 token 页面", Left = 4, Top = 78, Width = 220, Height = 38 };
            open.Click += (s, e) => OpenNgrokPage();
            p.Controls.Add(open);

            var label = Paragraph("authtoken", 138, 30, 10F, true);
            p.Controls.Add(label);
            token.Left = 4; token.Top = 170; token.Width = 760; token.Height = 34;
            token.UseSystemPasswordChar = true;
            token.Font = new Font("Segoe UI", 10F);
            p.Controls.Add(token);

            p.Controls.Add(Paragraph("可以直接 Ctrl+V 粘贴。安装完成后由 WebCoding 调用 ngrok 本机客户端写入官方配置；不会写进 WebCoding 日志或 MCP 链接。留空就是稍后配置。", 216, 66));
            ngrokStatus.Left = 4; ngrokStatus.Top = 296; ngrokStatus.Width = 760; ngrokStatus.Height = 42;
            ngrokStatus.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            ngrokStatus.ForeColor = muted;
            ngrokStatus.Text = "正在检测是否已有 ngrok 配置…";
            p.Controls.Add(ngrokStatus);

            p.Controls.Add(Card("什么时候必须配置？", "只有选择 ngrok 作为隧道时才需要。即使现在跳过，第一次在 webcoding 里选择 ngrok 时仍会自动引导。", 354));
            return p;
        }

        Panel BuildInstallPage()
        {
            var p = Page();
            p.Controls.Add(Paragraph("准备就绪。点击“开始安装”后，窗口会显示每一步进度；安装过程中不再弹出额外确认框。", 4, 52, 10.5F));
            progress.Left = 4; progress.Top = 72; progress.Width = 760; progress.Height = 14;
            progress.Style = ProgressBarStyle.Blocks;
            p.Controls.Add(progress);
            installLog.Left = 4; installLog.Top = 108; installLog.Width = 760; installLog.Height = 330;
            installLog.Multiline = true; installLog.ReadOnly = true; installLog.ScrollBars = ScrollBars.Vertical;
            installLog.BackColor = Color.FromArgb(250, 251, 253);
            installLog.BorderStyle = BorderStyle.FixedSingle;
            installLog.Font = new Font("Consolas", 9F);
            p.Controls.Add(installLog);
            return p;
        }

        Panel BuildFinishPage()
        {
            var p = Page();
            finishTitle.Left = 4; finishTitle.Top = 34; finishTitle.Width = 760; finishTitle.Height = 58;
            finishTitle.Font = new Font("Microsoft YaHei UI", 22F, FontStyle.Bold);
            finishTitle.ForeColor = Color.FromArgb(34, 139, 77);
            finishTitle.Text = T("安装完成", "Installation complete");
            p.Controls.Add(finishTitle);

            finishText.Left = 4; finishText.Top = 118; finishText.Width = 760; finishText.Height = 180;
            finishText.Font = new Font("Microsoft YaHei UI", 11F);
            finishText.ForeColor = ink;
            finishText.Text = T("接下来只需要：\r\n\r\n1. 关闭旧终端并重新打开。\r\n2. 进入你的项目文件夹。\r\n3. 在工作区文件夹的命令行输入 webcoding 启动隧道。\r\n\r\n第一次启动会让你用 1–4 选择隧道，以后会自动记住。", "Next steps:\r\n\r\n1. Close and reopen your terminal.\r\n2. Open a command line in your workspace folder.\r\n3. Enter webcoding to start the tunnel.\r\n\r\nThe first launch lets you choose a tunnel (1–4); your choice is remembered.");
            p.Controls.Add(finishText);

            var config = new Button { Text = "以后需要时：打开工具与规则设置", Left = 4, Top = 330, Width = 250, Height = 38 };
            config.Click += (s, e) => {
                string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebCoding", "bin", "webcoding.exe");
                if (File.Exists(file)) Process.Start(new ProcessStartInfo(file, "--config") { UseShellExecute = true });
            };
            p.Controls.Add(config);
            return p;
        }

        void ShowPage(int index)
        {
            if (index < 0 || index >= pages.Count) return;
            pageIndex = index;
            host.Controls.Clear();
            host.Controls.Add(pages[index]);
            pages[index].BringToFront();
            PreparePageLayout(pages[index]);

            string[] titles = {
                "欢迎使用 WebCoding",
                "检查运行环境",
                "连接 ngrok（可选）",
                "安装 WebCoding",
                "可以开始使用了"
            };
            string[] subtitles = {
                "一个轻量的本地 MCP 桥接工具",
                "确认系统状态，并说明安装器会自动补齐什么",
                "只需打开官方页面并粘贴 token；也可以现在跳过",
                "所有进度都留在这一个窗口里",
                "正常使用只需要记住一个命令：webcoding"
            };
            title.Text = Localize(titles[index]);
            subtitle.Text = Localize(subtitles[index]);

            for (int i = 0; i < stepLabels.Length; i++)
            {
                stepLabels[i].BackColor = i == index ? Color.White : panelBg;
                stepLabels[i].ForeColor = i == index ? accent : (i < index ? ink : muted);
                stepLabels[i].Font = new Font("Microsoft YaHei UI", 10F, i == index ? FontStyle.Bold : FontStyle.Regular);
            }

            back.Visible = index > 0 && index < 4;
            cancel.Visible = index < 4;
            next.Visible = true;
            next.Text = index == 0 ? "开始" : index == 3 ? "开始安装" : index == 4 ? "完成" : "下一步";
            Text = T("WebCoding 2.0.2 安装", "WebCoding 2.0.2 Setup");
            cancel.Text = T("取消", "Cancel");
            back.Text = T("上一步", "Back");
            for (int i = 0; i < stepLabels.Length; i++) stepLabels[i].Text = (i + 1) + "   " + Localize(stepNames[i]);
            LocalizeControls(pages[index]);
            next.Text = Localize(next.Text);
            LayoutShell();
            LayoutPage(pages[index]);

            if (index == 1) StartEnvironmentCheck();
            if (index == 2) CheckNgrokStatus();
            if (index == 4) back.Visible = false;

            ClearNotice();
        }

        void NextClicked(object sender, EventArgs e)
        {
            if (busy) return;
            if (pageIndex == 4) { Close(); return; }
            if (pageIndex == 3)
            {
                StartInstall();
                return;
            }
            ShowPage(pageIndex + 1);
        }

        void StartEnvironmentCheck()
        {
            if (busy || string.IsNullOrWhiteSpace(stage)) return;
            busy = true;
            checkLog.Clear();
            for (int i = 0; i < envRows.Length; i++)
                envRows[i].Text = "○  " + new[] { "Windows / .NET", "Node.js", "Cloudflare", "ngrok", "安装范围" }[i] + "：检查中…";

            ThreadPool.QueueUserWorkItem(_ => {
                int code = RunPowerShell("-CheckOnly", line => BeginInvoke((Action)(() => ApplyCheckLine(line))));
                BeginInvoke((Action)(() => {
                    envRows[4].Text = "●  安装范围：当前用户 · %LOCALAPPDATA%\\WebCoding";
                    envRows[4].ForeColor = ink;
                    busy = false;
                    if (code != 0) ShowNotice("环境检查没有完成。可以查看下方详情，修复后重新检查。", true);
                }));
            });
        }

        void ApplyCheckLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            checkLog.AppendText(line + Environment.NewLine);
            if (line.StartsWith("Windows x64：") || line.StartsWith(".NET Framework"))
            {
                envRows[0].Text = "●  Windows / .NET：" + line.Replace("Windows x64：", "").Replace(".NET Framework 4.8+：", "");
                envRows[0].ForeColor = ink;
            }
            else if (line.StartsWith("Node.js"))
            {
                envRows[1].Text = "●  " + line;
                envRows[1].ForeColor = ink;
            }
            else if (line.StartsWith("cloudflared"))
            {
                envRows[2].Text = "●  " + line;
                envRows[2].ForeColor = ink;
            }
            else if (line.StartsWith("ngrok"))
            {
                envRows[3].Text = "●  " + line;
                envRows[3].ForeColor = ink;
            }
        }

        void CheckNgrokStatus()
        {
            ThreadPool.QueueUserWorkItem(_ => {
                string ngrok = FindNgrok();
                bool ok = !string.IsNullOrWhiteSpace(ngrok) && File.Exists(ngrok) && RunQuiet(ngrok, "config check") == 0;
                BeginInvoke((Action)(() => {
                    ngrokStatus.Text = ok ? "✓ 已检测到可用的 ngrok 配置；可以保持 token 输入框为空。" : "○ 目前未检测到 ngrok 认证配置；现在粘贴 token 或稍后再配置都可以。";
                    ngrokStatus.ForeColor = ok ? Color.FromArgb(34, 139, 77) : muted;
                }));
            });
        }

        void StartInstall()
        {
            if (busy) return;
            busy = true;
            progress.Style = ProgressBarStyle.Marquee;
            installLog.Clear();
            next.Enabled = false; back.Enabled = false; cancel.Enabled = false;
            ClearNotice();

            ThreadPool.QueueUserWorkItem(_ => {
                int code = RunPowerShell("", line => BeginInvoke((Action)(() => {
                    installLog.AppendText(line + Environment.NewLine);
                    installLog.SelectionStart = installLog.TextLength;
                    installLog.ScrollToCaret();
                })));

                bool ngrokOk = true;
                string provided = null;
                BeginInvoke((Action)(() => provided = token.Text.Trim()));
                // Allow the UI thread to copy the token value.
                int spin = 0;
                while (provided == null && spin++ < 100) Thread.Sleep(10);

                if ((code == 0 || code == 3010) && !string.IsNullOrWhiteSpace(provided))
                {
                    string ngrok = FindNgrok();
                    string ignored;
                    if (string.IsNullOrWhiteSpace(ngrok) || !File.Exists(ngrok) || RunCapture(ngrok, "config add-authtoken " + Payload.Quote(provided), out ignored) != 0 ||
                        RunCapture(ngrok, "config check", out ignored) != 0)
                        ngrokOk = false;
                    provided = null;
                }

                installExit = code;
                installed = (code == 0 || code == 3010) && ngrokOk;
                BeginInvoke((Action)(() => {
                    progress.Style = ProgressBarStyle.Blocks;
                    progress.Value = installed ? 100 : 0;
                    busy = false;
                    next.Enabled = true; back.Enabled = true; cancel.Enabled = true;
                    if (installed)
                    {
                        if (installExit == 3010)
                            finishText.Text += "\r\n\r\n注意：.NET 安装要求先重启电脑。";
                        ShowPage(4);
                    }
                    else
                    {
                        ShowNotice(ngrokOk ? "安装没有完成。请查看上方日志后重试。" : "WebCoding 已安装，但 ngrok token 配置失败。你可以返回 ngrok 页面重新粘贴，也可以先跳过。", true);
                    }
                }));
            });
        }

        int RunPowerShell(string extra, Action<string> output)
        {
            try
            {
                string args = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " +
                    Payload.Quote(Path.Combine(stage, "Install.ps1")) +
                    " -PayloadDirectory " + Payload.Quote(stage) +
                    (string.IsNullOrWhiteSpace(extra) ? "" : " " + extra) + " -Language " + (english ? "en" : "zh");
                var info = new ProcessStartInfo(Payload.PowerShell(), args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = stage
                };
                using (var p = new Process { StartInfo = info })
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) output(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) output(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception e)
            {
                output("安装进程启动失败：" + e.Message);
                return 1;
            }
        }

        static string FindNgrok()
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WebCoding", "runtime", "ngrok", "ngrok.exe");
            if (File.Exists(local)) return local;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string candidate = Path.Combine((dir ?? "").Trim().Trim('"'), "ngrok.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            foreach (string baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                try
                {
                    string candidate = Path.Combine(baseDir, "ngrok", "ngrok.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        static int RunQuiet(string exe, string args)
        {
            string ignored;
            return RunCapture(exe, args, out ignored);
        }

        static int RunCapture(string exe, string args, out string output)
        {
            output = "";
            try
            {
                var info = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(info))
                {
                    string a = p.StandardOutput.ReadToEnd();
                    string b = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    output = (a + Environment.NewLine + b).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception e) { output = e.Message; return -1; }
        }

        void OpenNgrokPage()
        {
            try { Process.Start(new ProcessStartInfo("https://dashboard.ngrok.com/get-started/your-authtoken") { UseShellExecute = true }); }
            catch { ShowNotice("无法自动打开浏览器。请访问 dashboard.ngrok.com/get-started/your-authtoken", true); }
        }

        void ShowNotice(string text, bool error)
        {
            notice.Text = text;
            notice.BackColor = error ? Color.FromArgb(255, 238, 238) : Color.FromArgb(235, 246, 255);
            notice.ForeColor = error ? Color.FromArgb(170, 40, 40) : Color.FromArgb(30, 85, 150);
            notice.Visible = true;
            LayoutShell();
        }

        void ClearNotice()
        {
            notice.Visible = false;
            notice.Text = "";
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (busy)
            {
                e.Cancel = true;
                ShowNotice("安装正在进行，请等当前步骤完成后再关闭窗口。", true);
                return;
            }
            Payload.Cleanup(stage);
            ExitCode = installed || pageIndex < 3 ? 0 : installExit;
        }
    }
}
