using System;
using System.Buffers.Binary;

namespace Nightcall;

public static class AudioChannels
{
    // Compatibility only: legacy clients still provide 16 kHz mono PCM.
    // Native Opus clients provide genuine 48 kHz stereo directly.
    public static byte[] Stereo48(byte[] mono16)
    {
        if ((mono16.Length & 1) != 0) throw new ArgumentException("PCM must contain complete samples.");
        var stereo = new byte[mono16.Length * 6];
        for (int i = 0; i < mono16.Length / 2; i++)
        {
            short a = BinaryPrimitives.ReadInt16LittleEndian(mono16.AsSpan(2 * i));
            short b = i + 1 < mono16.Length / 2 ? BinaryPrimitives.ReadInt16LittleEndian(mono16.AsSpan(2 * (i + 1))) : a;
            for (int phase = 0; phase < 3; phase++)
            {
                short sample = (short)((a * (3 - phase) + b * phase) / 3);
                int offset = (i * 3 + phase) * 4;
                BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(offset), sample);
                BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(offset + 2), sample);
            }
        }
        return stereo;
    }
}
