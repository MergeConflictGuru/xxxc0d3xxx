using System;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Nightcall;

// Use an installed cable's render endpoint. Its capture endpoint is the Windows microphone.
// Never fall back to speakers: that would unexpectedly play private microphone audio aloud.
public sealed class VirtualMicrophone : IDisposable
{
    private readonly object gate = new();
    private WasapiOut? output;
    private MMDevice? device;
    private BufferedWaveProvider? buffer;
    private bool disposed;
    public string Status { get; private set; } = "Virtual microphone starts when a phone connects";
    public void Start()
    {
        lock (gate)
        {
            if (disposed || output != null) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .FirstOrDefault(x => x.DeviceFriendlyName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase));
                if (device == null) { Status = "Virtual microphone requires an installed VB-Audio Cable; dictation still works"; return; }
                buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
                    { BufferDuration = TimeSpan.FromMilliseconds(200), DiscardOnBufferOverflow = true, ReadFully = true };
                output = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
                output.Init(buffer); output.Play(); Status = "Windows microphone: CABLE Output";
            }
            catch (Exception e) { Stop(); Status = "Virtual microphone unavailable: " + e.Message; }
        }
    }
    public void Push(byte[] pcm) => PushStereo(AudioChannels.Stereo48(pcm));
    public void PushStereo(byte[] pcm) { lock (gate) buffer?.AddSamples(pcm, 0, pcm.Length); }
    public void Stop()
    {
        lock (gate)
        {
            output?.Stop(); output?.Dispose(); output = null;
            device?.Dispose(); device = null; buffer = null;
        }
    }
    public void Dispose() { lock (gate) { disposed = true; Stop(); } }
}
