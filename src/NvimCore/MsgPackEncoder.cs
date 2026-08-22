using System;
using System.Collections.Generic;
using System.IO;

namespace NvimCore;

/// <summary>
/// Minimal msgpack-RPC encoder for Neovim.
/// Wire format (verified against nvim 0.12 source, msgpack_rpc/channel.c):
///   Request:      [0, request_id:int, method:str, params:[...]]
///   Response:     [1, response_id:int, error:[type,msg]|null, result]
///   Notification: [2, method_name:str, args:[...]]  (3-element array!)
/// </summary>
public static class MsgPackEncoder
{
    public const int TypeRequest = 0;       // [0, id:int, method:str, params:[...]]
    public const int TypeResponse = 1;      // [1, response_id:int, error|null, result]
    public const int TypeNotification = 2;  // [2, method_name:str, args:[...]]

    /// <summary>Encode a request: [0, id, method, params].</summary>
    public static byte[] EncodeRequest(int id, string method, IList<object?>? args)
    {
        using var ms = new MemoryStream();
        WriteArrayHeader(ms, 4);
        WriteUint(ms, (uint)TypeRequest); // 0: positive fixint in nvim's stream
        WriteUint(ms, (uint)id);
        WriteString(ms, method);
        WriteArgs(ms, args ?? Array.Empty<object?>());
        return ms.ToArray();
    }

    /// <summary>Encode a notification: [2, method, params].</summary>
    public static byte[] EncodeNotification(string method, IList<object?>? args)
    {
        using var ms = new MemoryStream();
        WriteArrayHeader(ms, 3);
        WriteUint(ms, (uint)TypeNotification); // 2: positive fixint in nvim's stream
        WriteString(ms, method);
        WriteArgs(ms, args ?? Array.Empty<object?>());
        return ms.ToArray();
    }

    private static void WriteArgs(MemoryStream ms, IList<object?> args)
    {
        WriteArrayHeader(ms, (uint)args.Count);
        foreach (var a in args)
            WriteValue(ms, a);
    }

    private static void WriteValue(MemoryStream ms, object? v)
    {
        switch (v)
        {
            case null:
                ms.WriteByte(0xC0); break;
            case bool b:
                ms.WriteByte(b ? (byte)0xC3 : (byte)0xC2); break;
            case int i: WriteUint(ms, (uint)i); break;
            case long l: if (l < int.MinValue || l > uint.MaxValue) throw new NotSupportedException("int64 not supported"); WriteUint(ms, unchecked((uint)(long)l)); break;
            case string s: WriteString(ms, s); break;
            case IDictionary<string, object?> map:
                {
                    // msgpack map with string keys (nvim UI option maps etc.)
                    if (map.Count <= 15) ms.WriteByte((byte)(0x80 | map.Count));
                    else if (map.Count <= 65535) { ms.WriteByte(0xDE); WriteBigEndian16(ms, (ushort)map.Count); }
                    else throw new NotSupportedException("Map too large for this encoder");
                    foreach (var kv in map)
                    {
                        WriteString(ms, kv.Key);
                        WriteValue(ms, kv.Value);
                    }
                    break;
                }
            case IList<object?> list:
                {
                    WriteArrayHeader(ms, (uint)list.Count);
                    foreach (var x in list) WriteValue(ms, x);
                    break;
                }
            default:
                throw new NotSupportedException($"Unsupported msgpack value type: {v.GetType().Name}");
        }
    }

    private static void WriteUint(MemoryStream ms, uint v)
    {
        if (v <= 0x7F)
        {
            ms.WriteByte((byte)v);
        }
        else if (v <= 0xFF)
        {
            ms.Write(new byte[] { 0xCC, unchecked((byte)(uint)v) });
        }
        else if (v <= 0xFFFF)
        {
            ms.WriteByte(0xCD);
            WriteBigEndian16(ms, (ushort)v);
        }
        else
        {
            ms.WriteByte(0xCE);
            WriteBigEndian32(ms, v);
        }
    }

    private static void WriteArrayHeader(MemoryStream ms, uint n)
    {
        if (n <= 15)
        {
            ms.WriteByte((byte)(0x90 | n));
        }
        else if (n <= 65535)
        {
            ms.WriteByte(0xDC);
            WriteBigEndian16(ms, (ushort)n);
        }
        else
        {
            ms.WriteByte(0xDD);
            WriteBigEndian32(ms, n);
        }
    }

    private static void WriteString(MemoryStream ms, string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= 31)
        {
            ms.WriteByte((byte)(0xA0 | bytes.Length));
        }
        else if (bytes.Length <= 255)
        {
            var buf = new byte[2];
            buf[0] = 0xD9;
            buf[1] = unchecked((byte)bytes.Length);
            ms.Write(buf);
        }
        else
        {
            throw new NotSupportedException("String too long for this encoder");
        }

        ms.Write(bytes, 0, bytes.Length);
    }

    // Write helpers (big-endian per msgpack spec).
    private static void WriteBigEndian16(MemoryStream ms, ushort v)
    {
        var b = new byte[2];
        b[0] = (byte)(v >> 8);
        b[1] = (byte)v;
        ms.Write(b, 0, 2);
    }

    private static void WriteBigEndian32(MemoryStream ms, uint v)
    {
        var b = new byte[4];
        b[0] = (byte)(v >> 24);
        b[1] = (byte)((v >> 16) & 0xFF);
        b[2] = (byte)((v >> 8) & 0xFF);
        b[3] = (byte)v;
        ms.Write(b, 0, 4);
    }
}
