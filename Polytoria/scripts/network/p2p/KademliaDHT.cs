// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Networking.P2P;

/// <summary>
/// Kademlia DHT implementation for peer discovery and distributed storage.
/// Uses XOR distance metric, k-buckets, and iterative lookups.
/// </summary>
public class KademliaDHT : IDisposable
{
    private const int NodeIdLength = 20; // 160-bit node IDs
    private const int K = 20;            // Bucket size
    private const int Alpha = 3;         // Concurrency parameter
    private const int ReplicationRadius = 5; // Replicate to 5 closest nodes
    private const int TokenRefreshMs = 3600000; // 1 hour
    private const int BucketRefreshMs = 3600000;
    private const int ValueExpiryMs = 86400000; // 24 hours
    private const int MaxMessageSize = 65507; // Max UDP datagram size

    private readonly byte[] _nodeId;
    private readonly UdpClient _udp;
    private readonly IPEndPoint _localEndPoint;
    private readonly KBucket[] _routingTable;
    private readonly ConcurrentDictionary<string, DHTEntry> _storage = [];
    private readonly ConcurrentDictionary<string, DateTime> _keyRefreshTimes = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DHTEntry?>> _pendingLookups = [];
    private CancellationTokenSource? _cts;
    private bool _running;

    public byte[] NodeId => _nodeId;
    public string NodeIdHex => Convert.ToHexString(_nodeId);
    public int Port => _localEndPoint.Port;
    public int NodeCount => CountNodes();

    public event Action? NodeDiscovered;
    public event Action<string, byte[]?>? ValueStored;
    public event Action<string, byte[]?>? ValueFound;
    public event Action<IPEndPoint>? PolytoriaNodeDiscovered;

    // SHA1("polytoria-dht-v1") — fixed info_hash for Polytoria node discovery
    private static readonly byte[] PolytoriaInfoHash =
    [
        0x0a, 0x9d, 0x72, 0x9b, 0xb6, 0xad, 0x6b, 0x74, 0x8e, 0x3c,
        0x12, 0xd6, 0x5f, 0x82, 0x1d, 0x4e, 0x32, 0xa9, 0xa1, 0xc2
    ];

    // Peers stored for the Polytoria info_hash (from announce_peer)
    private readonly ConcurrentDictionary<string, IPEndPoint> _polytoriaPeers = [];
    private readonly Random _krpcRandom = new();
    private byte[] _tokenSecret = GenerateNodeId(); // Rotated periodically

    // Default bootstrap nodes (BitTorrent DHT network)
    private static readonly (string Host, int Port)[] DefaultBootstrapNodes =
    [
        ("router.bittorrent.com", 6881),
        ("dht.transmissionbt.com", 6881),
        ("router.utorrent.com", 6881),
        ("dht.libtorrent.org", 25401)
    ];

    public KademliaDHT(int port = 0)
    {
        _nodeId = GenerateNodeId();
        _udp = new UdpClient(port);
        _localEndPoint = (IPEndPoint)_udp.Client.LocalEndPoint!;
        _routingTable = new KBucket[NodeIdLength * 8]; // 160 buckets

        for (int i = 0; i < _routingTable.Length; i++)
        {
            _routingTable[i] = new KBucket(K);
        }

        PT.Print($"[DHT] Node ID: {NodeIdHex[..16]}... on port {_localEndPoint.Port}");
    }

    /// <summary>
    /// Start the DHT node.
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();

        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(RoutingTableRefreshLoop);
        _ = Task.Run(PolytoriaLoop);

        PT.Print("[DHT] Started");
    }

    /// <summary>
    /// Stop the DHT node.
    /// </summary>
    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _udp.Close();
        PT.Print("[DHT] Stopped");
    }

    /// <summary>
    /// Bootstrap into the DHT network using known nodes.
    /// </summary>
    public async Task Bootstrap(string? customBootstrapHost = null, int customBootstrapPort = 0)
    {
        PT.Print("[DHT] Bootstrapping...");

        List<IPEndPoint> bootstrapEndpoints = [];

        if (customBootstrapHost != null)
        {
            try
            {
                IPAddress[] ips = await Dns.GetHostAddressesAsync(customBootstrapHost);
                bootstrapEndpoints.Add(new IPEndPoint(ips[0], customBootstrapPort));
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[DHT] Custom bootstrap failed: {ex.Message}");
            }
        }

        foreach (var (host, port) in DefaultBootstrapNodes)
        {
            try
            {
                IPAddress[] ips = await Dns.GetHostAddressesAsync(host);
                bootstrapEndpoints.Add(new IPEndPoint(ips[0], port));
            }
            catch
            {
                // Skip failed bootstrap nodes
            }
        }

        // Send FIND_NODE to bootstrap nodes to populate our routing table
        List<Task> tasks = [];
        foreach (IPEndPoint ep in bootstrapEndpoints)
        {
            tasks.Add(SendFindNode(ep, _nodeId));
        }

        await Task.WhenAll(tasks);

        int nodeCount = CountNodes();
        PT.Print($"[DHT] Bootstrapped with {nodeCount} nodes");

        // Do an iterative lookup on our own ID to find nearby nodes
        await IterativeFindNode(_nodeId);
    }

    /// <summary>
    /// Store a key-value pair in the DHT.
    /// </summary>
    public async Task Store(string key, byte[] value, TimeSpan? expiry = null)
    {
        byte[] keyHash = HashKey(key);

        DHTEntry entry = new()
        {
            Key = key,
            KeyHash = keyHash,
            Value = value,
            StoredAt = DateTime.UtcNow,
            ExpiresAt = expiry.HasValue ? DateTime.UtcNow + expiry.Value : DateTime.UtcNow.AddMilliseconds(ValueExpiryMs)
        };

        _storage[key] = entry;
        _keyRefreshTimes[key] = DateTime.UtcNow;

        // Find closest nodes and store there too
        List<KBucketNode> closest = FindClosestNodes(keyHash, ReplicationRadius);

        List<Task> tasks = [];
        foreach (KBucketNode node in closest)
        {
            tasks.Add(SendStore(node.EndPoint, keyHash, value));
        }

        await Task.WhenAll(tasks);

        ValueStored?.Invoke(key, value);
    }

    /// <summary>
    /// Store a string value.
    /// </summary>
    public async Task Store(string key, string value, TimeSpan? expiry = null)
    {
        await Store(key, Encoding.UTF8.GetBytes(value), expiry);
    }

    /// <summary>
    /// Store a JSON-serializable object.
    /// </summary>
    public async Task Store<T>(string key, T value, TimeSpan? expiry = null)
    {
        string json = JsonSerializer.Serialize(value);
        await Store(key, json, expiry);
    }

    /// <summary>
    /// Retrieve a value by key from the DHT.
    /// </summary>
    public async Task<byte[]?> Get(string key)
    {
        byte[] keyHash = HashKey(key);

        // Check local storage first
        if (_storage.TryGetValue(key, out DHTEntry? localEntry))
        {
            if (localEntry.ExpiresAt > DateTime.UtcNow)
            {
                ValueFound?.Invoke(key, localEntry.Value);
                return localEntry.Value;
            }
            _storage.TryRemove(key, out _);
        }

        // Iterative lookup
        return await IterativeFindValue(keyHash, key);
    }

    /// <summary>
    /// Get a string value.
    /// </summary>
    public async Task<string?> GetString(string key)
    {
        byte[]? data = await Get(key);
        return data != null ? Encoding.UTF8.GetString(data) : null;
    }

    /// <summary>
    /// Get and deserialize a JSON object.
    /// </summary>
    public async Task<T?> Get<T>(string key)
    {
        string? json = await GetString(key);
        return json != null ? JsonSerializer.Deserialize<T>(json) : default;
    }

    /// <summary>
    /// Get all keys in local storage that start with a given prefix.
    /// </summary>
    public List<string> GetKeysByPrefix(string prefix)
    {
        return _storage
            .Where(kvp => kvp.Key.StartsWith(prefix) && kvp.Value.ExpiresAt > DateTime.UtcNow)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// Remove a key from the DHT.
    /// </summary>
    public void Remove(string key)
    {
        _storage.TryRemove(key, out _);
        _keyRefreshTimes.TryRemove(key, out _);
    }

    /// <summary>
    /// Find the N closest nodes to a given key.
    /// </summary>
    public List<KBucketNode> FindClosest(byte[] keyHash, int count = K)
    {
        return FindClosestNodes(keyHash, count);
    }

    /// <summary>
    /// Get all nodes in the routing table.
    /// </summary>
    public List<KBucketNode> GetAllNodes()
    {
        List<KBucketNode> allNodes = [];
        foreach (KBucket bucket in _routingTable)
        {
            allNodes.AddRange(bucket.GetAllNodes());
        }
        return allNodes;
    }

    public void Dispose()
    {
        Stop();
        _udp.Dispose();
        _cts?.Dispose();
    }

    // === Kademlia RPC Methods ===

    private async Task ReceiveLoop()
    {
        while (_running)
        {
            try
            {
                UdpReceiveResult result = await _udp.ReceiveAsync();
                _ = Task.Run(() => HandleMessage(result.Buffer, result.RemoteEndPoint));
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_running) GD.PushError($"[DHT] Receive error: {ex.Message}");
            }
        }
    }

    private void HandleMessage(byte[] data, IPEndPoint remoteEndPoint)
    {
        if (data.Length < 1) return;

        // KRPC messages are bencoded dictionaries starting with 'd' (0x64)
        if (data[0] == 0x64)
        {
            HandleKrpcMessage(data, remoteEndPoint);
            return;
        }

        byte msgType = data[0];

        switch (msgType)
        {
            case 0x01: HandlePing(data, remoteEndPoint); break;
            case 0x02: HandleFindNode(data, remoteEndPoint); break;
            case 0x03: HandleFindNodeResponse(data, remoteEndPoint); break;
            case 0x04: HandleFindValue(data, remoteEndPoint); break;
            case 0x05: HandleFindValueResponse(data, remoteEndPoint); break;
            case 0x06: HandleStore(data, remoteEndPoint); break;
            case 0x07: HandleStoreResponse(data, remoteEndPoint); break;
        }
    }

    // === KRPC (BitTorrent Mainline DHT Protocol) ===

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>> _krpcPending = [];

    private void HandleKrpcMessage(byte[] data, IPEndPoint from)
    {
        try
        {
            var dict = (Dictionary<string, object>)Bencode.Decode(data);
            if (!dict.TryGetValue("y", out object? y)) return;

            switch ((string)y)
            {
                case "q": HandleKrpcQuery(dict, from); break;
                case "r": HandleKrpcResponse(dict, from); break;
            }
        }
        catch (Exception ex)
        {
            if (_running) GD.PushWarning($"[DHT] KRPC decode error from {from}: {ex.Message}");
        }
    }

    private void HandleKrpcQuery(Dictionary<string, object> dict, IPEndPoint from)
    {
        string? t = dict.GetValueOrDefault("t") as string;
        string? q = dict.GetValueOrDefault("q") as string;
        if (q == null) return;

        AddNode(from);

        var args = dict.GetValueOrDefault("a") as Dictionary<string, object>;

        switch (q)
        {
            case "ping":
                SendKrpcResponse(t, from, new Dictionary<string, object> { { "id", _nodeId } });
                break;

            case "find_node":
                HandleKrpcFindNode(t, args, from);
                break;

            case "get_peers":
                HandleKrpcGetPeers(t, args, from);
                break;

            case "announce_peer":
                HandleKrpcAnnouncePeer(t, args, from);
                break;
        }
    }

    private void HandleKrpcResponse(Dictionary<string, object> dict, IPEndPoint from)
    {
        string? t = dict.GetValueOrDefault("t") as string;
        if (t == null) return;

        AddNode(from);

        if (_krpcPending.TryRemove(t, out var tcs))
        {
            var r = dict.GetValueOrDefault("r") as Dictionary<string, object>;
            tcs.TrySetResult(r ?? []);
        }
    }

    private void HandleKrpcFindNode(string? t, Dictionary<string, object>? args, IPEndPoint from)
    {
        if (args == null) return;
        if (!TryGetBytes(args, "target", out byte[]? target) || target.Length != 20) return;

        List<KBucketNode> closest = FindClosestNodes(target, K);
        string nodes = EncodeCompactNodeList(closest);

        SendKrpcResponse(t, from, new Dictionary<string, object>
        {
            { "id", _nodeId },
            { "nodes", nodes }
        });
    }

    private void HandleKrpcGetPeers(string? t, Dictionary<string, object>? args, IPEndPoint from)
    {
        if (args == null) return;
        if (!TryGetBytes(args, "info_hash", out byte[]? infoHash) || infoHash.Length != 20) return;

        string token = GenerateToken(from);

        // If this is our Polytoria info_hash and we have peers, return them
        if (infoHash.SequenceEqual(PolytoriaInfoHash) && _polytoriaPeers.Count > 0)
        {
            var values = new List<object>();
            foreach (var peer in _polytoriaPeers.Values)
            {
                byte[] compact = CompactPeerInfo(peer);
                // Encode as raw bytes string (bencoded string = byte string in Latin1)
                // Bencode needs a string; we pass raw bytes via Latin1
                string peerStr = Encoding.Latin1.GetString(compact);
                values.Add(peerStr);
            }

            SendKrpcResponse(t, from, new Dictionary<string, object>
            {
                { "id", _nodeId },
                { "token", token },
                { "values", values }
            });
        }
        else
        {
            // Return close nodes instead
            List<KBucketNode> closest = FindClosestNodes(infoHash, K);
            string nodes = EncodeCompactNodeList(closest);

            SendKrpcResponse(t, from, new Dictionary<string, object>
            {
                { "id", _nodeId },
                { "token", token },
                { "nodes", nodes }
            });
        }
    }

    private void HandleKrpcAnnouncePeer(string? t, Dictionary<string, object>? args, IPEndPoint from)
    {
        if (args == null) return;

        if (!TryGetBytes(args, "info_hash", out byte[]? infoHash) || infoHash.Length != 20) return;
        if (!TryGetBytes(args, "id", out byte[]? _)) return;

        string token = args.GetValueOrDefault("token") as string ?? "";
        if (!VerifyToken(from, token))
        {
            SendKrpcError(t, from, 203, "bad token");
            return;
        }

        // If port is specified, use it; otherwise use the sender's port
        int port;
        if (args.TryGetValue("implied_port", out object? implied) && Convert.ToInt64(implied) == 1)
        {
            port = from.Port;
        }
        else
        {
            port = Convert.ToInt32(args.GetValueOrDefault("port", 0));
        }

        if (port <= 0) return;

        IPEndPoint peerEndPoint = new(from.Address, port);

        if (infoHash.SequenceEqual(PolytoriaInfoHash))
        {
            string peerKey = $"{from.Address}:{port}";
            if (_polytoriaPeers.TryAdd(peerKey, peerEndPoint))
            {
                StorePolytoriaDiscoveredServer(peerEndPoint);
                _ = Task.Run(() => PolytoriaNodeDiscovered?.Invoke(peerEndPoint));
            }
        }

        SendKrpcResponse(t, from, new Dictionary<string, object> { { "id", _nodeId } });
    }

    // === KRPC Helpers ===

    private void SendKrpcResponse(string? t, IPEndPoint to, Dictionary<string, object> r)
    {
        r["id"] = _nodeId;
        var msg = new Dictionary<string, object>
        {
            { "t", t ?? "tt" },
            { "y", "r" },
            { "r", r }
        };
        byte[] encoded = Bencode.Encode(msg);
        SendUdpSafe(encoded, to);
    }

    private void SendKrpcQuery(string queryName, Dictionary<string, object> args, IPEndPoint to)
    {
        string t = ((char)_krpcRandom.Next(32, 127)).ToString() + (char)_krpcRandom.Next(32, 127);
        var msg = new Dictionary<string, object>
        {
            { "t", t },
            { "y", "q" },
            { "q", queryName },
            { "a", args }
        };
        byte[] encoded = Bencode.Encode(msg);
        SendUdpSafe(encoded, to);
    }

    private void SendKrpcError(string? t, IPEndPoint to, int code, string message)
    {
        var msg = new Dictionary<string, object>
        {
            { "t", t ?? "tt" },
            { "y", "e" },
            { "e", new List<object> { (long)code, message } }
        };
        byte[] encoded = Bencode.Encode(msg);
        SendUdpSafe(encoded, to);
    }

    private string GenerateToken(IPEndPoint from)
    {
        // Token = SHA1(IP_bytes + secret) — standard BitTorrent DHT approach
        byte[] ipBytes = from.Address.GetAddressBytes();
        byte[] combined = new byte[ipBytes.Length + _tokenSecret.Length];
        ipBytes.CopyTo(combined, 0);
        _tokenSecret.CopyTo(combined, ipBytes.Length);
        byte[] hash = SHA1.HashData(combined);
        return Encoding.Latin1.GetString(hash);
    }

    private bool VerifyToken(IPEndPoint from, string token)
    {
        string expected = GenerateToken(from);
        return token == expected;
    }

    private static string EncodeCompactNodeList(List<KBucketNode> nodes)
    {
        byte[] result = new byte[nodes.Count * 26];
        int offset = 0;
        foreach (var node in nodes)
        {
            node.NodeId.CopyTo(result, offset);
            offset += 20;
            byte[] addr = node.EndPoint.Address.GetAddressBytes();
            // Pad to 4 bytes (IPv4)
            for (int i = 0; i < 4; i++)
                result[offset + i] = i < addr.Length ? addr[i] : (byte)0;
            offset += 4;
            // Port in big-endian (network byte order)
            result[offset] = (byte)(node.EndPoint.Port >> 8);
            result[offset + 1] = (byte)(node.EndPoint.Port & 0xFF);
            offset += 2;
        }
        return Encoding.Latin1.GetString(result);
    }

    private static byte[] CompactPeerInfo(IPEndPoint ep)
    {
        byte[] result = new byte[6];
        byte[] addr = ep.Address.GetAddressBytes();
        for (int i = 0; i < 4; i++)
            result[i] = i < addr.Length ? addr[i] : (byte)0;
        result[4] = (byte)(ep.Port >> 8);
        result[5] = (byte)(ep.Port & 0xFF);
        return result;
    }

    private static List<IPEndPoint> ParseCompactPeers(string data)
    {
        byte[] raw = Encoding.Latin1.GetBytes(data);
        var result = new List<IPEndPoint>();
        for (int i = 0; i + 6 <= raw.Length; i += 6)
        {
            byte[] ipBytes = [raw[i], raw[i + 1], raw[i + 2], raw[i + 3]];
            ushort port = (ushort)((raw[i + 4] << 8) | raw[i + 5]);
            result.Add(new IPEndPoint(new IPAddress(ipBytes), port));
        }
        return result;
    }

    private static List<KBucketNode> ParseCompactNodeList(string data)
    {
        byte[] raw = Encoding.Latin1.GetBytes(data);
        var result = new List<KBucketNode>();
        for (int i = 0; i + 26 <= raw.Length; i += 26)
        {
            byte[] nodeId = raw[i..(i + 20)];
            byte[] ipBytes = [raw[i + 20], raw[i + 21], raw[i + 22], raw[i + 23]];
            ushort port = (ushort)((raw[i + 24] << 8) | raw[i + 25]);
            IPEndPoint ep = new(new IPAddress(ipBytes), port);
            result.Add(new KBucketNode { NodeId = nodeId, EndPoint = ep, LastSeen = DateTime.UtcNow });
        }
        return result;
    }

    private static bool TryGetBytes(Dictionary<string, object> dict, string key, out byte[]? result)
    {
        if (dict.TryGetValue(key, out object? val) && val is string s)
        {
            result = Encoding.Latin1.GetBytes(s);
            return true;
        }
        result = null;
        return false;
    }

    private void SendUdpSafe(byte[] data, IPEndPoint target)
    {
        try { _udp.Send(data, data.Length, target); }
        catch { /* silently drop send failures */ }
    }

    // === Polytoria DHT Bootstrap & Discovery ===

    /// <summary>
    /// Stores a discovered Polytoria node in the local DHT as a server: entry
    /// so it appears in the WebServer "Other" section.
    /// </summary>
    private void StorePolytoriaDiscoveredServer(IPEndPoint ep)
    {
        string serverKey = $"server:dht:{ep.Address}:{ep.Port}";
        var serverInfo = new
        {
            code = serverKey,
            hostUsername = $"(DHT)",
            ip = ep.Address.ToString(),
            port = ep.Port,
            playerCount = 1,
            discovered = DateTime.UtcNow
        };
        byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(serverInfo);
        DHTEntry entry = new()
        {
            Key = serverKey,
            KeyHash = HashKey(serverKey),
            Value = json,
            StoredAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };
        _storage[serverKey] = entry;
    }

    /// <summary>
    /// Bootstraps into the BitTorrent Mainline DHT via KRPC, then
    /// periodically announces our node and discovers other Polytoria nodes.
    /// </summary>
    private async Task PolytoriaLoop()
    {
        // Wait for the node to be fully started
        await Task.Delay(2000);

        // Bootstrap into the BitTorrent DHT
        await KrpcBootstrap();

        while (_running)
        {
            try
            {
                // Rotate token secret hourly
                _tokenSecret = GenerateNodeId();

                // Announce ourselves on the Polytoria info_hash
                await AnnouncePolytoriaNode();

                // Discover other Polytoria nodes
                await DiscoverPolytoriaNodes();

                await Task.Delay(TimeSpan.FromMinutes(30), _cts?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                GD.PushWarning($"[DHT] Polytoria loop error: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(5), _cts?.Token ?? CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Sends a KRPC find_node to bootstrap nodes to populate our routing table
    /// and find nodes close to our Polytoria info_hash.
    /// </summary>
    private async Task KrpcBootstrap()
    {
        byte[] randomTarget = GenerateNodeId();

        foreach (var (host, port) in DefaultBootstrapNodes)
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host);
                IPEndPoint ep = new(addresses[0], port);

                var args = new Dictionary<string, object>
                {
                    { "id", _nodeId },
                    { "target", Encoding.Latin1.GetString(randomTarget) }
                };
                SendKrpcQuery("find_node", args, ep);

                // Repeat with our info_hash as the target
                var args2 = new Dictionary<string, object>
                {
                    { "id", _nodeId },
                    { "target", Encoding.Latin1.GetString(PolytoriaInfoHash) }
                };
                SendKrpcQuery("find_node", args2, ep);
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[DHT] Bootstrap to {host}:{port} failed: {ex.Message}");
            }

            await Task.Delay(500);
        }
    }

    /// <summary>
    /// Announces our node on the Polytoria info_hash so other clients discover us.
    /// Per the BitTorrent DHT spec: sends get_peers to obtain a write token,
    /// then sends announce_peer with that token.
    /// </summary>
    private async Task AnnouncePolytoriaNode()
    {
        List<KBucketNode> close = FindClosestNodes(PolytoriaInfoHash, K);
        if (close.Count == 0) return;

        foreach (var node in close)
        {
            string t = ((char)_krpcRandom.Next(32, 127)).ToString() + (char)_krpcRandom.Next(32, 127);
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            _krpcPending[t] = tcs;

            var args = new Dictionary<string, object>
            {
                { "id", _nodeId },
                { "info_hash", Encoding.Latin1.GetString(PolytoriaInfoHash) }
            };

            var getPeersMsg = new Dictionary<string, object>
            {
                { "t", t },
                { "y", "q" },
                { "q", "get_peers" },
                { "a", args }
            };
            byte[] encoded = Bencode.Encode(getPeersMsg);
            SendUdpSafe(encoded, node.EndPoint);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            if (completed == tcs.Task)
            {
                var r = tcs.Task.Result;
                if (r.TryGetValue("token", out object? rawToken) && rawToken is string token)
                {
                    var announceArgs = new Dictionary<string, object>
                    {
                        { "id", _nodeId },
                        { "info_hash", Encoding.Latin1.GetString(PolytoriaInfoHash) },
                        { "port", _localEndPoint.Port },
                        { "token", token },
                        { "implied_port", 0L }
                    };
                    SendKrpcQuery("announce_peer", announceArgs, node.EndPoint);
                }
            }

            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Queries known nodes for Polytoria peers using KRPC get_peers.
    /// Fires PolytoriaNodeDiscovered for each new peer found.
    /// </summary>
    private async Task DiscoverPolytoriaNodes()
    {
        List<KBucketNode> close = FindClosestNodes(PolytoriaInfoHash, K);
        if (close.Count == 0) return;

        foreach (var node in close)
        {
            try
            {
                string t = ((char)_krpcRandom.Next(32, 127)).ToString() + (char)_krpcRandom.Next(32, 127);
                var tcs = new TaskCompletionSource<Dictionary<string, object>>();
                _krpcPending[t] = tcs;

                var args = new Dictionary<string, object>
                {
                    { "id", _nodeId },
                    { "info_hash", Encoding.Latin1.GetString(PolytoriaInfoHash) }
                };

                string qT = t;
                var msg = new Dictionary<string, object>
                {
                    { "t", qT },
                    { "y", "q" },
                    { "q", "get_peers" },
                    { "a", args }
                };
                byte[] encoded = Bencode.Encode(msg);
                SendUdpSafe(encoded, node.EndPoint);

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                if (completed == tcs.Task)
                {
                    var r = tcs.Task.Result;
                    if (r.TryGetValue("values", out object? rawValues) && rawValues is List<object> values)
                    {
                        foreach (object v in values)
                        {
                            if (v is string peerStr)
                            {
                                List<IPEndPoint> peers = ParseCompactPeers(peerStr);
                                foreach (var peer in peers)
                                {
                                    string peerKey = $"{peer.Address}:{peer.Port}";
                                    if (!peer.Equals(_localEndPoint) && _polytoriaPeers.TryAdd(peerKey, peer))
                                    {
                                        StorePolytoriaDiscoveredServer(peer);
                                        PolytoriaNodeDiscovered?.Invoke(peer);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { /* skip node that failed */ }
        }
    }

    // PING
    private void HandlePing(byte[] data, IPEndPoint from)
    {
        AddNode(from);
        byte[] response = new byte[1 + NodeIdLength];
        response[0] = 0x01;
        Array.Copy(_nodeId, 0, response, 1, NodeIdLength);
        _udp.Send(response, response.Length, from);
    }

    // FIND_NODE
    private void HandleFindNode(byte[] data, IPEndPoint from)
    {
        if (data.Length < 1 + NodeIdLength) return;
        byte[] targetId = data[1..(1 + NodeIdLength)];
        AddNode(from);

        List<KBucketNode> closest = FindClosestNodes(targetId, K);
        byte[] response = BuildFindNodeResponse(closest);
        _udp.Send(response, response.Length, from);
    }

    private void HandleFindNodeResponse(byte[] data, IPEndPoint from)
    {
        if (data.Length < 1 + NodeIdLength) return;
        byte[] _ = data[1..(1 + NodeIdLength)]; // Target ID (unused in response)
        AddNode(from);

        List<KBucketNode> nodes = ParseNodeList(data, 1 + NodeIdLength);
        foreach (KBucketNode node in nodes)
        {
            AddNode(node.EndPoint, node.NodeId);
        }

        // Check if this is a response to our lookup
        string msgId = Convert.ToHexString(from.Address.GetAddressBytes()) + from.Port;
        if (_pendingLookups.TryRemove(msgId, out TaskCompletionSource<DHTEntry?>? tcs))
        {
            tcs.TrySetResult(null);
        }
    }

    // FIND_VALUE
    private void HandleFindValue(byte[] data, IPEndPoint from)
    {
        if (data.Length < 1 + NodeIdLength) return;
        byte[] keyHash = data[1..(1 + NodeIdLength)];
        AddNode(from);

        string key = FindKeyByHash(keyHash);

        if (key != null && _storage.TryGetValue(key, out DHTEntry? entry) && entry.ExpiresAt > DateTime.UtcNow)
        {
            // Found value
            byte[] response = new byte[1 + NodeIdLength + entry.Value.Length];
            response[0] = 0x05;
            Array.Copy(keyHash, 0, response, 1, NodeIdLength);
            Array.Copy(entry.Value, 0, response, 1 + NodeIdLength, entry.Value.Length);
            _udp.Send(response, response.Length, from);
        }
        else
        {
            // Return closest nodes
            List<KBucketNode> closest = FindClosestNodes(keyHash, K);
            byte[] response = BuildFindNodeResponse(closest);
            response[0] = 0x05; // FIND_VALUE response (no value)
            _udp.Send(response, response.Length, from);
        }
    }

    private void HandleFindValueResponse(byte[] data, IPEndPoint from)
    {
        AddNode(from);
        // Handled by pending lookup
    }

    // STORE
    private void HandleStore(byte[] data, IPEndPoint from)
    {
        if (data.Length < 1 + NodeIdLength + 4) return;
        AddNode(from);

        byte[] keyHash = data[1..(1 + NodeIdLength)];
        int valueLength = BitConverter.ToInt32(data, 1 + NodeIdLength);

        if (data.Length < 1 + NodeIdLength + 4 + valueLength) return;

        byte[] value = data[(1 + NodeIdLength + 4)..(1 + NodeIdLength + 4 + valueLength)];

        string key = FindKeyByHash(keyHash);
        if (key == null) key = Convert.ToHexString(keyHash);

        DHTEntry entry = new()
        {
            Key = key,
            KeyHash = keyHash,
            Value = value,
            StoredAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMilliseconds(ValueExpiryMs)
        };

        _storage[key] = entry;

        byte[] response = [0x07, 0x01]; // STORE response, success
        _udp.Send(response, response.Length, from);
    }

    private void HandleStoreResponse(byte[] data, IPEndPoint from)
    {
        AddNode(from);
    }

    // === Iterative Lookup ===

    private async Task IterativeFindNode(byte[] targetId)
    {
        List<KBucketNode> closest = FindClosestNodes(targetId, Alpha);
        HashSet<string> queried = [];
        List<KBucketNode> candidates = new(closest);

        while (candidates.Count > 0)
        {
            List<KBucketNode> toQuery = candidates
                .Where(n => !queried.Contains(n.EndPoint.ToString()))
                .Take(Alpha)
                .ToList();

            if (toQuery.Count == 0) break;

            List<Task> tasks = [];
            foreach (KBucketNode node in toQuery)
            {
                queried.Add(node.EndPoint.ToString());
                tasks.Add(SendFindNode(node.EndPoint, targetId));
            }

            await Task.WhenAll(tasks);

            candidates = FindClosestNodes(targetId, K);
        }
    }

    private async Task<byte[]?> IterativeFindValue(byte[] keyHash, string originalKey)
    {
        List<KBucketNode> closest = FindClosestNodes(keyHash, Alpha);
        HashSet<string> queried = [];

        int maxRounds = 5;
        for (int round = 0; round < maxRounds; round++)
        {
            List<KBucketNode> toQuery = closest
                .Where(n => !queried.Contains(n.EndPoint.ToString()))
                .Take(Alpha)
                .ToList();

            if (toQuery.Count == 0) break;

            List<Task<byte[]?>> tasks = [];
            foreach (KBucketNode node in toQuery)
            {
                queried.Add(node.EndPoint.ToString());
                tasks.Add(SendFindValue(node.EndPoint, keyHash));
            }

            byte[][] results = await Task.WhenAll(tasks);

            foreach (byte[]? result in results)
            {
                if (result != null && result.Length > NodeIdLength)
                {
                    byte[] value = result[NodeIdLength..];
                    ValueFound?.Invoke(originalKey, value);
                    return value;
                }
            }

            // Get more candidates
            List<KBucketNode> newCandidates = FindClosestNodes(keyHash, K);
            closest = newCandidates;
        }

        return null;
    }

    // === Network I/O ===

    private async Task SendFindNode(IPEndPoint target, byte[] lookupId)
    {
        byte[] message = new byte[1 + NodeIdLength];
        message[0] = 0x02;
        Array.Copy(_nodeId, 0, message, 1, NodeIdLength);

        _udp.Send(message, message.Length, target);
        await Task.Delay(50); // Small delay to avoid flooding
    }

    private async Task<byte[]?> SendFindValue(IPEndPoint target, byte[] keyHash)
    {
        byte[] message = new byte[1 + NodeIdLength];
        message[0] = 0x04;
        Array.Copy(keyHash, 0, message, 1, NodeIdLength);

        _udp.Send(message, message.Length, target);

        // Wait for response
        TaskCompletionSource<DHTEntry?> tcs = new();
        string msgId = Convert.ToHexString(target.Address.GetAddressBytes()) + target.Port;
        _pendingLookups[msgId] = tcs;

        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            if (completed == tcs.Task && tcs.Task.Result != null)
            {
                return tcs.Task.Result.Value;
            }
        }
        catch
        {
            _pendingLookups.TryRemove(msgId, out _);
        }

        return null;
    }

    private async Task SendStore(IPEndPoint target, byte[] keyHash, byte[] value)
    {
        byte[] message = new byte[1 + NodeIdLength + 4 + value.Length];
        message[0] = 0x06;
        Array.Copy(keyHash, 0, message, 1, NodeIdLength);
        BitConverter.GetBytes(value.Length).CopyTo(message, 1 + NodeIdLength);
        Array.Copy(value, 0, message, 1 + NodeIdLength + 4, value.Length);

        _udp.Send(message, message.Length, target);
        await Task.Delay(50);
    }

    // === Routing Table ===

    private void AddNode(IPEndPoint endPoint, byte[]? nodeId = null)
    {
        if (nodeId == null)
        {
            // Generate a pseudo-ID from the endpoint for basic tracking
            nodeId = HashKey(endPoint.ToString());
        }

        int bucketIndex = GetBucketIndex(nodeId);
        _routingTable[bucketIndex].AddOrRefresh(new KBucketNode
        {
            NodeId = nodeId,
            EndPoint = endPoint,
            LastSeen = DateTime.UtcNow
        });
    }

    private List<KBucketNode> FindClosestNodes(byte[] targetId, int count)
    {
        List<KBucketNode> allNodes = [];
        foreach (KBucket bucket in _routingTable)
        {
            allNodes.AddRange(bucket.GetAllNodes());
        }

        return allNodes
            .OrderBy(n => XorDistance(n.NodeId, targetId))
            .Take(count)
            .ToList();
    }

    private int GetBucketIndex(byte[] nodeId)
    {
        byte[] distance = XorDistanceBytes(_nodeId, nodeId);

        for (int i = 0; i < distance.Length; i++)
        {
            if (distance[i] != 0)
            {
                int bit = FindHighestBit(distance[i]);
                return i * 8 + bit;
            }
        }

        return _routingTable.Length - 1;
    }

    private int CountNodes()
    {
        int count = 0;
        foreach (KBucket bucket in _routingTable)
        {
            count += bucket.Count;
        }
        return count;
    }

    // === Utility ===

    private static byte[] GenerateNodeId()
    {
        byte[] id = new byte[NodeIdLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(id);
        return id;
    }

    public static byte[] HashKey(string key)
    {
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(key));
    }

    public static byte[] XorDistanceBytes(byte[] a, byte[] b)
    {
        byte[] result = new byte[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < result.Length; i++)
        {
            byte aByte = i < a.Length ? a[a.Length - 1 - i] : (byte)0;
            byte bByte = i < b.Length ? b[b.Length - 1 - i] : (byte)0;
            result[i] = (byte)(aByte ^ bByte);
        }
        return result;
    }

    public static BigInteger XorDistance(byte[] a, byte[] b)
    {
        byte[] distance = XorDistanceBytes(a, b);
        return new BigInteger(distance.Reverse().ToArray());
    }

    private static int FindHighestBit(byte b)
    {
        int bit = 7;
        while (bit >= 0 && (b & (1 << bit)) == 0) bit--;
        return bit;
    }

    private string FindKeyByHash(byte[] hash)
    {
        foreach (var (key, entry) in _storage)
        {
            if (entry.KeyHash.SequenceEqual(hash))
                return key;
        }
        return "";
    }

    private byte[] BuildFindNodeResponse(List<KBucketNode> nodes)
    {
        List<byte> response = [0x03];
        response.AddRange(_nodeId);

        foreach (KBucketNode node in nodes)
        {
            response.AddRange(node.NodeId);
            byte[] addrBytes = node.EndPoint.Address.GetAddressBytes();
            response.AddRange(addrBytes);
            response.AddRange(BitConverter.GetBytes((ushort)node.EndPoint.Port));
        }

        return [.. response];
    }

    private List<KBucketNode> ParseNodeList(byte[] data, int offset)
    {
        List<KBucketNode> nodes = [];
        int entrySize = NodeIdLength + 4 + 2; // ID + IP + Port

        while (offset + entrySize <= data.Length && nodes.Count < K)
        {
            byte[] nodeId = data[offset..(offset + NodeIdLength)];
            byte[] ipBytes = data[(offset + NodeIdLength)..(offset + NodeIdLength + 4)];
            ushort port = BitConverter.ToUInt16(data, offset + NodeIdLength + 4);

            IPAddress ip = new(ipBytes);
            IPEndPoint ep = new(ip, port);

            if (ep.Port > 0 && !ep.Equals(_localEndPoint))
            {
                nodes.Add(new KBucketNode
                {
                    NodeId = nodeId,
                    EndPoint = ep,
                    LastSeen = DateTime.UtcNow
                });
            }

            offset += entrySize;
        }

        return nodes;
    }

    private async Task RoutingTableRefreshLoop()
    {
        while (_running)
        {
            try
            {
                await Task.Delay(BucketRefreshMs, _cts?.Token ?? CancellationToken.None);

                // Refresh random buckets
                Random rng = new();
                for (int i = 0; i < 3; i++)
                {
                    int bucketIdx = rng.Next(_routingTable.Length);
                    if (_routingTable[bucketIdx].Count == 0)
                    {
                        byte[] randomId = GenerateNodeId();
                        randomId[0] = (byte)(bucketIdx / 8);
                        await IterativeFindNode(randomId);
                    }
                }

                // Refresh stored values
                foreach (var (key, _) in _keyRefreshTimes)
                {
                    if (_storage.TryGetValue(key, out DHTEntry? entry))
                    {
                        if (entry.ExpiresAt > DateTime.UtcNow)
                        {
                            List<KBucketNode> closest = FindClosestNodes(entry.KeyHash, ReplicationRadius);
                            foreach (KBucketNode node in closest)
                            {
                                await SendStore(node.EndPoint, entry.KeyHash, entry.Value);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                GD.PushError($"[DHT] Refresh error: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// A k-bucket in the Kademlia routing table.
/// </summary>
public class KBucket
{
    private readonly LinkedList<KBucketNode> _nodes = new();
    private readonly int _k;

    public int Count => _nodes.Count;

    public KBucket(int k)
    {
        _k = k;
    }

    public void AddOrRefresh(KBucketNode node)
    {
        LinkedListNode<KBucketNode>? existing = null;
        var comparer = new NodeIdComparer();
        for (var current = _nodes.First; current != null; current = current.Next)
        {
            if (comparer.Equals(current.Value, node))
            {
                existing = current;
                break;
            }
        }

        if (existing != null)
        {
            _nodes.Remove(existing);
            _nodes.AddFirst(node);
        }
        else if (_nodes.Count < _k)
        {
            _nodes.AddFirst(node);
        }
        else
        {
            // Bucket full - would normally ping least recently seen
            // For simplicity, just replace
            _nodes.RemoveLast();
            _nodes.AddFirst(node);
        }
    }

    public void Remove(IPEndPoint endPoint)
    {
        LinkedListNode<KBucketNode>? node = null;
        var comparer = new EndpointComparer();
        var searchNode = new KBucketNode { EndPoint = endPoint };
        for (var current = _nodes.First; current != null; current = current.Next)
        {
            if (comparer.Equals(current.Value, searchNode))
            {
                node = current;
                break;
            }
        }

        if (node != null)
        {
            _nodes.Remove(node);
        }
    }

    public List<KBucketNode> GetAllNodes()
    {
        return [.. _nodes];
    }
}

public class KBucketNode
{
    public byte[] NodeId { get; set; } = [];
    public IPEndPoint EndPoint { get; set; } = null!;
    public DateTime LastSeen { get; set; }
}

public class DHTEntry
{
    public string Key { get; set; } = "";
    public byte[] KeyHash { get; set; } = [];
    public byte[] Value { get; set; } = [];
    public DateTime StoredAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public TimeSpan? Ttl { get; set; }
    public string? OriginNodeId { get; set; }
}

public class NodeIdComparer : IEqualityComparer<KBucketNode>
{
    public bool Equals(KBucketNode? x, KBucketNode? y)
    {
        if (x == null || y == null) return false;
        return x.NodeId.SequenceEqual(y.NodeId);
    }

    public int GetHashCode(KBucketNode obj)
    {
        return obj.NodeId.GetHashCode();
    }
}

public class EndpointComparer : IEqualityComparer<KBucketNode>
{
    public bool Equals(KBucketNode? x, KBucketNode? y)
    {
        if (x == null || y == null) return false;
        return x.EndPoint.Equals(y.EndPoint);
    }

    public int GetHashCode(KBucketNode obj)
    {
        return obj.EndPoint.GetHashCode();
    }
}
