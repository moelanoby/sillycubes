// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using MemoryPack;
using Polytoria.Attributes;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Data;
using Polytoria.Datamodel.Services;
using Polytoria.Networking;
using Polytoria.Networking.P2P;
using Polytoria.Networking.RateLimiters;
using Polytoria.Shared;
using Polytoria.Utils;
using Polytoria.Utils.Compression;
using Polytoria.Utils.DTOs;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Networking.P2P;

/// <summary>
/// P2P Network Service that replaces the server-client model with peer-to-peer.
/// Uses the hybrid authority system for state management.
/// </summary>
public sealed partial class P2PNetworkService
{
    private const int ConsensusTimeoutMs = 3000;
    private const int MaxBroadcastPacketPerSec = 100;
    private const double BatchInterval = 0.05;

    private readonly P2PNetworkInstance _networkInstance;
    private readonly PeerClusterManager _clusterManager;
    private readonly SwarmDHT _dht;
    private readonly HybridAuthorityManager _authorityManager;

    private World _world = null!;
    private Players _players = null!;

    private readonly Dictionary<int, RateLimiters> _peerRateLimiters = [];
    private readonly Lock _rateLimiterLock = new();

    // Sync systems
    private readonly P2PTransformSync _transformSync;
    private readonly P2PPropSync _propSync;
    private readonly P2PReplicateSync _replicateSync;

    public event Action? PeerConnected;
    public event Action<int>? PeerDisconnected;
    public event Action<string, byte[]?>? StateUpdated;

    public P2PNetworkInstance NetworkInstance => _networkInstance;
    public PeerClusterManager ClusterManager => _clusterManager;
    public SwarmDHT DHT => _dht;
    public HybridAuthorityManager AuthorityManager => _authorityManager;

    public int LocalPeerId => _networkInstance.LocalPeerId;
    public bool IsListening => _networkInstance.IsListening;
    public int PeerCount => _networkInstance.PeerCount;

    public P2PNetworkService()
    {
        _networkInstance = new P2PNetworkInstance();
        _clusterManager = new PeerClusterManager(_networkInstance);
        _dht = new SwarmDHT(_networkInstance, _clusterManager);
        _authorityManager = new HybridAuthorityManager(_networkInstance, _clusterManager, _dht);

        _transformSync = new P2PTransformSync(this);
        _propSync = new P2PPropSync(this);
        _replicateSync = new P2PReplicateSync(this);

        // Wire up events
        _networkInstance.PeerConnected += OnPeerConnected;
        _networkInstance.PeerDisconnected += OnPeerDisconnected;
        _networkInstance.MessageReceived += OnMessageReceived;

        _authorityManager.StateUpdated += OnStateUpdated;
    }

    /// <summary>
    /// Initialize the P2P network service.
    /// </summary>
    public void Initialize(World world)
    {
        _world = world;
        _players = world.FindChild<Players>("Players")!;

        _clusterManager.Initialize();
        _dht.Start();

        PT.Print("[P2P] Network service initialized");
    }

    /// <summary>
    /// Start listening for incoming peer connections.
    /// </summary>
    public async Task StartListening(int port, bool useNatTraversal = false, string? stunServer = null)
    {
        if (useNatTraversal && stunServer != null)
        {
            await _networkInstance.ConnectWithNatTraversal(stunServer);
        }
        else
        {
            _networkInstance.StartListening(port);
        }

        PT.Print($"[P2P] Listening on port {port}");
    }

    /// <summary>
    /// Connect to another peer.
    /// </summary>
    public void ConnectToPeer(string address, int port)
    {
        _networkInstance.ConnectToPeer(address, port);
    }

    /// <summary>
    /// Connect using a peer endpoint (from discovery or DHT).
    /// </summary>
    public void ConnectToPeer(System.Net.IPEndPoint endpoint)
    {
        _networkInstance.ConnectToPeer(endpoint.Address.ToString(), endpoint.Port);
    }

    /// <summary>
    /// Disconnect from all peers.
    /// </summary>
    public void Disconnect()
    {
        _networkInstance.Shutdown();
        _dht.Stop();
    }

    /// <summary>
    /// Send RPC to a specific peer.
    /// </summary>
    public void RpcId(int targetPeerId, string methodName, params object?[] args)
    {
        byte[] data = SerializeRpc(methodName, args);
        _networkInstance.SendMessage(targetPeerId, data, TransferMode.Reliable);
    }

    /// <summary>
    /// Broadcast RPC to all connected peers.
    /// </summary>
    public void Rpc(string methodName, params object?[] args)
    {
        byte[] data = SerializeRpc(methodName, args);
        _networkInstance.BroadcastMessage(data, TransferMode.Reliable);
    }

    /// <summary>
    /// Broadcast to all peers in the local swarm.
    /// </summary>
    public void RpcToSwarm(string methodName, params object?[] args)
    {
        byte[] data = SerializeRpc(methodName, args);
        _clusterManager.BroadcastToSwarm(data, TransferMode.Reliable);
    }

    /// <summary>
    /// Route a message to a specific peer (possibly in another swarm).
    /// </summary>
    public void RouteMessage(int targetPeerId, byte[] data, TransferMode transferMode)
    {
        _clusterManager.RouteMessage(targetPeerId, data, transferMode);
    }

    /// <summary>
    /// Claim authority over a game object.
    /// </summary>
    public async Task<bool> ClaimObjectAuthority(string objectId, byte[]? initialState = null)
    {
        return await _authorityManager.ClaimAuthority(objectId, initialState);
    }

    /// <summary>
    /// Update object state with consensus.
    /// </summary>
    public async Task<bool> UpdateObjectState(string objectId, byte[] newState, bool requireConsensus = true)
    {
        return await _authorityManager.UpdateState(objectId, newState, requireConsensus);
    }

    /// <summary>
    /// Get object state.
    /// </summary>
    public byte[]? GetObjectState(string objectId)
    {
        return _authorityManager.GetState(objectId);
    }

    /// <summary>
    /// Store a value in the distributed DHT.
    /// </summary>
    public void DHTPut(string key, byte[] value, TimeSpan? ttl = null)
    {
        _dht.Put(key, value, ttl);
    }

    /// <summary>
    /// Retrieve a value from the distributed DHT.
    /// </summary>
    public async Task<byte[]?> DHTGet(string key)
    {
        return await _dht.Get(key);
    }

    /// <summary>
    /// Get the world instance.
    /// </summary>
    public World? GetWorld()
    {
        return _world;
    }

    /// <summary>
    /// Get all connected peer IDs.
    /// </summary>
    public List<int> GetConnectedPeers()
    {
        return _networkInstance.GetConnectedPeerIds();
    }

    /// <summary>
    /// Get peer information.
    /// </summary>
    public PeerInfo? GetPeerInfo(int peerId)
    {
        return _clusterManager.AllPeers.FirstOrDefault(p => p.PeerId == peerId);
    }

    /// <summary>
    /// Get network statistics.
    /// </summary>
    public P2PNetworkStats GetStats()
    {
        return _networkInstance.GetStats();
    }

    private void OnPeerConnected(int peerId)
    {
        PT.Print($"[P2P] Peer {peerId} connected");

        lock (_rateLimiterLock)
        {
            _peerRateLimiters[peerId] = new();
        }

        // Add player for this peer
        Player plr = Globals.LoadInstance<Player>(_world)!;
        plr.PeerID = peerId;
        plr.Name = $"Peer_{peerId}";
        plr.Parent = _players;

        // Store peer info in DHT
        PeerInfo? peerInfo = _clusterManager.AllPeers.FirstOrDefault(p => p.PeerId == peerId);
        if (peerInfo != null)
        {
            _dht.StorePeerInfo(peerInfo);
        }

        PeerConnected?.Invoke();
    }

    private void OnPeerDisconnected(int peerId)
    {
        PT.Print($"[P2P] Peer {peerId} disconnected");

        lock (_rateLimiterLock)
        {
            _peerRateLimiters.Remove(peerId);
        }

        // Remove player
        Player? plr = _players.GetPlayerFromPeerID(peerId);
        if (plr != null)
        {
            _players.PeerIDToPlayer.Remove(peerId);
            _players.InvokePlayerRemoved(plr);
            plr.ForceDelete();
        }

        // Rebalance authority
        _authorityManager.RebalanceAuthority();

        PeerDisconnected?.Invoke(peerId);
    }

    private void OnMessageReceived(int peerId, byte[] data, TransferMode transferMode)
    {
        try
        {
            // Rate limiting
            lock (_rateLimiterLock)
            {
                if (_peerRateLimiters.TryGetValue(peerId, out var rateLimiter))
                {
                    if (transferMode == TransferMode.Reliable)
                    {
                        if (!rateLimiter.Reliable.TryAccept())
                        {
                            return; // Rate limited
                        }
                    }
                    else
                    {
                        if (!rateLimiter.Unreliable.TryAccept())
                        {
                            return;
                        }
                    }
                }
            }

            // Check if this is a routed message
            if (MessageRouter.IsRoutedMessage(data))
            {
                // Already handled by PeerClusterManager
                return;
            }

            // Handle RPC messages
            HandleRpcMessage(peerId, data, transferMode);
        }
        catch (Exception ex)
        {
            GD.PushError($"[P2P] Error handling message: {ex.Message}");
        }
    }

    private void HandleRpcMessage(int peerId, byte[] data, TransferMode transferMode)
    {
        try
        {
            string json = System.Text.Encoding.UTF8.GetString(data);
            RpcMessage? message = JsonSerializer.Deserialize<RpcMessage>(json);

            if (message == null) return;

            // Find target object
            NetworkedObject? targetObj = _world.GetNetObjectFromID(message.TargetId);
            if (targetObj == null) return;

            // Find and invoke method
            MethodInfo? method = targetObj.GetType().GetMethod(message.MethodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (method == null)
            {
                PT.PrintErr($"[P2P] Method not found: {message.MethodName}");
                return;
            }

            // Check authority
            NetRpcAttribute? rpcAttr = method.GetCustomAttribute<NetRpcAttribute>();
            if (rpcAttr != null)
            {
                if (!ValidateRpcAuthority(rpcAttr, targetObj, peerId))
                {
                    PT.PrintErr($"[P2P] Unauthorized RPC from {peerId}: {message.MethodName}");
                    return;
                }
            }

            // Deserialize parameters
            object?[] args = new object?[message.Args.Length];
            for (int i = 0; i < message.Args.Length; i++)
            {
                ParameterInfo[] methodParams = method.GetParameters();
                if (i < methodParams.Length)
                {
                    args[i] = JsonSerializer.Deserialize(message.Args[i], methodParams[i].ParameterType);
                }
            }

            // Invoke method
            method.Invoke(targetObj, args);
        }
        catch (Exception ex)
        {
            GD.PushError($"[P2P] RPC invoke error: {ex.Message}");
        }
    }

    private bool ValidateRpcAuthority(NetRpcAttribute rpcAttr, NetworkedObject targetObj, int fromPeerId)
    {
        return rpcAttr.AuthorMode switch
        {
            AuthorityMode.Server => fromPeerId == 1 || fromPeerId == LocalPeerId,
            AuthorityMode.Authority => fromPeerId == targetObj.NetworkAuthority || fromPeerId == LocalPeerId,
            AuthorityMode.Any => true,
            _ => false
        };
    }

    private void OnStateUpdated(string objectId, byte[]? newState)
    {
        StateUpdated?.Invoke(objectId, newState);

        // Broadcast to other peers
        StateUpdateMessage update = new()
        {
            ObjectId = objectId,
            State = newState,
            SequenceNumber = _authorityManager.GetAuthority(objectId)?.SequenceNumber ?? 0
        };

        byte[] data = JsonSerializer.SerializeToUtf8Bytes(update);
        _networkInstance.BroadcastMessage(data, TransferMode.Reliable);
    }

    private byte[] SerializeRpc(string methodName, object?[] args)
    {
        RpcMessage message = new()
        {
            MethodName = methodName,
            Args = args.Select(a => a != null ? JsonSerializer.Serialize(a) : "null").ToArray(),
            SenderId = LocalPeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        return JsonSerializer.SerializeToUtf8Bytes(message);
    }

    private class RateLimiters
    {
        public SlidingWindowRateLimiter Reliable = new(MaxBroadcastPacketPerSec, TimeSpan.FromSeconds(1));
        public SlidingWindowRateLimiter Unreliable = new(MaxBroadcastPacketPerSec, TimeSpan.FromSeconds(1));
    }

    [MemoryPackable]
    private partial class RpcMessage
    {
        public string MethodName { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string[] Args { get; set; } = [];
        public int SenderId { get; set; }
        public long Timestamp { get; set; }
    }

    [MemoryPackable]
    private partial class StateUpdateMessage
    {
        public string ObjectId { get; set; } = "";
        public byte[]? State { get; set; }
        public long SequenceNumber { get; set; }
    }
}

/// <summary>
/// P2P Transform synchronization.
/// </summary>
public class P2PTransformSync
{
    private readonly P2PNetworkService _networkService;
    private readonly ConcurrentDictionary<string, TransformPayloadDto> _pendingTransforms = [];

    public P2PTransformSync(P2PNetworkService networkService)
    {
        _networkService = networkService;
    }

    public void SendTransform(string objectId, Transform3D transform, bool reliable = false)
    {
        TransformPayloadDto payload = TransformPayloadDto.FromGDTransform(transform);

        byte[] data = SerializeUtils.Serialize(payload);
        string rpcName = reliable ? "NetRecvTransformReliable" : "NetRecvTransform";

        _networkService.Rpc(rpcName, objectId, data);
    }

    public void SendTransformToAuthority(string objectId, Transform3D transform, int authorityPeerId)
    {
        TransformPayloadDto payload = TransformPayloadDto.FromGDTransform(transform);
        byte[] data = SerializeUtils.Serialize(payload);

        _networkService.RpcId(authorityPeerId, "NetRecvTransform", objectId, data);
    }

    public void BroadcastTransform(string objectId, Transform3D transform)
    {
        TransformPayloadDto payload = TransformPayloadDto.FromGDTransform(transform);
        byte[] data = SerializeUtils.Serialize(payload);

        _networkService.Rpc("NetRecvTransform", objectId, data);
    }
}

/// <summary>
/// P2P Property synchronization.
/// </summary>
public class P2PPropSync
{
    private readonly P2PNetworkService _networkService;

    public P2PPropSync(P2PNetworkService networkService)
    {
        _networkService = networkService;
    }

    public async Task SendPropUpdate(string objectId, string propName, object? value)
    {
        byte[] data = Polytoria.Networking.Synchronizers.NetworkPropSync.SerializePropValue(value);

        // Get authority for this object
        ObjectAuthority? authority = _networkService.AuthorityManager.GetAuthority(objectId);

        if (authority != null && authority.PrimaryPeerId != _networkService.LocalPeerId)
        {
            // Send to authority
            _networkService.RpcId(authority.PrimaryPeerId, "NetRecvPropUpdate", objectId, propName, data);
        }
        else
        {
            // Broadcast update
            _networkService.Rpc("NetRecvPropUpdate", objectId, propName, data);
        }
    }

    public async Task BatchPropUpdates(List<(string ObjectId, string PropName, object? Value)> updates)
    {
        foreach (var (objectId, propName, value) in updates)
        {
            await SendPropUpdate(objectId, propName, value);
        }
    }
}

/// <summary>
/// P2P Object replication.
/// </summary>
public class P2PReplicateSync
{
    private readonly P2PNetworkService _networkService;
    private readonly Dictionary<string, List<byte[]>> _pendingReplicates = [];

    public event Action<int, int>? InstanceLoadedProgress;

    public P2PReplicateSync(P2PNetworkService networkService)
    {
        _networkService = networkService;
    }

    public void ReplicateObject(NetworkedObject obj)
    {
        _ = _networkService.ClaimObjectAuthority(obj.NetworkedObjectID, SerializeUtils.Serialize(obj));
        _networkService.RpcToSwarm("NetRecvReplicate", obj.NetworkedObjectID, SerializeUtils.Serialize(obj));
    }

    public void RemoveReplicatedObject(string objectId)
    {
        _networkService.Rpc("NetRecvReplicateRemove", objectId);
    }

    public void SyncToNewPeer(int peerId)
    {
        World? world = _networkService.GetWorld();
        if (world == null) return;

        List<NetworkedObject> replicated = world.GetReplicateDescendants().ToList();

        foreach (NetworkedObject obj in replicated)
        {
            byte[] data = SerializeUtils.Serialize(obj);
            _networkService.RpcId(peerId, "NetRecvReplicate", obj.NetworkedObjectID, data);
        }
    }
}
