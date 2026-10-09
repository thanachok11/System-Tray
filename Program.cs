using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TrayWrapperApp
{
    public class AppConfig
    {
        public string TargetPath { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public bool HideWindow { get; set; } = false;
        public bool AutoRestartOnCrash { get; set; } = false;
        public string TrayTooltip { get; set; } = "Tray Wrapper Service";
        public string CustomIconPath { get; set; } = "";
        public string ExitPassword { get; set; } = "";
    }

    static class Program
    {
        #region Win32 API
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        #endregion

        private static Mutex? mutex;
        private static NotifyIcon? trayIcon;
        private static Process? childProcess;
        private static AppConfig config = new AppConfig();
        private static bool isManualExit = false;
        private static DateTime processStartTime;
        private static int crashCount = 0;
        private static bool isWindowCurrentlyShown = true;
        private static int spawnedPid = 0;

        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        [STAThread]
        static void Main(string[] args)
        {
            // Single Instance Lock
            const string appGuid = "Global\\TrayWrapperApp_SingleInstance_Guid_987123";
            mutex = new Mutex(true, appGuid, out bool isNewInstance);
            if (!isNewInstance)
            {
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LoadConfig();

            // 1. ถ้ารองรับ Drag & Drop ไฟล์มาวางบนไอคอน .exe ให้จำค่านั้นทันที
            if (args != null && args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            {
                string droppedFile = CleanPath(args[0]);
                if (File.Exists(droppedFile))
                {
                    config.TargetPath = droppedFile;
                    config.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(droppedFile)) ?? "";
                    SaveConfig();
                }
            }

            InitTrayIcon();

            // 2. ถ้ายังไม่มี target ให้เด้งถามเลือกไฟล์
            if (string.IsNullOrWhiteSpace(config.TargetPath) || !File.Exists(config.TargetPath))
            {
                PromptSelectTargetFile();
            }
            else
            {
                StartTargetProcess();
            }

            Application.Run();

            GC.KeepAlive(mutex);
        }

        private static void LoadConfig()
        {
            if (!File.Exists(ConfigPath))
            {
                config = new AppConfig();
                SaveConfig();
                return;
            }

            string rawText = File.ReadAllText(ConfigPath);

            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(rawText, new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }) ?? new AppConfig();
            }
            catch
            {
                config = ParseConfigManually(rawText);
            }

            config.TargetPath = CleanPath(config.TargetPath);
            config.WorkingDirectory = CleanPath(config.WorkingDirectory);
            config.CustomIconPath = CleanPath(config.CustomIconPath);

            if (string.IsNullOrWhiteSpace(config.WorkingDirectory) && !string.IsNullOrWhiteSpace(config.TargetPath))
            {
                try
                {
                    string? dir = Path.GetDirectoryName(Path.GetFullPath(config.TargetPath));
                    if (!string.IsNullOrWhiteSpace(dir)) config.WorkingDirectory = dir;
                }
                catch { }
            }
        }

        private static AppConfig ParseConfigManually(string text)
        {
            var cfg = new AppConfig();

            string? ExtractValue(string key)
            {
                var match = Regex.Match(text, $@"""{key}""\s*:\s*""?([^"",\r\n}}]+)""?", RegexOptions.IgnoreCase);
                return match.Success ? match.Groups[1].Value.Trim() : null;
            }

            bool ExtractBool(string key, bool defaultVal)
            {
                var val = ExtractValue(key);
                if (val != null && bool.TryParse(val, out bool b)) return b;
                return defaultVal;
            }

            cfg.TargetPath = ExtractValue("TargetPath") ?? cfg.TargetPath;
            cfg.Arguments = ExtractValue("Arguments") ?? "";
            cfg.WorkingDirectory = ExtractValue("WorkingDirectory") ?? "";
            cfg.TrayTooltip = ExtractValue("TrayTooltip") ?? "Tray Wrapper Service";
            cfg.CustomIconPath = ExtractValue("CustomIconPath") ?? "";
            cfg.ExitPassword = ExtractValue("ExitPassword") ?? "";
            cfg.HideWindow = ExtractBool("HideWindow", false);
            cfg.AutoRestartOnCrash = ExtractBool("AutoRestartOnCrash", false);

            return cfg;
        }

        private static string CleanPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            return path.Trim().Trim('\"', '\'');
        }

        private static void SaveConfig()
        {
            try
            {
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        private static void InitTrayIcon()
        {
            Icon icon = SystemIcons.Application;

            if (!string.IsNullOrEmpty(config.CustomIconPath) && File.Exists(config.CustomIconPath))
            {
                try { icon = new Icon(config.CustomIconPath); } catch { }
            }
            else if (File.Exists(config.TargetPath) && Path.GetExtension(config.TargetPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try { icon = Icon.ExtractAssociatedIcon(config.TargetPath) ?? SystemIcons.Application; } catch { }
            }

            trayIcon = new NotifyIcon
            {
                Icon = icon,
                Text = config.TrayTooltip.Length > 63 ? config.TrayTooltip.Substring(0, 63) : config.TrayTooltip,
                Visible = true
            };

            // ดับเบิ้ลคลิกเพื่อสลับ แสดง/ซ่อน หน้าต่าง
            trayIcon.DoubleClick += (s, e) => ToggleTargetWindow();

            ContextMenuStrip contextMenu = new ContextMenuStrip();
            
            var toggleWinItem = new ToolStripMenuItem("👁️ แสดง / ซ่อน หน้าต่าง (Show/Hide)", null, (s, e) => ToggleTargetWindow());
            var restartItem = new ToolStripMenuItem("🔄 Restart Service", null, (s, e) => RestartProcess());
            var selectTargetItem = new ToolStripMenuItem("🎯 เปลี่ยนโปรแกรมเป้าหมาย...", null, (s, e) => PromptSelectTargetFile());
            var openFolderItem = new ToolStripMenuItem("📁 เปิดโฟลเดอร์โปรแกรม", null, (s, e) => OpenTargetFolder());
            var exitItem = new ToolStripMenuItem("❌ ปิดโปรแกรม (Exit)", null, (s, e) => HandleExitRequest());

            contextMenu.Items.Add(toggleWinItem);
            contextMenu.Items.Add(restartItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(selectTargetItem);
            contextMenu.Items.Add(openFolderItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(exitItem);

            trayIcon.ContextMenuStrip = contextMenu;
        }

        private static void PromptSelectTargetFile()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "เลือกไฟล์โปรแกรม (.bat / .exe / .lnk / script) ที่ต้องการครอบ";
                ofd.Filter = "Executable & Scripts (*.bat;*.cmd;*.exe;*.lnk)|*.bat;*.cmd;*.exe;*.lnk|All Files (*.*)|*.*";
                
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    config.TargetPath = ofd.FileName;
                    config.WorkingDirectory = Path.GetDirectoryName(ofd.FileName) ?? "";
                    SaveConfig();

                    // อัปเดตไอคอน
                    InitTrayIcon();
                    RestartProcess();
                }
            }
        }

        private static void StartTargetProcess()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(config.TargetPath) || !File.Exists(config.TargetPath))
                {
                    return;
                }

                string target = config.TargetPath;
                string args = config.Arguments ?? "";
                string ext = Path.GetExtension(target).ToLower();

                ProcessStartInfo psi = new ProcessStartInfo();

                if (ext == ".bat" || ext == ".cmd")
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = $"/c \"\"{target}\" {args}\"";
                }
                else if (ext == ".lnk")
                {
                    psi.FileName = target;
                    psi.Arguments = args;
                    psi.UseShellExecute = true;
                }
                else
                {
                    psi.FileName = target;
                    psi.Arguments = args;
                }

                if (!string.IsNullOrWhiteSpace(config.WorkingDirectory) && Directory.Exists(config.WorkingDirectory))
                {
                    psi.WorkingDirectory = config.WorkingDirectory;
                }
                else if (File.Exists(target))
                {
                    psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(target)) ?? "";
                }

                if (config.HideWindow && ext != ".lnk")
                {
                    psi.UseShellExecute = false;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                }
                else
                {
                    psi.UseShellExecute = true;
                    psi.WindowStyle = ProcessWindowStyle.Normal;
                }

                childProcess = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                processStartTime = DateTime.Now;
                isWindowCurrentlyShown = !config.HideWindow;

                childProcess.Exited += (s, e) =>
                {
                    if (isManualExit) return;

                    var runtime = DateTime.Now - processStartTime;

                    if (!config.AutoRestartOnCrash || runtime.TotalSeconds < 10)
                    {
                        return;
                    }

                    crashCount++;
                    if (crashCount <= 3)
                    {
                        Task.Delay(5000).ContinueWith(_ => StartTargetProcess());
                    }
                    else
                    {
                        trayIcon?.ShowBalloonTip(5000, "Tray Wrapper", "Service หยุดทำงานบ่อยเกินไป จึงระงับ Auto-Restart", ToolTipIcon.Warning);
                    }
                };

                childProcess.Start();
                spawnedPid = childProcess.Id;

                // ถ้าตั้งซ่อนหน้าต่างไว้ ให้รอหาหน้าต่างแล้วสั่งซ่อน
                if (config.HideWindow)
                {
                    Task.Run(async () =>
                    {
                        for (int i = 0; i < 15; i++)
                        {
                            await Task.Delay(300);
                            IntPtr hWnd = FindProcessWindow();
                            if (hWnd != IntPtr.Zero)
                            {
                                ShowWindow(hWnd, SW_HIDE);
                                isWindowCurrentlyShown = false;
                                break;
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                trayIcon?.ShowBalloonTip(4000, "Tray Wrapper Error", $"ไม่สามารถเปิด Process ได้: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private static IntPtr FindProcessWindow()
        {
            // 1) ลองหาจาก childProcess โดยตรง
            if (childProcess != null)
            {
                try
                {
                    childProcess.Refresh();
                    if (childProcess.MainWindowHandle != IntPtr.Zero)
                    {
                        return childProcess.MainWindowHandle;
                    }
                }
                catch { }
            }

            // 2) สแกนหาหน้าต่างของ Process ที่เกี่ยวข้อง หรือหน้าต่างที่มีชื่อสอดคล้อง
            IntPtr foundHwnd = IntPtr.Zero;
            string targetFileName = Path.GetFileNameWithoutExtension(config.TargetPath);

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint procId);

                // ถ้าเป็น PID เดียวกับที่สั่งเปิด
                if (spawnedPid > 0 && procId == spawnedPid)
                {
                    foundHwnd = hWnd;
                    return false;
                }

                // หรือตรวจจาก Title ของหน้าต่าง
                int length = GetWindowTextLength(hWnd);
                if (length > 0)
                {
                    StringBuilder sb = new StringBuilder(length + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    string title = sb.ToString();

                    if (!string.IsNullOrEmpty(targetFileName) && title.IndexOf(targetFileName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foundHwnd = hWnd;
                        return false;
                    }
                }

                return true;
            }, IntPtr.Zero);

            return foundHwnd;
        }

        private static void ToggleTargetWindow()
        {
            try
            {
                IntPtr hWnd = FindProcessWindow();

                if (hWnd == IntPtr.Zero)
                {
                    trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", "ไม่พบหน้าต่างโปรแกรม หรือโปรแกรมทำงานแบบ Background", ToolTipIcon.Info);
                    return;
                }

                if (isWindowCurrentlyShown && IsWindowVisible(hWnd))
                {
                    ShowWindow(hWnd, SW_HIDE);
                    isWindowCurrentlyShown = false;
                }
                else
                {
                    ShowWindow(hWnd, SW_RESTORE);
                    ShowWindow(hWnd, SW_SHOW);
                    SetForegroundWindow(hWnd);
                    isWindowCurrentlyShown = true;
                }
            }
            catch (Exception ex)
            {
                trayIcon?.ShowBalloonTip(3000, "Error", $"ไม่สามารถสลับหน้าต่างได้: {ex.Message}", ToolTipIcon.Warning);
            }
        }

        private static void RestartProcess()
        {
            crashCount = 0;
            KillTargetProcess();
            StartTargetProcess();
            trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", "สั่ง Restart เรียบร้อยแล้ว", ToolTipIcon.Info);
        }

        private static void KillTargetProcess()
        {
            try
            {
                if (childProcess != null && !childProcess.HasExited)
                {
                    childProcess.Kill(entireProcessTree: true);
                    childProcess.WaitForExit(3000);
                }
            }
            catch { }
        }

        private static void OpenTargetFolder()
        {
            try
            {
                string dir = !string.IsNullOrWhiteSpace(config.WorkingDirectory) && Directory.Exists(config.WorkingDirectory)
                    ? config.WorkingDirectory
                    : Path.GetDirectoryName(Path.GetFullPath(config.TargetPath)) ?? "";

                if (Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
            }
            catch { }
        }

        private static void HandleExitRequest()
        {
            if (!string.IsNullOrEmpty(config.ExitPassword))
            {
                string? input = PromptPasswordDialog("กรุณากรอกรหัสผ่านเพื่อปิดโปรแกรม:", "ยืนยันการปิด Service");
                if (input == null) return;

                if (input != config.ExitPassword)
                {
                    MessageBox.Show("รหัสผ่านไม่ถูกต้อง!", "ปฏิเสธการเข้าถึง", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            ExitApplication();
        }

        private static string? PromptPasswordDialog(string text, string caption)
        {
            using (Form prompt = new Form())
            {
                prompt.Width = 360;
                prompt.Height = 175;
                prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
                prompt.Text = caption;
                prompt.StartPosition = FormStartPosition.CenterScreen;
                prompt.MaximizeBox = false;
                prompt.MinimizeBox = false;
                prompt.TopMost = true;

                Label textLabel = new Label() { Left = 20, Top = 15, Text = text, AutoSize = true };
                TextBox textBox = new TextBox() { Left = 20, Top = 45, Width = 300, PasswordChar = '●' };
                
                Button confirmation = new Button() { Text = "ตกลง", Left = 155, Width = 80, Top = 85, DialogResult = DialogResult.OK };
                Button cancel = new Button() { Text = "ยกเลิก", Left = 240, Width = 80, Top = 85, DialogResult = DialogResult.Cancel };

                confirmation.Click += (sender, e) => { prompt.Close(); };
                cancel.Click += (sender, e) => { prompt.Close(); };

                prompt.Controls.Add(textBox);
                prompt.Controls.Add(confirmation);
                prompt.Controls.Add(cancel);
                prompt.Controls.Add(textLabel);
                prompt.AcceptButton = confirmation;
                prompt.CancelButton = cancel;

                return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : null;
            }
        }

        private static void ExitApplication()
        {
            isManualExit = true;
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
            }
            KillTargetProcess();
            Application.Exit();
        }
    }
}
