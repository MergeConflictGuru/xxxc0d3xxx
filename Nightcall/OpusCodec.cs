using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nightcall;

public sealed class OpusCodec : IDisposable
{
    public const int Bitrate = 96000, Samples = 960, FrameBytes = 3840;
    public static readonly bool Available = NativeLibrary.TryLoad("nightcall_audio", Assembly.GetExecutingAssembly(), DllImportSearchPath.AssemblyDirectory, out _);
    private IntPtr decoder, monoDecoder, encoder;
    [DllImport("nightcall_audio", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr nightcall_decoder_create();
    [DllImport("nightcall_audio", CallingConvention = CallingConvention.Cdecl)] private static extern int nightcall_decode(IntPtr state, byte[] packet, int length, short[] pcm);
    [DllImport("nightcall_audio", CallingConvention = CallingConvention.Cdecl)] private static extern void nightcall_decoder_destroy(IntPtr state);
    [DllImport("nightcall_audio", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr nightcall_encoder_create();
    [DllImport("nightcall_audio", CallingConvention = CallingConvention.Cdecl)] private static extern int nightcall_encode(IntPtr state, short[] pcm, byte[] packet, int capacity);
    [DllImport("nightcall_audio", CallingConvention = CallingConvention.Cdecl)] private static extern void nightcall_encoder_destroy(IntPtr state);
    public byte[] Decode(byte[] packet)
    {
        if (!Available || packet.Length is < 1 or > 1275) throw new InvalidDataException("Invalid or unsupported Opus packet.");
        if (decoder == IntPtr.Zero) decoder = nightcall_decoder_create();
        if (decoder == IntPtr.Zero) throw new InvalidDataException("Cannot create the Opus decoder.");
        var pcm = new short[Samples * 2];
        if (nightcall_decode(decoder, packet, packet.Length, pcm) != Samples) throw new InvalidDataException("Invalid Opus frame duration or payload.");
        var bytes = new byte[FrameBytes]; Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length); return bytes;
    }
    [DllImport("nightcall_audio", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr nightcall_decoder_mono_create();
    [DllImport("nightcall_audio", CallingConvention = CallingConvention.Cdecl)] private static extern int nightcall_decode_mono(IntPtr state, byte[] packet, int length, short[] pcm);
    public byte[] DecodeMono(byte[] packet)
    {
        if (!Available || packet.Length is < 1 or > 1275) throw new InvalidDataException("Invalid or unsupported Opus packet.");
        if (monoDecoder == IntPtr.Zero) monoDecoder = nightcall_decoder_mono_create();
        var pcm = new short[320];
        if (monoDecoder == IntPtr.Zero || nightcall_decode_mono(monoDecoder, packet, packet.Length, pcm) != 320) throw new InvalidDataException("Invalid Opus frame.");
        var bytes = new byte[640]; Buffer.BlockCopy(pcm,0,bytes,0,bytes.Length); return bytes;
    }
    public byte[] Encode(byte[] bytes)
    {
        if (!Available || bytes.Length != FrameBytes) throw new InvalidDataException("Opus needs a 20 ms PCM frame.");
        if (encoder == IntPtr.Zero) encoder = nightcall_encoder_create();
        if (encoder == IntPtr.Zero) throw new InvalidDataException("Cannot create the Opus encoder.");
        var pcm = new short[Samples * 2]; Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
        var packet = new byte[1275]; int count = nightcall_encode(encoder, pcm, packet, packet.Length);
        if (count < 1) throw new InvalidDataException("Opus encoding failed.");
        return packet[..count];
    }
    public void Dispose()
    {
        if (decoder != IntPtr.Zero) nightcall_decoder_destroy(decoder);
        if (monoDecoder != IntPtr.Zero) nightcall_decoder_destroy(monoDecoder);
        if (encoder != IntPtr.Zero) nightcall_encoder_destroy(encoder);
        decoder = monoDecoder = encoder = IntPtr.Zero;
    }
}
