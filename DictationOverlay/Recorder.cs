using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DictationOverlay;

public sealed class Recorder : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Format
    {
        public ushort Tag, Channels;
        public uint Rate, BytesPerSecond;
        public ushort Align, Bits, Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Header
    {
        public IntPtr Data;
        public uint Length, Recorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }
    private delegate void Callback(IntPtr input, uint message, IntPtr instance, IntPtr param1, IntPtr param2);
    [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint device, ref Format format, Callback callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInStop(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
    private IntPtr device;
    private readonly Callback callback;
    private readonly List<(IntPtr Header, IntPtr Data)> buffers = new();
    private readonly MemoryStream pcm = new();
    private readonly object gate = new();
    private volatile bool recording;
    private bool disposed;
    private const uint HeaderSize = 48; // WAVEHDR on Windows x64
    public event Action<double>? Level;
    public double Peak { get; private set; }
    public TimeSpan Duration { get { lock (gate) return TimeSpan.FromSeconds(pcm.Length / 32000.0); } }
    public Recorder() { callback = OnAudio; }
    public void Start()
    {
        if (IntPtr.Size != 8) throw new InvalidOperationException("Use the Windows x64 build.");
        var format = new Format { Tag = 1, Channels = 1, Rate = 16000, BytesPerSecond = 32000, Align = 2, Bits = 16 };
        Check(waveInOpen(out device, uint.MaxValue, ref format, callback, IntPtr.Zero, 0x30000));
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var data = Marshal.AllocHGlobal(2048);
                var header = Marshal.AllocHGlobal((int)HeaderSize);
                Marshal.StructureToPtr(new Header { Data = data, Length = 2048 }, header, false);
                buffers.Add((header, data));
                Check(waveInPrepareHeader(device, header, HeaderSize));
                Check(waveInAddBuffer(device, header, HeaderSize));
            }
            recording = true;
            Check(waveInStart(device));
        }
        catch { Dispose(); throw; }
    }
    private void OnAudio(IntPtr input, uint message, IntPtr instance, IntPtr address, IntPtr unused)
    {
        if (message != 0x3C0 || address == IntPtr.Zero) return;
        try
        {
            var header = Marshal.PtrToStructure<Header>(address);
            var data = new byte[header.Recorded];
            if (data.Length > 0)
            {
                Marshal.Copy(header.Data, data, 0, data.Length);
                double sum = 0, peak = 0;
                for (int i = 0; i + 1 < data.Length; i += 2)
                {
                    double sample = (short)(data[i] | data[i + 1] << 8) / 32768.0;
                    sum += sample * sample;
                    peak = Math.Max(peak, Math.Abs(sample));
                }
                lock (gate) { if (pcm.Length < 32000L * 300) pcm.Write(data); Peak = Math.Max(Peak, peak); }
                Level?.Invoke(Math.Sqrt(sum / Math.Max(1, data.Length / 2)));
            }
            if (recording) waveInAddBuffer(input, address, HeaderSize);
        }
        catch { /* Exceptions must not cross a native callback boundary. */ }
    }
    public byte[] Stop()
    {
        recording = false;
        if (device != IntPtr.Zero) { waveInStop(device); waveInReset(device); }
        byte[] data;
        lock (gate) data = pcm.ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + data.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(16000); writer.Write(32000);
        writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(data.Length); writer.Write(data);
        return stream.ToArray();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; recording = false;
        if (device != IntPtr.Zero)
        {
            waveInStop(device); waveInReset(device);
            foreach (var pair in buffers) waveInUnprepareHeader(device, pair.Header, HeaderSize);
            waveInClose(device); device = IntPtr.Zero;
        }
        foreach (var pair in buffers) { Marshal.FreeHGlobal(pair.Header); Marshal.FreeHGlobal(pair.Data); }
        buffers.Clear(); pcm.Dispose();
    }
    private static void Check(uint result)
    {
        if (result != 0) throw new InvalidOperationException($"Cannot open the microphone (Windows audio error {result}). Check microphone permissions and the default recording device.");
    }
}
