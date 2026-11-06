using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OmenSuperHub {
  internal sealed class FanCurvePoint {
    public FanCurvePoint(int temperature, int fanSpeed)
        : this(temperature, fanSpeed, fanSpeed) {
    }

    public FanCurvePoint(int temperature, int fan1Speed, int fan2Speed) {
      Temperature = temperature;
      Fan1Speed = fan1Speed;
      Fan2Speed = fan2Speed;
    }

    public int Temperature { get; set; }
    public int Fan1Speed { get; set; }
    public int Fan2Speed { get; set; }
  }

  internal sealed class FanCurveProfile {
    private const string CpuTemperatureKey = "Fan_Table_CPU_Temperature_List";
    private const string CpuFan1SpeedKey = "Fan_Table_CPU_Fan1_Speed_List";
    private const string CpuFan2SpeedKey = "Fan_Table_CPU_Fan2_Speed_List";
    private const string CpuLegacySpeedKey = "Fan_Table_CPU_Fan_Speed_List";
    private const string GpuTemperatureKey = "Fan_Table_GPU_Temperature_List";
    private const string GpuFan1SpeedKey = "Fan_Table_GPU_Fan1_Speed_List";
    private const string GpuFan2SpeedKey = "Fan_Table_GPU_Fan2_Speed_List";
    private const string GpuLegacySpeedKey = "Fan_Table_GPU_Fan_Speed_List";

    public FanCurveProfile(IEnumerable<FanCurvePoint> cpuPoints, IEnumerable<FanCurvePoint> gpuPoints) {
      CpuPoints = cpuPoints.OrderBy(point => point.Temperature).ToList();
      GpuPoints = gpuPoints.OrderBy(point => point.Temperature).ToList();
    }

    public List<FanCurvePoint> CpuPoints { get; }
    public List<FanCurvePoint> GpuPoints { get; }

    public static FanCurveProfile Load(string filePath) {
      string[] lines = File.ReadAllLines(filePath);
      if (lines.Length == 0)
        throw new InvalidDataException(Strings.FanCurveInvalidFile);

      if (lines.Any(line => line.Contains("=")))
        return LoadKeyValueFormat(lines);

      return LoadLegacyCsvFormat(lines);
    }

    public void Save(string filePath) {
      ValidatePoints(CpuPoints);
      ValidatePoints(GpuPoints);

      string[] lines = {
        CpuTemperatureKey + "=" + string.Join(",", CpuPoints.Select(point => point.Temperature)),
        CpuFan1SpeedKey + "=" + string.Join(",", CpuPoints.Select(point => point.Fan1Speed)),
        CpuFan2SpeedKey + "=" + string.Join(",", CpuPoints.Select(point => point.Fan2Speed)),
        GpuTemperatureKey + "=" + string.Join(",", GpuPoints.Select(point => point.Temperature)),
        GpuFan1SpeedKey + "=" + string.Join(",", GpuPoints.Select(point => point.Fan1Speed)),
        GpuFan2SpeedKey + "=" + string.Join(",", GpuPoints.Select(point => point.Fan2Speed)),
        // Keep the previous single-fan keys so older builds can still read these profiles.
        CpuLegacySpeedKey + "=" + string.Join(",", CpuPoints.Select(point => point.Fan1Speed)),
        GpuLegacySpeedKey + "=" + string.Join(",", GpuPoints.Select(point => point.Fan1Speed))
      };
      File.WriteAllLines(filePath, lines);
    }

    private static FanCurveProfile LoadKeyValueFormat(IEnumerable<string> lines) {
      var valuesByKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
      foreach (string rawLine in lines) {
        if (string.IsNullOrWhiteSpace(rawLine)) continue;
        int separatorIndex = rawLine.IndexOf('=');
        if (separatorIndex < 0) continue;

        string key = rawLine.Substring(0, separatorIndex).Trim();
        string value = rawLine.Substring(separatorIndex + 1).Trim();
        valuesByKey[key] = ParseIntegerList(value);
      }

      List<int> cpuTemperatures;
      List<int> gpuTemperatures;
      if (!valuesByKey.TryGetValue(CpuTemperatureKey, out cpuTemperatures) ||
          !valuesByKey.TryGetValue(GpuTemperatureKey, out gpuTemperatures))
        throw new InvalidDataException(Strings.FanCurveInvalidFile);

      List<int> cpuFan1Speeds;
      List<int> cpuFan2Speeds;
      List<int> gpuFan1Speeds;
      List<int> gpuFan2Speeds;
      if (valuesByKey.TryGetValue(CpuFan1SpeedKey, out cpuFan1Speeds) &&
          valuesByKey.TryGetValue(CpuFan2SpeedKey, out cpuFan2Speeds) &&
          valuesByKey.TryGetValue(GpuFan1SpeedKey, out gpuFan1Speeds) &&
          valuesByKey.TryGetValue(GpuFan2SpeedKey, out gpuFan2Speeds)) {
        return Create(cpuTemperatures, cpuFan1Speeds, cpuFan2Speeds,
            gpuTemperatures, gpuFan1Speeds, gpuFan2Speeds);
      }

      // Profiles written by earlier HEAD builds contain one speed list per
      // temperature source. Treat that value as the curve for both fans.
      List<int> cpuSpeeds;
      List<int> gpuSpeeds;
      if (!valuesByKey.TryGetValue(CpuLegacySpeedKey, out cpuSpeeds) ||
          !valuesByKey.TryGetValue(GpuLegacySpeedKey, out gpuSpeeds))
        throw new InvalidDataException(Strings.FanCurveInvalidFile);

      return Create(cpuTemperatures, cpuSpeeds, cpuSpeeds,
          gpuTemperatures, gpuSpeeds, gpuSpeeds);
    }

    private static FanCurveProfile LoadLegacyCsvFormat(string[] lines) {
      var cpuTemperatures = new List<int>();
      var cpuFan1Speeds = new List<int>();
      var cpuFan2Speeds = new List<int>();
      var gpuTemperatures = new List<int>();
      var gpuFan1Speeds = new List<int>();
      var gpuFan2Speeds = new List<int>();

      for (int index = 1; index < lines.Length; index++) {
        if (string.IsNullOrWhiteSpace(lines[index])) continue;
        string[] parts = lines[index].Split(',');
        if (parts.Length < 6) continue;

        cpuTemperatures.Add(ParseInteger(parts[0]));
        cpuFan1Speeds.Add(ParseInteger(parts[1]));
        cpuFan2Speeds.Add(ParseInteger(parts[2]));
        gpuTemperatures.Add(ParseInteger(parts[3]));
        gpuFan1Speeds.Add(ParseInteger(parts[4]));
        gpuFan2Speeds.Add(ParseInteger(parts[5]));
      }

      return Create(cpuTemperatures, cpuFan1Speeds, cpuFan2Speeds,
          gpuTemperatures, gpuFan1Speeds, gpuFan2Speeds);
    }

    private static FanCurveProfile Create(
        IList<int> cpuTemperatures,
        IList<int> cpuFan1Speeds,
        IList<int> cpuFan2Speeds,
        IList<int> gpuTemperatures,
        IList<int> gpuFan1Speeds,
        IList<int> gpuFan2Speeds) {
      if (cpuTemperatures.Count != cpuFan1Speeds.Count ||
          cpuTemperatures.Count != cpuFan2Speeds.Count ||
          gpuTemperatures.Count != gpuFan1Speeds.Count ||
          gpuTemperatures.Count != gpuFan2Speeds.Count ||
          cpuTemperatures.Count < 2 ||
          gpuTemperatures.Count < 2)
        throw new InvalidDataException(Strings.FanCurveInvalidFile);

      var cpuPoints = cpuTemperatures
          .Select((temperature, index) => new FanCurvePoint(
              temperature, cpuFan1Speeds[index], cpuFan2Speeds[index]))
          .ToList();
      var gpuPoints = gpuTemperatures
          .Select((temperature, index) => new FanCurvePoint(
              temperature, gpuFan1Speeds[index], gpuFan2Speeds[index]))
          .ToList();

      ValidatePoints(cpuPoints);
      ValidatePoints(gpuPoints);
      return new FanCurveProfile(cpuPoints, gpuPoints);
    }

    private static List<int> ParseIntegerList(string value) {
      var result = value
          .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
          .Select(ParseInteger)
          .ToList();
      if (result.Count == 0)
        throw new InvalidDataException(Strings.FanCurveInvalidFile);
      return result;
    }

    private static int ParseInteger(string value) {
      int result;
      if (!int.TryParse(value.Trim(), out result))
        throw new InvalidDataException(Strings.FanCurveInvalidFile);
      return result;
    }

    private static void ValidatePoints(IList<FanCurvePoint> points) {
      if (points.Count < 2 ||
          points.Any(point => point.Temperature < 0 || point.Fan1Speed < 0 || point.Fan2Speed < 0) ||
          points.GroupBy(point => point.Temperature).Any(group => group.Count() > 1))
        throw new InvalidDataException(Strings.FanCurveInvalidFile);
    }
  }
}
