using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hp.Bridge.Client.SDKs.PerformanceControl.DataStructure;
using HP.Omen.Core.Common.NVidiaApi;
using HP.Omen.Core.Model.Device.Models;
using Microsoft.Win32;
using static HP.Omen.Core.Model.Device.Models.GraphicsSwitcherHelper;
using static OmenSuperHub.GpuAppManager;
using static OmenSuperHub.OmenHardware;

namespace OmenSuperHub {
  static partial class Program {
    static ToolStripMenuItem languageMenu;

    static void InitTrayIcon() {
      trayIcon = new NotifyIcon() {
        Icon = DefaultTrayIcon,
        ContextMenuStrip = new ContextMenuStrip(),
        Visible = true
      };
      trayIcon.MouseClick += TrayIcon_MouseClick;

      try {
        // 读取图标配置
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\OmenSuperHub")) {
          if (key != null) {
            customIcon = (string)key.GetValue("CustomIcon", "original");
            // 检查是否错误配置为自定义图标
            if (customIcon == "custom" && !CheckCustomIcon()) {
              customIcon = "original";
              SaveConfig("CustomIcon");
              trayIcon.Icon = DefaultTrayIcon;
              UpdateCheckedState("CustomIcon", Strings.IconOriginal);
            }
          }
        }
      } catch (Exception ex) {
        Logger.Error($"Error restoring configuration: {ex.Message}");
      }

      switch (customIcon) {
        case "original": trayIcon.Icon = DefaultTrayIcon; break;
        case "custom": SetCustomIcon(); break;
        case "dynamic": UpdateDynamicIcon(); break;
      }

      BuildTrayMenu(trayIcon.ContextMenuStrip);

      // Initialize tooltip update timer
      tooltipUpdateTimer = new System.Timers.Timer(1000); // Set interval to 1 second (low, default)
      tooltipUpdateTimer.Elapsed += (s, e) => UpdateTooltip();
      tooltipUpdateTimer.AutoReset = true; // Ensure the timer keeps running
      tooltipUpdateTimer.Start();
    }

    static void BuildTrayMenu(ContextMenuStrip menu) {
      menu.Items.Clear();

      menu.Closing -= TrayMenu_Closing;
      menu.Closing += TrayMenu_Closing;

      ToolStripMenuItem sysInfoMenu = new ToolStripMenuItem(Strings.SysInfo);
      sysInfoMenu.DropDownItems.Add(new ToolStripMenuItem($"{Strings.SysModelName}: {deviceDisplayName}") { Enabled = false });
      sysInfoMenu.DropDownItems.Add(new ToolStripMenuItem($"{Strings.SysModelValidation}: {Validation(deviceDisplayName)}") { Enabled = false });
      sysInfoMenu.DropDownItems.Add(new ToolStripMenuItem($"{Strings.SysBoardProduct}: {systemSSID}") { Enabled = false });
      // BIOS 版本
      sysInfoMenu.DropDownItems.Add(new ToolStripMenuItem($"{Strings.SysBiosVersion}: {biosVersion}") { Enabled = false });

      ToolStripMenuItem pawnIOStateMenu = null;
      pawnIOStateMenu = new ToolStripMenuItem($"{Strings.SysPawnIOState}: ") { Enabled = false };
      sysInfoMenu.DropDownItems.Add(pawnIOStateMenu);

      // CPU 完整型号
      string cpuModel = GetCpuModel();
      sysInfoMenu.DropDownItems.Add(new ToolStripMenuItem($"{Strings.SysCpu}: {cpuModel}") { Enabled = false });
      if (maxCPUTemp.HasValue) {
        sysInfoMenu.DropDownItems.Add(new ToolStripMenuItem($"{Strings.SysCpuTjMax}: {maxCPUTemp.Value}°C") { Enabled = false });
      }

      ToolStripMenuItem gpuModelMenu = null, gpuPowerLimitsMenu = null;
      if (hasNVIDIAGpu) {
        gpuModelMenu = new ToolStripMenuItem("GPU: ") { Enabled = false };
        sysInfoMenu.DropDownItems.Add(gpuModelMenu);
        if (maxGPUTemp.HasValue) {
          sysInfoMenu.DropDownItems.Add(new ToolStripMenuItem($"{Strings.SysNvidiaTjMax}: {maxGPUTemp.Value}°C") { Enabled = false });
        }
        gpuPowerLimitsMenu = new ToolStripMenuItem($"{Strings.SysNvidiaPower}: --W / --W") { Enabled = false };
        sysInfoMenu.DropDownItems.Add(gpuPowerLimitsMenu);
        System.Threading.Tasks.Task.Run(() => {
          string gpuModel = GetGpuModelFromNvidiaSmi();
          var limits = GetGpuPowerLimits();
          string limitsText = limits[0] == -2f ? "--W / --W" : $"{limits[0]:F0}W / {limits[1]:F0}W";
          Thread.Sleep(2000);
          uiContext.Post(_ => {
            gpuPowerLimitsMenu.Text = $"{Strings.SysNvidiaPower}: {limitsText}";
            gpuModelMenu.Text = "GPU: " + gpuModel;
          }, null);
        });
      }
      irSensorMenu = new ToolStripMenuItem($"{Strings.SysIRSensor}: --°C") { Enabled = false };
      ambientSensorMenu = new ToolStripMenuItem($"{Strings.SysAmbient}: --°C") { Enabled = false };
      pchSensorMenu = new ToolStripMenuItem($"{Strings.SysPCH}: --°C") { Enabled = false };
      vrSensorMenu = new ToolStripMenuItem($"{Strings.SysVR}: --°C") { Enabled = false };
      sysInfoMenu.DropDownItems.Add(irSensorMenu);
      sysInfoMenu.DropDownItems.Add(ambientSensorMenu);
      sysInfoMenu.DropDownItems.Add(pchSensorMenu);
      sysInfoMenu.DropDownItems.Add(vrSensorMenu);
      ToolStripMenuItem adapterPowerMenu = null;
      adapterPowerMenu = new ToolStripMenuItem($"{Strings.SysAdapterPower}: ") { Enabled = false };
      sysInfoMenu.DropDownItems.Add(adapterPowerMenu);

      System.Threading.Tasks.Task.Run(() => {
        // PawnIO信息
        if (!IsPawnIOInstalled())
          pawnIOState = Strings.SysPawnIONotInstalled;
        else
          pawnIOState = GetPawnIOState();
        int adapterPower = GetAdapterPower();
        Thread.Sleep(2000);
        uiContext.Post(_ => {
          pawnIOStateMenu.Text = $"{Strings.SysPawnIOState}: {pawnIOState}";
          adapterPowerMenu.Text = $"{Strings.SysAdapterPower}: {adapterPower}W";
        }, null);
      });

      // 订阅 DropDownOpening 和 DropDownClosed 事件来控制是否更新信息
      sysInfoMenu.DropDownOpening += (s, e) => {
        if (hasNVIDIAGpu) {
          System.Threading.Tasks.Task.Run(() => {
            var limits = GetGpuPowerLimits();
            string limitsText = limits[0] == -2f ? "--W / --W" : $"{limits[0]:F0}W / {limits[1]:F0}W";
            // 更新 UI（必须在 UI 线程）
            menu.BeginInvoke(new Action(() => {
              gpuPowerLimitsMenu.Text = $"{Strings.SysNvidiaPower}: {limitsText}";
            }));
          });
        }

        System.Threading.Tasks.Task.Run(() => {
          int irTemp = GetSensorTemperature(0);
          int ambientTemp = GetSensorTemperature(1);
          int pchTemp = GetSensorTemperature(2);
          int vrTemp = GetSensorTemperature(3);
          // 更新 UI（必须在 UI 线程）
          menu.BeginInvoke(new Action(() => {
            if (irSensorMenu != null) irSensorMenu.Text = $"{Strings.SysIRSensor}: {FormatSensorTemperature(irTemp)}";
            if (ambientSensorMenu != null) ambientSensorMenu.Text = $"{Strings.SysAmbient}: {FormatSensorTemperature(ambientTemp)}";
            if (pchSensorMenu != null) pchSensorMenu.Text = $"{Strings.SysPCH}: {FormatSensorTemperature(pchTemp)}";
            if (vrSensorMenu != null) vrSensorMenu.Text = $"{Strings.SysVR}: {FormatSensorTemperature(vrTemp)}";
          }));
        });

        isSysInfoMenuOpen = true;
      };
      sysInfoMenu.DropDownClosed += (s, e) => { isSysInfoMenuOpen = false; };

      menu.Items.Add(sysInfoMenu);
      menu.Items.Add(new ToolStripSeparator());

      // ─────────────────────────────────────────────────────────────────────────
      // 预设配置
      // ─────────────────────────────────────────────────────────────────────────
      ToolStripMenuItem presetsMenu = new ToolStripMenuItem(Strings.PresetsMenu);

      presetsMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.PresetNote) { Enabled = false });
      if (isCPUPowerControlSupported) {
        presetsMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.PresetInternalNote) { Enabled = false });
        var extremeItem = CreateMenuItem(Strings.PresetExtreme, "presetsGroup", (s, e) => applyPresetLogic("PresetExtreme"), currentPreset == "PresetExtreme", Strings.PresetExtremeTooltip);
        extremeItem.Name = "PresetExtreme";
        presetsMenu.DropDownItems.Add(extremeItem);
        var gpuPriorityItem = CreateMenuItem(Strings.PresetGpuPriority, "presetsGroup", (s, e) => applyPresetLogic("PresetGpuPriority"), currentPreset == "PresetGpuPriority", Strings.PresetGpuPriorityTooltip);
        gpuPriorityItem.Name = "PresetGpuPriority";
        presetsMenu.DropDownItems.Add(gpuPriorityItem);
        var lightUseItem = CreateMenuItem(Strings.PresetLightUse, "presetsGroup", (s, e) => applyPresetLogic("PresetLightUse"), currentPreset == "PresetLightUse", Strings.PresetLightUseTooltip);
        lightUseItem.Name = "PresetLightUse";
        presetsMenu.DropDownItems.Add(lightUseItem);
        presetsMenu.DropDownItems.Add(new ToolStripSeparator());
      }

      var custom1Item = CreateMenuItem(presetCustom1Name, "presetsGroup", (s, e) => applyPresetLogic("PresetCustom1"), currentPreset == "PresetCustom1");
      custom1Item.Name = "PresetCustom1";
      var custom2Item = CreateMenuItem(presetCustom2Name, "presetsGroup", (s, e) => applyPresetLogic("PresetCustom2"), currentPreset == "PresetCustom2");
      custom2Item.Name = "PresetCustom2";
      var custom3Item = CreateMenuItem(presetCustom3Name, "presetsGroup", (s, e) => applyPresetLogic("PresetCustom3"), currentPreset == "PresetCustom3");
      custom3Item.Name = "PresetCustom3";
      presetsMenu.DropDownOpening += (s, e) => {
        custom1Item.Text = presetCustom1Name;
        custom2Item.Text = presetCustom2Name;
        custom3Item.Text = presetCustom3Name;
      };

      void attachRename(ToolStripMenuItem item, string presetKey) {
        var renameItem = new ToolStripMenuItem(Strings.RenamePreset);
        renameItem.Click += (s, e) => {
          Form renameForm = new Form {
            Width = 400,
            Height = 200,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = Strings.RenamePresetTitle,
            StartPosition = FormStartPosition.CenterScreen
          };
          Label textLabel = new Label() { Left = 10, Top = 10, Width = 360, Text = Strings.RenamePresetPrompt };
          TextBox inputBox = new TextBox() { Left = 10, Top = 40, Width = 360, Text = item.Text };
          Button confirmation = new Button() { Text = "OK", Left = 120, Width = 140, Height = 45, Top = 80, DialogResult = DialogResult.OK };
          renameForm.Controls.Add(textLabel);
          renameForm.Controls.Add(inputBox);
          renameForm.Controls.Add(confirmation);
          renameForm.AcceptButton = confirmation;

          if (renameForm.ShowDialog() == DialogResult.OK) {
            string result = inputBox.Text;
            if (!string.IsNullOrWhiteSpace(result) && result != presetCustom1Name && result != presetCustom2Name && result != presetCustom3Name) {
              item.Text = result;
              if (presetKey == "PresetCustom1") { presetCustom1Name = result; SaveConfig("PresetCustom1Name"); }
              if (presetKey == "PresetCustom2") { presetCustom2Name = result; SaveConfig("PresetCustom2Name"); }
              if (presetKey == "PresetCustom3") { presetCustom3Name = result; SaveConfig("PresetCustom3Name"); }
              if (currentPreset == presetKey) {
                UpdateTrayIconText();
              }
            } else if (string.IsNullOrWhiteSpace(result)) {
              MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.RenamePresetError, Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
          }
        };
        item.DropDownItems.Add(renameItem);
      }

      attachRename(custom1Item, "PresetCustom1");
      attachRename(custom2Item, "PresetCustom2");
      attachRename(custom3Item, "PresetCustom3");

      presetsMenu.DropDownItems.Add(custom1Item);
      presetsMenu.DropDownItems.Add(custom2Item);
      presetsMenu.DropDownItems.Add(custom3Item);

      menu.Items.Add(presetsMenu);

      menu.Items.Add(new ToolStripSeparator());
      bool isBuiltInPreset = (currentPreset == "PresetExtreme" || currentPreset == "PresetGpuPriority" || currentPreset == "PresetLightUse");

      ToolStripMenuItem fanConfigMenu = new ToolStripMenuItem(Strings.FanConfig);
      fanConfigMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.FanCurveNote) { Enabled = false });
      var silentFanItem = new ToolStripMenuItem(Strings.FanSilentMode) {
        Tag = "fanTableGroup",
        Checked = fanTable.Contains("silent"),
        ToolTipText = Strings.FanSilentTooltip
      };
      silentFanItem.MouseUp += (s, e) => {
        if (e.Button == MouseButtons.Left) {
          fanTable = "silent";
          LoadFanConfig("silent.txt");
          SaveConfig("FanTable");
          UpdateCheckedState("fanTableGroup", null, silentFanItem);
        } else if (e.Button == MouseButtons.Right) {
          ShowFanCurveEditor("silent.txt");
        }
      };
      fanConfigMenu.DropDownItems.Add(silentFanItem);
      var coolFanItem = new ToolStripMenuItem(Strings.FanCoolMode) {
        Tag = "fanTableGroup",
        Checked = fanTable.Contains("cool"),
        ToolTipText = Strings.FanCoolTooltip
      };
      coolFanItem.MouseUp += (s, e) => {
        if (e.Button == MouseButtons.Left) {
          fanTable = "cool";
          LoadFanConfig("cool.txt");
          SaveConfig("FanTable");
          UpdateCheckedState("fanTableGroup", null, coolFanItem);
        } else if (e.Button == MouseButtons.Right) {
          ShowFanCurveEditor("cool.txt");
        }
      };
      fanConfigMenu.DropDownItems.Add(coolFanItem);
      var customFanItem = new ToolStripMenuItem(Strings.FanCustomMode) {
        Tag = "fanTableGroup",
        Checked = fanTable.Contains("custom"),
        ToolTipText = Strings.FanCustomTooltip
      };
      customFanItem.MouseUp += (s, e) => {
        if (e.Button == MouseButtons.Left) {
          if (ApplyCustomFanConfig())
            UpdateCheckedState("fanTableGroup", null, customFanItem);
        } else if (e.Button == MouseButtons.Right) {
          ShowFanCurveEditor("custom.txt");
        }
      };
      fanConfigMenu.DropDownItems.Add(customFanItem);
      fanConfigMenu.DropDownItems.Add(new ToolStripSeparator());
      ToolStripMenuItem respondSpeedMenu = new ToolStripMenuItem(Strings.FanResponseSpeed);
      respondSpeedMenu.DropDownItems.Add(CreateMenuItem(Strings.FanRespRealtime, "tempSensitivityGroup", (s, e) => {
        tempSensitivity = "realtime";
        respondSpeed = 1;
        SaveConfig("TempSensitivity");
      }, false));
      respondSpeedMenu.DropDownItems.Add(CreateMenuItem(Strings.FanRespHigh, "tempSensitivityGroup", (s, e) => {
        tempSensitivity = "high";
        respondSpeed = 0.4f;
        SaveConfig("TempSensitivity");
      }, true));
      respondSpeedMenu.DropDownItems.Add(CreateMenuItem(Strings.FanRespMedium, "tempSensitivityGroup", (s, e) => {
        tempSensitivity = "medium";
        respondSpeed = 0.1f;
        SaveConfig("TempSensitivity");
      }, false));
      respondSpeedMenu.DropDownItems.Add(CreateMenuItem(Strings.FanRespLow, "tempSensitivityGroup", (s, e) => {
        tempSensitivity = "low";
        respondSpeed = 0.04f;
        SaveConfig("TempSensitivity");
      }, false));
      fanConfigMenu.DropDownItems.Add(respondSpeedMenu);

      // 高温自动保护开关
      ToolStripMenuItem autoFanProtectMenu = new ToolStripMenuItem(Strings.FanAutoProtect);
      autoFanProtectMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.FanAutoProtectNote) { Enabled = false });
      autoFanProtectMenu.DropDownItems.Add(CreateMenuItem(Strings.FanAutoProtectOn, "autoFanProtectGroup", (s, e) => {
        autoFanProtect = "on";
        SaveConfig("AutoFanProtect");
      }, autoFanProtect == "on"));
      autoFanProtectMenu.DropDownItems.Add(CreateMenuItem(Strings.FanAutoProtectOff, "autoFanProtectGroup", (s, e) => {
        autoFanProtect = "off";
        SaveConfig("AutoFanProtect");
      }, autoFanProtect == "off"));
      fanConfigMenu.DropDownItems.Add(autoFanProtectMenu);

      menu.Items.Add(fanConfigMenu);

      ToolStripMenuItem fanControlMenu = new ToolStripMenuItem(Strings.FanControl);
      if (isFanCleanSupported || isFanLegacyCleanSupported) {
        string menuText = Strings.CleanCreekMenuItem;
        if (!isFanCleanSupported && isFanLegacyCleanSupported)
          menuText = Strings.CleanCreekLegacyMenuItem;
        fanControlMenu.DropDownItems.Add(CreateMenuItem(menuText, null, (s, e) => {
          if (MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.CleanCreekConfirmMessage, Strings.CleanCreekTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK) {
            fanControlMenu.Enabled = false;
            if (isFanCleanSupported) {
              SetMaxFanSpeedOff();
              fanControlTimer.Change(Timeout.Infinite, Timeout.Infinite);
              // 准备开始清洁
              Action start = () => {
                SetFanLevel(platformSettings.CleanCreekCpuFanSpeed, platformSettings.CleanCreekGpuFanSpeed, Is3FanNb, true);
              };
              Action stop = () => {
                fanControlMenu.Enabled = true;
                RestoreFanControl();  // 恢复原始转速或自动控制
              };
              // 显示进度窗体，持续时间从配置读取（单位毫秒）
              StartCleanCreekWithProgress(platformSettings.CleanCreekDuration, Strings.CleanCreekTitle, start, stop);
            } else if (isFanLegacyCleanSupported) {
              SetMaxFanSpeedOff();
              fanControlTimer.Change(Timeout.Infinite, Timeout.Infinite);
              Action start = () => SetLegacyCleanCreek(true);
              Action stop = () => {
                fanControlMenu.Enabled = true;
                SetLegacyCleanCreek(false);
                RestoreFanControl();
              };
              StartCleanCreekWithProgress(platformSettings.CleanCreekDuration, Strings.CleanCreekTitle, start, stop);
            }
          }
        }, false));
        fanControlMenu.DropDownItems.Add(new ToolStripSeparator());
      }
      fanControlMenu.DropDownItems.Add(CreateMenuItem(Strings.FanAuto, "fanControlGroup", (s, e) => {
        fanControl = "auto";
        SetMaxFanSpeedOff();
        fanControlTimer.Change(0, 1000);
        SaveConfig("FanControl");
      }, true));
      fanControlMenu.DropDownItems.Add(CreateMenuItem(Strings.FanMax, "fanControlGroup", (s, e) => {
        fanControl = "max";
        SetMaxFanSpeedOn();
        fanControlTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SaveConfig("FanControl");
      }, false));
      fanControlMenu.DropDownItems.Add(CreateMenuItem(Strings.SetFanSpeedSlider, "fanControlGroup", (s, e) => { }, false));
      fanTrackBar = new ToolStripTrackBar();
      fanTrackBar.Minimum = 0;
      fanTrackBar.Maximum = platformMaxFanSpeed > 0 ? (int)(platformMaxFanSpeed * 1.1 / 100) : 64;
      fanTrackBar.Value = fanTrackBar.Maximum / 2;
      fanTrackBar.TickFrequency = fanTrackBar.Maximum - fanTrackBar.Minimum;
      fanTrackBar.Width = 800;

      fanValueLabel = new ToolStripMenuItem(string.Format(Strings.CurrentSliderValueTemp, $"{fanTrackBar.Value * 100} RPM")) { Enabled = false };

      fanTrackBar.ValueChanged += (sender, e) => {
        fanControl = fanTrackBar.Value * 100 + " RPM";
        fanValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, $"{fanTrackBar.Value * 100} RPM");
        fanControlTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SetFanLevel((byte)fanTrackBar.Value, (byte)fanTrackBar.Value, Is3FanNb);
        SaveConfig("FanControl");
        UpdateCheckedState("fanControlGroup", Strings.SetFanSpeedSlider);
      };

      fanTrackBar.MouseDown += (sender, e) => {
        SetMaxFanSpeedOff();
        fanControlTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SetFanLevel((byte)fanTrackBar.Value, (byte)fanTrackBar.Value, Is3FanNb);
        UpdateCheckedState("fanControlGroup", Strings.SetFanSpeedSlider);
      };

      fanTrackBar.MouseUp += (sender, e) => {
        SaveConfig("FanControl");
      };

      fanControlMenu.DropDownItems.Add(fanTrackBar);
      fanControlMenu.DropDownItems.Add(fanValueLabel);
      menu.Items.Add(fanControlMenu);

      var performanceControlMenu = new ToolStripMenuItem(Strings.PerfControl);
      // 图形模式
      if (supportHotSwitch) {
        if (NvGraphicsMode == GraphicsSwitcherMode.Optimus || NvGraphicsMode == GraphicsSwitcherMode.Hybrid) {
          var hotSwitchItem = CreateMenuItem(Strings.HotSwitch, null, (s, e) => {
            if (NvApiWrapper.NVAPI_SYS_UIControl(true) != 0)
              MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.DdsInitFail, Strings.Warning, MessageBoxButtons.OK, MessageBoxIcon.Warning);
          }, false);
          hotSwitchItem.CheckOnClick = false;
          performanceControlMenu.DropDownItems.Add(hotSwitchItem);
        }
      }
      ToolStripMenuItem graphicsModeControlMenu = null;
      if (hasNVIDIAGpu) {
        byte supportedGfxModes = GetSupportedGfxModes();

        if (supportedGfxModes != 0) {
          graphicsModeControlMenu = new ToolStripMenuItem(Strings.GraphicsMode);
          graphicsModeControlMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.GfxOnlyInternal) { Enabled = false });
          graphicsModeControlMenu.DropDownItems.Add(new ToolStripSeparator());
          bool supportsUMA = (supportedGfxModes & 0x01) != 0;
          bool supportsHybrid = (supportedGfxModes & 0x02) != 0;
          bool supportsDiscrete = (supportedGfxModes & 0x04) != 0;
          bool supportsDDS = (supportedGfxModes & 0x08) != 0;
          if (supportsDDS || NvGraphicsMode == GraphicsSwitcherMode.Optimus) {
            graphicsModeControlMenu.DropDownItems.Add(CreateMenuItem("NVIDIA Advanced Optimus", "graphicsModeGroup", (s, e) => {
              if (SetGfxMode(GraphicsSwitcherMode.Optimus))
                MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GfxSwitchedTo("NVIDIA Advanced Optimus"), Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Information);
              else {
                SetGfxMode(NvGraphicsMode);
                MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GfxUnsupported, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Information);
              }
            }, NvGraphicsMode == GraphicsSwitcherMode.Optimus));
          }
          if (supportsDiscrete || NvGraphicsMode == GraphicsSwitcherMode.Discrete) {
            graphicsModeControlMenu.DropDownItems.Add(CreateMenuItem("Discrete", "graphicsModeGroup", (s, e) => {
              if (SetGfxMode(GraphicsSwitcherMode.Discrete))
                MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GfxSwitchedTo("Discrete"), Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Information);
              else {
                SetGfxMode(NvGraphicsMode);
                MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GfxUnsupported, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Information);
              }
            }, NvGraphicsMode == GraphicsSwitcherMode.Discrete));
          }
          if (supportsUMA || NvGraphicsMode == GraphicsSwitcherMode.UMAMode) {
            graphicsModeControlMenu.DropDownItems.Add(CreateMenuItem("UMA", "graphicsModeGroup", (s, e) => {
              if (MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GfxUMAConfirm, Strings.GfxUMATitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
                if (SetGfxMode(GraphicsSwitcherMode.UMAMode))
                  MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GfxSwitchedTo("UMA"), Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Information);
                else {
                  SetGfxMode(NvGraphicsMode);
                  MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GfxUnsupported, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
              }
            }, NvGraphicsMode == GraphicsSwitcherMode.UMAMode));
          }
          if (supportsHybrid || NvGraphicsMode == GraphicsSwitcherMode.Hybrid) {
            graphicsModeControlMenu.DropDownItems.Add(CreateMenuItem("Hybrid", "graphicsModeGroup", (s, e) => {
              if (SetGfxMode(GraphicsSwitcherMode.Hybrid))
                MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GfxSwitchedTo("Hybrid"), Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Information);
              else {
                SetGfxMode(NvGraphicsMode);
                MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GfxUnsupported, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Information);
              }
            }, NvGraphicsMode == GraphicsSwitcherMode.Hybrid));
          }
        }
      }
      if (graphicsModeControlMenu != null) {
        performanceControlMenu.DropDownItems.Add(graphicsModeControlMenu);
        graphicsModeControlMenu.DropDownOpening += (s, e) => {
          if (hasNVIDIAGpu) {
            var nvMode = GetGfxMode();
            string chk;
            switch (nvMode) {
              case GraphicsSwitcherMode.Discrete: chk = "Discrete"; break;
              case GraphicsSwitcherMode.Optimus: chk = "NVIDIA Advanced Optimus"; break;
              case GraphicsSwitcherMode.UMAMode: chk = "UMA"; break;
              default: chk = "Hybrid"; break;
            }
            UpdateCheckedState("graphicsModeGroup", chk);
          }
        };
      }
      if (hasNVIDIAGpu) {
        ToolStripMenuItem gpuAppsMenu = new ToolStripMenuItem(Strings.GpuAppsMenu);
        gpuAppsMenu.DropDownOpening += (s, e) => {
          gpuAppsMenu.DropDownItems.Clear();
          var apps = GetGpuApps();
          if (apps.Count == 0) {
            gpuAppsMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.GpuAppsNone) { Enabled = false });
          } else {
            foreach (var app in apps) {
              var appItem = new ToolStripMenuItem($"{app.ProcessName} (PID: {app.ProcessId})");
              appItem.Click += (sender, args) => {
                if (MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GpuCloseConfirm(app.ProcessName), Strings.GpuCloseTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
                  try {
                    Process.GetProcessById(app.ProcessId).Kill();
                  } catch (Exception ex) {
                    MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GpuCloseError(ex.Message), Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                  }
                }
              };
              gpuAppsMenu.DropDownItems.Add(appItem);
            }
          }
        };
        performanceControlMenu.DropDownItems.Add(gpuAppsMenu);

        ToolStripMenuItem restartGpuMenu = new ToolStripMenuItem(Strings.GpuRestartMenu);
        restartGpuMenu.ToolTipText = Strings.GpuRestartTooltip;
        restartGpuMenu.Click += (s, e) => {
          if (MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.GpuRestartConfirm, Strings.GpuRestartTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
            Task.Run(() => RestartGpu());
          }
        };
        performanceControlMenu.DropDownItems.Add(restartGpuMenu);
      }
      performanceControlMenu.DropDownItems.Add(new ToolStripSeparator()); // Separator between groups
      //ToolStripMenuItem pl4Menu = new ToolStripMenuItem("PL4");
      //pl4Menu.DropDownItems.Add(CreateMenuItem("不设置", "pl4PowerGroup", (s, e) => {
      //  powerLimit4 = "null";
      //  SaveConfig("PL4Power");
      //}, true));
      //pl4Menu.DropDownItems.Add(CreateMenuItem("最大", "pl4PowerGroup", (s, e) => {
      //  powerLimit4 = "max";
      //  if (isTwoBytePL4) {
      //    SetPL4DoubleByte(500);
      //  } else {
      //    SetCpuPowerLimit4(254);
      //  }
      //  SaveConfig("PL4Power");
      //}, false));
      //int doubleFactor = isTwoBytePL4 ? 2 : 1;
      //for (int power = 40; power <= 240 * doubleFactor; power += 20 * doubleFactor) {
      //  int currentPower = power;
      //  pl4Menu.DropDownItems.Add(CreateMenuItem(currentPower + " W", "pl4PowerGroup", (s, e) => {
      //    powerLimit4 = currentPower + " W";
      //    if (isTwoBytePL4) {
      //      SetPL4DoubleByte((ushort)currentPower);
      //    } else {
      //      SetCpuPowerLimit4((byte)currentPower);
      //    }
      //    SaveConfig("PL4Power");
      //  }, false));
      //}
      //performanceControlMenu.DropDownItems.Add(pl4Menu);
      if (platformSettings != null && platformSettings.UnleashedModeMaxIccMax > 0) {
        ToolStripMenuItem iccMaxMenu = new ToolStripMenuItem(Strings.IccMaxMenu);
        iccMaxMenu.DropDownItems.Add(CreateMenuItem(Strings.NotSet, "iccMaxGroup", (s, e) => {
          iccMax = "null";
          SaveConfig("IccMax");
        }, true));
        for (int ampere = 150; ampere <= 350; ampere += 20) {
          int currentAmpere = ampere;
          iccMaxMenu.DropDownItems.Add(CreateMenuItem(currentAmpere + " A", "iccMaxGroup", (s, e) => {
            iccMax = currentAmpere + " A";
            SetIccMaxByWmi((decimal)currentAmpere);
            SaveConfig("IccMax");
          }, false));
        }
        performanceControlMenu.DropDownItems.Add(iccMaxMenu);
      }
      if (IsLoadLineSupported()) {
        ToolStripMenuItem acLoadLineMenu = new ToolStripMenuItem(Strings.AcLoadLineMenu);
        acLoadLineMenu.DropDownItems.Add(CreateMenuItem(Strings.NotSet, "acLoadLineGroup", (s, e) => {
          acLoadline = "null";
          SaveConfig("AcLoadLine");
        }, true));
        int maxSupportedLevel = GetLoadLineSupportLevels();
        for (int level = 1; level <= maxSupportedLevel; level++) {
          int currentLevel = level;
          string displayText = (180 - 10 * currentLevel).ToString();
          acLoadLineMenu.DropDownItems.Add(CreateMenuItem(displayText, "acLoadLineGroup", (s, e) => {
            acLoadline = currentLevel.ToString();
            SetLoadLine(currentLevel);
            SaveConfig("AcLoadLine");
          }, false));
        }
        performanceControlMenu.DropDownItems.Add(acLoadLineMenu);
      }

      if (isCPUPowerControlSupported) {
        ToolStripMenuItem cpuPowerMenu = new ToolStripMenuItem(Strings.CpuPowerMenu);

        cpuPowerMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.PerfCpuPowerTip) { Enabled = false });
        cpuPowerMenu.DropDownItems.Add(new ToolStripSeparator());
        cpuPowerMenu.DropDownItems.Add(CreateMenuItem(Strings.NotSet, "cpuPowerGroup", (s, e) => {
          cpuPower = "null";
          SaveConfig("CpuPower");
        }, true));
        // 添加提示（只读）
        ToolStripMenuItem cpuPowerSliderItem = CreateMenuItem(Strings.SetCpuPowerSlider, "cpuPowerGroup", (s, e) => { }, false);
        cpuPowerMenu.DropDownItems.Add(cpuPowerSliderItem);

        // 创建滑块项
        cpuPowerTrackBar = new ToolStripTrackBar();
        cpuPowerTrackBar.Minimum = 10;
        cpuPowerTrackBar.Maximum = 254;
        if (platformSettings != null) {
          cpuPowerTrackBar.Value = platformSettings.NbPL1UpperBoundPerformance > 0 ? platformSettings.NbPL1UpperBoundPerformance : 100;
        } else {
          cpuPowerTrackBar.Value = 100;
        }
        cpuPowerTrackBar.TickFrequency = cpuPowerTrackBar.Maximum - cpuPowerTrackBar.Minimum;     // 设置刻度间隔
        cpuPowerTrackBar.Width = 800;           // 设置宿主宽度，内部控件会自动填充

        // 显示当前值的只读标签
        cpuPowerValueLabel = new ToolStripMenuItem(string.Format(Strings.CurrentSliderValueTemp, $"{cpuPowerTrackBar.Value} W")) { Enabled = false };

        // 滑块值改变时更新标签并应用设置
        cpuPowerTrackBar.ValueChanged += (sender, e) => {
          int val = cpuPowerTrackBar.Value;
          cpuPowerValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, $"{val} W");
          cpuPower = cpuPowerTrackBar.Value + " W";
          if (isCPUPowerControlSupported)
            SetCpuPowerLimit((byte)cpuPowerTrackBar.Value);
          SaveConfig("CpuPower");
          UpdateCheckedState("cpuPowerGroup", Strings.SetCpuPowerSlider);
        };

        // 鼠标松开
        cpuPowerTrackBar.MouseUp += (sender, e) => {
          cpuPower = cpuPowerTrackBar.Value + " W";
          if (isCPUPowerControlSupported)
            SetCpuPowerLimit((byte)cpuPowerTrackBar.Value);
          SaveConfig("CpuPower");
          UpdateCheckedState("cpuPowerGroup", Strings.SetCpuPowerSlider);
        };

        cpuPowerMenu.DropDownItems.Add(cpuPowerTrackBar);
        cpuPowerMenu.DropDownItems.Add(cpuPowerValueLabel);
        performanceControlMenu.DropDownItems.Add(cpuPowerMenu);
      }

      ToolStripMenuItem gpuPowerMenu = new ToolStripMenuItem(Strings.GpuPowerControlMenu);

      ToolStripMenuItem tgpMenu = new ToolStripMenuItem("Tgp");
      tgpMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.PerfTgpTip) { Enabled = false });
      tgpMenu.DropDownItems.Add(new ToolStripSeparator());
      tgpMenu.DropDownItems.Add(CreateMenuItem(Strings.Enable, "tgpPowerGroup", (s, e) => {
        tgpPower = "on";
        SetGpuPowerState(true, ppabPower == "on", dState == "normal" ? 1 : 2);
        SaveConfig("TgpPower");
      }, true));
      tgpMenu.DropDownItems.Add(CreateMenuItem(Strings.Disable, "tgpPowerGroup", (s, e) => {
        tgpPower = "off";
        SetGpuPowerState(false, ppabPower == "on", dState == "normal" ? 1 : 2);
        SaveConfig("TgpPower");
      }, false));
      gpuPowerMenu.DropDownItems.Add(tgpMenu);

      ToolStripMenuItem ppabMenu = new ToolStripMenuItem("Ppab");
      ppabMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.PerfPpabTip) { Enabled = false });
      ppabMenu.DropDownItems.Add(new ToolStripSeparator());
      ppabMenu.DropDownItems.Add(CreateMenuItem(Strings.Enable, "ppabPowerGroup", (s, e) => {
        ppabPower = "on";
        SetGpuPowerState(tgpPower == "on", true, dState == "normal" ? 1 : 2);
        SaveConfig("PpabPower");
      }, true));
      ppabMenu.DropDownItems.Add(CreateMenuItem(Strings.Disable, "ppabPowerGroup", (s, e) => {
        ppabPower = "off";
        SetGpuPowerState(tgpPower == "on", false, dState == "normal" ? 1 : 2);
        SaveConfig("PpabPower");
      }, false));
      gpuPowerMenu.DropDownItems.Add(ppabMenu);

      if (platformSettings != null && platformSettings.TppSupport) {
        ToolStripMenuItem tppMenu = new ToolStripMenuItem(Strings.PpabPowerMenu);
        tppMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.PerfTppTip) { Enabled = false });
        tppMenu.DropDownItems.Add(new ToolStripSeparator());
        tppMenu.DropDownItems.Add(CreateMenuItem(Strings.NotSet, "tppPowerGroup", (s, e) => {
          tppPower = "null";
          SaveConfig("TppPower");
        }, true));
        tppMenu.DropDownItems.Add(CreateMenuItem(Strings.SetTppSlider, "tppPowerGroup", (s, e) => { }, false));
        tppTrackBar = new ToolStripTrackBar();
        tppTrackBar.Minimum = 20;
        tppTrackBar.Maximum = 254;
        tppTrackBar.Value = platformSettings != null ? platformSettings.TppMaxValue : 60;
        tppTrackBar.TickFrequency = tppTrackBar.Maximum - tppTrackBar.Minimum;
        tppTrackBar.Width = 800;

        tppValueLabel = new ToolStripMenuItem(string.Format(Strings.CurrentSliderValueTemp, $"{tppTrackBar.Value} W")) { Enabled = false };

        tppTrackBar.ValueChanged += (sender, e) => {
          tppValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, $"{tppTrackBar.Value} W");
          tppPower = tppTrackBar.Value + " W";
          SetConcurrentTdp((byte)tppTrackBar.Value);
          SaveConfig("TppPower");
          UpdateCheckedState("tppPowerGroup", Strings.SetTppSlider);
        };

        tppTrackBar.MouseUp += (sender, e) => {
          tppPower = tppTrackBar.Value + " W";
          SetConcurrentTdp((byte)tppTrackBar.Value);
          SaveConfig("TppPower");
          UpdateCheckedState("tppPowerGroup", Strings.SetTppSlider);
        };

        tppMenu.DropDownItems.Add(tppTrackBar);
        tppMenu.DropDownItems.Add(tppValueLabel);
        gpuPowerMenu.DropDownItems.Add(tppMenu);
      }

      ToolStripMenuItem dStateMenu = new ToolStripMenuItem(Strings.DStateSubMenu);
      dStateMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.PerfDStateTip) { Enabled = false });
      dStateMenu.DropDownItems.Add(new ToolStripSeparator());
      dStateMenu.DropDownItems.Add(CreateMenuItem(Strings.Standard, "dStateGroup", (s, e) => {
        dState = "normal";
        SetGpuPowerState(tgpPower == "on", ppabPower == "on", 1);
        SaveConfig("DState");
      }, true));
      dStateMenu.DropDownItems.Add(CreateMenuItem(Strings.LowPower, "dStateGroup", (s, e) => {
        dState = "low";
        SetGpuPowerState(tgpPower == "on", ppabPower == "on", 2);
        SaveConfig("DState");
      }, false));
      gpuPowerMenu.DropDownItems.Add(dStateMenu);

      performanceControlMenu.DropDownItems.Add(gpuPowerMenu);
      menu.Items.Add(performanceControlMenu);

      menu.Items.Add(new ToolStripSeparator()); // Separator between groups
      ToolStripMenuItem CreateMonitorMetricItem(string text, string group, Func<bool> getValue, Action<bool> setValue, string configName) {
        var item = new ToolStripMenuItem(text) {
          Tag = group,
          Checked = getValue()
        };
        item.Click += (s, e) => {
          bool value = !getValue();
          setValue(value);
          item.Checked = value;
          SaveConfig(configName);
          RefreshMonitorDisplay();
        };
        return item;
      }

      ToolStripMenuItem hardwareMonitorMenu = new ToolStripMenuItem(Strings.HwMonitor);
      ToolStripMenuItem monitorCPUMenu = new ToolStripMenuItem(Strings.MonitorCpuLabel);
      monitorCPUMenu.DropDownItems.Add(CreateMenuItem(Strings.MonitorCpuOn, "monitorCPUGroup", (s, e) => {
        monitorCPU = true;
        cpuTempReady = false; // 等待获取到温度后再参与风扇控制
        rawPowerCPU = 0f;     // 清除可能残留的脏功率值
        CPUPower = 0f;
        SetCpuMonitorState(true);
        SaveConfig("MonitorCPU");
      }, true));
      monitorCPUMenu.DropDownItems.Add(CreateMenuItem(Strings.MonitorCpuOff, "monitorCPUGroup", (s, e) => {
        // 自动转速模式下禁止彻底关闭监控
        if (!monitorGPU && fanControl == "auto") {
          MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.MonitorAutoFanWarning, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
          UpdateCheckedState("monitorCPUGroup", monitorCPU ? Strings.MonitorCpuOn : Strings.MonitorCpuOff);
          skipCheckedUpdate = true;
          return;
        }
        monitorCPU = false;
        cpuTempReady = false;
        rawPowerCPU = 0f;  // 关闭时清零，避免重新开启时读到旧值
        CPUPower = 0f;
        SetCpuMonitorState(false);
        SaveConfig("MonitorCPU");
        // 手动更新勾选状态（因为提前 return 会跳过 CreateMenuItem 的自动勾选）
      }, false));
      monitorCPUMenu.DropDownItems.Add(new ToolStripSeparator());
      monitorCPUMenu.DropDownItems.Add(CreateMonitorMetricItem(Strings.MonitorCpuTempLabel, "showCPUTempGroup", () => showCPUTemp, value => showCPUTemp = value, "ShowCPUTemp"));
      monitorCPUMenu.DropDownItems.Add(CreateMonitorMetricItem(Strings.MonitorCpuPowerLabel, "showCPUPowerGroup", () => showCPUPower, value => showCPUPower = value, "ShowCPUPower"));
      hardwareMonitorMenu.DropDownItems.Add(monitorCPUMenu);
      if (hasNVIDIAGpu) {
        ToolStripMenuItem monitorGPUMenu = new ToolStripMenuItem(Strings.MonitorGpuLabel);
        monitorGPUMenu.DropDownItems.Add(CreateMenuItem(Strings.MonitorGpuOn, "monitorGPUGroup", (s, e) => {
          monitorGPU = true;
          gpuTempReady = false; // 等待获取到温度后再参与风扇控制
          rawPowerGPU = 0f;     // 清除可能残留的脏功率值
          GPUPower = 0f;
          SetGpuMonitorState(true);
          SaveConfig("MonitorGPU");
        }, true));
        monitorGPUMenu.DropDownItems.Add(CreateMenuItem(Strings.MonitorGpuOff, "monitorGPUGroup", (s, e) => {
          // 自动转速模式下禁止彻底关闭监控
          if (!monitorCPU && fanControl == "auto") {
            MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.MonitorAutoFanWarning, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateCheckedState("monitorGPUGroup", monitorGPU ? Strings.MonitorGpuOn : Strings.MonitorGpuOff);
            skipCheckedUpdate = true;
            return;
          }
          monitorGPU = false;
          gpuTempReady = false;
          rawPowerGPU = 0f;  // 关闭时清零，避免重新开启时读到旧值
          GPUPower = 0f;
          SetGpuMonitorState(false);
          SaveConfig("MonitorGPU");
        }, false));
        monitorGPUMenu.DropDownItems.Add(new ToolStripSeparator());
        monitorGPUMenu.DropDownItems.Add(CreateMonitorMetricItem(Strings.MonitorGpuTempLabel, "showGPUTempGroup", () => showGPUTemp, value => showGPUTemp = value, "ShowGPUTemp"));
        monitorGPUMenu.DropDownItems.Add(CreateMonitorMetricItem(Strings.MonitorGpuPowerLabel, "showGPUPowerGroup", () => showGPUPower, value => showGPUPower = value, "ShowGPUPower"));
        hardwareMonitorMenu.DropDownItems.Add(monitorGPUMenu);
      }
      ToolStripMenuItem monitorFanMenu = new ToolStripMenuItem(Strings.MonitorFanLabel);
      monitorFanMenu.DropDownItems.Add(CreateMenuItem(Strings.MonitorFanOn, "monitorFanGroup", (s, e) => {
        monitorFan = true;
        SaveConfig("MonitorFan");
      }, false));
      monitorFanMenu.DropDownItems.Add(CreateMenuItem(Strings.MonitorFanOff, "monitorFanGroup", (s, e) => {
        monitorFan = false;
        SaveConfig("MonitorFan");
      }, true));
      hardwareMonitorMenu.DropDownItems.Add(monitorFanMenu);
      ToolStripMenuItem monitorRefreshMenu = new ToolStripMenuItem(Strings.MonitorRefresh);
      monitorRefreshMenu.DropDownItems.Add(CreateMenuItem(Strings.MonitorRefreshHigh, "monitorRefreshGroup", (s, e) => {
        monitorRefreshRate = "high";
        tooltipUpdateTimer.Interval = 250;
        SaveConfig("MonitorRefreshRate");
      }, false));
      monitorRefreshMenu.DropDownItems.Add(CreateMenuItem(Strings.MonitorRefreshLow, "monitorRefreshGroup", (s, e) => {
        monitorRefreshRate = "low";
        tooltipUpdateTimer.Interval = 1000;
        SaveConfig("MonitorRefreshRate");
      }, true));
      hardwareMonitorMenu.DropDownItems.Add(monitorRefreshMenu);
      ToolStripMenuItem tempDisplayMenu = new ToolStripMenuItem(Strings.TempDisplay);
      tempDisplayMenu.DropDownItems.Add(CreateMenuItem(Strings.TempSmoothed, "tempDisplayGroup", (s, e) => {
        tempDisplayMode = "smoothed";
        SaveConfig("TempDisplayMode");
      }, true));
      tempDisplayMenu.DropDownItems.Add(CreateMenuItem(Strings.TempRaw, "tempDisplayGroup", (s, e) => {
        tempDisplayMode = "raw";
        SaveConfig("TempDisplayMode");
      }, false));
      hardwareMonitorMenu.DropDownItems.Add(tempDisplayMenu);
      menu.Items.Add(hardwareMonitorMenu);
      ToolStripMenuItem floatingBarMenu = new ToolStripMenuItem(Strings.FloatingBar);
      floatingBarMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.FloatingToggleTip) { Enabled = false });
      floatingBarMenu.DropDownItems.Add(CreateMenuItem(Strings.FloatingHide, "floatingBarGroup", (s, e) => {
        floatingBar = "off";
        CloseFloatingForm();
        SaveConfig("FloatingBar");
      }, true));
      floatingBarMenu.DropDownItems.Add(CreateMenuItem(Strings.FloatingShow, "floatingBarGroup", (s, e) => {
        floatingBar = "on";
        ShowFloatingForm();
        SaveConfig("FloatingBar");
      }, false));
      floatingBarMenu.DropDownItems.Add(new ToolStripSeparator()); // Separator between groups
      floatingBarMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.SetTextSizeSlider) { Enabled = false });
      textSizeTrackBar = new ToolStripTrackBar();
      textSizeTrackBar.Minimum = 6;
      textSizeTrackBar.Maximum = 18;
      textSizeTrackBar.Value = 10;
      textSizeTrackBar.TickFrequency = textSizeTrackBar.Maximum - textSizeTrackBar.Minimum;
      textSizeTrackBar.Width = 400;

      textSizeLabel = new ToolStripMenuItem(string.Format(Strings.CurrentSliderValueTemp, $"{textSizeTrackBar.Value * 4}")) { Enabled = false };

      textSizeTrackBar.ValueChanged += (sender, e) => {
        int calculatedSize = textSizeTrackBar.Value * 4; // 实际值 24 - 72
        textSizeLabel.Text = string.Format(Strings.CurrentSliderValueTemp, $"{textSizeTrackBar.Value * 4}");
        textSize = calculatedSize;

        // 即时刷新浮窗
        if (floatingForm != null && floatingForm.Visible) {
          floatingForm.SetText(monitorText(), textSize, floatingBarLoc, GetFloatingScreen());
        }
        SaveConfig("FloatingBarSize");
      };

      textSizeTrackBar.MouseUp += (sender, e) => {
        SaveConfig("FloatingBarSize");
      };

      floatingBarMenu.DropDownItems.Add(textSizeTrackBar);
      floatingBarMenu.DropDownItems.Add(textSizeLabel);
      floatingBarMenu.DropDownItems.Add(new ToolStripSeparator()); // Separator between groups
      floatingBarMenu.DropDownItems.Add(CreateMenuItem(Strings.FloatingLocLeft, "floatingBarLocGroup", (s, e) => {
        floatingBarLoc = "left";
        UpdateFloatingText();
        SaveConfig("FloatingBarLoc");
      }, true));
      floatingBarMenu.DropDownItems.Add(CreateMenuItem(Strings.FloatingLocRight, "floatingBarLocGroup", (s, e) => {
        floatingBarLoc = "right";
        UpdateFloatingText();
        SaveConfig("FloatingBarLoc");
      }, false));
      floatingBarMenu.DropDownItems.Add(new ToolStripSeparator());

      // ---- 显示器选择 ----
      ToolStripMenuItem floatingScreenMenu = new ToolStripMenuItem(Strings.FloatingScreen);
      // 每次打开时动态枚举当前所有显示器，并标记当前选中项
      floatingScreenMenu.DropDownOpening += (s, e) => {
        floatingScreenMenu.DropDownItems.Clear();
        var screens = Screen.AllScreens
            .OrderBy(sc => sc.Primary ? 0 : 1)
            .ThenBy(sc => sc.Bounds.Left)
            .ToArray();
        for (int idx = 0; idx < screens.Length; idx++) {
          var sc = screens[idx];
          int displayNum = idx + 1;
          string deviceName = sc.DeviceName;
          string label = $"{Strings.FloatingScreen} {displayNum}";
          if (sc.Primary) label += $"  ({Strings.FloatingScreenPrimary})";
          bool isCurrent = floatingBarScreen == deviceName
                        || (string.IsNullOrEmpty(floatingBarScreen) && sc.Primary);
          var screenItem = new ToolStripMenuItem(label) {
            Tag = "floatingScreenGroup",
            Checked = isCurrent
          };
          screenItem.Click += (sender, args) => {
            floatingBarScreen = deviceName;
            // 同步取消其他项的勾选
            foreach (ToolStripMenuItem mi in floatingScreenMenu.DropDownItems.OfType<ToolStripMenuItem>())
              mi.Checked = (mi == screenItem);
            // 移动浮窗到新显示器（若已显示则关闭重建以确保跨屏幕渲染正确）
            if (floatingBar == "on") {
              CloseFloatingForm();
              ShowFloatingForm();
            }
            SaveConfig("FloatingBarScreen");
          };
          floatingScreenMenu.DropDownItems.Add(screenItem);
        }
      };
      floatingBarMenu.DropDownItems.Add(floatingScreenMenu);
      menu.Items.Add(floatingBarMenu);
      ToolStripMenuItem omenKeyMenu = new ToolStripMenuItem(Strings.OmenKeyMenu);
      omenKeyMenu.DropDownItems.Add(CreateMenuItem(Strings.OmenKeyDefault, "omenKeyGroup", (s, e) => {
        tooltipUpdateTimer.Enabled = false;
        ApplyOmenKeyAction(OmenKeyActions.Default);
      }, omenKey == OmenKeyActions.Default));
      omenKeyMenu.DropDownItems.Add(CreateMenuItem(Strings.OmenKeyToggle, "omenKeyGroup", (s, e) => {
        ApplyOmenKeyAction(OmenKeyActions.Overlay);
      }, omenKey == OmenKeyActions.Overlay));

      bool keepOmenKeyPresetCandidatesMenuOpen = false;

      ToolStripMenuItem omenKeyPresetCandidatesMenu = CreateMenuItem(Strings.OmenKeySwitchPreset, "omenKeyGroup", (s, e) => {
        ApplyOmenKeyAction(OmenKeyActions.Preset);
      }, omenKey == OmenKeyActions.Preset);

      omenKeyPresetCandidatesMenu.DropDownItems.Add(new ToolStripMenuItem());
      ToolStripDropDownClosingEventHandler keepPresetCandidatesMenuOpen = (s, e) => {
        if (keepOmenKeyPresetCandidatesMenuOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) {
          e.Cancel = true;
        }
      };
      menu.Closing += keepPresetCandidatesMenuOpen;
      omenKeyMenu.DropDown.Closing += keepPresetCandidatesMenuOpen;
      omenKeyPresetCandidatesMenu.DropDown.Closing += keepPresetCandidatesMenuOpen;
      omenKeyPresetCandidatesMenu.DropDown.Closed += (s, e) => {
        keepOmenKeyPresetCandidatesMenuOpen = false;
      };
      omenKeyPresetCandidatesMenu.DropDown.MouseLeave += (s, e) => {
        var dropDown = omenKeyPresetCandidatesMenu.DropDown;
        if (!dropDown.ClientRectangle.Contains(dropDown.PointToClient(System.Windows.Forms.Control.MousePosition))) {
          dropDown.Close(ToolStripDropDownCloseReason.CloseCalled);
        }
      };
      omenKeyPresetCandidatesMenu.DropDownOpening += (s, e) => {
        omenKeyPresetCandidatesMenu.DropDownItems.Clear();


        var selectedPresetKeys = GetOmenKeyPresetCandidateKeys();
        foreach (string presetKey in GetAvailablePresetKeys()) {
          string localPresetKey = presetKey;
          var presetItem = new ToolStripMenuItem(GetPresetDisplayName(localPresetKey)) {
            Checked = selectedPresetKeys.Contains(localPresetKey),
            CheckOnClick = false
          };
          presetItem.MouseDown += (sender, args) => {
            if (args.Button == MouseButtons.Left) {
              keepOmenKeyPresetCandidatesMenuOpen = true;
            }
          };
          presetItem.Click += (sender, args) => {
            keepOmenKeyPresetCandidatesMenuOpen = true;
            bool nextState = !presetItem.Checked;
            if (SetOmenKeyPresetCandidate(localPresetKey, nextState)) {
              presetItem.Checked = nextState;
              SaveConfig("OmenKeyPresetCandidates");
            } else {
              presetItem.Checked = true;
            }
            menu.BeginInvoke(new Action(() => keepOmenKeyPresetCandidatesMenuOpen = false));
          };
          omenKeyPresetCandidatesMenu.DropDownItems.Add(presetItem);
        }
      };
      omenKeyMenu.DropDownItems.Add(omenKeyPresetCandidatesMenu);

      ToolStripMenuItem omenKeyLaunchAppMenu = CreateMenuItem(Strings.OmenKeyLaunchApp, "omenKeyGroup", (s, e) => {
        if (!IsOmenKeyAppTargetAvailable())
          SelectOmenKeyApp();
        else
          ApplyOmenKeyAction(OmenKeyActions.App);
      }, omenKey == OmenKeyActions.App);

      var currentAppItem = new ToolStripMenuItem($"{Strings.OmenKeyCurrentApp}: {GetOmenKeyAppDisplayName()}") { Enabled = false };

      // Refresh display items every time the submenu opens.
      // This covers: (a) initial startup where BuildTrayMenu runs before RestoreConfig loads saved values,
      // and (b) any stale state after actions that don't trigger a full menu rebuild.
      omenKeyLaunchAppMenu.DropDownOpening += (s, e) => {
        currentAppItem.Text = $"{Strings.OmenKeyCurrentApp}: {GetOmenKeyAppDisplayName()}";
      };

      omenKeyLaunchAppMenu.DropDownItems.Add(currentAppItem);
      omenKeyLaunchAppMenu.DropDownItems.Add(CreateMenuItem(Strings.OmenKeySelectDesktopApp, null, (s, e) => {
        // Modeless editor: bring the existing window to the front when it is already open.
        SelectOmenKeyApp();
      }, false));
      omenKeyLaunchAppMenu.DropDownItems.Add(CreateMenuItem(Strings.OmenKeySelectUwpApp, null, (s, e) => {
        SelectOmenKeyUwpApp();
      }, false));
      omenKeyLaunchAppMenu.DropDownItems.Add(CreateMenuItem(Strings.OmenKeyClearApp, null, (s, e) => {
        omenKeyAppPath = "";
        omenKeyAppName = "";
        SaveConfig("OmenKeyAppPath");
        SaveConfig("OmenKeyAppName");
        if (omenKey == OmenKeyActions.App) {
          ApplyOmenKeyAction(OmenKeyActions.None);
          UpdateCheckedState("omenKeyGroup", Strings.OmenKeyNone);
        }
        currentAppItem.Text = $"{Strings.OmenKeyCurrentApp}: {GetOmenKeyAppDisplayName()}";
      }, false));
      omenKeyMenu.DropDownItems.Add(omenKeyLaunchAppMenu);

      ToolStripMenuItem omenKeyShortcutMenu = CreateMenuItem(Strings.OmenKeyShortcut, "omenKeyGroup", (s, e) => {
        if (string.IsNullOrWhiteSpace(omenKeyShortcut))
          SelectOmenKeyShortcut();
        else
          ApplyOmenKeyAction(OmenKeyActions.Shortcut);
      }, omenKey == OmenKeyActions.Shortcut);

      var currentShortcutItem = new ToolStripMenuItem($"{Strings.OmenKeyCurrentShortcut}: {FormatOmenKeyShortcut(omenKeyShortcut)}") { Enabled = false };

      omenKeyShortcutMenu.DropDownOpening += (s, e) => {
        currentShortcutItem.Text = $"{Strings.OmenKeyCurrentShortcut}: {FormatOmenKeyShortcut(omenKeyShortcut)}";
      };

      omenKeyShortcutMenu.DropDownItems.Add(currentShortcutItem);
      omenKeyShortcutMenu.DropDownItems.Add(CreateMenuItem(Strings.OmenKeySetShortcut, null, (s, e) => {
        SelectOmenKeyShortcut();
      }, false));
      omenKeyShortcutMenu.DropDownItems.Add(CreateMenuItem(Strings.OmenKeyClearShortcut, null, (s, e) => {
        omenKeyShortcut = "";
        SaveConfig("OmenKeyShortcut");
        if (omenKey == OmenKeyActions.Shortcut) {
          ApplyOmenKeyAction(OmenKeyActions.None);
          UpdateCheckedState("omenKeyGroup", Strings.OmenKeyNone);
        }
        currentShortcutItem.Text = $"{Strings.OmenKeyCurrentShortcut}: {FormatOmenKeyShortcut(omenKeyShortcut)}";
      }, false));
      omenKeyMenu.DropDownItems.Add(omenKeyShortcutMenu);

      omenKeyMenu.DropDownItems.Add(new ToolStripSeparator());
      omenKeyMenu.DropDownItems.Add(CreateMenuItem(Strings.OmenKeyNone, "omenKeyGroup", (s, e) => {
        ApplyOmenKeyAction(OmenKeyActions.None);
      }, omenKey == OmenKeyActions.None));

      menu.Items.Add(omenKeyMenu);

      ToolStripMenuItem settingMenu = new ToolStripMenuItem(Strings.OtherSettings);
      languageMenu = new ToolStripMenuItem(Strings.LanguageMenu);
      var langItems = new (string label, string code)[] {
        (Strings.LangSimplified,  "zh-CN"),
        (Strings.LangTraditional, "zh-TW"),
        (Strings.LangEnglish,     "en"),
      };
      foreach (var lang in langItems) {
        var localLang = lang;
        var langItem = new ToolStripMenuItem(localLang.label) {
          Tag = "languageGroup",
          Checked = (appLanguage == localLang.code)
        };
        langItem.Click += (s, e) => {
          if (appLanguage == localLang.code) return;

          CloseAllOpenForms();
          appLanguage = localLang.code;
          ApplyLanguage(appLanguage);
          SaveConfig("AppLanguage");
          RefreshMenu();
        };
        languageMenu.DropDownItems.Add(langItem);
      }
      settingMenu.DropDownItems.Add(languageMenu);
      ToolStripMenuItem customIconMenu = new ToolStripMenuItem(Strings.IconMenu);
      customIconMenu.DropDownItems.Add(CreateMenuItem(Strings.IconOriginal, "customIconGroup", (s, e) => {
        customIcon = "original";
        SwapTrayIconToDefault();   // 释放此前的动态 / 自定义图标，并复用默认图标的唯一实例
        SaveConfig("CustomIcon");
      }, true));
      customIconMenu.DropDownItems.Add(CreateMenuItem(Strings.IconCustom, "customIconGroup", (s, e) => {
        customIcon = "custom";
        SetCustomIcon();
        SaveConfig("CustomIcon");
      }, false));
      customIconMenu.DropDownItems.Add(CreateMenuItem(Strings.IconDynamic, "customIconGroup", (s, e) => {
        customIcon = "dynamic";
        UpdateDynamicIcon();
        SaveConfig("CustomIcon");
      }, false));
      settingMenu.DropDownItems.Add(customIconMenu);
      ToolStripMenuItem dataLocalizeMenu = new ToolStripMenuItem(Strings.DataLocalize);
      dataLocalizeMenu.DropDownItems.Add(CreateMenuItem(Strings.Enable, "dataLocalizeGroup", (s, e) => {
        dataLocalize = "on";
        SaveConfig("DataLocalize");
      }, false));
      dataLocalizeMenu.DropDownItems.Add(CreateMenuItem(Strings.Disable, "dataLocalizeGroup", (s, e) => {
        dataLocalize = "off";
        SaveConfig("DataLocalize");
      }, true));
      settingMenu.DropDownItems.Add(dataLocalizeMenu);
      ToolStripMenuItem autoStartMenu = new ToolStripMenuItem(Strings.AutoStart);
      autoStartMenu.DropDownItems.Add(CreateMenuItem(Strings.Enable, "autoStartGroup", (s, e) => {
        autoStart = "on";
        System.Threading.Tasks.Task.Run(() => AutoStartEnable());
        SaveConfig("AutoStart");
      }, false));
      autoStartMenu.DropDownItems.Add(CreateMenuItem(Strings.Disable, "autoStartGroup", (s, e) => {
        autoStart = "off";
        System.Threading.Tasks.Task.Run(() => AutoStartDisable());
        SaveConfig("AutoStart");
      }, true));
      settingMenu.DropDownItems.Add(autoStartMenu);

      menu.Items.Add(settingMenu);

      menu.Items.Add(new ToolStripSeparator()); // Separator between groups
      menu.Items.Add(CreateMenuItem(Strings.Help, null, (s, e) => {
        HelpForm.Instance.Show();
      }, false));
      menu.Items.Add(new ToolStripSeparator()); // Separator between groups
      menu.Items.Add(CreateMenuItem(Strings.Exit, null, (s, e) => Exit(), false));

      // 所有菜单项添加完毕后，递归挂载 Closing 事件
      AttachClosingHandler(menu);
    }

    static void AttachClosingHandler(ToolStripDropDown dropDown) {
      dropDown.Closing -= TrayMenu_Closing;
      dropDown.Closing += TrayMenu_Closing;

      foreach (ToolStripItem item in dropDown.Items) {
        if (item is ToolStripMenuItem menuItem && menuItem.HasDropDownItems) {
          AttachClosingHandler(menuItem.DropDown);
        }
      }
    }

    static void TrayMenu_Closing(object sender, ToolStripDropDownClosingEventArgs e) {
      if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) {
        e.Cancel = true;  // 只拦截点击菜单项导致的关闭
      }
      // 其余原因（失焦、ESC、AppClicked 等）全部放行，不设 e.Cancel
    }

    static ToolStripMenuItem FindMenuItemByName(ToolStripItemCollection items, string name) {
      foreach (ToolStripMenuItem item in items.OfType<ToolStripMenuItem>()) {
        if (item.Name == name) return item;
        if (item.HasDropDownItems) {
          var found = FindMenuItemByName(item.DropDownItems, name);
          if (found != null) return found;
        }
      }
      return null;
    }

    public static void StartCleanCreekWithProgress(int durationMs, string title, Action startCleanAction, Action stopCleanAction) {
      // 创建进度窗体
      Form progressForm = new Form();
      progressForm.Text = title;
      progressForm.Size = new System.Drawing.Size(300, 150);
      progressForm.FormBorderStyle = FormBorderStyle.FixedDialog;
      progressForm.ControlBox = false;
      progressForm.StartPosition = FormStartPosition.CenterScreen;

      Label lblMessage = new Label();
      lblMessage.Text = string.Format(Strings.CleanCreekProgressMessageTemplate, durationMs / 1000);
      lblMessage.Dock = DockStyle.Top;
      lblMessage.Height = 40;
      lblMessage.TextAlign = ContentAlignment.MiddleCenter;

      Button btnStop = new Button();
      btnStop.Text = Strings.CleanCreekStopButton;
      btnStop.DialogResult = DialogResult.Cancel;
      btnStop.Dock = DockStyle.Bottom;
      btnStop.Height = 35;

      progressForm.Controls.Add(lblMessage);
      progressForm.Controls.Add(btnStop);

      // 倒计时计时器
      System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
      timer.Interval = 1000; // 每秒更新一次
      DateTime startTime = DateTime.Now;
      int remainingSeconds = durationMs / 1000;
      timer.Tick += (sender, e) => {
        TimeSpan elapsed = DateTime.Now - startTime;
        int remaining = (int)(durationMs - elapsed.TotalMilliseconds) / 1000;
        if (remaining <= 0) {
          timer.Stop();
          progressForm.Close();      // 倒计时结束，关闭窗体
        } else {
          lblMessage.Text = string.Format(Strings.CleanCreekProgressMessageTemplate, remaining);
        }
      };

      // 停止按钮点击事件
      btnStop.Click += (sender, e) => {
        timer.Stop();
        progressForm.Close();          // 用户点击停止，关闭窗体
      };

      // 窗体关闭时执行停止清洁（无论是倒计时结束还是用户点击停止）
      progressForm.FormClosed += (sender, e) => {
        stopCleanAction?.Invoke();
      };

      // 开始清洁
      startCleanAction?.Invoke();

      // 启动倒计时
      timer.Start();

      // 显示模态对话框（阻止父窗体操作）
      progressForm.ShowDialog();

      // 注意：ShowDialog 会阻塞直到窗体关闭，但内部倒计时和停止按钮正常工作
    }

    public class CustomTrackBar : TrackBar {
      private const int WM_MOUSEWHEEL = 0x020A;

      protected override void WndProc(ref Message m) {
        if (m.Msg == WM_MOUSEWHEEL) {
          // 解析滚轮滚动量
          int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
          Console.WriteLine($"delta: {delta}");
          if (delta < 120 || delta > 120) {
            delta = delta > 0 ? 120 : -120;
          }
          int newValue = this.Value + delta / 120;
          if (newValue < this.Minimum) newValue = this.Minimum;
          if (newValue > this.Maximum) newValue = this.Maximum;
          if (newValue != this.Value)
            this.Value = newValue;        // 触发 ValueChanged 事件

          // 标记消息已处理，不再调用默认窗口过程
          m.Result = IntPtr.Zero;
          return;
        }
        base.WndProc(ref m);
      }
    }

    public class ToolStripTrackBar : ToolStripControlHost {
      public ToolStripTrackBar() : base(new CustomTrackBar()) {
        // 确保宿主允许自定义宽度，内部控件填充
        this.AutoSize = false;
        this.Width = 800;          // 默认宽度
        TrackBarControl.Dock = DockStyle.Fill;
      }

      // 公开内部 TrackBar 控件
      public TrackBar TrackBarControl => Control as TrackBar;

      // 常用属性代理
      public int Minimum {
        get => TrackBarControl.Minimum;
        set => TrackBarControl.Minimum = value;
      }

      public int Maximum {
        get => TrackBarControl.Maximum;
        set => TrackBarControl.Maximum = value;
      }

      public int Value {
        get => TrackBarControl.Value;
        set => TrackBarControl.Value = value;
      }

      public int TickFrequency {
        get => TrackBarControl.TickFrequency;
        set => TrackBarControl.TickFrequency = value;
      }

      public Orientation Orientation {
        get => TrackBarControl.Orientation;
        set => TrackBarControl.Orientation = value;
      }

      // 直接设置内部控件宽度（如果需要独立控制）
      public int TrackBarWidth {
        get => TrackBarControl.Width;
        set => TrackBarControl.Width = value;
      }

      // 事件代理
      public event EventHandler ValueChanged {
        add => TrackBarControl.ValueChanged += value;
        remove => TrackBarControl.ValueChanged -= value;
      }

      // 注意：使用 new 隐藏基类 MouseUp，并使用 MouseEventHandler
      public new event MouseEventHandler MouseUp {
        add => TrackBarControl.MouseUp += value;
        remove => TrackBarControl.MouseUp -= value;
      }

      public new event MouseEventHandler MouseDown {
        add => TrackBarControl.MouseDown += value;
        remove => TrackBarControl.MouseDown -= value;
      }
    }

    private static FanCurveForm activeFanCurveEditor;
    private static string activeFanCurveEditorPath;

    static string GetFanCurveEditorTitle(string fileName) {
      string modeName;
      string fileKey = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
      if (fileKey == "silent") modeName = Strings.FanSilentMode;
      else if (fileKey == "cool") modeName = Strings.FanCoolMode;
      else modeName = Strings.FanCustomMode;

      return $"{modeName}" + Strings.FanCurveEditorTitle;
    }

    static void CloseAllOpenForms() {
      var openForms = Application.OpenForms.Cast<Form>().ToList();
      foreach (Form form in openForms) {
        if (form == null || form.IsDisposed) continue;
        try {
          form.Close();
        } catch (Exception ex) {
          Logger.Error($"Failed to close open form '{form.Text}': {ex.Message}");
        }
      }
    }

    static void CloseOpenTrayMenus(ToolStripDropDown dropDown) {
      if (dropDown == null) return;

      foreach (ToolStripItem item in dropDown.Items) {
        var menuItem = item as ToolStripMenuItem;
        if (menuItem != null && menuItem.HasDropDownItems) {
          CloseOpenTrayMenus(menuItem.DropDown);
        }
      }

      if (dropDown.Visible) {
        try {
          dropDown.Close(ToolStripDropDownCloseReason.CloseCalled);
        } catch {
        }
      }
    }

    static void RefreshMenu() {
      if (trayIcon == null || trayIcon.ContextMenuStrip == null) return;
      CloseOpenTrayMenus(trayIcon.ContextMenuStrip);
      BuildTrayMenu(trayIcon.ContextMenuStrip);
      RestoreConfig();
    }

    // Generalized fan curve editor: handles cool.txt, silent.txt, and custom.txt.
    // "Save & Apply" saves the file, switches fanTable to the corresponding mode, reloads it, and updates checkmarks.
    static void ShowFanCurveEditor(string fileName) {
      string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
      string filePath = Path.Combine(baseDirectory, fileName);
      bool isSilent = fileName.IndexOf("silent", StringComparison.OrdinalIgnoreCase) >= 0;
      string windowTitle = GetFanCurveEditorTitle(fileName);

      try {
        if (activeFanCurveEditor != null && !activeFanCurveEditor.IsDisposed) {
          if (string.Equals(activeFanCurveEditorPath, filePath, StringComparison.OrdinalIgnoreCase)) {
            if (activeFanCurveEditor.WindowState == FormWindowState.Minimized) {
              activeFanCurveEditor.WindowState = FormWindowState.Normal;
            }
            activeFanCurveEditor.Show();
            activeFanCurveEditor.BringToFront();
            activeFanCurveEditor.Activate();
            return;
          }

          var oldEditor = activeFanCurveEditor;
          activeFanCurveEditor = null;
          activeFanCurveEditorPath = null;
          try { oldEditor.Close(); } catch { }
        }

        FanCurveProfile initialProfile;
        if (File.Exists(filePath)) {
          initialProfile = FanCurveProfile.Load(filePath);
        } else {
          initialProfile = CreateDefaultFanCurveProfile(isSilent);
        }

        int cpuMaximum = maxCPUTemp ?? 100;
        int gpuMaximum = maxGPUTemp ?? 90;
        int detectedMaximum = platformMaxFanSpeed ?? 5600;
        int fanMaximum = Math.Max(1000, (int)(Math.Ceiling(detectedMaximum * 1.1 / 100D) * 100D));
        var editor = new FanCurveForm(initialProfile, cpuMaximum, gpuMaximum, fanMaximum, filePath, windowTitle);
        activeFanCurveEditor = editor;
        activeFanCurveEditorPath = filePath;

        editor.FormClosed += (sender, args) => {
          if (ReferenceEquals(activeFanCurveEditor, editor)) {
            activeFanCurveEditor = null;
            activeFanCurveEditorPath = null;
          }

          if (editor.EditorResult == FanCurveEditorResult.SavedAndApplied) {
            string fanTableKey = System.IO.Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
            fanTable = fanTableKey;
            LoadFanConfig(fileName);
            SaveConfig("FanTable");
            string modeText =
                fanTableKey == "silent" ? Strings.FanSilentMode :
                fanTableKey == "cool" ? Strings.FanCoolMode :
                Strings.FanCustomMode;
            UpdateCheckedState("fanTableGroup", modeText);
          }
        };
        editor.Show();
        editor.BringToFront();
        editor.Activate();
      } catch (Exception ex) when (
          ex is IOException ||
          ex is UnauthorizedAccessException ||
          ex is InvalidDataException) {
        if (ReferenceEquals(activeFanCurveEditor, null)) {
          activeFanCurveEditorPath = null;
        }
        MessageBox.Show(
            Application.OpenForms.OfType<HelpForm>().FirstOrDefault(),
            Strings.FanCurveLoadFailed + Environment.NewLine + ex.Message,
            Strings.Error,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
      }
    }


    static bool ApplyCustomFanConfig() {
      string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
      string coolFilePath = Path.Combine(baseDirectory, "cool.txt");
      string customFilePath = Path.Combine(baseDirectory, "custom.txt");

      try {
        if (!File.Exists(customFilePath)) {
          if (!File.Exists(coolFilePath))
            CreateDefaultFanCurveProfile(false).Save(coolFilePath);
          File.Copy(coolFilePath, customFilePath);
        }

        fanTable = "custom";
        LoadFanConfig("custom.txt");
        SaveConfig("FanTable");
        return true;
      } catch (Exception ex) when (
          ex is IOException ||
          ex is UnauthorizedAccessException ||
          ex is InvalidDataException) {
        MessageBox.Show(
            Application.OpenForms.OfType<HelpForm>().FirstOrDefault(),
            Strings.FanCurveLoadFailed + Environment.NewLine + ex.Message,
            Strings.Error,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        return false;
      }
    }

    static ToolStripMenuItem CreateMenuItem(string text, string group, EventHandler action, bool isChecked, string toolTip = null) {
      var item = new ToolStripMenuItem(text) {
        Tag = group,
        Checked = isChecked, // Set initial checked state
        ToolTipText = toolTip   // 设置提示文本
      };
      item.Click += (s, e) => {
        if (item.Text == Strings.IconCustom && !CheckCustomIcon())
          return;

        action(s, e); // Perform the original action
        if (group != null) {
          if (skipCheckedUpdate) {
            skipCheckedUpdate = false;
          } else {
            UpdateCheckedState(group, null, item);
          }
        }
      };
      return item;
    }

    /// <summary>
    /// 将 GetSensorTemperature 的返回值格式化为显示字符串。
    /// &lt;= 0 → 不支持；== 1 → 连接断开；其他 → "xx°C"
    /// </summary>
    static string FormatSensorTemperature(int value) {
      if (value <= 0) return Strings.SysSensorUnsupported;
      if (value == 1) return Strings.SysSensorDisconnected;
      return $"{value} ℃";
    }

    static void UpdateCheckedState(string group, string itemText = null, ToolStripMenuItem menuItemToCheck = null) {
      if (menuItemToCheck == null) {
        // 先尝试匹配相同 group 和名称的选项，防止不同菜单组出现同名冲突（如都有“开启”/“关闭”）
        ToolStripMenuItem FindExact(ToolStripItemCollection items) {
          foreach (ToolStripMenuItem item in items.OfType<ToolStripMenuItem>()) {
            if (item.Text == itemText && string.Equals(item.Tag as string, group)) return item;
            if (item.HasDropDownItems) {
              var found = FindExact(item.DropDownItems);
              if (found != null) return found;
            }
          }
          return null;
        }

        menuItemToCheck = FindExact(trayIcon.ContextMenuStrip.Items) ?? FindMenuItem(trayIcon.ContextMenuStrip.Items, itemText);

        if (menuItemToCheck == null)
          return;
      }

      void UpdateMenuItemsCheckedState(ToolStripItemCollection items, ToolStripMenuItem clicked) {
        foreach (ToolStripMenuItem menuItem in items.OfType<ToolStripMenuItem>()) {
          // 检查是否属于同一个组
          if (menuItem.Tag as string == group) {
            menuItem.Checked = (menuItem == clicked);
          }
          // 如果当前项有子菜单，递归调用处理子菜单项
          if (menuItem.HasDropDownItems) {
            UpdateMenuItemsCheckedState(menuItem.DropDownItems, clicked);
          }
        }
      }
      // 从ContextMenuStrip的根菜单项开始递归
      UpdateMenuItemsCheckedState(trayIcon.ContextMenuStrip.Items, menuItemToCheck);
    }

    static void SetMenuItemChecked(string group, string itemText, bool isChecked) {
      if (trayIcon?.ContextMenuStrip == null) return;

      ToolStripMenuItem FindExact(ToolStripItemCollection items) {
        foreach (ToolStripMenuItem item in items.OfType<ToolStripMenuItem>()) {
          if (item.Text == itemText && string.Equals(item.Tag as string, group)) return item;
          if (item.HasDropDownItems) {
            var found = FindExact(item.DropDownItems);
            if (found != null) return found;
          }
        }
        return null;
      }

      var menuItem = FindExact(trayIcon.ContextMenuStrip.Items);
      if (menuItem != null) menuItem.Checked = isChecked;
    }

    // 递归查找指定文本的菜单项
    static ToolStripMenuItem FindMenuItem(ToolStripItemCollection items, string itemText, int select = 2) {
      foreach (ToolStripMenuItem menuItem in items.OfType<ToolStripMenuItem>()) {
        if (menuItem.Text == itemText) {
          return menuItem;
        }

        if (menuItem.HasDropDownItems) {
          var foundItem = FindMenuItem(menuItem.DropDownItems, itemText);
          if (foundItem != null) {
            // 启用或禁用对应项
            if (select == 1)
              foundItem.Enabled = true;
            else if (select == 0)
              foundItem.Enabled = false;
            return foundItem;
          }
        }
      }
      return null;
    }

    /// <summary>
    /// 根据当前 appLanguage 值恢复语言菜单的勾选状态。
    /// 语言菜单项的 Tag 为 "languageGroup"，通过遍历而非文本匹配来找到对应项，
    /// 以免因多语言文本不一致导致匹配失败。
    /// </summary>
    static void RestoreLanguageChecked() {
      // 映射：语言代码 → 该语言下自己的显示文本
      string targetText;
      switch (appLanguage) {
        case "zh-TW": targetText = Strings.LangTraditional; break;
        case "en": targetText = Strings.LangEnglish; break;
        default: targetText = Strings.LangSimplified; break;
      }
      // 在菜单树中找到 languageGroup 中文本匹配的项并勾选
      void Walk(ToolStripItemCollection items) {
        foreach (ToolStripMenuItem mi in items.OfType<ToolStripMenuItem>()) {
          if ((mi.Tag as string) == "languageGroup") {
            mi.Checked = (mi.Text == targetText);
          }
          if (mi.HasDropDownItems) Walk(mi.DropDownItems);
        }
      }
      Walk(trayIcon.ContextMenuStrip.Items);
    }
  }
}
