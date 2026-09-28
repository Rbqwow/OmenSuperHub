using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace OmenSuperHub.Control {
  internal sealed class BoundedLog {
    internal const int MaxBytes = 5 * 1024 * 1024, MaxRecordBytes = 64 * 1024, Capacity = 256;
    private readonly object gate = new object();
    private readonly Dictionary<string, long> seen = new Dictionary<string, long>();
    private readonly string path;
    private readonly IClock clock;
    private readonly Action<string> console;
    private readonly Action<string, byte[]> append;
    private long fileRetryAt;
    private byte[] pending;
    internal int KeyCount { get { lock (gate) return seen.Count; } }
    internal BoundedLog(string path, IClock clock, Action<string> console, Action<string, byte[]> append = null) {
      this.path = path; this.clock = clock; this.console = console; this.append = append ?? Append;
    }
    internal void Write(string level, string message, string id = null) {
      lock (gate) {
        long now = clock.Milliseconds;
        string key = level + ":" + (id ?? message ?? "");
        long previous;
        bool suppressed = seen.TryGetValue(key, out previous) && now - previous < 30000;
        byte[] bytes = null;
        if (!suppressed) {
          foreach (string old in seen.Where(x => now - x.Value >= 30000).Select(x => x.Key).ToArray()) seen.Remove(old);
          if (seen.Count >= Capacity) seen.Remove(seen.OrderBy(x => x.Value).First().Key);
          seen[key] = now;
          bytes = EncodeRecord(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + message);
          try { console(Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n')); } catch { }
        }
        if (bytes != null) pending = bytes; // One bounded retry record, never a growing I/O queue.
        if (pending == null || now < fileRetryAt) return;
        try {
          Rotate(pending.Length); append(path, pending); pending = null; fileRetryAt = 0;
        } catch (Exception ex) {
          fileRetryAt = now + 30000;
          try { Debug.WriteLine("Log file unavailable: " + ex.Message); } catch { }
        }
      }
    }
    private static byte[] EncodeRecord(string line) {
      const string marker = " ... [truncated]\r\n";
      int budget = MaxRecordBytes - Encoding.UTF8.GetByteCount(marker);
      if (Encoding.UTF8.GetByteCount(line + Environment.NewLine) <= MaxRecordBytes)
        return Encoding.UTF8.GetBytes(line + Environment.NewLine);
      byte[] buffer = new byte[budget];
      int charsUsed, bytesUsed; bool completed;
      Encoding.UTF8.GetEncoder().Convert(line.ToCharArray(), 0, line.Length, buffer, 0, budget, true,
        out charsUsed, out bytesUsed, out completed);
      return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buffer, 0, bytesUsed) + marker);
    }
    private void Rotate(int incoming) {
      string backup = path + ".1";
      if (File.Exists(backup) && new FileInfo(backup).Length > MaxBytes) File.Delete(backup);
      if (!File.Exists(path)) return;
      long length = new FileInfo(path).Length;
      if (length + incoming <= MaxBytes) return;
      if (File.Exists(backup)) File.Delete(backup);
      if (length > MaxBytes) File.Delete(path);
      else File.Move(path, backup);
    }
    private static void Append(string path, byte[] bytes) {
      using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        stream.Write(bytes, 0, bytes.Length);
    }
  }
}
