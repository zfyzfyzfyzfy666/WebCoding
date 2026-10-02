using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("WebCoding")]
[assembly: AssemblyProduct("WebCoding")]
[assembly: AssemblyDescription("Minimal single-folder MCP link utility")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion("2.0.2.0")]
[assembly: AssemblyFileVersion("2.0.2.0")]

namespace WebCoding
{
    internal static class Program
    {
        static readonly ManualResetEvent Quit = new ManualResetEvent(false);
        static readonly AutoResetEvent SwitchRequested = new AutoResetEvent(false);
        static readonly object SwitchLock = new object();
        static readonly object ChildLock = new object();
        static readonly List<Process> Children = new List<Process>();
        static IntPtr job;
        static string tempDirectory;
        static string pendingProvider;
        static int cleaned;
        static bool englishLogs;

        sealed class TunnelSession
        {
            public string Provider;
            public string Mode;
            public string Link;
            public string Detail;
            public Process Process;
        }

        sealed class LocalizedWriter : TextWriter
        {
            readonly TextWriter inner;
            readonly Dictionary<string, string> map = new Dictionary<string, string> {
                {"正在启动目录映射", "Starting workspace mapping"}, {"正在创建公网链接", "Creating public link"}, {"活动日志", "Activity log"},
                {"保持此窗口开启", "Keep this window open"}, {"连接", "Connection"}, {"切换", "Switch"}, {"默认", "Default"},
                {"链接已复制到剪贴板。", "Link copied to clipboard."}, {"剪贴板暂时不可用，请手动复制上面的链接。", "Clipboard unavailable; copy the link above manually."},
                {"第一次使用，请选择公网启动方式：", "Choose a public tunnel for the first launch:"}, {"已记为默认；以后直接输入 webcoding 即可。", "Saved as default; enter webcoding next time."},
                {"ngrok 需要免费账号和 authtoken。", "ngrok requires a free account and authtoken."}, {"安装成功。", "Installation succeeded."},
                {"错误：", "Error: "}, {"未找到", "Not found: "}, {"请重新运行 WebCoding-Setup.exe。", "Run WebCoding-Setup.exe again."}
            };
            public LocalizedWriter(TextWriter inner) { this.inner = inner; }
            public override Encoding Encoding { get { return inner.Encoding; } }
            string Translate(string value) { if (!englishLogs || value == null) return value; foreach (var item in map) value = value.Replace(item.Key, item.Value); return value; }
            public override void WriteLine(string value) { inner.WriteLine(Translate(value)); }
            public override void Write(string value) { inner.Write(Translate(value)); }
        }

        static void ConfigureLanguage(ref string[] args)
        {
            string selected = null;
            try { selected = File.ReadAllText(Path.Combine(InstallHome(), "language.txt"), Encoding.UTF8).Trim().ToLowerInvariant(); } catch { }
            var kept = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--language" && i + 1 < args.Length) { selected = args[++i]; continue; }
                if (arg.StartsWith("--language=", StringComparison.OrdinalIgnoreCase)) { selected = arg.Substring(11); continue; }
                kept.Add(arg);
            }
            englishLogs = selected == "en" || selected == "english";
            if (englishLogs) { Console.SetOut(new LocalizedWriter(Console.Out)); Console.SetError(new LocalizedWriter(Console.Error)); }
            args = kept.ToArray();
        }
        [STAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            ConfigureLanguage(ref args);
            try { Console.Title = "WebCoding"; } catch { }

            if (args.Length == 1 && (args[0] == "--config" || args[0] == "--uninstall" || args[0] == "--ngrok-setup"))
            {
                try
                {
                    string home = InstallHome();
                    if (args[0] == "--ngrok-setup") return GuideNgrok();
                    string executable = args[0] == "--config"
                        ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebCoding.Config.exe")
                        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebCoding-Uninstall.exe");
                    string arguments = args[0] == "--config" ? Quote(home) : "";
                    if (args[0] == "--uninstall")
                    {
                        if (!File.Exists(executable)) throw new IOException("未找到图形卸载程序，请重新安装 WebCoding。");
                        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                        return 0;
                    }
                    using (var editor = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false }))
                    {
                        editor.WaitForExit();
                        return editor.ExitCode;
                    }
                }
                catch (Exception e) { return Error(e.Message); }
            }

            bool local = false, copy = true, debugTunnel = false;
            string root = null, protocolMode = "auto", providerOverride = null, switchOnly = null, setDefault = null;
            bool protocolSpecified = false, providerSpecified = false;

            for (int argIndex = 0; argIndex < args.Length; argIndex++)
            {
                string arg = args[argIndex];
                if (arg == "--help" || arg == "-h" || arg == "/?")
                {
                    PrintHelp();
                    return 0;
                }
                if (arg == "--protocol" || arg.StartsWith("--protocol=", StringComparison.Ordinal))
                {
                    if (protocolSpecified) return Error("--protocol 不能重复指定。");
                    protocolMode = ReadOptionValue(args, ref argIndex, arg, "--protocol", "auto、http2、prefer-http2、quic").ToLowerInvariant();
                    try { GetProtocols(protocolMode); } catch (ArgumentException e) { return Error(e.Message); }
                    protocolSpecified = true;
                }
                else if (arg == "--tunnel" || arg.StartsWith("--tunnel=", StringComparison.Ordinal))
                {
                    if (providerSpecified) return Error("--tunnel 不能重复指定。");
                    providerOverride = NormalizeProvider(ReadOptionValue(args, ref argIndex, arg, "--tunnel", "cloudflare、ngrok"));
                    providerSpecified = true;
                }
                else if (arg == "--switch" || arg.StartsWith("--switch=", StringComparison.Ordinal))
                {
                    if (switchOnly != null) return Error("--switch 不能重复指定。");
                    switchOnly = NormalizeProvider(ReadOptionValue(args, ref argIndex, arg, "--switch", "cloudflare、ngrok"));
                }
                else if (arg == "--set-tunnel" || arg.StartsWith("--set-tunnel=", StringComparison.Ordinal))
                {
                    if (setDefault != null) return Error("--set-tunnel 不能重复指定。");
                    setDefault = NormalizeProvider(ReadOptionValue(args, ref argIndex, arg, "--set-tunnel", "cloudflare、ngrok"));
                }
                else if (arg == "--local-only") local = true;
                else if (arg == "--no-clipboard") copy = false;
                else if (arg == "--debug-tunnel") debugTunnel = true;
                else if (arg.StartsWith("--", StringComparison.Ordinal)) return Error("不支持的选项，请用 --help 查看用法。");
                else if (root == null) root = arg;
                else return Error("一次只映射一个文件夹。");
            }

            try
            {
                root = Path.GetFullPath(root ?? Environment.CurrentDirectory);
                if (!Directory.Exists(root)) return Error("指定的文件夹不存在。");

                if (setDefault != null)
                {
                    if (switchOnly != null || local || providerSpecified || protocolSpecified) return Error("--set-tunnel 不能与映射/切换选项同时使用。");
                    string defaultMode = setDefault == "ngrok" ? "ngrok" : "cloudflare-auto";
                    WriteDefaultMode(defaultMode);
                    Console.WriteLine("默认启动方式已设置为 " + ModeLabel(defaultMode) + "。");
                    return 0;
                }

                if (switchOnly != null)
                {
                    if (local || providerSpecified || protocolSpecified) return Error("--switch 只用于控制已运行的映射，不能与 --local-only/--tunnel/--protocol 同时使用。");
                    return SendSwitch(root, switchOnly);
                }

                string mode = null;
                if (!local)
                {
                    if (providerOverride != null)
                    {
                        if (providerOverride == "ngrok" && protocolSpecified) return Error("--protocol 只适用于 Cloudflare；ngrok 请不要指定该选项。");
                        mode = providerOverride == "ngrok" ? "ngrok" : ProtocolToMode(protocolMode);
                    }
                    else
                    {
                        mode = ReadDefaultMode();
                        if (mode == null) mode = PromptModeAndSave();
                        else mode = PromptStartupOverride(mode);
                    }
                }
                if (local && (protocolSpecified || providerSpecified)) return Error("--local-only 不创建公网隧道，不能同时指定 --protocol 或 --tunnel。");
                string provider = local ? null : ModeProvider(mode);
                protocolMode = local ? protocolMode : ModeProtocol(mode);

                string node = FindTool("node.exe");
                if (node == null) return Error("未找到 Node.js 18+。请重新运行 WebCoding-Setup.exe 配置环境。");
                if (!local && FindTunnelTool(provider) == null)
                    return Error("未找到 " + ProviderLabel(provider) + "。请重新运行 WebCoding-Setup.exe，或改用 --tunnel " + (provider == "ngrok" ? "cloudflare" : "ngrok") + "。");

                CreateJob();
                Console.CancelKeyPress += (sender, e) => { e.Cancel = true; Quit.Set(); };
                AppDomain.CurrentDomain.ProcessExit += (sender, e) => Cleanup();

                tempDirectory = Path.Combine(Path.GetTempPath(), "WebCoding-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);
                string script = Path.Combine(tempDirectory, "bridge.js");
                using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("WebCoding.bridge.js"))
                using (FileStream dest = File.Create(script))
                {
                    if (source == null) throw new IOException("内置服务脚本缺失。");
                    source.CopyTo(dest);
                }

                byte[] random = new byte[24];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
                string token = BitConverter.ToString(random).Replace("-", "").ToLowerInvariant();

                Console.WriteLine("正在启动目录映射...");
                int port = 0;
                var nodeReady = new ManualResetEvent(false);
                Process server = Spawn(node, "--max-old-space-size=256 " + Quote(script), root,
                    line =>
                    {
                        var match = Regex.Match(line ?? "", "^READY:([0-9]+)$");
                        if (match.Success) { port = int.Parse(match.Groups[1].Value); nodeReady.Set(); }
                        else if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine(line);
                    },
                    new Dictionary<string, string>
                    {
                        {"FL_ROOT", root},
                        {"FL_TOKEN", token},
                        {"WC_HOME", InstallHome()},
                        {"WC_NATIVE", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebCoding.Native.exe")}
                    });

                var until = DateTime.UtcNow.AddSeconds(20);
                while (!nodeReady.WaitOne(100))
                {
                    if (Quit.WaitOne(0)) return 0;
                    if (server.HasExited || DateTime.UtcNow >= until)
                        throw new IOException("本地服务启动失败。请确认 Node.js 版本不低于 18。");
                }

                StartControlServer(root);

                string link;
                TunnelSession tunnel = null;
                if (local)
                {
                    link = "http://127.0.0.1:" + port + "/mcp/" + token;
                }
                else
                {
                    if (mode == "ngrok" && !EnsureNgrokConfigured(true)) return Error("ngrok 尚未完成认证。");
                    Console.WriteLine("正在创建公网链接（" + ModeLabel(mode) + "）...");
                    tunnel = StartTunnelMode(mode, port, token, debugTunnel);
                    link = tunnel.Link;
                }

                if (!CheckEndpoint(link)) throw new IOException("链接验证失败，未发布不可用地址。");
                PrintLink(link, copy, tunnel);
                Console.WriteLine("活动日志：" + Path.GetFullPath(Path.Combine(InstallHome(), "logs", "activity.jsonl")));
                if (debugTunnel) Console.WriteLine("已开启详细隧道诊断：logs\\tunnel-debug.log");
                if (local)
                    Console.WriteLine("保持此窗口开启；关闭即停止映射。Ctrl+C 或 Q 退出，C 再次复制。");
                else
                    Console.WriteLine("保持此窗口开启；1=ngrok，2=Cloudflare 自动，3=QUIC，4=HTTP/2，D=设当前为默认，C=复制，Q=退出。");

                bool tunnelStoppedNotice = false;
                while (!Quit.WaitOne(150))
                {
                    if (server.HasExited) throw new IOException("本地 MCP 服务已停止。请重新启动程序。");

                    string requested = null;
                    if (SwitchRequested.WaitOne(0))
                    {
                        lock (SwitchLock) { requested = pendingProvider; pendingProvider = null; }
                    }

                    if (!local && requested != null)
                    {
                        if (tunnel != null && !tunnel.Process.HasExited && tunnel.Mode == requested)
                        {
                            Console.WriteLine("[切换] 当前已经是 " + ModeLabel(requested) + "。");
                        }
                        else
                        {
                            Console.WriteLine("[切换] 正在启动 " + ModeLabel(requested) + "，旧链接会保持到新链接验证成功...");
                            try
                            {
                                if (requested == "ngrok" && !EnsureNgrokConfigured(true)) throw new IOException("ngrok 尚未完成认证。");
                                var next = StartTunnelMode(requested, port, token, debugTunnel);
                                Process old = tunnel == null ? null : tunnel.Process;
                                tunnel = next;
                                mode = requested;
                                provider = next.Provider;
                                link = next.Link;
                                tunnelStoppedNotice = false;
                                PrintLink(link, copy, tunnel);
                                if (old != null && old != next.Process) Stop(old);
                                Console.WriteLine("[切换] 完成。MCP 本地服务和访问 token 未重启，只更换公网入口。");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("[切换] 失败：" + ex.Message);
                                if (tunnel != null && !tunnel.Process.HasExited)
                                    Console.WriteLine("[切换] 原 " + ProviderLabel(tunnel.Provider) + " 链接继续使用。");
                            }
                        }
                    }

                    if (!local && tunnel != null && tunnel.Process.HasExited && !tunnelStoppedNotice)
                    {
                        tunnelStoppedNotice = true;
                        Console.WriteLine("[连接] " + ProviderLabel(tunnel.Provider) + " 隧道进程已停止；本地 MCP 仍在运行。可按 N/F 或使用 webcoding --switch ... 恢复公网入口。");
                    }

                    if (!Console.IsInputRedirected && Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true).Key;
                        if (key == ConsoleKey.Q || key == ConsoleKey.Escape) Quit.Set();
                        else if (key == ConsoleKey.C) Console.WriteLine(CopyLink(link) ? "链接已复制。" : "无法复制，请手动选择链接。");
                        else if (!local && key == ConsoleKey.D1) QueueSwitch("ngrok");
                        else if (!local && key == ConsoleKey.D2) QueueSwitch("cloudflare-auto");
                        else if (!local && key == ConsoleKey.D3) QueueSwitch("cloudflare-quic");
                        else if (!local && key == ConsoleKey.D4) QueueSwitch("cloudflare-http2");
                        else if (!local && key == ConsoleKey.D && tunnel != null) { WriteDefaultMode(tunnel.Mode); Console.WriteLine("[默认] 已设为 " + ModeLabel(tunnel.Mode) + "。"); }
                    }
                }
                return 0;
            }
            catch (Exception e) { return Error(e.Message); }
            finally { Cleanup(); }
        }

        static void PrintHelp()
        {
            if (englishLogs)
            {
                Console.WriteLine("WebCoding 2.0.2");
                Console.WriteLine("webcoding [folder] [--tunnel cloudflare|ngrok] [--protocol MODE] [--local-only] [--no-clipboard] [--debug-tunnel] [--language zh|en]");
                Console.WriteLine("webcoding --switch cloudflare|ngrok [folder]");
                Console.WriteLine("webcoding --set-tunnel cloudflare|ngrok");
                Console.WriteLine("webcoding --ngrok-setup | --config | --uninstall");
                Console.WriteLine();
                Console.WriteLine("Enter webcoding in a workspace folder; the first launch asks you to choose a tunnel and remembers it.");
                Console.WriteLine("--language zh|en     Select CLI log language (the installer selection is used by default).");
                Console.WriteLine("--tunnel / --switch / --set-tunnel are available for scripts and debugging.");
                Console.WriteLine("--ngrok-setup        Open the ngrok page and configure an authtoken.");
                Console.WriteLine("--protocol auto|http2|prefer-http2|quic");
                Console.WriteLine();
                Console.WriteLine("Hotkeys: 1=ngrok, 2=Cloudflare auto, 3=QUIC, 4=HTTP/2, D=set default, C=copy, Q=quit.");
                return;
            }
            Console.WriteLine("WebCoding 2.0.2");
            Console.WriteLine("webcoding [folder] [--tunnel cloudflare|ngrok] [--protocol MODE] [--local-only] [--no-clipboard] [--debug-tunnel]");
            Console.WriteLine("webcoding --switch cloudflare|ngrok [folder]        # 高级/脚本用");
            Console.WriteLine("webcoding --set-tunnel cloudflare|ngrok            # 高级/脚本用");
            Console.WriteLine("webcoding --ngrok-setup | --config | --uninstall");
            Console.WriteLine();
            Console.WriteLine("正常使用只需输入 webcoding：首次用菜单选择并记住启动方式。");
            Console.WriteLine("--tunnel / --switch / --set-tunnel 仍保留给脚本和调试。");
            Console.WriteLine("--ngrok-setup      打开 ngrok 官方页面，直接粘贴 token，由 WebCoding 自动配置。");
            Console.WriteLine("--protocol auto          Cloudflare：先 QUIC，失败再尝试 HTTP/2");
            Console.WriteLine("--protocol http2         Cloudflare：固定 HTTP/2（TCP）");
            Console.WriteLine("--protocol prefer-http2  Cloudflare：先 HTTP/2，失败再尝试 QUIC");
            Console.WriteLine("--protocol quic          Cloudflare：固定 QUIC（UDP）");
            Console.WriteLine();
            Console.WriteLine("运行中热键：1=ngrok，2=Cloudflare 自动，3=QUIC，4=HTTP/2，D=设当前为默认，C=复制，Q=退出。");
            Console.WriteLine("ngrok 首次使用需要免费账号和 authtoken；Cloudflare Quick Tunnel 不需要账号。");
            Console.WriteLine("链接相当于访问密钥。终端工具具有当前 Windows 用户权限，请勿公开分享。");
        }

        static string ReadOptionValue(string[] args, ref int index, string arg, string name, string choices)
        {
            if (arg == name)
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(name + " 缺少值，可选 " + choices + "。");
                return args[++index];
            }
            return arg.Substring((name + "=").Length);
        }

        static string NormalizeProvider(string provider)
        {
            string value = (provider ?? "").Trim().ToLowerInvariant();
            if (value == "cloudflare" || value == "cf") return "cloudflare";
            if (value == "ngrok") return "ngrok";
            throw new ArgumentException("无效的隧道类型；可选 cloudflare、ngrok。");
        }

        static string ProviderLabel(string provider)
        {
            return provider == "ngrok" ? "ngrok" : "Cloudflare";
        }

        static string InstallHome()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        }

        static string ProviderFile()
        {
            return Path.Combine(InstallHome(), "tunnel-provider.txt");
        }

        static string NormalizeMode(string mode)
        {
            string value = (mode ?? "").Trim().ToLowerInvariant();
            if (value == "ngrok") return "ngrok";
            if (value == "cloudflare" || value == "cf" || value == "cloudflare-auto") return "cloudflare-auto";
            if (value == "cloudflare-quic" || value == "quic") return "cloudflare-quic";
            if (value == "cloudflare-http2" || value == "http2") return "cloudflare-http2";
            throw new ArgumentException("无效启动方式。");
        }

        static string ReadDefaultMode()
        {
            try
            {
                string file = ProviderFile();
                if (!File.Exists(file)) return null;
                string value = File.ReadAllText(file, Encoding.UTF8).Trim();
                if (string.IsNullOrWhiteSpace(value)) return null;
                return NormalizeMode(value);
            }
            catch { return null; }
        }

        static void WriteDefaultMode(string mode)
        {
            string file = ProviderFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, NormalizeMode(mode) + Environment.NewLine, new UTF8Encoding(false));
        }

        static string ModeProvider(string mode)
        {
            return NormalizeMode(mode) == "ngrok" ? "ngrok" : "cloudflare";
        }

        static string ModeProtocol(string mode)
        {
            switch (NormalizeMode(mode))
            {
                case "cloudflare-quic": return "quic";
                case "cloudflare-http2": return "http2";
                case "cloudflare-auto": return "auto";
                default: return "auto";
            }
        }

        static string ProtocolToMode(string protocol)
        {
            switch ((protocol ?? "auto").ToLowerInvariant())
            {
                case "quic": return "cloudflare-quic";
                case "http2": return "cloudflare-http2";
                case "auto":
                case "prefer-http2":
                default: return "cloudflare-auto";
            }
        }

        static string ModeLabel(string mode)
        {
            switch (NormalizeMode(mode))
            {
                case "ngrok": return "ngrok";
                case "cloudflare-quic": return "Cloudflare QUIC";
                case "cloudflare-http2": return "Cloudflare HTTP/2";
                default: return "Cloudflare 自动";
            }
        }

        static string PromptStartupOverride(string defaultMode)
        {
            if (Console.IsInputRedirected) return defaultMode;
            Console.WriteLine();
            Console.WriteLine("默认启动：" + ModeLabel(defaultMode) + "（1.5 秒后自动启动）");
            Console.WriteLine("按 1=ngrok / 2=Cloudflare自动 / 3=QUIC / 4=HTTP/2 可临时改用；启动后按 D 可保存当前方式。");
            DateTime until = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < until)
            {
                if (Console.KeyAvailable)
                {
                    ConsoleKey key = Console.ReadKey(true).Key;
                    if (key == ConsoleKey.D1) return "ngrok";
                    if (key == ConsoleKey.D2) return "cloudflare-auto";
                    if (key == ConsoleKey.D3) return "cloudflare-quic";
                    if (key == ConsoleKey.D4) return "cloudflare-http2";
                    if (key == ConsoleKey.Enter) break;
                }
                Thread.Sleep(50);
            }
            return defaultMode;
        }

        static string PromptModeAndSave()
        {
            if (Console.IsInputRedirected) return "cloudflare-auto";
            Console.WriteLine();
            Console.WriteLine("第一次使用，请选择公网启动方式：");
            Console.WriteLine("  [1] ngrok");
            Console.WriteLine("  [2] Cloudflare 自动（QUIC → HTTP/2）");
            Console.WriteLine("  [3] Cloudflare QUIC");
            Console.WriteLine("  [4] Cloudflare HTTP/2");
            Console.Write("选择 1-4：");
            while (true)
            {
                ConsoleKey key = Console.ReadKey(true).Key;
                string mode = key == ConsoleKey.D1 ? "ngrok"
                    : key == ConsoleKey.D2 ? "cloudflare-auto"
                    : key == ConsoleKey.D3 ? "cloudflare-quic"
                    : key == ConsoleKey.D4 ? "cloudflare-http2" : null;
                if (mode != null)
                {
                    Console.WriteLine(ModeLabel(mode));
                    WriteDefaultMode(mode);
                    Console.WriteLine("已记为默认；以后直接输入 webcoding 即可。");
                    return mode;
                }
            }
        }

        static int GuideNgrok()
        {
            return EnsureNgrokConfigured(true) ? 0 : 1;
        }

        static bool EnsureNgrokConfigured(bool interactive)
        {
            string ngrok = FindTool("ngrok.exe");
            if (ngrok == null)
            {
                Console.WriteLine("未找到 ngrok.exe，请重新运行 WebCoding-Setup.exe。");
                return false;
            }

            string checkOutput;
            if (RunSimple(ngrok, "config check", out checkOutput) == 0) return true;
            if (!interactive || Console.IsInputRedirected) return false;

            Console.WriteLine();
            Console.WriteLine("ngrok 需要免费账号和 authtoken。");
            Console.WriteLine("接下来会打开 ngrok 官方页面；注册/登录后复制 authtoken，回到这里直接粘贴即可。");
            Console.WriteLine("WebCoding 不保存 token，配置由 ngrok 自己管理。");
            try
            {
                Process.Start(new ProcessStartInfo("https://dashboard.ngrok.com/get-started/your-authtoken") { UseShellExecute = true });
            }
            catch
            {
                Console.WriteLine("无法自动打开浏览器，请手动访问：https://dashboard.ngrok.com/get-started/your-authtoken");
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                Console.Write("authtoken（直接回车取消）: ");
                string token = ReadSecretLine();
                Console.WriteLine();
                if (string.IsNullOrWhiteSpace(token)) return false;

                string ignored;
                int code = RunSimple(ngrok, "config add-authtoken " + Quote(token.Trim()), out ignored);
                token = null;
                if (code == 0 && RunSimple(ngrok, "config check", out ignored) == 0)
                {
                    Console.WriteLine("ngrok 认证配置完成。");
                    return true;
                }

                Console.WriteLine("token 配置失败，请确认复制的是 ngrok Dashboard 的 authtoken。");
            }
            return false;
        }

        static string ReadSecretLine()
        {
            if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
            var b = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (b.Length > 0)
                    {
                        b.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
                }
                if (!char.IsControl(key.KeyChar))
                {
                    b.Append(key.KeyChar);
                    Console.Write("*");
                }
            }
            return b.ToString();
        }

        static int RunSimple(string executable, string arguments, out string output)
        {
            output = "";
            try
            {
                var info = new ProcessStartInfo(executable, arguments)
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
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    output = (stdout + Environment.NewLine + stderr).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception e)
            {
                output = e.Message;
                return -1;
            }
        }

        static void QueueSwitch(string mode)
        {
            lock (SwitchLock) pendingProvider = NormalizeMode(mode);
            SwitchRequested.Set();
        }

        static string ControlPipeName(string root)
        {
            string normalized = Path.GetFullPath(root).TrimEnd('\\').ToLowerInvariant();
            byte[] bytes = Encoding.UTF8.GetBytes(normalized);
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(bytes);
            var b = new StringBuilder("WebCoding-");
            for (int i = 0; i < 12; i++) b.Append(hash[i].ToString("x2"));
            return b.ToString();
        }

        static void StartControlServer(string root)
        {
            string pipeName = ControlPipeName(root);
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            pipe.WaitForConnection();
                            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                            {
                                string line = reader.ReadLine() ?? "";
                                if (line.StartsWith("switch ", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        string raw = line.Substring(7).Trim();
                                        string mode = raw.Equals("ngrok", StringComparison.OrdinalIgnoreCase) ? "ngrok" : "cloudflare-auto";
                                        QueueSwitch(mode);
                                        writer.WriteLine("OK 已请求切换到 " + ModeLabel(mode));
                                    }
                                    catch (Exception e) { writer.WriteLine("ERROR " + e.Message); }
                                }
                                else writer.WriteLine("ERROR 不支持的控制命令");
                            }
                        }
                    }
                    catch { Thread.Sleep(300); }
                }
            });
            thread.IsBackground = true;
            thread.Name = "WebCoding-control";
            thread.Start();
        }

        static int SendSwitch(string root, string provider)
        {
            string pipeName = ControlPipeName(root);
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(1800);
                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                    {
                        writer.WriteLine("switch " + provider);
                        string reply = reader.ReadLine();
                        if (string.IsNullOrWhiteSpace(reply)) throw new IOException("运行中的映射未返回控制响应。");
                        Console.WriteLine(reply);
                        return reply.StartsWith("OK ", StringComparison.Ordinal) ? 0 : 1;
                    }
                }
            }
            catch (TimeoutException)
            {
                return Error("当前目录没有可控制的 WebCoding 映射。请在正在映射的同一目录执行该命令。");
            }
            catch (IOException e)
            {
                return Error("无法连接当前目录的 WebCoding 映射：" + e.Message);
            }
        }

        static TunnelSession StartTunnelMode(string mode, int port, string token, bool debugTunnel)
        {
            mode = NormalizeMode(mode);
            TunnelSession session = mode == "ngrok"
                ? StartNgrok(port, token, debugTunnel)
                : StartCloudflare(port, token, ModeProtocol(mode), debugTunnel);
            session.Mode = mode;
            return session;
        }

        static TunnelSession StartCloudflare(int port, string token, string protocolMode, bool debugTunnel)
        {
            string tunnelTool = FindTool("cloudflared.exe");
            if (tunnelTool == null) throw new IOException("未找到 cloudflared。请重新运行 WebCoding-Setup.exe。");

            string[] protocols = GetProtocols(protocolMode);
            Console.WriteLine("[连接] Cloudflare 协议策略：" + protocolMode + "；启动尝试顺序：" + string.Join(" → ", protocols));
            Process tunnel = null;
            for (int protocolIndex = 0; protocolIndex < protocols.Length; protocolIndex++)
            {
                string protocol = protocols[protocolIndex];
                Console.WriteLine("[连接] Cloudflare 正在尝试 " + (protocol == "http2" ? "HTTP/2（TCP）" : "QUIC（UDP）"));
                if (Quit.WaitOne(0)) throw new OperationCanceledException();

                string address = null;
                bool connected = false, protocolMismatch = false;
                object gate = new object();
                var tunnelLog = new TunnelLog(debugTunnel, Path.Combine(InstallHome(), "logs"));
                tunnel = Spawn(tunnelTool, "tunnel --no-autoupdate --protocol " + protocol + " --url http://127.0.0.1:" + port, tempDirectory,
                    line =>
                    {
                        tunnelLog.OnLine(line);
                        lock (gate)
                        {
                            var match = Regex.Match(line ?? "", @"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);
                            if (match.Success) address = match.Value;
                            if ((line ?? "").IndexOf("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var observed = Regex.Match(line ?? "", @"\bprotocol=(quic|http2)\b", RegexOptions.IgnoreCase);
                                if (observed.Success && string.Equals(observed.Groups[1].Value, protocol, StringComparison.OrdinalIgnoreCase)) connected = true;
                                else if (observed.Success)
                                {
                                    protocolMismatch = true;
                                    Console.WriteLine("[连接] cloudflared 报告的协议与本次选择不符，拒绝发布该链接。");
                                }
                            }
                        }
                    }, null);

                DateTime until = DateTime.UtcNow.AddSeconds(40);
                while (DateTime.UtcNow < until && !tunnel.HasExited && !Quit.WaitOne(200))
                {
                    lock (gate) { if (protocolMismatch || (address != null && connected)) break; }
                }

                string candidate;
                lock (gate) candidate = address == null || !connected || protocolMismatch ? null : address + "/mcp/" + token;
                if (candidate != null && VerifyCandidate(candidate, tunnel))
                {
                    Console.WriteLine("[连接] Cloudflare 当前协议：" + (protocol == "http2" ? "HTTP/2（TCP）" : "QUIC（UDP）"));
                    return new TunnelSession { Provider = "cloudflare", Link = candidate, Detail = protocol, Process = tunnel };
                }

                Stop(tunnel);
                tunnel = null;
                if (protocolIndex + 1 < protocols.Length && !Quit.WaitOne(0))
                    Console.WriteLine("[连接] " + protocol + " 未通过连接验证，正在尝试 " + protocols[protocolIndex + 1] + "...");
            }

            throw new IOException("Cloudflare 公网链接创建失败（协议策略：" + protocolMode + "）。请检查网络、代理和防火墙，或热切换到 ngrok。");
        }

        static TunnelSession StartNgrok(int port, string token, bool debugTunnel)
        {
            string ngrok = FindTool("ngrok.exe");
            if (ngrok == null) throw new IOException("未找到 ngrok。请重新运行 WebCoding-Setup.exe。");

            string address = null;
            object gate = new object();
            var recent = new Queue<string>();
            bool authProblem = false;
            Console.WriteLine("[连接] ngrok 正在连接；首次使用需要免费账号和 authtoken。");

            Process tunnel = Spawn(ngrok, "http http://127.0.0.1:" + port + " --log stdout --log-format json", tempDirectory,
                line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    if (debugTunnel) WriteTunnelDiagnostic("ngrok " + line);
                    lock (gate)
                    {
                        recent.Enqueue(line);
                        while (recent.Count > 8) recent.Dequeue();
                        if (Regex.IsMatch(line, "authtoken|ERR_NGROK_4018|authentication failed|authentication error", RegexOptions.IgnoreCase))
                            authProblem = true;
                        var match = Regex.Match(line, @"https://[a-z0-9][a-z0-9.-]*\.ngrok(?:-free)?\.(?:app|io)", RegexOptions.IgnoreCase);
                        if (match.Success) address = match.Value;
                    }
                }, null);

            DateTime until = DateTime.UtcNow.AddSeconds(35);
            int apiProbe = 0;
            while (DateTime.UtcNow < until && !tunnel.HasExited && !Quit.WaitOne(200))
            {
                lock (gate) { if (address != null) break; }
                if (++apiProbe % 3 == 0)
                {
                    string fromApi = GetNgrokPublicUrl();
                    if (!string.IsNullOrWhiteSpace(fromApi))
                    {
                        lock (gate) address = fromApi;
                        break;
                    }
                }
            }

            string candidate = null;
            lock (gate) if (address != null) candidate = address.TrimEnd('/') + "/mcp/" + token;

            if (candidate != null && VerifyCandidate(candidate, tunnel))
            {
                Console.WriteLine("[连接] ngrok 隧道已连接。");
                return new TunnelSession { Provider = "ngrok", Link = candidate, Detail = "https", Process = tunnel };
            }

            Stop(tunnel);
            string detail;
            lock (gate) detail = string.Join(" | ", recent.ToArray());
            detail = SanitizeTunnelText(detail);

            if (authProblem)
                throw new IOException("ngrok 尚未完成认证。运行 webcoding --ngrok-setup，免费注册后执行官方的 ngrok config add-authtoken 命令。");
            if (!string.IsNullOrWhiteSpace(detail))
                throw new IOException("ngrok 未能创建可用公网链接。最近信息：" + Limit(detail, 700));
            throw new IOException("ngrok 未能创建可用公网链接。请运行 webcoding --ngrok-setup 检查认证，并检查网络/代理。");
        }

        static string GetNgrokPublicUrl()
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:4040/api/tunnels");
                request.Method = "GET";
                request.Timeout = 800;
                request.ReadWriteTimeout = 800;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    var match = Regex.Match(json, @"""public_url""\s*:\s*""(https://[^""]+)""", RegexOptions.IgnoreCase);
                    return match.Success ? match.Groups[1].Value.Replace("\\/","/") : null;
                }
            }
            catch { return null; }
        }

        static bool VerifyCandidate(string candidate, Process tunnel)
        {
            for (int n = 0; n < 6 && !Quit.WaitOne(0) && tunnel != null && !tunnel.HasExited; n++)
            {
                if (CheckEndpoint(candidate)) return true;
                if (Quit.WaitOne(1200)) break;
            }
            return false;
        }

        static void PrintLink(string link, bool copy, TunnelSession tunnel)
        {
            Console.WriteLine();
            if (tunnel != null) Console.WriteLine("[" + ProviderLabel(tunnel.Provider) + "] " + link);
            else Console.WriteLine(link);
            Console.WriteLine();
            if (copy) Console.WriteLine(CopyLink(link) ? "链接已复制到剪贴板。" : "剪贴板暂时不可用，请手动复制上面的链接。");
        }

        internal static string[] GetProtocols(string mode)
        {
            switch (mode)
            {
                case "auto": return new[] { "quic", "http2" };
                case "http2": return new[] { "http2" };
                case "prefer-http2": return new[] { "http2", "quic" };
                case "quic": return new[] { "quic" };
                default: throw new ArgumentException("无效的 --protocol 值；可选 auto、http2、prefer-http2、quic。");
            }
        }

        static int Error(string message)
        {
            Console.Error.WriteLine("错误：" + message);
            if (!Console.IsInputRedirected)
            {
                Console.WriteLine("按任意键关闭。");
                try { Console.ReadKey(true); } catch { }
            }
            return 1;
        }

        static bool CopyLink(string link)
        {
            for (int i = 0; i < 4; i++)
            {
                try { Clipboard.SetText(link); return true; }
                catch (ExternalException) { Thread.Sleep(100); }
                catch { return false; }
            }
            return false;
        }

        static string Quote(string value)
        {
            var b = new StringBuilder(); b.Append('"'); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                b.Append(c); slashes = 0;
            }
            b.Append('\\', slashes * 2); b.Append('"'); return b.ToString();
        }

        static string FindTunnelTool(string provider)
        {
            return FindTool(provider == "ngrok" ? "ngrok.exe" : "cloudflared.exe");
        }

        static string FindTool(string name)
        {
            string runtime = Path.Combine(InstallHome(), "runtime");
            var dirs = new List<string>
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine(runtime, "node"),
                Path.Combine(runtime, "cloudflared"),
                Path.Combine(runtime, "ngrok")
            };
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            dirs.Add(Path.Combine(pf, "nodejs"));
            dirs.Add(Path.Combine(pf, "cloudflared"));
            dirs.Add(Path.Combine(pf86, "cloudflared"));
            dirs.Add(Path.Combine(pf, "Cloudflare"));
            dirs.Add(Path.Combine(pf, "ngrok"));
            dirs.Add(Path.Combine(pf86, "ngrok"));
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            foreach (string dir in dirs)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        string file = Path.Combine(dir.Trim().Trim('"'), name);
                        if (File.Exists(file)) return file;
                    }
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        static Process Spawn(string executable, string arguments, string workingDirectory, Action<string> output, Dictionary<string, string> environment)
        {
            var info = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            string runtime = Path.Combine(InstallHome(), "runtime");
            var privatePaths = new List<string>();
            foreach (string folder in new[] { "node", "cloudflared", "ngrok" })
            {
                string directory = Path.Combine(runtime, folder);
                if (Directory.Exists(directory)) privatePaths.Add(directory);
            }
            if (privatePaths.Count > 0)
                info.EnvironmentVariables["PATH"] = string.Join(";", privatePaths.ToArray()) + ";" + (info.EnvironmentVariables["PATH"] ?? "");
            if (environment != null)
                foreach (var item in environment) info.EnvironmentVariables[item.Key] = item.Value;

            var p = new Process { StartInfo = info };
            p.OutputDataReceived += (sender, e) => { if (e.Data != null) output(e.Data); };
            p.ErrorDataReceived += (sender, e) => { if (e.Data != null) output(e.Data); };
            p.Start();
            lock (ChildLock) Children.Add(p);
            if (!AssignProcessToJobObject(job, p.Handle))
            {
                Stop(p);
                throw new IOException("无法设置子进程自动清理，已停止启动。");
            }
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        static bool CheckEndpoint(string url)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Accept = "application/json, text/event-stream";
                request.UserAgent = "WebCoding-check/2.0.2";
                request.Timeout = 6000;
                request.ReadWriteTimeout = 6000;
                byte[] body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"WebCoding-check\",\"version\":\"2.0.2\"}}}");
                request.ContentLength = body.Length;
                using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string result = reader.ReadToEnd();
                    return response.StatusCode == HttpStatusCode.OK
                        && result.Contains("\"name\":\"WebCoding\"")
                        && result.Contains("\"protocolVersion\"");
                }
            }
            catch { return false; }
        }

        static void WriteTunnelDiagnostic(string line)
        {
            try
            {
                string directory = Path.Combine(InstallHome(), "logs");
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "tunnel-debug.log");
                if (File.Exists(file) && new FileInfo(file).Length >= 2 * 1024 * 1024)
                {
                    if (File.Exists(file + ".2")) File.Delete(file + ".2");
                    if (File.Exists(file + ".1")) File.Move(file + ".1", file + ".2");
                    File.Move(file, file + ".1");
                }
                File.AppendAllText(file, DateTime.UtcNow.ToString("o") + " " + SanitizeTunnelText(line) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        static string SanitizeTunnelText(string text)
        {
            if (text == null) return "";
            string safe = Regex.Replace(text, @"/mcp/[a-f0-9]{24,}", "/mcp/[REDACTED]", RegexOptions.IgnoreCase);
            safe = Regex.Replace(safe, @"https://[a-z0-9-]+\.trycloudflare\.com", "https://[private-cloudflare]", RegexOptions.IgnoreCase);
            safe = Regex.Replace(safe, @"https://[a-z0-9][a-z0-9.-]*\.ngrok(?:-free)?\.(?:app|io)", "https://[private-ngrok]", RegexOptions.IgnoreCase);
            safe = Regex.Replace(safe, @"(?i)(authtoken[\s""'=:\\]+)[A-Za-z0-9_\-]+", "$1[REDACTED]");
            return safe;
        }

        static string Limit(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, max) + "...";
        }

        static void Stop(Process p)
        {
            try
            {
                if (p != null && !p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch { }
        }

        static void Cleanup()
        {
            if (Interlocked.Exchange(ref cleaned, 1) != 0) return;
            if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
            lock (ChildLock)
            {
                foreach (Process p in Children)
                {
                    Stop(p);
                    try { p.Dispose(); } catch { }
                }
            }
            if (tempDirectory != null)
            {
                try { Directory.Delete(tempDirectory, true); } catch { }
            }
        }

        static void CreateJob()
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new IOException("无法创建子进程清理任务。");
            var info = new JobInfo();
            info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int length = Marshal.SizeOf(typeof(JobInfo));
            IntPtr pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                if (!SetInformationJobObject(job, 9, pointer, (uint)length))
                    throw new IOException("无法配置子进程清理任务。");
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinWorkingSet, MaxWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Counters
        {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JobInfo
        {
            public BasicLimits Basic;
            public Counters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr handle, int kind, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr handle, IntPtr process);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
    }
}
