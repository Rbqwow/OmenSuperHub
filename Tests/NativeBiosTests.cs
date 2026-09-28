using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using OmenSuperHub.Control;

internal static partial class ControlTests {
  private sealed class NativeCall {
    internal string Instance;
    internal uint Method, Command, Type;
    internal int OutputBytes;
  }
  private sealed class NativeSession : INativeBiosSession {
    internal readonly List<NativeCall> Calls = new List<NativeCall>();
    internal Func<NativeCall, byte[]> OnInvoke;
    internal Action OnDiscover;
    internal int Discoveries;
    internal bool Disposed;
    public IReadOnlyList<string> InstanceNames() {
      Discoveries++; OnDiscover?.Invoke();
      return new[] { "ACPI\\PNP0C14\\H19P_0" };
    }
    public byte[] Invoke(string instance, uint method, byte[] input, int outputBytes) {
      var call = new NativeCall { Instance = instance, Method = method, Command = BitConverter.ToUInt32(input, 4),
        Type = BitConverter.ToUInt32(input, 8), OutputBytes = outputBytes };
      Calls.Add(call);
      if (OnInvoke != null) return OnInvoke(call);
      return GoodNativeResponse(call);
    }
    public void Dispose() { Disposed = true; }
  }
  private static byte[] GoodNativeResponse(NativeCall call) {
    var result = new byte[call.OutputBytes];
    if (call.Type == 0x28) { result[8] = 0xC8; result[11] = 1; }
    return result;
  }
  private static T Throws<T>(Action action) where T : Exception {
    try { action(); } catch (T ex) { return ex; }
    throw new Exception("Expected " + typeof(T).Name);
  }
  private static void Put32(byte[] data, int offset, uint value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, offset, 4); }
  private static byte[] InstanceBlock(string name) {
    byte[] text = Encoding.Unicode.GetBytes(name);
    var block = new byte[68 + 2 + text.Length];
    Put32(block, 0, (uint)block.Length); Put32(block, 52, 1); Put32(block, 56, 64); Put32(block, 64, 68);
    Buffer.BlockCopy(BitConverter.GetBytes((ushort)text.Length), 0, block, 68, 2);
    Buffer.BlockCopy(text, 0, block, 70, text.Length);
    return block;
  }
  private static void NativePacketPreservesFirmwareAbi() {
    byte[] payload = { 30, 31 };
    byte[] request = BiosWmiPacket.Request(0x20008, 0x2E, payload);
    Check(Encoding.ASCII.GetString(request, 0, 4) == "SECU" && request.Length == 144, "Incorrect HP BIOS envelope signature or padding.");
    Check(BitConverter.ToUInt32(request, 4) == 0x20008 && BitConverter.ToUInt32(request, 8) == 0x2E &&
      BitConverter.ToUInt32(request, 12) == 2 && request[16] == 30 && request[17] == 31, "Native request changed the command or fan levels.");
    int[] sizes = { 0, 4, 128, 1024, 4096 };
    for (int i = 0; i < sizes.Length; i++) Check(BiosWmiPacket.Method(sizes[i]) == i + 1, "Incorrect WMI method selector.");
    Throws<ArgumentOutOfRangeException>(() => BiosWmiPacket.Method(5));
    Throws<ArgumentOutOfRangeException>(() => BiosWmiPacket.Request(1, 1, new byte[4097]));
  }
  private static void NativePacketRejectsInvalidResponses() {
    Throws<InvalidDataException>(() => BiosWmiPacket.Response(new byte[7], 0));
    Throws<InvalidDataException>(() => BiosWmiPacket.Response(new byte[8], 4));
    var rejected = new byte[12]; Put32(rejected, 4, 3);
    Check(Throws<BiosReturnCodeException>(() => BiosWmiPacket.Response(rejected, 4)).ReturnCode == 3, "BIOS rejection was accepted.");
    var valid = new byte[12]; valid[8] = 30; valid[9] = 31;
    Check(BiosWmiPacket.Response(valid, 4).SequenceEqual(new byte[] { 30, 31, 0, 0 }), "Native response included its protocol header.");
    Check(BiosWmiPacket.Response(new byte[8], 0).Length == 0, "A zero-output acknowledgement was lost.");
  }
  private static void NativeInstanceNamesAreDecoded() {
    byte[] first = InstanceBlock("ACPI\\PNP0C14\\H19P_0"), second = InstanceBlock("ACPI\\PNP0C14\\0_0");
    int next = (first.Length + 7) & ~7;
    var blocks = new byte[next + second.Length];
    Buffer.BlockCopy(first, 0, blocks, 0, first.Length); Put32(blocks, 12, (uint)next);
    Buffer.BlockCopy(second, 0, blocks, next, second.Length);
    var names = KernelBiosSession.ReadInstanceNames(blocks, blocks.Length);
    Check(names.SequenceEqual(new[] { "ACPI\\PNP0C14\\H19P_0", "ACPI\\PNP0C14\\0_0" }), "WMI instance names or chained nodes were decoded incorrectly.");
  }
  private static void NativeInstanceOffsetsAreValidated() {
    Throws<InvalidDataException>(() => KernelBiosSession.ReadInstanceNames(new byte[63], 63));
    byte[] block = InstanceBlock("instance"); Put32(block, 64, uint.MaxValue);
    Throws<InvalidDataException>(() => KernelBiosSession.ReadInstanceNames(block, block.Length));
    block = InstanceBlock("instance"); Put32(block, 52, uint.MaxValue);
    Throws<InvalidDataException>(() => KernelBiosSession.ReadInstanceNames(block, block.Length));
    block = InstanceBlock("instance"); Put32(block, 12, 4);
    Throws<InvalidDataException>(() => KernelBiosSession.ReadInstanceNames(block, block.Length));
    block = InstanceBlock("instance"); block[68] = 255;
    Throws<InvalidDataException>(() => KernelBiosSession.ReadInstanceNames(block, block.Length));
  }
  private static void NativeFallbackProbesBeforeWriting() {
    var session = new NativeSession(); var client = new NativeBiosWmi(() => session);
    byte[] result = client.Execute(0x20008, 0x2E, new byte[] { 30, 30 }, 0, () => true);
    Check(result.Length == 0 && session.Calls.Count == 2 && session.Calls[0].Type == 0x28 && session.Calls[0].Method == 3 &&
      session.Calls[1].Type == 0x2E && session.Calls[1].Method == 1, "Native control bypassed read-only endpoint validation.");
    Check(session.Disposed, "Native WMI handle was not released.");
  }
  private static void NativeFallbackRejectsUnverifiedEndpoint() {
    var session = new NativeSession { OnInvoke = call => new byte[call.OutputBytes] };
    var client = new NativeBiosWmi(() => session);
    Throws<IOException>(() => client.Execute(0x20008, 0x27, new byte[] { 1 }, 0, () => true));
    Check(session.Calls.Count == 1 && session.Calls[0].Type == 0x28 && session.Disposed, "A zero-filled probe permitted a hardware write.");
  }
  private static void NativeFallbackHonorsFirmwareRejection() {
    var session = new NativeSession { OnInvoke = call => {
      byte[] output = GoodNativeResponse(call);
      if (call.Type == 0x2E) Put32(output, 4, 5);
      return output;
    } };
    var client = new NativeBiosWmi(() => session);
    Check(Throws<BiosReturnCodeException>(() => client.Execute(0x20008, 0x2E, new byte[] { 30, 30 }, 0, () => true)).ReturnCode == 5,
      "Native transport swallowed the BIOS return code.");
    Check(session.Calls.Count == 2 && session.Disposed, "A rejected native write was repeated or its handle leaked.");
  }
  private static void NativeFallbackCancelsStaleCommands() {
    foreach (bool cancelDuringProbe in new[] { false, true }) {
      bool current = true;
      var session = new NativeSession();
      if (!cancelDuringProbe) session.OnDiscover = () => current = false;
      else session.OnInvoke = call => { current = false; return GoodNativeResponse(call); };
      var client = new NativeBiosWmi(() => session);
      Check(client.Execute(0x20008, 0x2E, new byte[] { 30, 30 }, 0, () => current) == null, "A superseded native command was applied.");
      Check(session.Calls.All(call => call.Type == 0x28) && session.Disposed, "A stale command escaped discovery/probe cancellation.");
    }
  }
  private static void NativeTransportFailureForcesRevalidation() {
    var sessions = new List<NativeSession>(); bool fail = true;
    var client = new NativeBiosWmi(() => {
      var session = new NativeSession { OnInvoke = call => {
        if (call.Type == 0x2D && fail) { fail = false; throw new Win32Exception(4201); }
        return GoodNativeResponse(call);
      } };
      sessions.Add(session); return session;
    });
    Throws<Win32Exception>(() => client.Execute(0x20008, 0x2D, new byte[4], 128, () => true));
    client.Execute(0x20008, 0x2D, new byte[4], 128, () => true);
    Check(sessions.All(s => s.Disposed && s.Calls[0].Type == 0x28), "A failed native endpoint was reused without validation.");
  }
  private static void NativeAccessDenialIsPreserved() {
    int attempts = 0;
    var client = new NativeBiosWmi(() => { attempts++; throw new Win32Exception(5); });
    var error = Throws<Win32Exception>(() => client.Execute(0x20008, 0x27, new byte[] { 1 }, 0, () => true));
    Check(error.NativeErrorCode == 5 && attempts == 1, "Access denial was retried or misreported as BIOS acceptance.");
  }
  private static void NativeVerifiedEndpointAvoidsRepeatedProbes() {
    var sessions = new List<NativeSession>();
    var client = new NativeBiosWmi(() => { var session = new NativeSession(); sessions.Add(session); return session; });
    client.Execute(0x20008, 0x10, new byte[4], 4, () => true);
    client.Execute(0x20008, 0x2E, new byte[] { 30, 30 }, 0, () => true);
    Check(sessions.Sum(s => s.Discoveries) == 1 && sessions.SelectMany(s => s.Calls).Count(c => c.Type == 0x28) == 1,
      "A verified endpoint was needlessly probed for every command.");
    Check(sessions.All(s => s.Disposed), "Cached endpoint leaked native handles.");
  }
}
