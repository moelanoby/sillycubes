using Polytoria.Networking.P2P;
using System.Collections.Generic;
using System.Text;

namespace Polytoria.Tests;

public class DhtTest
{
    [Fact]
    public void Bencode_String_RoundTrip()
    {
        byte[] encoded = Bencode.Encode("hello");
        var decoded = Bencode.Decode(encoded);
        Assert.Equal("hello", decoded);
    }

    [Fact]
    public void Bencode_Integer_RoundTrip()
    {
        byte[] encoded = Bencode.Encode(42L);
        var decoded = Bencode.Decode(encoded);
        Assert.Equal(42L, decoded);
    }

    [Fact]
    public void Bencode_NegativeInteger_RoundTrip()
    {
        byte[] encoded = Bencode.Encode(-100L);
        var decoded = Bencode.Decode(encoded);
        Assert.Equal(-100L, decoded);
    }

    [Fact]
    public void Bencode_List_RoundTrip()
    {
        var list = new List<object> { "abc", 123L, "xyz" };
        byte[] encoded = Bencode.Encode(list);
        var decoded = Bencode.Decode(encoded);
        var result = Assert.IsType<List<object>>(decoded);
        Assert.Equal(3, result.Count);
        Assert.Equal("abc", result[0]);
        Assert.Equal(123L, result[1]);
        Assert.Equal("xyz", result[2]);
    }

    [Fact]
    public void Bencode_Dict_RoundTrip()
    {
        var dict = new Dictionary<string, object>
        {
            { "q", "ping" },
            { "t", "aa" },
            { "y", "q" }
        };
        byte[] encoded = Bencode.Encode(dict);
        var decoded = Bencode.Decode(encoded);
        var result = Assert.IsType<Dictionary<string, object>>(decoded);
        Assert.Equal("ping", result["q"]);
        Assert.Equal("aa", result["t"]);
        Assert.Equal("q", result["y"]);
    }

    [Fact]
    public void Bencode_Nested_RoundTrip()
    {
        var inner = new Dictionary<string, object>
        {
            { "id", "\x01\x02\x03\x04" }
        };
        var outer = new Dictionary<string, object>
        {
            { "t", "aa" },
            { "y", "q" },
            { "q", "ping" },
            { "a", inner }
        };
        byte[] encoded = Bencode.Encode(outer);
        var decoded = Bencode.Decode(encoded);
        var result = Assert.IsType<Dictionary<string, object>>(decoded);
        Assert.Equal("aa", result["t"]);
        Assert.Equal("q", result["y"]);
        Assert.Equal("ping", result["q"]);
        var args = Assert.IsType<Dictionary<string, object>>(result["a"]);
        Assert.Equal("\x01\x02\x03\x04", args["id"]);
    }

    [Fact]
    public void Bencode_RawBytes_RoundTrip()
    {
        byte[] raw = [0x00, 0x01, 0x02, 0xFF, 0xFE];
        string asStr = Encoding.Latin1.GetString(raw);
        byte[] encoded = Bencode.Encode(asStr);
        var decoded = Bencode.Decode(encoded);
        string result = Assert.IsType<string>(decoded);
        byte[] roundTripped = Encoding.Latin1.GetBytes(result);
        Assert.Equal(raw, roundTripped);
    }

    [Fact]
    public void Bencode_KrpcPingQuery()
    {
        var msg = new Dictionary<string, object>
        {
            { "t", "aa" },
            { "y", "q" },
            { "q", "ping" },
            { "a", new Dictionary<string, object> { { "id", "abcdefghij1234567890" } } }
        };
        byte[] encoded = Bencode.Encode(msg);
        // First byte must be 'd' (dictionary)
        Assert.Equal((byte)'d', encoded[0]);

        var decoded = Bencode.Decode(encoded);
        var result = Assert.IsType<Dictionary<string, object>>(decoded);
        Assert.Equal("aa", result["t"]);
        Assert.Equal("q", result["y"]);
        Assert.Equal("ping", result["q"]);
    }
}
