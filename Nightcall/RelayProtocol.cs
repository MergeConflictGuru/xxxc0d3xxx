using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nightcall;

// Wire format: big-endian length (including type), type byte, payload.
// Audio is 16 kHz, mono, signed PCM16 little-endian; each packet is <= 100 ms.
public static class RelayProtocol
{
    public const int Port = 47831, DiscoveryPort = 47832, Rate = 16000;
    public const byte Json = 1, Audio = 2, Opus = 3, Apk = 4, Clip = 5;
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static async Task Write(Stream stream, byte type, byte[] payload, CancellationToken cancel)
    {
        if (payload.Length > 4095) throw new InvalidDataException("Relay packet is too large.");
        byte[] frame = new byte[payload.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1); frame[4] = type;
        payload.CopyTo(frame, 5);
        await stream.WriteAsync(frame, cancel);
    }
    public static Task Send(Stream stream, object message, CancellationToken cancel) =>
        Write(stream, Json, JsonSerializer.SerializeToUtf8Bytes(message, Options), cancel);
    public static async Task<(byte Type, byte[] Data)> Read(Stream stream, CancellationToken cancel)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancel);
        int size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size < 2 || size > 4096) throw new InvalidDataException("Invalid relay packet length.");
        var frame = new byte[size]; await stream.ReadExactlyAsync(frame, cancel);
        if (frame[0] != Json && frame[0] != Audio && frame[0] != Opus && frame[0] != Apk && frame[0] != Clip) throw new InvalidDataException("Unknown relay packet type.");
        return (frame[0], frame[1..]);
    }
}
