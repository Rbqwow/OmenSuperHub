using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using NvAPIWrapper.GPU;

namespace OmenSuperHub {
  public static class GpuAppManager {
    public class GpuAppInfo {
      public int ProcessId { get; set; }
      public string ProcessName { get; set; }
    }

    public static List<GpuAppInfo> GetGpuApps() {
      var apps = new List<GpuAppInfo>();
      try {
        // 直接构建命令字符串
        string command = "nvidia-smi --query-compute-apps=pid,process_name --format=csv,noheader";
        ProcessResult result = ExecuteCommand(command);

        if (result.ExitCode == 0) {
          string[] lines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
          foreach (string line in lines) {
            string[] parts = line.Split(',');
            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out int pid)) {
              apps.Add(new GpuAppInfo {
                ProcessId = pid,
                ProcessName = parts[1].Trim()
              });
            }
          }
        }
      } catch { }
      return apps;
    }

    public static void RestartGpu() {
      try {
        // 1. WMI 查询获取 NVIDIA 显卡的 PNPDeviceID（保持不变）
        string instanceId = null;
        string query = "SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'Display'";
        using (var searcher = new System.Management.ManagementObjectSearcher(query)) {
          foreach (System.Management.ManagementObject device in searcher.Get()) {
            string description = device["Description"]?.ToString();
            if (!string.IsNullOrEmpty(description) &&
                description.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0) {
              instanceId = device["PNPDeviceID"]?.ToString();
              break;
            }
          }
        }

        if (string.IsNullOrEmpty(instanceId)) {
          MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.DeviceNotFound, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
          return;
        }

        // 2. 通过 ExecuteCommand 执行 pnputil 重启设备
        string command = $"pnputil /restart-device \"{instanceId}\"";
        ProcessResult result = ExecuteCommand(command);

        // 可选：根据结果给出提示
        if (result.ExitCode != 0) {
          MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), $"{Strings.RestartGPUFailed} {Strings.Error}：{result.Error}", Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
      } catch {
        MessageBox.Show(Application.OpenForms.OfType<HelpForm>().FirstOrDefault(), Strings.RestartGPUFailed, Strings.Hint, MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    /// <summary>
    /// 获取所有显卡名称列表（跳过 Microsoft 基本显示适配器）
    /// </summary>
    public static List<string> GetAllGpuNamesList() {
      var gpuNames = new List<string>();
      try {
        // 增加查询 PNPDeviceID 字段
        using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController"))
        using (var collection = searcher.Get()) {
          foreach (ManagementObject obj in collection) {
            string name = obj["Name"]?.ToString() ?? "";
            string compatibility = obj["AdapterCompatibility"]?.ToString() ?? "";
            string pnpDeviceId = obj["PNPDeviceID"]?.ToString() ?? "";

            // 1. 过滤物理硬件特征：必须是 PCI 设备（排除 ROOT\ 等虚拟根设备）
            if (!pnpDeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
              continue;

            // 2. 过滤微软基础渲染/远程桌面代理
            if (name.Contains("Microsoft") || compatibility.Contains("Microsoft"))
              continue;

            // 3. 常见的虚拟显卡黑名单关键字（双重保险）
            if (name.Contains("Idd") || name.Contains("Virtual") || name.Contains("spacedesk"))
              continue;

            if (!string.IsNullOrWhiteSpace(name))
              gpuNames.Add(name.Trim());
          }
        }
      } catch (Exception ex) {
        Logger.Error($"GetAllGpuNamesList 异常: {ex.Message}");
      }
      return gpuNames.Distinct().ToList(); // 去重，防止核显独显重复上报
    }

    /// <summary>
    /// 通过 nvidia-smi -L 获取第一个 NVIDIA 显卡的型号名称
    /// </summary>
    public static string GetGpuModelFromNvidiaSmi() {
      var result = ExecuteCommand("nvidia-smi -L");
      if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        return null;

      // 输出格式示例：GPU 0: NVIDIA GeForce RTX 4060 Laptop GPU (UUID: ...)
      // 提取 "GPU 0: " 之后，左括号之前的内容
      var output = result.Output.Trim();
      var colonIndex = output.IndexOf(':');
      if (colonIndex == -1) return null;

      var afterColon = output.Substring(colonIndex + 1).TrimStart();
      var parenIndex = afterColon.IndexOf('(');
      if (parenIndex != -1)
        afterColon = afterColon.Substring(0, parenIndex).TrimEnd();

      return string.IsNullOrEmpty(afterColon) ? null : afterColon;
    }

    /// <summary>是否存在 NVIDIA 独显。</summary>
    public static bool HasNvidiaGpu() {
      try {
        var gpus = PhysicalGPU.GetPhysicalGPUs();

        return gpus != null &&
               gpus.Length > 0;
      } catch {
        return false;
      }
    }

    public static float[] GetGpuPowerLimits() {
      // Returns [Current Limit, Max Limit]
      var limits = new float[2] { -2f, -2f };
      try {
        ProcessResult result = ExecuteCommand("nvidia-smi -q -d POWER");

        if (result.ExitCode == 0) {
          string currentPattern = @"Current Power Limit\s+:\s+([\d.]+)\s+W";
          string maxPattern = @"Max Power Limit\s+:\s+([\d.]+)\s+W";

          var currentMatch = Regex.Match(result.Output, currentPattern);
          var maxMatch = Regex.Match(result.Output, maxPattern);

          if (currentMatch.Success && maxMatch.Success) {
            limits[0] = float.Parse(currentMatch.Groups[1].Value);
            limits[1] = float.Parse(maxMatch.Groups[1].Value);
          }
        }
      } catch { }
      return limits;
    }

    public static int GetGpuTemperatureTarget() {
      int limit = -2;
      try {
        ProcessResult result = ExecuteCommand("nvidia-smi -q -d TEMPERATURE");
        if (result.ExitCode == 0) {
          // 匹配形如 "GPU Target Temperature               : 87 C"
          string targetPattern = @"GPU Target Temperature\s+:\s+(\d+)\s+C";
          var targetMatch = Regex.Match(result.Output, targetPattern);
          if (targetMatch.Success) {
            limit = int.Parse(targetMatch.Groups[1].Value);
          }
        }
      } catch { }
      return limit;
    }

    public static ProcessResult ExecuteCommand(string command) {
      var processStartInfo = new ProcessStartInfo {
        FileName = "cmd.exe",
        Arguments = $"/c {command}",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
      };

      using (var process = new Process { StartInfo = processStartInfo }) {
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ProcessResult {
          ExitCode = process.ExitCode,
          Output = output,
          Error = error
        };
      }
    }

    public class ProcessResult {
      public int ExitCode { get; set; }
      public string Output { get; set; }
      public string Error { get; set; }
    }
  }
}
