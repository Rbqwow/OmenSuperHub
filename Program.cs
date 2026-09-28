using System;
using OmenSuperHub.Control;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hp.Bridge.Client.SDKs.PerformanceControl.DataStructure;
using HP.Omen.Core.Model.Device.Enums;
using HP.Omen.Core.Model.Device.Models;
using Microsoft.Win32;
using static HP.Omen.Core.Model.Device.Models.GraphicsSwitcherHelper;
using static OmenSuperHub.GpuAppManager;
using static OmenSuperHub.OmenHardware;
using LibreComputer = LibreHardwareMonitor.Hardware.Computer;

namespace OmenSuperHub {
  static partial class Program {
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    static int textSize = 40;
    static int alreadyRead = 0, alreadyReadCode = 1000;
    static readonly string[] PresetOrder = { "PresetExtreme", "PresetGpuPriority", "PresetLightUse", "PresetCustom1", "PresetCustom2", "PresetCustom3" };
    static string currentPreset = "PresetCustom1", presetCustom1Name = Strings.PresetCustom1, presetCustom2Name = Strings.PresetCustom2, presetCustom3Name = Strings.PresetCustom3;
    static string fanTable = "cool", fanControl = "auto", tempSensitivity = "high", tppPower = "null", iccMax = "null", acLoadline = "null", cpuPower = "null", tgpPower = "on", ppabPower = "on", dState = "normal", autoStart = "off", customIcon = "original", floatingBar = "off", floatingBarLoc = "left", floatingBarScreen = "", omenKey = OmenKeyActions.Default, omenKeyAppPath = "", omenKeyAppName = "", omenKeyShortcut = "", omenKeyPresetCandidates = "", dataLocalize = "off", appLanguage = "zh-CN", autoFanProtect = "on";
    static volatile bool monitorFan = false;
    static bool skipCheckedUpdate = false; // action 内拦截时置 true，阻止 CreateMenuItem 覆盖勾选
    static bool showCPUTemp = true, showCPUPower = true, showGPUTemp = true, showGPUPower = true;
    static bool powerOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
    static bool monitorCPU = true, monitorGPU = true; // isTwoBytePL4 = false;
    static bool hasNVIDIAGpu; // 启动时一次性检测，硬件状态不会改变
    static string monitorRefreshRate = "low"; // 刷新频率：low=1s, high=0.25s

    static float respondSpeed = 0.4f;
    // 进程退出标志：Exit() 首行置位，所有定时器回调与后台任务入口据此立即返回，
    // 避免在资源已释放后继续访问（例如已 Close() 的 libreComputer）。
    static volatile bool _isExiting = false;

    static int? maxCPUTemp = null;
    static int? maxGPUTemp = null;
    static float CPUTemp, GPUTemp;
    static float CPUPower = float.NaN, GPUPower = float.NaN;
    static volatile bool cpuTempReady = false; // CPU 温度已初始化给平滑值，允许参与风扇控制
    static volatile bool gpuTempReady = false; // GPU 温度已初始化给平滑值，允许参与风扇控制
    static LibreComputer libreComputer = new LibreComputer();

    static string pawnIOState = "";
    static string tempDisplayMode = "smoothed"; // 温度显示方式：smoothed=平滑值, raw=原始值
    static int? platformMaxFanSpeed = null; // 平台最大转速（RPM），由LoadDefaultFanConfig获取后缓存
    static SortedDictionary<float, int> CPUTempFanMap = new SortedDictionary<float, int>();
    static SortedDictionary<float, int> GPUTempFanMap = new SortedDictionary<float, int>();
    static System.Threading.Timer fanControlTimer;
    static System.Timers.Timer tooltipUpdateTimer; // Timer for updating tooltip
    static NotifyIcon trayIcon;
    // 内置默认托盘图标的唯一实例。Properties.Resources.smallfan 每次访问都会经 ResourceManager
    // 反序列化出一个新的 Icon（非基元类型资源不缓存）：用它做引用比较恒为 true，且每次访问都
    // 产生一个只能等终结器回收的 HICON。默认图标的赋值与比较一律经由本字段。
    static readonly Icon DefaultTrayIcon = Properties.Resources.smallfan;
    // 上次成功应用到托盘的动态图标及其显示数值（仅 UI 线程写入），用于跳过数值未变化时的重绘与 Shell 通知
    static Icon _lastDynamicIcon;
    static int _lastDynamicIconValue = int.MinValue;
    static FloatingForm floatingForm;
    static ToolStripMenuItem irSensorMenu;
    static ToolStripMenuItem ambientSensorMenu;
    static ToolStripMenuItem pchSensorMenu;
    static ToolStripMenuItem vrSensorMenu;
    static ToolStripTrackBar fanTrackBar, cpuPowerTrackBar, tppTrackBar, textSizeTrackBar;
    static ToolStripMenuItem fanValueLabel, cpuPowerValueLabel, tppValueLabel, textSizeLabel;

    static bool Is3FanNb = false, isFanCleanSupported = false, isFanLegacyCleanSupported = false;
    static volatile bool isSysInfoMenuOpen = false;
    static string systemSSID, sku, biosVersion;
    static bool supportHotSwitch = false;
    static bool isCPUPowerControlSupported = false, isAmbientSensorSupported = false;
    static DeviceEnums.DeviceType deviceType;
    static string deviceDisplayName;
    static int cycleNumber;
    static PlatformSettings platformSettings;
    static GraphicsSwitcherMode NvGraphicsMode;
    static SynchronizationContext uiContext;
    //static Stopwatch sw = Stopwatch.StartNew();

    [STAThread]
    static void Main(string[] args) {
      //Console.WriteLine($"0.1: {sw.ElapsedMilliseconds}ms");

      // ── 静默重启模式：由任务计划登录触发器调用
      if (args.Length > 0 && args[0] == "--relaunch") {
        // 终止其他已有实例
        var currentId = Process.GetCurrentProcess().Id;
        foreach (var proc in Process.GetProcessesByName("OmenSuperHub")) {
          if (proc.Id == currentId) continue;
          try { proc.Kill(); proc.WaitForExit(3000); } catch { }
        }
        // 启动新实例（不带参数，走正常流程）
        Process.Start(new ProcessStartInfo {
          FileName = Application.ExecutablePath,
          UseShellExecute = true
        });
        return; // 本实例立即退出，不做任何初始化
      }

      bool isNewInstance;
      using (Mutex mutex = new Mutex(true, "MyUniqueAppMutex", out isNewInstance)) {
        if (!isNewInstance) {
          return;
        }

        if (Environment.OSVersion.Version.Major >= 6) {
          SetProcessDPIAware();
        }

        AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
        Application.ThreadException += new ThreadExceptionEventHandler(Application_ThreadException);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;

        Version version = Assembly.GetExecutingAssembly().GetName().Version;
        string versionString = version.ToString().Replace(".", "");
        alreadyReadCode = new Random(int.Parse(versionString)).Next(1000, 10000);

        // 读取 deviceDisplayName / cycleNumber / deviceType / systemSSID / alreadyRead
        LoadDeviceInfoFromRegistry();
        //Console.WriteLine($"0.2: {sw.ElapsedMilliseconds}ms");
        // 每版本仅显示一次
        if (alreadyRead != alreadyReadCode) {
          string validationResult = Validation(deviceDisplayName);
          if (validationResult == Strings.ValidationUnsupported) {
            var result = MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.ProductUnsupported, Strings.Warning, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.OK)
              return; // 退出程序
          } else if (validationResult == Strings.ValidationUnsupportedHPProduct) {
            var result = MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.ProductUnsupportedHP, Strings.Warning, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.OK)
              return; // 退出程序
          } else if (validationResult == Strings.ValidationOldOmenProduct) {
            var result = MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.ProductOldOmen, Strings.Warning, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.OK)
              return; // 退出程序
          }
        }

        var t1 = Task.Run(() => {
          //Console.WriteLine($"1.1: {sw.ElapsedMilliseconds}ms");
          platformSettings = PerformanceControlHelper.GetPlatformSettings(deviceType.ToString(), sku);
          //Console.WriteLine($"1.2: {sw.ElapsedMilliseconds}ms");

          if (platformSettings != null) {
            currentPreset = "PresetExtreme";
            isCPUPowerControlSupported = true;
          }
          //isCPUPowerControlSupported = IsPowerControlSupported(deviceType); // 似乎不准确
          InitPlatformMaxFanSpeed();
          InitMaxTemp();
        });
        var t2 = Task.Run(() => {
          biosVersion = GetBiosVersion();
          cachedCpuModel = GetCpuModel();
          cachedAdapterPower = GetAdapterPower();
          pawnIOState = GetPawnIOState();
          cachedGfxModes = GetSupportedGfxModes();
          cachedLoadLineSupported = IsLoadLineSupported();
          cachedLoadLineLevels = GetLoadLineSupportLevels();
        });
        var t3 = Task.Run(() => {
          hasNVIDIAGpu = HasNvidiaGpu();
          if (hasNVIDIAGpu) {
            cachedGpuModel = GetGpuModelFromNvidiaSmi();
            int gpuTemperatureTarget = GetGpuTemperatureTarget();
            if (gpuTemperatureTarget > 50) maxGPUTemp = gpuTemperatureTarget;
            ExtractAndPreloadNativeDll("NvidiaApi.dll");
          }
        });
        var t5 = Task.Run(() => NvGraphicsMode = GetGfxMode());
        var t6 = Task.Run(() => {
          Is3FanNb = IsThreeFanSupported();
        });
        var t7 = Task.Run(() => {
          isFanCleanSupported = IsCleanCreekSupported();
          isFanLegacyCleanSupported = IsLegacyCleanCreekSupported();
        });
        var t8 = Task.Run(() => {
          getOmenKeyTask();
        });
        var t9 = Task.Run(() => {
          SetBrowserEmulationForWebBrowser();
          int irTemp = GetSensorTemperature(0);
          int ambientTemp = GetSensorTemperature(1);
          isAmbientSensorSupported = ambientTemp > 1 && irTemp != ambientTemp;
        });
        //var t10 = Task.Run(() => isTwoBytePL4 = IsTwoBytePL4Supported());

        //Console.WriteLine($"1: {sw.ElapsedMilliseconds}ms");
        Task.WaitAll(t1, t2, t3, t5, t6, t7, t8, t9);
        //Console.WriteLine($"2: {sw.ElapsedMilliseconds}ms");

        LoadLanguageSetting();  // 必须在 InitTrayIcon 之前，使菜单使用正确语言
        InitTrayIcon();
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);
        InitializeBackgroundControl();
        //Console.WriteLine($"3: {sw.ElapsedMilliseconds}ms");

        // Main loop to query CPU and GPU temperature every second
        fanControlTimer = new System.Threading.Timer(_ => EvaluateFan(), null, Timeout.Infinite, Timeout.Infinite);

        RestoreConfig();
        if (runtimeTarget == null) { LoadFanConfig(fanTable + ".txt"); CommitSettings(); }
        tooltipUpdateTimer.Start();
        fanControlTimer.Change(0, 1000);
        //Console.WriteLine($"4: {sw.ElapsedMilliseconds}ms");

        if (alreadyRead != alreadyReadCode) {
          HelpForm.Instance.Show();
          alreadyRead = alreadyReadCode;
          SaveConfig("AlreadyRead");
        }

        SystemEvents.PowerModeChanged += new PowerModeChangedEventHandler(OnPowerChange);
        SystemEvents.DisplaySettingsChanged += OnDisplayChange;
        //PrintSystemDesignData();

        //MessageBox.Show($"消息测试", Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);

        //trayIcon.BalloonTipTitle = "消息测试";
        //trayIcon.BalloonTipText = $"消息测试";
        //trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
        //trayIcon.ShowBalloonTip(3000);

        //Stopwatch sw = Stopwatch.StartNew();
        //Console.WriteLine($"1: {sw.ElapsedMilliseconds}ms");
        //Console.Error.WriteLine("CRASH: " + $"1: {sw.ElapsedMilliseconds}ms");

        //Platform omenPlatform = DeviceModel.OmenPlatform;
        //Console.WriteLine($"Platform Name: {omenPlatform.Name}");
        //Console.WriteLine($"Display Name: {omenPlatform.DisplayName}");
        //Console.WriteLine($"Features: {string.Join(", ", omenPlatform.Feature ?? new List<string>())}");
        //Console.WriteLine($"IsNVdGPU: {OmenHsaClient.IsNVdGPU}");
        //Console.WriteLine($"IsIntelGPU: {OmenHsaClient.IsIntelGPU}");
        //Console.WriteLine($"IsGpuSupport: {OmenHsaClient.IsGpuSupport}");
        //Console.WriteLine($"IsIntelGraphics: {OmenHsaClient.IsIntelGraphics()}");

        Logger.Info($"version: {version}");
        Application.Run();
      }
    }

    private static void SetBrowserEmulationForWebBrowser() {
      string appName = Process.GetCurrentProcess().ProcessName + ".exe";
      using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION", true)) {
        if (key == null) {
          // 如果键不存在则创建
          Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
          using (var newKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION", true)) {
            newKey?.SetValue(appName, 11001, RegistryValueKind.DWord);
          }
        } else {
          key.SetValue(appName, 11001, RegistryValueKind.DWord);
        }
      }
    }

    static string GetBiosVersion() {
      using (var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
      using (var collection = searcher.Get())
        foreach (ManagementObject obj in collection)
          return obj["SMBIOSBIOSVersion"]?.ToString() ?? "未知";
      return "未知";
    }

    static string GetCpuModel() {
      using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
      using (var collection = searcher.Get())
        foreach (ManagementObject obj in collection)
          return obj["Name"]?.ToString()?.Trim() ?? "未知";
      return "未知";
    }

    public static bool HasIntelCpu() {
      try {
        using (var searcher = new ManagementObjectSearcher(
            "root\\CIMV2", "SELECT Manufacturer, Name FROM Win32_Processor")) {
          foreach (var obj in searcher.Get()) {
            string manufacturer = obj["Manufacturer"]?.ToString() ?? "";
            string name = obj["Name"]?.ToString() ?? "";

            // GenuineIntel 是 Intel CPU 的标准制造商字符串
            if (manufacturer.IndexOf("GenuineIntel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0) {
              return true;
            }
          }
        }
      } catch { }
      return false;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    private static void ExtractAndPreloadNativeDll(string dllName) {
      var currentAssembly = Assembly.GetExecutingAssembly();

      // 在嵌入资源中查找（资源名通常是 "命名空间.文件名"）
      var resourceName = currentAssembly
          .GetManifestResourceNames()
          .FirstOrDefault(r => r.EndsWith(dllName, StringComparison.OrdinalIgnoreCase));

      if (resourceName == null) {
        throw new FileNotFoundException($"嵌入资源中找不到 {dllName}");
      }

      // 释放到程序目录（或 Temp 目录）
      string outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dllName);

      if (!File.Exists(outputPath)) {
        using (var stream = currentAssembly.GetManifestResourceStream(resourceName))
        using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write)) {
          stream.CopyTo(fs);
        }
      }

      // 提前加载，之后 DllImport 会自动复用
      IntPtr handle = LoadLibrary(outputPath);
      if (handle == IntPtr.Zero) {
        Logger.Error($"LoadLibrary 失败，错误码: {Marshal.GetLastWin32Error()}");
      } else {
        supportHotSwitch = true;
      }
    }

    private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs args) {
      var assemblyName = new AssemblyName(args.Name).Name + ".dll";

      var currentAssembly = Assembly.GetExecutingAssembly();

      var resourceName = currentAssembly
          .GetManifestResourceNames()
          .FirstOrDefault(r => r.EndsWith(assemblyName, StringComparison.OrdinalIgnoreCase));

      if (resourceName == null)
        return null;

      using (var stream = currentAssembly.GetManifestResourceStream(resourceName)) {
        if (stream == null)
          return null;

        var buffer = new byte[stream.Length];
        stream.Read(buffer, 0, buffer.Length);

        return Assembly.Load(buffer);
      }
    }

    /// <summary>
    /// 依据电源状态自适应调整硬件轮询频率。
    ///
    /// - 电池供电（Offline）：强制降频至 1000ms，减少 UI 线程与线程池的定时唤醒次数，
    ///   避免破坏 CPU C-State 驻留、拉高待机功耗；
    /// - 接入电源（Online）：恢复用户在"刷新频率"菜单中的配置（high → 250ms，否则 1000ms）。
    ///
    /// 注意：本方法必须在 UI 线程调用（与 tooltipUpdateTimer.Interval 的其余写入点同线程）。
    /// 预设切换与菜单切换刷新频率时也应经由本方法，否则会把电池降频结果覆盖掉。
    /// </summary>
    static void ApplyAdaptivePollingInterval() {
      bool onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
      int targetInterval = SensorRules.PollingInterval(onBattery, monitorRefreshRate == "high");

      if (tooltipUpdateTimer != null && tooltipUpdateTimer.Interval != targetInterval)
        tooltipUpdateTimer.Interval = targetInterval;
    }

    static void OnPowerChange(object s, PowerModeChangedEventArgs e) {
      if (_isExiting || (e.Mode != PowerModes.Resume && e.Mode != PowerModes.StatusChange)) return;
      PublishUi(power: powerUpdates.Offer(e.Mode == PowerModes.Resume));
    }

    static void ApplyPowerChange(bool resume) {
      if (_isExiting) return;
      bool connected = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
      if (resume) {
        libreComputer.RequestGpuRefresh();
        monitorVersion++;
        RestorePowerConfig();
        tooltipUpdateTimer.Start();
      } else if (connected && !powerOnline) RestorePowerConfig();
      powerOnline = connected;
      ApplyAdaptivePollingInterval();
    }

    static void TrayIcon_MouseClick(object sender, MouseEventArgs e) {
      if (e.Button == MouseButtons.Left) {
        ToggleFloatingBar();
      }
    }

    static bool CheckCustomIcon() {
      string currentPath = AppDomain.CurrentDomain.BaseDirectory;
      string iconPath = Path.Combine(currentPath, "custom.ico");
      // 检查图标文件是否存在
      if (File.Exists(iconPath)) {
        return true;
      } else {
        MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.NoCustomIcon, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
      }
    }

    static void SetCustomIcon() {
      string currentPath = AppDomain.CurrentDomain.BaseDirectory;
      string iconPath = Path.Combine(currentPath, "custom.ico");
      // 检查图标文件是否存在
      if (File.Exists(iconPath)) {
        trayIcon.Icon = new Icon(iconPath);
      } else {
        MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.NoCustomIcon, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    // 根据当前监控状态决定动态图标显示内容：
    // CPU监控开 → CPU温度；CPU关GPU开 → GPU温度；均关 → 原版图标（不改 customIcon 设置）
    static void UpdateDynamicIcon() {
      if (customIcon != "dynamic") return;
      if (trayIcon?.ContextMenuStrip != null && trayIcon.ContextMenuStrip.Visible) return;
      if (monitorCPU && displayedTelemetry.CpuTemperature.Fresh(controlClock.Milliseconds)) {
        GenerateDynamicIconIfChanged((int)CPUTemp);
      } else if (monitorGPU && displayedTelemetry.GpuTemperature.Fresh(controlClock.Milliseconds)) {
        GenerateDynamicIconIfChanged((int)GPUTemp);
      } else {
        ApplyTrayIconSwapToDefault();
      }
    }

    /// <summary>
    /// 数值未变化且托盘当前显示的仍是上次生成的动态图标时，跳过位图渲染、HICON 创建与
    /// Shell_NotifyIcon 更新（250ms 档下原本每秒执行 4 次）。
    /// </summary>
    static void GenerateDynamicIconIfChanged(int number) {
      var current = trayIcon?.Icon;
      if (number == _lastDynamicIconValue && current != null && ReferenceEquals(current, _lastDynamicIcon))
        return;
      GenerateDynamicIcon(number);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    extern static bool DestroyIcon(IntPtr handle);
    static void GenerateDynamicIcon(int number) {
      // 获取系统推荐的图标尺寸（已适配 DPI）
      Size iconSize = SystemInformation.IconSize;
      int width = iconSize.Width * 2;
      int height = iconSize.Height * 2;

      Icon newIcon = null;
      IntPtr hIcon = IntPtr.Zero;

      try {
        using (Bitmap bitmap = new Bitmap(width, height)) {
          using (Graphics graphics = Graphics.FromImage(bitmap)) {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            string text = number.ToString("00");

            using (Font font = new Font("Arial", 45.5f, FontStyle.Bold)) {
              // 测量文本大小
              SizeF textSize = graphics.MeasureString(text, font);

              // 计算居中位置
              float x = (width - textSize.Width) / 2;
              float y = (height - textSize.Height) / 8;

              // 绘制文本
              graphics.DrawString(text, font, Brushes.Tan, x, y);
            }

            // 转换为图标
            hIcon = bitmap.GetHicon();
            // Icon.FromHandle 仅对原生句柄做**弱引用**封装，不复制位图数据。
            // 必须先 Clone() 得到独立托管副本，之后才可安全销毁原始 hIcon，
            // 否则任务栏（Shell）正在引用的底层 HICON 会被提前物理释放，
            // 导致图标花屏或偶发 ArgumentException。
            using (Icon temp = Icon.FromHandle(hIcon))
              newIcon = (Icon)temp.Clone();
          }
        }
      } finally {
        // 副本已独立，此处销毁原始句柄不会影响 newIcon
        if (hIcon != IntPtr.Zero)
          DestroyIcon(hIcon);
      }

      ApplyTrayIconSwap(newIcon, number);
    }

    /// <summary>
    /// 把托盘图标的新图标切回 UI 线程赋值。
    /// NotifyIcon 创建于 UI 主线程，并持有接收 Shell 消息的不可见原生窗口；
    /// 从线程池线程直接赋值 `trayIcon.Icon` 违背 WinForms 线程访问隔离规范。
    /// uiContext 为空或已进入退出流程时安全降级：直接释放新图标并保留旧图标，避免句柄泄漏。
    /// </summary>
    static void ApplyTrayIconSwap(Icon newIcon, int number) {
      if (newIcon == null) return;

      var ctx = uiContext;
      if (_isExiting || ctx == null) {
        newIcon.Dispose();   // 安全降级：释放未被采用的图标，旧图标保持不变
        return;
      }

      ctx.Post(_ => SwapTrayIcon(newIcon, number), null);
    }

    /// <summary>把"切回内置图标"的动作切回 UI 线程执行。</summary>
    static void ApplyTrayIconSwapToDefault() {
      var ctx = uiContext;
      if (_isExiting || ctx == null) return;   // 无新建资源，无需释放
      if (ReferenceEquals(trayIcon?.Icon, DefaultTrayIcon)) return;   // 已是默认图标，无需切回 UI 线程
      ctx.Post(_ => SwapTrayIconToDefault(), null);
    }

    /// <summary>仅在 UI 线程调用：切换到新的动态图标并释放旧图标。</summary>
    static void SwapTrayIcon(Icon newIcon, int number) {
      if (_isExiting || trayIcon == null || newIcon == null) {
        newIcon?.Dispose();   // 未能采用则显式释放，避免句柄泄漏
        return;
      }

      Icon oldIcon = trayIcon.Icon;
      trayIcon.Icon = newIcon;
      _lastDynamicIconValue = number;
      _lastDynamicIcon = newIcon;
      // 旧图标为动态图标或自定义图标（本进程独占）时显式释放；默认图标为共享实例，不得释放
      if (oldIcon != null && !ReferenceEquals(oldIcon, DefaultTrayIcon)) {
        oldIcon.Dispose();
      }
    }

    /// <summary>仅在 UI 线程调用：切回内置图标并释放此前的动态 / 自定义图标。</summary>
    static void SwapTrayIconToDefault() {
      if (_isExiting || trayIcon == null) return;

      Icon oldIcon = trayIcon.Icon;
      if (ReferenceEquals(oldIcon, DefaultTrayIcon)) return;   // 已是默认图标，跳过 Shell 通知
      trayIcon.Icon = DefaultTrayIcon;
      _lastDynamicIcon = null;
      _lastDynamicIconValue = int.MinValue;
      oldIcon?.Dispose();
    }

    // 状态栏定时更新任务与硬件查询
    // 根据 floatingBarScreen 获取目标显示器，找不到时回退主屏幕
    static Screen GetFloatingScreen() {
      if (!string.IsNullOrEmpty(floatingBarScreen)) {
        foreach (var s in Screen.AllScreens) {
          if (s.DeviceName == floatingBarScreen) return s;
        }
      }
      return Screen.PrimaryScreen;
    }

    static readonly object _floatingLock = new object();
    // 显示浮窗
    static void ShowFloatingForm() {
      lock (_floatingLock) {
        if (floatingForm == null || floatingForm.IsDisposed) {
          floatingForm = new FloatingForm(monitorText(), textSize, floatingBarLoc, GetFloatingScreen());
          floatingForm.Show();
        } else {
          floatingForm.BringToFront();
        }
      }
    }

    // 关闭浮窗
    static void CloseFloatingForm() {
      lock (_floatingLock) {
        if (floatingForm != null && !floatingForm.IsDisposed) {
          floatingForm.Close();
          floatingForm.Dispose();
          floatingForm = null;
        }
      }
    }

    // 更新浮窗的文字内容
    static void UpdateFloatingText() {
      FloatingForm form;
      lock (_floatingLock) {
        form = floatingForm;
      }

      if (form == null || form.IsDisposed) return;

      lock (_floatingLock) {
        if (floatingForm == null || floatingForm.IsDisposed) return;
        // 构造时已设置置顶；在 UI 线程重复设置 TopMost 会激活浮窗，导致托盘菜单失焦关闭。
        floatingForm.SetText(monitorText(), textSize, floatingBarLoc, GetFloatingScreen());
      }
    }

    //生成监控信息
    static string monitorText() { return string.Join("\n", BuildMonitorLines(false)); }

    static void RefreshMonitorDisplay() {
      UpdateTrayIconText();
      if (floatingForm != null) {
        floatingForm.SetText(monitorText(), textSize, floatingBarLoc, GetFloatingScreen());
      }
    }

    static string GetPresetDisplayName(string presetKey) {
      switch (presetKey) {
        case "PresetExtreme": return Strings.PresetExtreme;
        case "PresetGpuPriority": return Strings.PresetGpuPriority;
        case "PresetLightUse": return Strings.PresetLightUse;
        case "PresetCustom1": return presetCustom1Name;
        case "PresetCustom2": return presetCustom2Name;
        case "PresetCustom3": return presetCustom3Name;
        default: return presetKey;
      }
    }

    static string GetCurrentPresetDisplayName() {
      return GetPresetDisplayName(currentPreset);
    }

    const int NotifyIconTextLimit = 63;

    static List<string> BuildMonitorLines(bool compact) {
      var lines = new List<string>();
      // 浮窗按冒号拆分标题着色，按逗号换行；托盘仍使用紧凑格式以适配长度限制。
      string titleSeparator = compact ? " " : ": ";
      string valueSeparator = compact ? " " : ", ";
      string numberFormat = compact ? "F0" : "F1";
      Telemetry data = displayedTelemetry;
      long now = controlClock.Milliseconds;
      bool matching = runtimeTarget != null && data.MonitorVersion == runtimeTarget.MonitorVersion;
      if (monitorCPU && (showCPUTemp || showCPUPower)) {
        var parts = new List<string>();
        if (showCPUTemp) parts.Add(matching && data.CpuTemperature.Fresh(now) ? CPUTemp.ToString(numberFormat) + "°C" +
          (data.CpuTemperature.Estimated ? " (" + Strings.EstimatedTemperature + ")" : "") : "--°C");
        if (showCPUPower) parts.Add(matching && data.CpuPower.Fresh(now) ? CPUPower.ToString(numberFormat) + "W" : Strings.UnknownPower);
        lines.Add("CPU" + titleSeparator + (matching && !data.CpuMonitorOpen && !data.CpuTemperature.Estimated ? Strings.ReadingUnavailable : string.Join(valueSeparator, parts)));
      }
      if (monitorGPU && (showGPUTemp || showGPUPower)) {
        var parts = new List<string>();
        if (showGPUTemp) parts.Add(matching && data.GpuTemperature.Fresh(now) ? GPUTemp.ToString(numberFormat) + "°C" : "--°C");
        if (showGPUPower) parts.Add(matching && data.GpuPower.Fresh(now) ? GPUPower.ToString(numberFormat) + "W" : Strings.UnknownPower);
        lines.Add("GPU" + titleSeparator + (matching && data.GpuSleeping ? Strings.GpuPoweredOff : matching && !data.GpuMonitorOpen ? Strings.ReadingUnavailable : string.Join(valueSeparator, parts)));
      }
      if (monitorFan) lines.Add("Fan" + titleSeparator + (matching && data.FanRpm.Fresh(now) && data.FanLevels != null ?
        string.Join(valueSeparator, data.FanLevels.Take(Is3FanNb ? 3 : 2).Select(x => (x * 100).ToString())) : Strings.ReadingUnavailable));
      if (lines.Count == 0) lines.Add(Strings.MonitorClosed);
      return lines;
    }

    static string EllipsizeUnicode(string value, int maxLength) {
      if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? "";
      if (maxLength <= 0) return "";
      if (maxLength == 1) return "…";

      int safeEnd = 0;
      int[] elementIndexes = StringInfo.ParseCombiningCharacters(value);
      for (int i = 0; i < elementIndexes.Length; i++) {
        int elementEnd = i + 1 < elementIndexes.Length ? elementIndexes[i + 1] : value.Length;
        if (elementEnd > maxLength - 1) break;
        safeEnd = elementEnd;
      }
      return value.Substring(0, safeEnd) + "…";
    }

    static string FormatTrayIconText(string activePresetLabel, string presetName,
        IList<string> monitorLines, int maxLength) {
      string monitor = string.Join("\n", monitorLines ?? new List<string>());
      if (monitor.Length > maxLength)
        return EllipsizeUnicode(monitor, maxLength);

      string presetLabel = activePresetLabel ?? "";
      string name = presetName ?? "";
      int firstLineMaxLength = maxLength - monitor.Length - 1;

      // 至少需要容纳标签和省略号；否则整行预设信息都省略。
      if (firstLineMaxLength < presetLabel.Length + 1)
        return monitor;

      string fullPresetLine = presetLabel + name;
      if (fullPresetLine.Length <= firstLineMaxLength)
        return fullPresetLine + "\n" + monitor;

      int availableNameLength = firstLineMaxLength - presetLabel.Length;
      string shortenedName = EllipsizeUnicode(name, availableNameLength);
      return presetLabel + shortenedName + "\n" + monitor;
    }

    static void UpdateTrayIconText() {
      if (trayIcon == null) return;

      string presetName = GetCurrentPresetDisplayName();
      string text = FormatTrayIconText(
        Strings.ActivePreset, presetName, BuildMonitorLines(true), NotifyIconTextLimit);

      // 最终防线：即使未来格式化逻辑发生变化，也不允许 NotifyIcon.Text 超限。
      text = EllipsizeUnicode(text, NotifyIconTextLimit);
      trayIcon.Text = text;
    }

    /// <summary>
    /// 进程退出生命周期收敛。
    /// 停止生产者后异步等待在途访问，关闭监控库，再释放窗口与托盘资源。
    /// 任一步骤失败均不阻断后续步骤；Application.Exit() 置于 finally 保证必然执行。
    /// </summary>
    static async void Exit() {
      if (_isExiting) return;   // 幂等：重复触发（如托盘双击 + 菜单项）不重复收敛
      _isExiting = true;

      try {
        // ① 停止后台生产者：管道监听令牌与 OMEN 键 WMI 事件订阅
        try { _pipeCts?.Cancel(); } catch { }

        // ② 收敛全部定时器（先 Stop 再 Dispose，避免释放后仍有回调在途）
        try { tooltipUpdateTimer?.Stop(); } catch { }
        try { tooltipUpdateTimer?.Dispose(); } catch { }

        try { fanControlTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { fanControlTimer?.Dispose(); } catch { }

        // ③ 注销静态事件：SystemEvents 由系统广播静态维持，未注销会长期附着托管引用链
        try { SystemEvents.PowerModeChanged -= OnPowerChange; SystemEvents.DisplaySettingsChanged -= OnDisplayChange; } catch { }
        uiPublisher?.Stop();
        libreComputer.StopGpuRefresh();
        deviceChangeWindow?.Dispose();
        Task extra;
        lock (infoTaskGate) extra = infoTask;
        try {
          await Task.WhenAll(StopHardwareAsync(), fileWriter.StopAsync(), extra,
            monitorLifetime.CloseAsync(() => libreComputer.Close()));
        } catch (Exception ex) { Logger.Error(ex.ToString(), "shutdown"); }
        if (OmenKeyActions.UsesPipe(omenKey)) await Task.Run(() => OmenKeyOff());

        // ④ 关闭窗口实例（悬浮窗 + 帮助页）
        try {
          if (floatingForm != null && !floatingForm.IsDisposed) floatingForm.Close();
          floatingForm?.Dispose();
        } catch { }
        floatingForm = null;

        try {
          foreach (Form form in Application.OpenForms.OfType<HelpForm>().ToArray()) {
            if (!form.IsDisposed) form.Close();
          }
        } catch { }

        // ⑤ 托盘图标：未显式 Dispose 会在通知区域残留“幽灵图标”
        try {
          if (trayIcon != null) {
            trayIcon.Visible = false;
            trayIcon.Dispose();
          }
        } catch { }
        trayIcon = null;

        // ⑥ 释放取消令牌源（内部包装系统等待句柄）
        try { _pipeCts?.Dispose(); } catch { }
        _pipeCts = null;

      } finally {
        Application.Exit();
      }
    }

    static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) {
      Logger.Error($"CurrentDomain_UnhandledException: {e.ExceptionObject}");
      MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.CrashMessage);
    }

    static void Application_ThreadException(object sender, ThreadExceptionEventArgs e) {
      Logger.Error($"Application_ThreadException: {e.Exception}");
      MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.CrashMessage);
    }
  }
}
