using System.Collections.Generic;
using System.Text;

namespace Polytoria.Networking.P2P;

public static class Bencode
{
    public static byte[] Encode(object value)
    {
        return value switch
        {
            string s => EncodeString(s),
            long i => EncodeInteger(i),
            int i => EncodeInteger(i),
            List<object> list => EncodeList(list),
            Dictionary<string, object> dict => EncodeDict(dict),
            byte[] raw => EncodeString(Encoding.Latin1.GetString(raw)),
            _ => Encoding.UTF8.GetBytes(value.ToString() ?? "")
        };
    }

    private static byte[] EncodeString(string s)
    {
        byte[] strBytes = Encoding.Latin1.GetBytes(s);
        byte[] prefix = Encoding.ASCII.GetBytes($"{strBytes.Length}:");
        byte[] result = new byte[prefix.Length + strBytes.Length];
        prefix.CopyTo(result, 0);
        strBytes.CopyTo(result, prefix.Length);
        return result;
    }

    private static byte[] EncodeInteger(long value)
    {
        return Encoding.ASCII.GetBytes($"i{value}e");
    }

    private static byte[] EncodeList(List<object> items)
    {
        List<byte> result = [(byte)'l'];
        foreach (var item in items)
            result.AddRange(Encode(item));
        result.Add((byte)'e');
        return [.. result];
    }

    private static byte[] EncodeDict(Dictionary<string, object> dict)
    {
        List<byte> result = [(byte)'d'];
        var sortedKeys = new List<string>(dict.Keys);
        sortedKeys.Sort();
        foreach (var key in sortedKeys)
        {
            result.AddRange(EncodeString(key));
            result.AddRange(Encode(dict[key]));
        }
        result.Add((byte)'e');
        return [.. result];
    }

    public static object Decode(byte[] data)
    {
        int pos = 0;
        return DecodeValue(data, ref pos);
    }

    private static object DecodeValue(byte[] data, ref int pos)
    {
        if (pos >= data.Length) throw new System.Exception("Unexpected end of data");

        char c = (char)data[pos];
        return c switch
        {
            'i' => DecodeInteger(data, ref pos),
            'l' => DecodeList(data, ref pos),
            'd' => DecodeDict(data, ref pos),
            >= '0' and <= '9' => DecodeString(data, ref pos),
            _ => throw new System.Exception($"Unknown bencode type: '{c}' at position {pos}")
        };
    }

    private static string DecodeString(byte[] data, ref int pos)
    {
        int colon = pos;
        while (colon < data.Length && data[colon] != ':')
            colon++;
        if (colon >= data.Length) throw new System.Exception("Invalid string: no colon found");

        int length = int.Parse(Encoding.ASCII.GetString(data, pos, colon - pos));
        pos = colon + 1;
        if (pos + length > data.Length) throw new System.Exception("String length exceeds data");

        string result = Encoding.Latin1.GetString(data, pos, length);
        pos += length;
        return result;
    }

    private static long DecodeInteger(byte[] data, ref int pos)
    {
        pos++; // skip 'i'
        int end = pos;
        while (end < data.Length && data[end] != 'e')
            end++;
        if (end >= data.Length) throw new System.Exception("Invalid integer: no 'e' found");

        string num = Encoding.ASCII.GetString(data, pos, end - pos);
        pos = end + 1;
        return long.Parse(num);
    }

    private static List<object> DecodeList(byte[] data, ref int pos)
    {
        pos++; // skip 'l'
        var result = new List<object>();
        while (pos < data.Length && data[pos] != 'e')
            result.Add(DecodeValue(data, ref pos));
        if (pos >= data.Length) throw new System.Exception("Invalid list: no 'e' found");
        pos++; // skip 'e'
        return result;
    }

    private static Dictionary<string, object> DecodeDict(byte[] data, ref int pos)
    {
        pos++; // skip 'd'
        var result = new Dictionary<string, object>();
        while (pos < data.Length && data[pos] != 'e')
        {
            string key = DecodeString(data, ref pos);
            result[key] = DecodeValue(data, ref pos);
        }
        if (pos >= data.Length) throw new System.Exception("Invalid dict: no 'e' found");
        pos++; // skip 'e'
        return result;
    }
}
