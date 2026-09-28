using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OmenSuperHub.Control {
  // The firmware GUID can remain registered with WmiAcpi while its hpqB* classes
  // are absent from the CIM repository. This uses that same Windows WMI provider;
  // it neither installs a driver nor edits/rebuilds the system WMI repository.
  internal interface INativeBiosSession : IDisposable {
    IReadOnlyList<string> InstanceNames();
    byte[] Invoke(string instance, uint method, byte[] input, int outputBytes);
  }

  internal sealed class BiosReturnCodeException : IOException {
    internal readonly uint ReturnCode;
    internal BiosReturnCodeException(uint code) : base("BIOS return code " + code) { ReturnCode = code; }
  }

  internal static class BiosWmiPacket {
    internal static uint Method(int outputSize) {
      switch (outputSize) {
        case 0: return 1;
        case 4: return 2;
        case 128: return 3;
        case 1024: return 4;
        case 4096: return 5;
        default: throw new ArgumentOutOfRangeException(nameof(outputSize));
      }
    }
    internal static byte[] Request(uint command, uint commandType, byte[] data) {
      int length = data?.Length ?? 0;
      if (length > 4096) throw new ArgumentOutOfRangeException(nameof(data));
      // HP's ACPI envelope: SECU, command, command type, payload size, payload.
      // Keep the minimum 128-byte backing payload used by the firmware WMI ABI.
      var buffer = new byte[16 + Math.Max(128, length)];
      Put(buffer, 0, 0x55434553); Put(buffer, 4, command);
      Put(buffer, 8, commandType); Put(buffer, 12, (uint)length);
      if (length != 0) Buffer.BlockCopy(data, 0, buffer, 16, length);
      return buffer;
    }
    internal static byte[] Response(byte[] buffer, int outputSize) {
      if (buffer == null || buffer.Length < 8) throw new InvalidDataException("Truncated HP BIOS response header");
      uint code = BitConverter.ToUInt32(buffer, 4);
      if (code != 0) throw new BiosReturnCodeException(code);
      if (buffer.Length < 8 + outputSize) throw new InvalidDataException("Truncated HP BIOS response payload: expected " + outputSize + ", got " + (buffer.Length - 8));
      var result = new byte[outputSize];
      Buffer.BlockCopy(buffer, 8, result, 0, outputSize);
      return result;
    }
    private static void Put(byte[] buffer, int offset, uint value) {
      Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, 4);
    }
  }

  internal sealed class NativeBiosWmi {
    private readonly Func<INativeBiosSession> open;
    private string verifiedInstance;
    internal NativeBiosWmi(Func<INativeBiosSession> open = null) { this.open = open ?? (() => new KernelBiosSession()); }
    // The caller holds the existing BIOS bus gate for discovery, probing and invocation.
    internal byte[] Execute(uint command, uint commandType, byte[] data, int outputSize, Func<bool> current) {
      uint method = BiosWmiPacket.Method(outputSize);
      byte[] input = BiosWmiPacket.Request(command, commandType, data);
      if (!current()) return null;
      try {
        using (INativeBiosSession session = open()) {
          if (verifiedInstance == null) {
            IReadOnlyList<string> instances = session.InstanceNames();
            Exception failure = null;
            foreach (string instance in instances) {
              if (!current()) return null;
              try {
                // Validate the endpoint with a read-only system-design query before
                // allowing any fan/power write through the repository-independent path.
                byte[] probe = BiosWmiPacket.Response(session.Invoke(instance, 3,
                  BiosWmiPacket.Request(0x20008, 0x28, new byte[4]), 136), 128);
                if (!probe.Any(value => value != 0)) throw new InvalidDataException("Empty HP system-design response");
                verifiedInstance = instance;
                break;
              } catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is InvalidDataException) { failure = ex; }
            }
            if (verifiedInstance == null) throw new IOException("No validated HP BIOS WMI instance", failure);
          }
          // Enumeration and the validation read can both block. Recheck after them.
          if (!current()) return null;
          return BiosWmiPacket.Response(session.Invoke(verifiedInstance, method, input, 8 + outputSize), outputSize);
        }
      } catch (BiosReturnCodeException) {
        // A firmware rejection does not mean the endpoint disappeared.
        throw;
      } catch {
        verifiedInstance = null;
        throw;
      }
    }
  }

  internal sealed class KernelBiosSession : INativeBiosSession {
    internal static readonly Guid BiosGuid = new Guid("5FB7F034-2C63-45E9-BE91-3D44E2C707E4");
    private readonly WmiBlockHandle handle;
    internal KernelBiosSession() {
      Guid guid = BiosGuid;
      uint status = Native.WmiOpenBlock(ref guid, 0x0001 | 0x0010, out handle); // QUERY | EXECUTE
      if (status != 0) { handle?.Dispose(); throw new Win32Exception((int)status, "Opening HP BIOS WMI GUID failed: " + status); }
    }
    public IReadOnlyList<string> InstanceNames() {
      uint size = 0;
      uint status = Native.WmiQueryAllDataW(handle, ref size, null);
      for (int attempt = 0; attempt < 3; attempt++) {
        if (status != 0 && status != 122 && status != 234)
          throw new Win32Exception((int)status, "Enumerating HP BIOS WMI instances failed: " + status);
        if (size < 64 || size > 1024 * 1024) throw new InvalidDataException("Invalid WMI instance buffer size: " + size);
        var buffer = new byte[size];
        status = Native.WmiQueryAllDataW(handle, ref size, buffer);
        if (status == 0) {
          if (size > buffer.Length) throw new InvalidDataException("WMI instance response exceeds buffer");
          return ReadInstanceNames(buffer, (int)size);
        }
      }
      throw new Win32Exception((int)status, "HP BIOS WMI instance enumeration did not stabilize: " + status);
    }
    internal static IReadOnlyList<string> ReadInstanceNames(byte[] buffer, int length) {
      if (buffer == null || length < 64 || length > buffer.Length) throw new InvalidDataException("Invalid WMI instance response");
      var names = new List<string>();
      int block = 0;
      while (true) {
        if (block > length - 64) throw new InvalidDataException("Truncated WNODE_ALL_DATA");
        uint bytes = BitConverter.ToUInt32(buffer, block);
        uint next = BitConverter.ToUInt32(buffer, block + 12);
        uint count = BitConverter.ToUInt32(buffer, block + 52);
        uint offsets = BitConverter.ToUInt32(buffer, block + 56);
        if (bytes < 64 || bytes > length - block || count > 256 || offsets > bytes || count * 4 > bytes - offsets)
          throw new InvalidDataException("Invalid WMI instance offsets");
        for (int i = 0; i < count; i++) {
          uint nameOffset = BitConverter.ToUInt32(buffer, block + (int)offsets + i * 4);
          if (nameOffset > bytes - 2) throw new InvalidDataException("Invalid WMI instance name offset");
          int nameBytes = BitConverter.ToUInt16(buffer, block + (int)nameOffset);
          if (nameBytes == 0 || (nameBytes & 1) != 0 || nameBytes > bytes - nameOffset - 2)
            throw new InvalidDataException("Invalid WMI instance name length");
          string name = Encoding.Unicode.GetString(buffer, block + (int)nameOffset + 2, nameBytes).TrimEnd('\0');
          if (name.Length == 0 || name.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid WMI instance name");
          if (!names.Contains(name)) names.Add(name);
        }
        if (next == 0) break;
        if (next < bytes || next > length - block) throw new InvalidDataException("Invalid WMI instance linkage");
        block += (int)next;
      }
      return names;
    }
    public byte[] Invoke(string instance, uint method, byte[] input, int outputBytes) {
      var output = new byte[outputBytes];
      uint size = (uint)output.Length;
      uint status = Native.WmiExecuteMethodW(handle, instance, method, (uint)input.Length, input, ref size, output);
      if (status != 0) throw new Win32Exception((int)status, "HP BIOS WMI method " + method + " failed: " + status);
      if (size > output.Length) throw new InvalidDataException("HP BIOS WMI response exceeds output buffer");
      Array.Resize(ref output, (int)size);
      return output;
    }
    public void Dispose() { handle.Dispose(); }
    private sealed class WmiBlockHandle : SafeHandleZeroOrMinusOneIsInvalid {
      public WmiBlockHandle() : base(true) { }
      protected override bool ReleaseHandle() { return Native.WmiCloseBlock(handle) == 0; }
    }
    private static class Native {
      [DllImport("advapi32.dll", ExactSpelling = true)]
      [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
      internal static extern uint WmiOpenBlock(ref Guid guid, uint access, out WmiBlockHandle handle);
      [DllImport("advapi32.dll", ExactSpelling = true)]
      [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
      internal static extern uint WmiCloseBlock(IntPtr handle);
      [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
      [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
      internal static extern uint WmiQueryAllDataW(WmiBlockHandle handle, ref uint size, [Out] byte[] buffer);
      [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
      [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
      internal static extern uint WmiExecuteMethodW(WmiBlockHandle handle, string instance, uint method,
        uint inputSize, [In] byte[] input, ref uint outputSize, [Out] byte[] output);
    }
  }
}
