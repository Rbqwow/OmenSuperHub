using System;
using System.IO;
using OmenSuperHub.Control;

namespace OmenSuperHub {
  public static class Logger {
    public static readonly string logFileName = "OmenSuperHub.log";
    private static readonly BoundedLog sink = new BoundedLog(
      Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logFileName), new MonotonicClock(), Console.WriteLine);
    public static void Info(string message, string id = null) { sink.Write("INFO", message, id); }
    public static void Error(string message, string id = null) { sink.Write("ERROR", message, id); }
  }
}
