using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace NvimCore;

/// <summary>
/// Incremental msgpack stream decoder for Neovim's RPC byte stream (recursive descent).
/// 
/// Usage:
///   var dec = new MsgpackStreamDecoder();
///   ... on each socket read: ...
///     dec.Append(buffer, 0, bytesRead);
///     while (dec.TryDecodeMessage(out object? msg)) { Dispatch(msg); }
/// 
/// The decoder never throws for incomplete frames. When data runs out mid-message it rewinds
/// and TryDecodeMessage returns false; the next Append resumes parsing where it left off.
/// 
/// Decoded shapes: long, bool, string (UTF-8), object?[] arrays, Dictionary<string,object?> maps.
/// </summary>
public sealed class MsgpackStreamDecoder
{
    private byte[] _buf = new byte[4096];
    private int _len;   // valid bytes in _buf
    private int _pos;   // read cursor

    public void Append(byte[] data, int offset, int count)
    {
        // Compact any unconsumed tail to the front of the buffer, then append new bytes after it.
        // (Bug history: previously _len/_pos were left stale here, so a second socket read while a
        // partial message was pending would re-introduce already-consumed bytes as live stream data.)
        var remaining = _len - _pos;
        if (remaining > 0 && _pos > 0)
            Buffer.BlockCopy(_buf, _pos, _buf, 0, remaining);

        _len = remaining;
        _pos = 0;

        if (_len + count > _buf.Length)
            Array.Resize(ref _buf, Math.Max(4096, (_len + count) * 2));
        Buffer.BlockCopy(data, offset, _buf, _len, count);
        _len += count;
    }

    public bool HasPendingData => _len > _pos;
    
    /// <summary>Bytes currently buffered but not consumed (for diagnostics).</summary>
    public int BufferedBytes => _len - _pos;

    /// <summary>Try to decode one complete msgpack value from the buffer.</summary>
    public bool TryDecodeMessage(out object? msg)
    {
        if (_pos >= _len) 
        { 
            msg = null!; 
            return false; 
        }
        
        int startPos = _pos;
        try
        {
            msg = ReadValue();
            return true;
        }
        catch (InvalidOperationException)
        {
            // Incomplete message: rewind so the next Append continues parsing here.
            _pos = startPos;
            msg = null!;
            return false;
        }
    }

    private object? ReadValue()
    {
        byte t = Peek();
        if (System.Environment.GetEnvironmentVariable("MP_TRACE") != null)
            System.IO.File.AppendAllText(@"C:\Users\knt41\nvim-winui-gui\tmp-test\cs_trace.log", $"pos={_pos} tag=0x{t:X2}\n");

        // Positive fixint (0x00-0x7F): values 0..127.
        // BUG HISTORY: this used to be `(t & 0xE0) == 0x00`, which only matched 0x00-0x1F and
        // threw "Unsupported msgpack type tag" for every integer 32..127 — exactly the column
        // indices inside redraw grid_line batches (e.g. col=69 → 0x45). Spec range is t < 0x80.
        if (t < 0x80)
        {
            Consume(1);
            return (long)t;
        }

        // Negative fixint (0xE0-0xFF): -1 to -32.
        if (t >= 0xE0)
        {
            Consume(1);
            return (long)(sbyte)t;
        }

        // Fixmap (0x80-0x8F): map with n entries.
        if ((t & 0xF0) == 0x80)
        {
            int count = t & 0x0F;
            Consume(1);
            return ReadMapEntries(count);
        }

        // Fixarray (0x90-0x9F): array with n elements.
        if ((t & 0xF0) == 0x90)
        {
            int count = t & 0x0F;
            Consume(1);
            return ReadArrayElements(count);
        }

        // Fixstr (0xA0-0xBF): string with n characters.
        if ((t & 0xE0) == 0xA0)
        {
            int count = t & 0x1F;
            Consume(1);
            byte[] raw = ReadRaw(count);
            return Encoding.UTF8.GetString(raw);
        }

        // Single-byte types + multi-byte type markers. The marker byte itself must be
        // consumed before reading any payload bytes below (each case reads its payload).
        Consume(1);
        switch (t)
        {
            case 0xC0: return null!;                    // nil
            case 0xC2: return false;                   // bool false
            case 0xC3: return true;                    // bool true

            // bin8 / bin16 / bin32 - treat as UTF-8 string.
            case 0xC4: { int n = ReadByte(); byte[] raw = ReadRaw(n); return Encoding.UTF8.GetString(raw); }
            case 0xC5: { ushort n16 = ReadBE16U(); byte[] raw = ReadRaw(n16); return Encoding.UTF8.GetString(raw); }
            case 0xC6: { uint n32 = ReadBE32U(); byte[] raw = ReadRaw((int)n32); return Encoding.UTF8.GetString(raw); }

            // str8 / str16 / str32 (nvim uses these for any string > 31 chars).
            case 0xD9: { int n = ReadByte(); byte[] raw = ReadRaw(n); return Encoding.UTF8.GetString(raw); }
            case 0xDA: { ushort n16 = ReadBE16U(); byte[] raw = ReadRaw(n16); return Encoding.UTF8.GetString(raw); }
            case 0xDB: { uint n32 = ReadBE32U(); byte[] raw = ReadRaw((int)n32); return Encoding.UTF8.GetString(raw); }

            // Signed ints.
            case 0xD0: { byte b = ReadByte(); return (long)(sbyte)b; }                              // int8
            case 0xD1: { ushort u = ReadBE16U(); return (long)unchecked((short)u); }              // int16
            case 0xD2: { uint u32 = ReadBE32U(); return (long)unchecked((int)u32); }              // int32
            case 0xD3: { uint h = ReadBE32U(); uint l2 = ReadBE32U(); long v64 = unchecked((long)(h * 4294967296UL + l2)); return (long)unchecked(((ulong)v64 << 1) >> 1); } // int64

            // Unsigned ints.
            case 0xCC: { byte b = ReadByte(); return (long)b; }                                     // uint8
            case 0xCD: { ushort u16 = ReadBE16U(); return (long)u16; }                                // uint16
            case 0xCE: { uint u32 = ReadBE32U(); return (long)u32; }                               // uint32
            case 0xCF: { ulong v = unchecked((ulong)ReadBE32U() * 4294967296UL + ReadBE32U()); if (v <= ulong.MaxValue - 1) return (long)v; throw new NotSupportedException("uint64 too large"); }

            // Extended types (ext8/ext16/ext32): [type-id byte][payload]. Surface raw; UI protocol never interprets them.
            case 0xC7: { int nExt = ReadByte(); return new MsgpackExt(ReadByte(), ReadRaw(nExt)); }          // ext8
            case 0xC8: { ushort n16e = ReadBE16U(); return new MsgpackExt(ReadByte(), ReadRaw(n16e)); }      // ext16
            case 0xC9: { uint n32e = ReadBE32U(); if (n32e > int.MaxValue) throw new NotSupportedException("ext32 too large"); return new MsgpackExt(ReadByte(), ReadRaw((int)n32e)); } // ext32
            case 0xCA: { byte[] r32 = ReadRaw(4); return System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(r32.AsSpan()); } // float32
            case 0xCB: { byte[] r64 = ReadRaw(8); return System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(r64.AsSpan()); } // float64

            // Extended arrays/maps (needed for large redraw batches).
            case 0xDC: { ushort n16 = ReadBE16U(); return ReadArrayElements(n16); }                // array16
            case 0xDD: { uint n32 = ReadBE32U(); if (n32 > int.MaxValue) throw new NotSupportedException("array32 too large"); return ReadArrayElements((int)n32); } // array32
            case 0xDE: { ushort n16m = ReadBE16U(); return ReadMapEntries(n16m); }                 // map16
            case 0xDF: { uint n32m = ReadBE32U(); if (n32m > int.MaxValue) throw new NotSupportedException("map32 too large"); return ReadMapEntries((int)n32m); }   // map32

            default:
                {
                    // Include cursor position + surrounding bytes so a desync is locatable in one run.
                    int p = _pos;
                    var ctx = new System.Text.StringBuilder();
                    for (int i = Math.Max(0, p - 8); i < Math.Min(_len, p + 16); i++)
                        ctx.Append(_buf[i].ToString("X2")).Append(' ');
                    throw new NotSupportedException($"Unsupported msgpack type tag 0x{t:X2} at pos={p} (of {_len}); context: {ctx.ToString().TrimEnd()}");
                }
        }
    }

    private object?[] ReadArrayElements(int count)
    {
        var arr = new object?[count];
        for (int i = 0; i < count; i++)
            arr[i] = ReadValue();
        return arr;
    }

    private Dictionary<string, object?> ReadMapEntries(int count)
    {
        var dict = new Dictionary<string, object?>(count);
        for (int i = 0; i < count; i++)
        {
            string key = ConvertMapKey(ReadValue());
            dict[key] = ReadValue();
        }
        return dict;
    }

    /// <summary>Convert a map key object to its string representation.</summary>
    private static string ConvertMapKey(object? val) => val switch
    {
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        string s => s,
        null => "nil",
        _ => throw new NotSupportedException($"Unsupported map key type: {val?.GetType().Name ?? "null"}")
    };

    /// <summary>Peek the next byte without consuming it.</summary>
    private byte Peek()
    {
        if (_pos >= _len) 
            throw new InvalidOperationException("Insufficient data");
        
        return _buf[_pos];
    }

    /// <summary>Consume n bytes from cursor, checking for incomplete input.</summary>
    private void Consume(int n)
    {
        if (_len - _pos < n) 
            throw new InvalidOperationException("Insufficient data");
        
        _pos += n;
    }

    /// <summary>Read a single byte and advance the cursor.</summary>
    private byte ReadByte()
    {
        byte b = Peek();
        Consume(1);
        return b;
    }

    /// <summary>Read exactly n raw bytes into an array, advancing the cursor.</summary>
    private byte[] ReadRaw(int count)
    {
        if (_len - _pos < count)
            throw new InvalidOperationException("Insufficient data");

        var result = new byte[count];
        Array.Copy(_buf, _pos, result, 0, count);
        Consume(count);
        return result;
    }

    /// <summary>Read a big-endian uint16.</summary>
    private ushort ReadBE16U()
    {
        byte b0 = ReadByte();
        byte b1 = ReadByte();
        return (ushort)((b0 << 8) | b1);
    }

    /// <summary>Read a big-endian uint32.</summary>
    private uint ReadBE32U()
    {
        byte b0 = ReadByte();
        byte b1 = ReadByte();
        byte b2 = ReadByte();
        byte b3 = ReadByte();

        return (uint)((b0 << 24) | (b1 << 16) | (b2 << 8) | b3);
    }

    /// <summary>Raw msgpack extended type: numeric type id + opaque payload bytes.</summary>
    public sealed record MsgpackExt(long TypeId, byte[] Data);
}
