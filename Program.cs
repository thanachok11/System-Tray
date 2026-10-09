using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TrayWrapperApp
{
    public class AppConfig
    {
        public string TargetPath { get; set; } = @"Z:\Prog\Library_Release\AutoStartPrintCenter\AutoStartDevPrintCenterTimer.bat";
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; } = @"Z:\Prog\Library_Release\AutoStartPrintCenter";
        public bool HideWindow { get; set; } = true;
        public bool AutoRestartOnCrash { get; set; } = false;
        public string TrayTooltip { get; set; } = "Print Center Service";
        public string CustomIconPath { get; set; } = "";
        
        /// <summary>
        /// รหัสผ่านสำหรับสั่งปิดโปรแกรม (ถ้าเว้นว่างไว้ "" จะไม่ต้องใส่รหัส)
        /// </summary>
        public string ExitPassword { get; set; } = "";
    }

    static class Program
    {
        private static Mutex mutex;
        private static NotifyIcon trayIcon;
        private static Process childProcess;
        private static AppConfig config;
        private static bool isManualExit = false;
        private static DateTime processStartTime;
        private static int crashCount = 0;

        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        [STAThread]
        static void Main()
        {
            // 1. Single Instance Lock (รันได้ 1 ตัวเท่านั้น)
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
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
                    {
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    }) ?? new AppConfig();
                }
                else
                {
                    config = new AppConfig();
                    string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(ConfigPath, json);
                }
            }
            catch (Exception ex)
            {
                config = new AppConfig();
                MessageBox.Show($"พบข้อผิดพลาดใน config.json: {ex.Message}\nระบบจะใช้ค่าเริ่มต้น", "Config Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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

            ContextMenuStrip contextMenu = new ContextMenuStrip();
            
            var statusItem = new ToolStripMenuItem("Status: Running") { Enabled = false };
            var restartItem = new ToolStripMenuItem("🔄 Restart Service", null, (s, e) => RestartProcess());
            var openFolderItem = new ToolStripMenuItem("📁 Open Folder", null, (s, e) => OpenTargetFolder());
            var exitItem = new ToolStripMenuItem("❌ Exit", null, (s, e) => HandleExitRequest());

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
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                }

                childProcess = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                processStartTime = DateTime.Now;

                childProcess.Exited += (s, e) =>
                {
                    if (isManualExit) return;

                    var runtime = DateTime.Now - processStartTime;

                    // ป้องกัน loop ถ้าไฟล์ทำงานเสร็จเร็ว หรือ AutoRestart เป็น false
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
            }
            catch (Exception ex)
            {
                trayIcon?.ShowBalloonTip(4000, "Tray Wrapper Error", $"ไม่สามารถเปิด Process ได้: {ex.Message}", ToolTipIcon.Error);
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
            // ถ้ามีการตั้งรหัสผ่านไว้
            if (!string.IsNullOrEmpty(config.ExitPassword))
            {
                string input = PromptPasswordDialog("กรุณากรอกรหัสผ่านเพื่อปิดโปรแกรม:", "ยืนยันการปิด Service");
                if (input == null)
                {
                    // ผู้ใช้กด Cancel
                    return;
                }

                if (input != config.ExitPassword)
                {
                    MessageBox.Show("รหัสผ่านไม่ถูกต้อง!", "ปฏิเสธการเข้าถึง", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            // ถ้าผ่านรหัสผ่าน หรือไม่ได้ตั้งรหัสไว้
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
