using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TrayWrapperApp
{
    public class AppConfig
    {
        public string TargetPath { get; set; } = @"C:\appsoft\bin\AutoStartPrintCenter\AutoStartDevPrintCenterTimer.bat";
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public bool HideWindow { get; set; } = true;
        public bool AutoRestartOnCrash { get; set; } = false;
        public string TrayTooltip { get; set; } = "Print Center Service";
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

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        #endregion

        private static Mutex mutex;
        private static NotifyIcon trayIcon;
        private static Process childProcess;
        private static AppConfig config;
        private static bool isManualExit = false;
        private static DateTime processStartTime;
        private static int crashCount = 0;
        private static bool isWindowCurrentlyShown = false;

        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        [STAThread]
        static void Main()
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
            InitTrayIcon();
            StartTargetProcess();

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
                });
            }
            catch
            {
                config = ParseConfigManually(rawText);
            }

            if (config == null) config = new AppConfig();

            config.TargetPath = CleanPath(config.TargetPath);
            config.WorkingDirectory = CleanPath(config.WorkingDirectory);
            config.CustomIconPath = CleanPath(config.CustomIconPath);

            if (string.IsNullOrWhiteSpace(config.WorkingDirectory) && !string.IsNullOrWhiteSpace(config.TargetPath))
            {
                try
                {
                    string dir = Path.GetDirectoryName(Path.GetFullPath(config.TargetPath));
                    if (!string.IsNullOrWhiteSpace(dir)) config.WorkingDirectory = dir;
                }
                catch { }
            }
        }

        private static AppConfig ParseConfigManually(string text)
        {
            var cfg = new AppConfig();

            string ExtractValue(string key)
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
            cfg.TrayTooltip = ExtractValue("TrayTooltip") ?? "Print Center Service";
            cfg.CustomIconPath = ExtractValue("CustomIconPath") ?? "";
            cfg.ExitPassword = ExtractValue("ExitPassword") ?? "";
            cfg.HideWindow = ExtractBool("HideWindow", true);
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

            // ดับเบิ้ลคลิกเพื่อ สลับ แสดง/ซ่อน หน้าต่าง
            trayIcon.DoubleClick += (s, e) => ToggleTargetWindow();

            ContextMenuStrip contextMenu = new ContextMenuStrip();
            
            var statusItem = new ToolStripMenuItem("Status: Running") { Enabled = false };
            var toggleWinItem = new ToolStripMenuItem("👁️ แสดง / ซ่อน หน้าต่าง (Show/Hide)", null, (s, e) => ToggleTargetWindow());
            var restartItem = new ToolStripMenuItem("🔄 Restart Service", null, (s, e) => RestartProcess());
            var openFolderItem = new ToolStripMenuItem("📁 Open Folder", null, (s, e) => OpenTargetFolder());
            var exitItem = new ToolStripMenuItem("❌ Exit", null, (s, e) => HandleExitRequest());

            contextMenu.Items.Add(statusItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(toggleWinItem);
            contextMenu.Items.Add(restartItem);
            contextMenu.Items.Add(openFolderItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(exitItem);

            trayIcon.ContextMenuStrip = contextMenu;
        }

        private static void StartTargetProcess()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(config.TargetPath)) return;

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
                    psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(target));
                }

                if (config.HideWindow && ext != ".lnk")
                {
                    // ไม่ใช้ CreateNoWindow = true เพราะจะทำให้ไม่สามารถ toggle เปิดหน้าต่างขึ้นมาดูได้ในภายหลัง
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

                // ถ้ากำหนดให้ซ่อน ให้ดักซ่อนหน้าต่างทันทีที่ process สร้าง window
                if (config.HideWindow)
                {
                    Task.Run(async () =>
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            await Task.Delay(200);
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
            if (childProcess == null) return IntPtr.Zero;

            try
            {
                childProcess.Refresh();
                if (childProcess.MainWindowHandle != IntPtr.Zero)
                {
                    return childProcess.MainWindowHandle;
                }

                // ค้นหา Window Handle ของ process หรือ child process ที่สร้างขึ้น
                IntPtr foundHwnd = IntPtr.Zero;
                uint currentPid = (uint)childProcess.Id;

                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint procId);
                    if (procId == currentPid)
                    {
                        foundHwnd = hWnd;
                        return false; // หยุดหาเมื่อเจอแล้ว
                    }
                    return true;
                }, IntPtr.Zero);

                return foundHwnd;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static void ToggleTargetWindow()
        {
            try
            {
                IntPtr hWnd = FindProcessWindow();

                if (hWnd == IntPtr.Zero)
                {
                    trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", "โปรแกรมนี้ทำงานแบบ Background ไม่มีหน้าต่างให้แสดง", ToolTipIcon.Info);
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
                    : Path.GetDirectoryName(Path.GetFullPath(config.TargetPath));

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
                string input = PromptPasswordDialog("กรุณากรอกรหัสผ่านเพื่อปิดโปรแกรม:", "ยืนยันการปิด Service");
                if (input == null) return;

                if (input != config.ExitPassword)
                {
                    MessageBox.Show("รหัสผ่านไม่ถูกต้อง!", "ปฏิเสธการเข้าถึง", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            ExitApplication();
        }

        private static string PromptPasswordDialog(string text, string caption)
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
