using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OmenSuperHub.Control {
  internal sealed class LatestFileWriter {
    private readonly object gate = new object();
    private readonly Action<string, string> write;
    private readonly Action<Exception> error;
    private readonly Dictionary<string, string> successful = new Dictionary<string, string>();
    private string[] latest;
    private bool busy, stopping;
    private TaskCompletionSource<bool> done;
    internal LatestFileWriter(Action<string, string> write, Action<Exception> error) { this.write = write; this.error = error; }
    internal void Publish(string cpu, string gpu, string fan) {
      lock (gate) {
        if (stopping) return;
        latest = new[] { cpu, gpu, fan };
        if (busy) return;
        busy = true; done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task.Run((Action)Drain);
      }
    }
    private void Drain() {
      string[] names = { "cpu_temp.txt", "gpu_temp.txt", "fan_rpm.txt" };
      while (true) {
        string[] values;
        lock (gate) {
          if (stopping || latest == null) { busy = false; done.TrySetResult(true); return; }
          values = latest; latest = null;
        }
        for (int i = 0; i < names.Length; i++) {
          lock (gate) if (stopping) break;
          string old;
          if (successful.TryGetValue(names[i], out old) && old == values[i]) continue;
          try { write(names[i], values[i]); successful[names[i]] = values[i]; }
          catch (Exception ex) { error(ex); }
        }
      }
    }
    internal Task StopAsync() {
      lock (gate) { stopping = true; latest = null; return busy ? done.Task : Task.CompletedTask; }
    }
  }
}
