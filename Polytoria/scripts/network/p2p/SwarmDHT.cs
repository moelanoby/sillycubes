// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Networking.P2P;

/// <summary>
/// Distributed Hash Table (DHT) for P2P swarm coordination.
/// Stores peer information, object ownership, and swarm state.
/// </summary>
public class SwarmDHT
{
    private const int ReplicationFactor = 3;
    private const int StaleTimeoutMs = 30000;
    private const int CleanupIntervalMs = 5000;

    private readonly P2PNetworkInstance _networkInstance;
    private readonly PeerClusterManager _clusterManager;
    private readonly ConcurrentDictionary<string, DHTEntry> _localStore = [];
    private readonly ConcurrentDictionary<string, DateTime> _lastAccess = [];
    private readonly string _localNodeId;
    private bool _running = false;
    private CancellationTokenSource? _cleanupCts;

    public event Action<string, byte[]?>? EntryUpdated;
    public event Action<string>? EntryRemoved;

    public int EntryCount => _localStore.Count;

    public SwarmDHT(P2PNetworkInstance networkInstance, PeerClusterManager clusterManager)
    {
        _networkInstance = networkInstance;
        _clusterManager = clusterManager;
        _localNodeId = Guid.NewGuid().ToString("N")[..16];

        _networkInstance.MessageReceived += OnMessageReceived;
    }

    /// <summary>
    /// Start the DHT and background cleanup.
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;

        _cleanupCts = new CancellationTokenSource();
        _ = Task.Run(() => CleanupLoop(_cleanupCts.Token));

        PT.Print($"[P2P-DHT] Started with node ID {_localNodeId[..8]}...");
    }

    /// <summary>
    /// Stop the DHT.
    /// </summary>
    public void Stop()
    {
        _running = false;
        _cleanupCts?.Cancel();
    }

    /// <summary>
    /// Store a value in the DHT.
    /// </summary>
    public void Put(string key, byte[] value, TimeSpan? ttl = null)
    {
        DHTEntry entry = new()
        {
            Key = key,
            Value = value,
            StoredAt = DateTime.UtcNow,
            Ttl = ttl,
            OriginNodeId = _localNodeId
        };

        _localStore[key] = entry;
        _lastAccess[key] = DateTime.UtcNow;

        // Replicate to other peers in swarm
        ReplicateEntry(entry);

        EntryUpdated?.Invoke(key, value);
    }

    /// <summary>
    /// Store a value with a string key and value.
    /// </summary>
    public void Put(string key, string value, TimeSpan? ttl = null)
    {
        Put(key, System.Text.Encoding.UTF8.GetBytes(value), ttl);
    }

    /// <summary>
    /// Store a JSON-serializable object.
    /// </summary>
    public void Put<T>(string key, T value, TimeSpan? ttl = null)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(value);
        Put(key, json, ttl);
    }

    /// <summary>
    /// Retrieve a value from the DHT.
    /// </summary>
    public async Task<byte[]?> Get(string key)
    {
        _lastAccess[key] = DateTime.UtcNow;

        // Check local store first
        if (_localStore.TryGetValue(key, out DHTEntry? localEntry))
        {
            if (!IsExpired(localEntry))
            {
                return localEntry.Value;
            }
            _localStore.TryRemove(key, out _);
        }

        // Query other peers in swarm
        return await QueryPeers(key);
    }

    /// <summary>
    /// Retrieve a string value from the DHT.
    /// </summary>
    public async Task<string?> GetString(string key)
    {
        byte[]? data = await Get(key);
        if (data != null)
        {
            return System.Text.Encoding.UTF8.GetString(data);
        }
        return null;
    }

    /// <summary>
    /// Retrieve and deserialize a JSON object from the DHT.
    /// </summary>
    public async Task<T?> Get<T>(string key)
    {
        string? json = await GetString(key);
        if (json != null)
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json);
        }
        return default;
    }

    /// <summary>
    /// Remove a key from the DHT.
    /// </summary>
    public void Remove(string key)
    {
        _localStore.TryRemove(key, out _);
        _lastAccess.TryRemove(key, out _);

        // Broadcast removal
        BroadcastRemoval(key);

        EntryRemoved?.Invoke(key);
    }

    /// <summary>
    /// Check if a key exists in the DHT.
    /// </summary>
    public async Task<bool> Exists(string key)
    {
        if (_localStore.ContainsKey(key))
        {
            return true;
        }

        byte[]? value = await Get(key);
        return value != null;
    }

    /// <summary>
    /// Get all keys matching a prefix.
    /// </summary>
    public List<string> GetKeysByPrefix(string prefix)
    {
        return _localStore.Keys
            .Where(k => k.StartsWith(prefix))
            .ToList();
    }

    /// <summary>
    /// Store object ownership information.
    /// </summary>
    public void SetObjectOwnership(string objectId, int peerId, string ownershipType = "primary")
    {
        string key = $"ownership:{objectId}:{ownershipType}";
        Put(key, BitConverter.GetBytes(peerId), TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Get object ownership information.
    /// </summary>
    public async Task<int> GetObjectOwnership(string objectId, string ownershipType = "primary")
    {
        string key = $"ownership:{objectId}:{ownershipType}";
        byte[]? data = await Get(key);
        if (data != null && data.Length >= 4)
        {
            return BitConverter.ToInt32(data, 0);
        }
        return 0;
    }

    /// <summary>
    /// Store peer information.
    /// </summary>
    public void StorePeerInfo(PeerInfo peer)
    {
        string key = $"peer:{peer.PeerId}";
        Put(key, peer, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Get peer information.
    /// </summary>
    public async Task<PeerInfo?> GetPeerInfo(int peerId)
    {
        string key = $"peer:{peerId}";
        return await Get<PeerInfo>(key);
    }

    /// <summary>
    /// Store swarm metadata.
    /// </summary>
    public void StoreSwarmMetadata(string swarmId, Dictionary<string, byte[]> metadata)
    {
        string key = $"swarm:{swarmId}:meta";
        Put(key, metadata, TimeSpan.FromSeconds(30));
    }

    private async Task<byte[]?> QueryPeers(string key)
    {
        List<PeerInfo> swarmPeers = _clusterManager.GetSwarmPeers();
        if (swarmPeers.Count == 0) return null;

        // Query closest peers first
        var queryTasks = swarmPeers
            .OrderBy(p => p.AverageLatency)
            .Take(3)
            .Select(peer => QuerySinglePeer(peer.PeerId, key));

        Task<byte[]?>[] tasks = [.. queryTasks];
        await Task.WhenAny(tasks);

        foreach (Task<byte[]?> task in tasks)
        {
            if (task.IsCompleted && task.Result != null)
            {
                return task.Result;
            }
        }

        return null;
    }

    private async Task<byte[]?> QuerySinglePeer(int peerId, string key)
    {
        try
        {
            byte[] queryKey = System.Text.Encoding.UTF8.GetBytes($"DHT_GET:{key}");
            _networkInstance.SendMessage(peerId, queryKey, TransferMode.Reliable);

            // Wait for response (simplified - real implementation would use proper request/response)
            await Task.Delay(100);

            // For now, just check if peer has the key
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void ReplicateEntry(DHTEntry entry)
    {
        List<PeerInfo> peers = _clusterManager.GetSwarmPeers();
        int replicateCount = Math.Min(ReplicationFactor, peers.Count);

        for (int i = 0; i < replicateCount; i++)
        {
            PeerInfo peer = peers[i];
            byte[] data = SerializeDHTMessage("PUT", entry);
            _networkInstance.SendMessage(peer.PeerId, data, TransferMode.Reliable);
        }
    }

    private void BroadcastRemoval(string key)
    {
        List<PeerInfo> peers = _clusterManager.GetSwarmPeers();
        byte[] data = SerializeDHTMessage("REMOVE", new DHTEntry { Key = key });

        foreach (PeerInfo peer in peers)
        {
            _networkInstance.SendMessage(peer.PeerId, data, TransferMode.Reliable);
        }
    }

    private void OnMessageReceived(int peerId, byte[] data, TransferMode transferMode)
    {
        if (data.Length < 4) return;

        // Check if it's a DHT message
        string header = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(4, data.Length));
        if (!header.StartsWith("DHT_")) return;

        HandleDHTMessage(peerId, data);
    }

    private void HandleDHTMessage(int peerId, byte[] data)
    {
        try
        {
            string text = System.Text.Encoding.UTF8.GetString(data);
            string[] parts = text.Split(':', 2);

            if (parts.Length < 2) return;

            string command = parts[0];
            string payload = parts[1];

            switch (command)
            {
                case "DHT_PUT":
                    DHTEntry? entry = DeserializeDHTEntry(payload);
                    if (entry != null && !IsExpired(entry))
                    {
                        _localStore[entry.Key] = entry;
                        _lastAccess[entry.Key] = DateTime.UtcNow;
                        EntryUpdated?.Invoke(entry.Key, entry.Value);
                    }
                    break;

                case "DHT_REMOVE":
                    if (_localStore.TryRemove(payload, out _))
                    {
                        _lastAccess.TryRemove(payload, out _);
                        EntryRemoved?.Invoke(payload);
                    }
                    break;

                case "DHT_GET":
                    if (_localStore.TryGetValue(payload, out DHTEntry? found) && !IsExpired(found))
                    {
                        byte[] response = SerializeDHTMessage("DHT_RESPONSE", found);
                        _networkInstance.SendMessage(peerId, response, TransferMode.Reliable);
                    }
                    break;

                case "DHT_RESPONSE":
                    // Handle response (simplified)
                    break;
            }
        }
        catch (Exception ex)
        {
            GD.PushError($"[P2P-DHT] Error handling message: {ex.Message}");
        }
    }

    private bool IsExpired(DHTEntry entry)
    {
        if (entry.Ttl == null) return false;
        return (DateTime.UtcNow - entry.StoredAt) > entry.Ttl.Value;
    }

    private byte[] SerializeDHTMessage(string command, DHTEntry entry)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(entry);
        return System.Text.Encoding.UTF8.GetBytes($"{command}:{json}");
    }

    private DHTEntry? DeserializeDHTEntry(string json)
    {
        return System.Text.Json.JsonSerializer.Deserialize<DHTEntry>(json);
    }

    private async Task CleanupLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupIntervalMs, ct);

                DateTime now = DateTime.UtcNow;
                List<string> expiredKeys = [];

                foreach (var (key, entry) in _localStore)
                {
                    if (IsExpired(entry))
                    {
                        expiredKeys.Add(key);
                    }
                }

                foreach (string key in expiredKeys)
                {
                    _localStore.TryRemove(key, out _);
                    _lastAccess.TryRemove(key, out _);
                    EntryRemoved?.Invoke(key);
                }

                // Cleanup stale access times
                List<string> staleAccess = _lastAccess
                    .Where(kvp => (now - kvp.Value).TotalMilliseconds > StaleTimeoutMs)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (string key in staleAccess)
                {
                    _lastAccess.TryRemove(key, out _);
                }

                if (expiredKeys.Count > 0)
                {
                    PT.Print($"[P2P-DHT] Cleaned up {expiredKeys.Count} expired entries");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                GD.PushError($"[P2P-DHT] Cleanup error: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Object ownership information stored in DHT.
/// </summary>
public class ObjectOwnership
{
    public string ObjectId { get; set; } = "";
    public int PrimaryPeerId { get; set; }
    public int[] ValidatorPeerIds { get; set; } = [];
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public long SequenceNumber { get; set; } = 0;
}
