using System;
using System.Collections.Generic;
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
    public class ProgramItemConfig
    {
        public string Name { get; set; } = "Service";
        public string TargetPath { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public bool HideWindow { get; set; } = true;
        public bool AutoRestartOnCrash { get; set; } = false;
    }

    public class AppConfig
    {
        public string TrayTooltip { get; set; } = "Print Center & Background Services";
        public string CustomIconPath { get; set; } = "";
        public string ExitPassword { get; set; } = "";
        public List<ProgramItemConfig> Programs { get; set; } = new List<ProgramItemConfig>();

        // Backward compatibility
        public string? TargetPath { get; set; }
        public string? Arguments { get; set; }
        public string? WorkingDirectory { get; set; }
        public bool? HideWindow { get; set; }
        public bool? AutoRestartOnCrash { get; set; }
    }

    public class ManagedApp
    {
        public ProgramItemConfig Config { get; set; }
        public Process? ChildProcess { get; set; }
        public IntPtr WindowHandle { get; set; } = IntPtr.Zero;
        public bool IsWindowShown { get; set; } = false;
        public int SpawnedPid { get; set; } = 0;
        public DateTime StartTime { get; set; }
        public int CrashCount { get; set; } = 0;

        public ManagedApp(ProgramItemConfig config)
        {
            Config = config;
        }
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

        [DllImport("user32.dll")]
        private static extern bool RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        private const uint SC_CLOSE = 0xF060;
        private const uint MF_BYCOMMAND = 0x00000000;
        private const uint MF_GRAYED = 0x00000001;
        private const uint MF_DISABLED = 0x00000002;
        #endregion

        private static Mutex? mutex;
        private static NotifyIcon? trayIcon;
        private static AppConfig config = new AppConfig();
        private static List<ManagedApp> managedApps = new List<ManagedApp>();
        private static bool isManualExit = false;

        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        [STAThread]
        static void Main(string[] args)
        {
            const string appGuid = "Global\\TrayWrapperApp_SingleInstance_Guid_987123";
            mutex = new Mutex(true, appGuid, out bool isNewInstance);
            if (!isNewInstance)
            {
                return;
            }

            // ซ่อมแซมและเติม System PATH เผื่อตอนรันตอน Auto-Start แล้ว PATH ยังไม่สมบูรณ์
            EnsureSystemPath();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LoadConfig();

            // รองรับ Drag & Drop ไฟล์มาหย่อนทับ
            if (args != null && args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            {
                string droppedFile = CleanPath(args[0]);
                if (File.Exists(droppedFile))
                {
                    if (managedApps.Count == 0)
                    {
                        var newProg = new ProgramItemConfig
                        {
                            Name = Path.GetFileNameWithoutExtension(droppedFile),
                            TargetPath = droppedFile,
                            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(droppedFile)) ?? "",
                            HideWindow = true
                        };
                        config.Programs.Add(newProg);
                        managedApps.Add(new ManagedApp(newProg));
                    }
                    else
                    {
                        managedApps[0].Config.TargetPath = droppedFile;
                        managedApps[0].Config.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(droppedFile)) ?? "";
                    }
                    SaveConfig();
                }
            }

            InitTrayIcon();

            if (managedApps.Count == 0)
            {
                PromptAddNewProgram();
            }
            else
            {
                StartAllProcesses();
            }

            Application.Run();

            GC.KeepAlive(mutex);
        }

        private static void EnsureSystemPath()
        {
            try
            {
                string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System); // C:\Windows\System32
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows); // C:\Windows
                string wbem = Path.Combine(sys32, "wbem");
                string powershell = Path.Combine(sys32, "WindowsPowerShell", "v1.0");

                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                var paths = new List<string>(currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries));

                if (!paths.Contains(sys32, StringComparer.OrdinalIgnoreCase)) paths.Insert(0, sys32);
                if (!paths.Contains(winDir, StringComparer.OrdinalIgnoreCase)) paths.Insert(1, winDir);
                if (!paths.Contains(wbem, StringComparer.OrdinalIgnoreCase)) paths.Add(wbem);
                if (!paths.Contains(powershell, StringComparer.OrdinalIgnoreCase)) paths.Add(powershell);

                string newPath = string.Join(";", paths);
                Environment.SetEnvironmentVariable("PATH", newPath);
            }
            catch { }
        }

        private static void LoadConfig()
        {
            if (!File.Exists(ConfigPath))
            {
                CreateDefaultConfig();
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
                config = new AppConfig();
            }

            if (config.Programs == null || config.Programs.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(config.TargetPath))
                {
                    config.Programs = new List<ProgramItemConfig>
                    {
                        new ProgramItemConfig
                        {
                            Name = "Service 1",
                            TargetPath = CleanPath(config.TargetPath),
                            Arguments = config.Arguments ?? "",
                            WorkingDirectory = CleanPath(config.WorkingDirectory ?? ""),
                            HideWindow = config.HideWindow ?? true,
                            AutoRestartOnCrash = config.AutoRestartOnCrash ?? false
                        }
                    };
                }
                else
                {
                    CreateDefaultConfig();
                    return;
                }
            }

            managedApps.Clear();
            foreach (var p in config.Programs)
            {
                p.TargetPath = CleanPath(p.TargetPath);
                p.WorkingDirectory = CleanPath(p.WorkingDirectory);

                if (string.IsNullOrWhiteSpace(p.WorkingDirectory) && !string.IsNullOrWhiteSpace(p.TargetPath))
                {
                    try
                    {
                        string? dir = Path.GetDirectoryName(Path.GetFullPath(p.TargetPath));
                        if (!string.IsNullOrWhiteSpace(dir)) p.WorkingDirectory = dir;
                    }
                    catch { }
                }

                managedApps.Add(new ManagedApp(p));
            }
        }

        private static void CreateDefaultConfig()
        {
            config = new AppConfig
            {
                TrayTooltip = "Print Center & Background Services",
                CustomIconPath = "",
                ExitPassword = "",
                Programs = new List<ProgramItemConfig>
                {
                    new ProgramItemConfig
                    {
                        Name = "Print Center Timer",
                        TargetPath = @"C:\appsoft\bin\AutoStartPrintCenter\AutoStartDevPrintCenterTimer.bat",
                        Arguments = "",
                        WorkingDirectory = @"C:\appsoft\bin\AutoStartPrintCenter",
                        HideWindow = true,
                        AutoRestartOnCrash = false
                    },
                    new ProgramItemConfig
                    {
                        Name = "Dev Cmd Timer",
                        TargetPath = @"C:\appsoft\bin\AutoStartDevCmdTimer.bat",
                        Arguments = "",
                        WorkingDirectory = @"C:\appsoft\bin",
                        HideWindow = true,
                        AutoRestartOnCrash = false
                    }
                }
            };

            SaveConfig();

            managedApps.Clear();
            foreach (var p in config.Programs)
            {
                managedApps.Add(new ManagedApp(p));
            }
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
            else if (managedApps.Count > 0 && File.Exists(managedApps[0].Config.TargetPath) && Path.GetExtension(managedApps[0].Config.TargetPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try { icon = Icon.ExtractAssociatedIcon(managedApps[0].Config.TargetPath) ?? SystemIcons.Application; } catch { }
            }

            trayIcon = new NotifyIcon
            {
                Icon = icon,
                Text = config.TrayTooltip.Length > 63 ? config.TrayTooltip.Substring(0, 63) : config.TrayTooltip,
                Visible = true
            };

            trayIcon.DoubleClick += (s, e) => ToggleAllTargetWindows();

            UpdateContextMenu();
        }

        private static void UpdateContextMenu()
        {
            if (trayIcon == null) return;

            ContextMenuStrip contextMenu = new ContextMenuStrip();
            
            var toggleAllItem = new ToolStripMenuItem("👁️ แสดง / ซ่อน ทุกหน้าต่าง (Show/Hide All)", null, (s, e) => ToggleAllTargetWindows());
            var restartAllItem = new ToolStripMenuItem("🔄 Restart ทุกโปรแกรม (Restart All)", null, (s, e) => RestartAllProcesses());
            
            contextMenu.Items.Add(toggleAllItem);
            contextMenu.Items.Add(restartAllItem);
            contextMenu.Items.Add(new ToolStripSeparator());

            // เมนูย่อยสำหรับแต่ละโปรแกรม
            for (int i = 0; i < managedApps.Count; i++)
            {
                var app = managedApps[i];
                string displayName = string.IsNullOrWhiteSpace(app.Config.Name) ? $"โปรแกรม {i + 1}" : app.Config.Name;

                var subMenu = new ToolStripMenuItem($"⚙️ {displayName}");
                subMenu.DropDownItems.Add(new ToolStripMenuItem("👁️ แสดง / ซ่อน หน้าต่าง", null, (s, e) => ToggleSingleAppWindow(app)));
                subMenu.DropDownItems.Add(new ToolStripMenuItem("🔄 Restart โปรแกรมนี้", null, (s, e) => RestartSingleProcess(app)));
                subMenu.DropDownItems.Add(new ToolStripMenuItem("🎯 เปลี่ยนไฟล์โปรแกรมนี้...", null, (s, e) => PromptChangeAppTarget(app)));
                subMenu.DropDownItems.Add(new ToolStripMenuItem("📁 เปิดโฟลเดอร์", null, (s, e) => OpenSingleFolder(app)));

                contextMenu.Items.Add(subMenu);
            }

            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(new ToolStripMenuItem("➕ เพิ่มโปรแกรมใหม่...", null, (s, e) => PromptAddNewProgram()));
            contextMenu.Items.Add(new ToolStripMenuItem("📝 แก้ไข config.json", null, (s, e) => OpenConfigFile()));
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(new ToolStripMenuItem("❌ ปิดโปรแกรมทั้งหมด (Exit)", null, (s, e) => HandleExitRequest()));

            trayIcon.ContextMenuStrip = contextMenu;
        }

        private static void PromptChangeAppTarget(ManagedApp app)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = $"เลือกไฟล์ใหม่สำหรับ: {app.Config.Name}";
                ofd.Filter = "Executable & Scripts (*.bat;*.cmd;*.exe;*.lnk)|*.bat;*.cmd;*.exe;*.lnk|All Files (*.*)|*.*";
                
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    app.Config.TargetPath = ofd.FileName;
                    app.Config.WorkingDirectory = Path.GetDirectoryName(ofd.FileName) ?? "";
                    SaveConfig();

                    UpdateContextMenu();
                    RestartSingleProcess(app);
                    trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", $"เปลี่ยนไฟล์สำหรับ {app.Config.Name} สำเร็จแล้ว", ToolTipIcon.Info);
                }
            }
        }

        private static void PromptAddNewProgram()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "เลือกไฟล์โปรแกรม (.bat / .exe / .lnk / script) ที่ต้องการเพิ่ม";
                ofd.Filter = "Executable & Scripts (*.bat;*.cmd;*.exe;*.lnk)|*.bat;*.cmd;*.exe;*.lnk|All Files (*.*)|*.*";
                
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    var newConfig = new ProgramItemConfig
                    {
                        Name = Path.GetFileNameWithoutExtension(ofd.FileName),
                        TargetPath = ofd.FileName,
                        WorkingDirectory = Path.GetDirectoryName(ofd.FileName) ?? "",
                        HideWindow = true,
                        AutoRestartOnCrash = false
                    };

                    config.Programs.Add(newConfig);
                    SaveConfig();

                    var newApp = new ManagedApp(newConfig);
                    managedApps.Add(newApp);

                    UpdateContextMenu();
                    StartSingleProcess(newApp);
                    trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", $"เพิ่มโปรแกรม {newConfig.Name} เรียบร้อยแล้ว", ToolTipIcon.Info);
                }
            }
        }

        private static void OpenConfigFile()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{ConfigPath}\"", UseShellExecute = true });
                }
            }
            catch { }
        }

        private static void StartAllProcesses()
        {
            foreach (var app in managedApps)
            {
                StartSingleProcess(app);
            }
        }

        private static void StartSingleProcess(ManagedApp app)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(app.Config.TargetPath) || !File.Exists(app.Config.TargetPath))
                {
                    return;
                }

                string target = app.Config.TargetPath;
                string args = app.Config.Arguments ?? "";
                string ext = Path.GetExtension(target).ToLower();

                ProcessStartInfo psi = new ProcessStartInfo();

                if (ext == ".bat" || ext == ".cmd")
                {
                    string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    string cmdExePath = Path.Combine(sys32, "cmd.exe");
                    psi.FileName = File.Exists(cmdExePath) ? cmdExePath : "cmd.exe";
                    psi.Arguments = $"/k \"\"{target}\" {args}\"";
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

                if (!string.IsNullOrWhiteSpace(app.Config.WorkingDirectory) && Directory.Exists(app.Config.WorkingDirectory))
                {
                    psi.WorkingDirectory = app.Config.WorkingDirectory;
                }
                else if (File.Exists(target))
                {
                    psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(target)) ?? "";
                }

                if (app.Config.HideWindow && ext != ".lnk")
                {
                    psi.UseShellExecute = false;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                }
                else
                {
                    psi.UseShellExecute = true;
                    psi.WindowStyle = ProcessWindowStyle.Normal;
                }

                app.ChildProcess = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                app.StartTime = DateTime.Now;

                app.ChildProcess.Exited += (s, e) =>
                {
                    if (isManualExit) return;

                    var runtime = DateTime.Now - app.StartTime;

                    if (!app.Config.AutoRestartOnCrash || runtime.TotalSeconds < 10)
                    {
                        return;
                    }

                    app.CrashCount++;
                    if (app.CrashCount <= 3)
                    {
                        Task.Delay(5000).ContinueWith(_ => StartSingleProcess(app));
                    }
                    else
                    {
                        trayIcon?.ShowBalloonTip(5000, "Tray Wrapper", $"โปรแกรม {app.Config.Name} ดับบ่อยเกินไป จึงระงับ Auto-Restart", ToolTipIcon.Warning);
                    }
                };

                app.ChildProcess.Start();
                app.SpawnedPid = app.ChildProcess.Id;

                Task.Run(async () =>
                {
                    app.WindowHandle = IntPtr.Zero;

                    for (int i = 0; i < 25; i++)
                    {
                        await Task.Delay(100);
                        app.WindowHandle = FindProcessWindow(app);
                        if (app.WindowHandle != IntPtr.Zero)
                        {
                            DisableCloseButton(app.WindowHandle);

                            if (app.Config.HideWindow)
                            {
                                ShowWindow(app.WindowHandle, SW_HIDE);
                                app.IsWindowShown = false;
                            }
                            else
                            {
                                app.IsWindowShown = true;
                            }
                            break;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                trayIcon?.ShowBalloonTip(4000, "Tray Wrapper Error", $"ไม่สามารถเปิด {app.Config.Name} ได้: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private static void DisableCloseButton(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            try
            {
                IntPtr hMenu = GetSystemMenu(hWnd, false);
                if (hMenu != IntPtr.Zero)
                {
                    EnableMenuItem(hMenu, SC_CLOSE, MF_BYCOMMAND | MF_DISABLED | MF_GRAYED);
                    RemoveMenu(hMenu, SC_CLOSE, MF_BYCOMMAND);
                }
            }
            catch { }
        }

        private static IntPtr FindProcessWindow(ManagedApp app)
        {
            if (app.ChildProcess != null)
            {
                try
                {
                    app.ChildProcess.Refresh();
                    if (app.ChildProcess.MainWindowHandle != IntPtr.Zero)
                    {
                        return app.ChildProcess.MainWindowHandle;
                    }
                }
                catch { }
            }

            IntPtr foundHwnd = IntPtr.Zero;
            string targetFileName = Path.GetFileNameWithoutExtension(app.Config.TargetPath);

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint procId);

                if (app.SpawnedPid > 0 && procId == app.SpawnedPid)
                {
                    foundHwnd = hWnd;
                    return false;
                }

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

        private static void ToggleAllTargetWindows()
        {
            bool anyVisible = false;

            foreach (var app in managedApps)
            {
                if (app.WindowHandle == IntPtr.Zero) app.WindowHandle = FindProcessWindow(app);
                if (app.WindowHandle != IntPtr.Zero && IsWindowVisible(app.WindowHandle))
                {
                    anyVisible = true;
                    break;
                }
            }

            foreach (var app in managedApps)
            {
                if (app.WindowHandle == IntPtr.Zero) app.WindowHandle = FindProcessWindow(app);
                if (app.WindowHandle == IntPtr.Zero) continue;

                DisableCloseButton(app.WindowHandle);

                if (anyVisible)
                {
                    ShowWindow(app.WindowHandle, SW_HIDE);
                    app.IsWindowShown = false;
                }
                else
                {
                    ShowWindow(app.WindowHandle, SW_RESTORE);
                    ShowWindow(app.WindowHandle, SW_SHOW);
                    SetForegroundWindow(app.WindowHandle);
                    app.IsWindowShown = true;
                }
            }
        }

        private static void ToggleSingleAppWindow(ManagedApp app)
        {
            try
            {
                if (app.WindowHandle == IntPtr.Zero)
                {
                    app.WindowHandle = FindProcessWindow(app);
                }

                if (app.WindowHandle == IntPtr.Zero)
                {
                    trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", $"ไม่พบหน้าต่างของ {app.Config.Name}", ToolTipIcon.Info);
                    return;
                }

                DisableCloseButton(app.WindowHandle);

                if (app.IsWindowShown && IsWindowVisible(app.WindowHandle))
                {
                    ShowWindow(app.WindowHandle, SW_HIDE);
                    app.IsWindowShown = false;
                }
                else
                {
                    ShowWindow(app.WindowHandle, SW_RESTORE);
                    ShowWindow(app.WindowHandle, SW_SHOW);
                    SetForegroundWindow(app.WindowHandle);
                    app.IsWindowShown = true;
                }
            }
            catch (Exception ex)
            {
                trayIcon?.ShowBalloonTip(3000, "Error", $"ไม่สามารถสลับหน้าต่างได้: {ex.Message}", ToolTipIcon.Warning);
            }
        }

        private static void RestartAllProcesses()
        {
            foreach (var app in managedApps)
            {
                RestartSingleProcess(app);
            }
            trayIcon?.ShowBalloonTip(2000, "Tray Wrapper", "สั่ง Restart ทุกโปรแกรมเรียบร้อยแล้ว", ToolTipIcon.Info);
        }

        private static void RestartSingleProcess(ManagedApp app)
        {
            app.CrashCount = 0;
            KillSingleProcess(app);
            StartSingleProcess(app);
        }

        private static void KillSingleProcess(ManagedApp app)
        {
            try
            {
                app.WindowHandle = IntPtr.Zero;
                if (app.ChildProcess != null && !app.ChildProcess.HasExited)
                {
                    app.ChildProcess.Kill(entireProcessTree: true);
                    app.ChildProcess.WaitForExit(3000);
                }
            }
            catch { }
        }

        private static void OpenSingleFolder(ManagedApp app)
        {
            try
            {
                string dir = !string.IsNullOrWhiteSpace(app.Config.WorkingDirectory) && Directory.Exists(app.Config.WorkingDirectory)
                    ? app.Config.WorkingDirectory
                    : Path.GetDirectoryName(Path.GetFullPath(app.Config.TargetPath)) ?? "";

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
                string? input = PromptPasswordDialog("กรุณากรอกรหัสผ่านเพื่อปิดโปรแกรมทั้งหมด:", "ยืนยันการปิด Services");
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

            foreach (var app in managedApps)
            {
                KillSingleProcess(app);
            }

            Application.Exit();
        }
    }
}
