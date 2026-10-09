using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace TrayWrapperApp
{
    public class AppConfig
    {
        /// <summary>
        /// ไฟล์ที่ต้องการรัน เช่น "AutoStartDevPrintCenterTimer.bat" หรือ @"C:\Path\To\app.bat" หรือ "node.exe"
        /// </summary>
        public string TargetPath { get; set; } = @"Z:\Prog\Library_Release\AutoStartPrintCenter\AutoStartDevPrintCenterTimer.bat";

        /// <summary>
        /// Arguments เพิ่มเติม (ถ้ามี) เช่น "index.js"
        /// </summary>
        public string Arguments { get; set; } = "";

        /// <summary>
        /// Working Directory (ถ้าเว้นว่างจะใช้โฟลเดอร์เดียวกับ TargetPath อัตโนมัติ)
        /// </summary>
        public string WorkingDirectory { get; set; } = "";

        /// <summary>
        /// ซ่อนหน้าต่างดำ (Command Prompt) หรือไม่
        /// </summary>
        public bool HideWindow { get; set; } = true;

        /// <summary>
        /// ถ้า Service ดับหรือ Crash ให้เปิดใหม่เองอัตโนมัติหรือไม่
        /// </summary>
        public bool AutoRestartOnCrash { get; set; } = true;

        /// <summary>
        /// ข้อความ Tooltip เมื่อเอาเมาส์ไปชี้ที่ System Tray Icon
        /// </summary>
        public string TrayTooltip { get; set; } = "Print Center Service (Running)";

        /// <summary>
        /// Path ของไฟล์ .ico กำหนดเอง (ถ้าไม่มีจะใช้ Icon ระบบ)
        /// </summary>
        public string CustomIconPath { get; set; } = "";
    }

    static class Program
    {
        private static NotifyIcon trayIcon;
        private static Process childProcess;
        private static AppConfig config;
        private static bool isManualExit = false;

        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 1. โหลดการตั้งค่าจาก config.json
            LoadConfig();

            // 2. สร้าง System Tray Icon
            InitTrayIcon();

            // 3. เริ่มรันไฟล์เป้าหมาย (.bat / node / .exe)
            StartTargetProcess();

            // รัน background loop
            Application.Run();
        }

        private static void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                else
                {
                    config = new AppConfig();
                    string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(ConfigPath, json);
                }
            }
            catch
            {
                config = new AppConfig();
            }
        }

        private static void InitTrayIcon()
        {
            Icon icon = SystemIcons.Application;

            // 1) เช็คว่ามี custom icon หรือไม่
            if (!string.IsNullOrEmpty(config.CustomIconPath) && File.Exists(config.CustomIconPath))
            {
                try { icon = new Icon(config.CustomIconPath); } catch { }
            }
            // 2) หรือถ้าเป็นไฟล์ .exe ให้ดึง icon ของไฟล์นั้นมาใช้อัตโนมัติ
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

            // สร้าง Context Menu (คลิกขวาที่ Tray Icon)
            ContextMenuStrip contextMenu = new ContextMenuStrip();
            
            var statusItem = new ToolStripMenuItem("Status: Running") { Enabled = false };
            var restartItem = new ToolStripMenuItem("🔄 Restart Service", null, (s, e) => RestartProcess());
            var openFolderItem = new ToolStripMenuItem("📁 Open Target Folder", null, (s, e) => OpenTargetFolder());
            var exitItem = new ToolStripMenuItem("❌ Exit", null, (s, e) => ExitApplication());

            contextMenu.Items.Add(statusItem);
            contextMenu.Items.Add(new ToolStripSeparator());
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

                // ถ้ารันไฟล์ .bat หรือ .cmd ในโหมดซ่อน ต้องสั่งผ่าน cmd.exe /c
                if (ext == ".bat" || ext == ".cmd")
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = $"/c \"\"{target}\" {args}\"";
                }
                else
                {
                    psi.FileName = target;
                    psi.Arguments = args;
                }

                // กำหนด Working Directory
                if (!string.IsNullOrWhiteSpace(config.WorkingDirectory) && Directory.Exists(config.WorkingDirectory))
                {
                    psi.WorkingDirectory = config.WorkingDirectory;
                }
                else if (File.Exists(target))
                {
                    psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(target));
                }

                if (config.HideWindow)
                {
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
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

                childProcess.Exited += (s, e) =>
                {
                    if (!isManualExit && config.AutoRestartOnCrash)
                    {
                        // ถ้า process ดับโดยไม่ได้สั่งปิด ให้ restart ตัวเองใหม่อัตโนมัติใน 3 วินาที
                        System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ => StartTargetProcess());
                    }
                };

                childProcess.Start();
            }
            catch (Exception ex)
            {
                trayIcon.ShowBalloonTip(4000, "Tray Wrapper Error", $"ไม่สามารถเริ่ม Process ได้: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private static void RestartProcess()
        {
            KillTargetProcess();
            StartTargetProcess();
            trayIcon.ShowBalloonTip(2000, "Tray Wrapper", "Service ถูกสั่ง Restart แล้ว", ToolTipIcon.Info);
        }

        private static void KillTargetProcess()
        {
            try
            {
                if (childProcess != null && !childProcess.HasExited)
                {
                    // ฆ่าทั้ง Process Tree เพื่อให้ลูกๆ เช่น node.exe ดับตามไปด้วย
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

        private static void ExitApplication()
        {
            isManualExit = true;
            trayIcon.Visible = false;
            KillTargetProcess();
            Application.Exit();
        }
    }
}
