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
using System.Threading.Tasks;

namespace Polytoria.Networking.P2P;

/// <summary>
/// Peer clustering system that groups peers by network proximity.
/// Each cluster forms a "swarm" with a bridge peer connecting to other swarms.
/// </summary>
public class PeerClusterManager
{
    private const int MaxPeersPerCluster = 8;
    private const int BridgePeersPerCluster = 2;
    private const float LatencySampleIntervalMs = 1000;

    private readonly P2PNetworkInstance _networkInstance;
    private readonly ConcurrentDictionary<int, PeerInfo> _peers = [];
    private readonly ConcurrentDictionary<string, SwarmInfo> _swarms = [];
    private string _localSwarmId = "";
    private PeerRole _localRole = PeerRole.Member;

    public event Action<int, PeerInfo>? PeerAdded;
    public event Action<int>? PeerRemoved;
    public event Action<int, SwarmInfo>? BridgePeerAssigned;

    public string LocalSwarmId => _localSwarmId;
    public PeerRole LocalRole => _localRole;
    public List<PeerInfo> AllPeers => [.. _peers.Values];
    public List<SwarmInfo> AllSwarms => [.. _swarms.Values];
    public SwarmInfo? LocalSwarm => _swarms.Values.FirstOrDefault(s => s.SwarmId == _localSwarmId);

    public PeerClusterManager(P2PNetworkInstance networkInstance)
    {
        _networkInstance = networkInstance;
        _networkInstance.PeerConnected += OnPeerConnected;
        _networkInstance.PeerDisconnected += OnPeerDisconnected;
        _networkInstance.MessageReceived += OnMessageReceived;
    }

    public void Initialize(IPEndPoint? knownSwarmEndpoint = null)
    {
        _localSwarmId = Guid.NewGuid().ToString("N")[..8];

        SwarmInfo localSwarm = new()
        {
            SwarmId = _localSwarmId,
            BridgePeers = [],
            Members = []
        };
        _swarms[_localSwarmId] = localSwarm;

        PT.Print($"[P2P] Initialized in swarm {_localSwarmId}");

        if (knownSwarmEndpoint != null)
        {
            _ = JoinSwarm(knownSwarmEndpoint);
        }
    }

    public async Task JoinSwarm(IPEndPoint endpoint)
    {
        PT.Print($"[P2P] Joining swarm at {endpoint}");
        _networkInstance.ConnectToPeer(endpoint.Address.ToString(), endpoint.Port);
        await Task.CompletedTask;
    }

    public float GetLatencyToPeer(int peerId)
    {
        if (_peers.TryGetValue(peerId, out PeerInfo? peer))
        {
            return peer.AverageLatency;
        }
        return float.MaxValue;
    }

    public List<PeerInfo> GetSwarmPeers()
    {
        return _peers.Values.Where(p => p.SwarmId == _localSwarmId).ToList();
    }

    public List<PeerInfo> GetBridgePeers()
    {
        return _peers.Values.Where(p => p.SwarmId == _localSwarmId && p.Role == PeerRole.Bridge).ToList();
    }

    public List<PeerInfo> GetClosestPeers(int count = MaxPeersPerCluster)
    {
        return _peers.Values
            .OrderBy(p => p.AverageLatency)
            .Take(count)
            .ToList();
    }

    public List<PeerInfo> GetFarthestPeers(int count = BridgePeersPerCluster)
    {
        return _peers.Values
            .Where(p => p.SwarmId == _localSwarmId)
            .OrderByDescending(p => p.AverageLatency)
            .Take(count)
            .ToList();
    }

    public void RebalanceClusters()
    {
        List<PeerInfo> sortedPeers = _peers.Values
            .OrderBy(p => p.AverageLatency)
            .ToList();

        List<List<PeerInfo>> clusters = FindClusterBoundaries(sortedPeers);

        foreach (var cluster in clusters)
        {
            string newSwarmId = Guid.NewGuid().ToString("N")[..8];

            foreach (PeerInfo peer in cluster)
            {
                if (peer.SwarmId != newSwarmId)
                {
                    peer.SwarmId = newSwarmId;
                    SendClusterAssignment(peer.PeerId, newSwarmId);
                }
            }
        }

        AssignBridgePeers();
        PT.Print($"[P2P] Rebalanced into {clusters.Count} clusters");
    }

    public void RouteMessage(int targetPeerId, byte[] data, TransferMode transferMode)
    {
        if (_peers.TryGetValue(targetPeerId, out PeerInfo? target))
        {
            if (target.SwarmId == _localSwarmId)
            {
                _networkInstance.SendMessage(targetPeerId, data, transferMode);
            }
            else
            {
                PeerInfo? bridge = GetBridgePeerForSwarm(target.SwarmId);
                if (bridge != null)
                {
                    byte[] routed = MessageRouter.WrapMessage(targetPeerId, data);
                    _networkInstance.SendMessage(bridge.PeerId, routed, transferMode);
                }
                else
                {
                    GD.PushWarning($"[P2P] No bridge peer for swarm {target.SwarmId}");
                }
            }
        }
    }

    public void BroadcastToSwarm(byte[] data, TransferMode transferMode, int[]? except = null)
    {
        List<int> swarmPeerIds = _peers.Values
            .Where(p => p.SwarmId == _localSwarmId && (except == null || !except.Contains(p.PeerId)))
            .Select(p => p.PeerId)
            .ToList();

        _networkInstance.BroadcastMessage(data, transferMode, except: [.. swarmPeerIds]);
    }

    public void BroadcastToAllSwarms(byte[] data, TransferMode transferMode)
    {
        BroadcastToSwarm(data, transferMode);

        List<PeerInfo> bridgePeers = GetBridgePeers();
        foreach (PeerInfo bridge in bridgePeers)
        {
            byte[] forwarded = MessageRouter.WrapBroadcast(data);
            _networkInstance.SendMessage(bridge.PeerId, forwarded, transferMode);
        }
    }

    private void OnPeerConnected(int peerId)
    {
        PT.Print($"[P2P] Peer {peerId} connected");

        PeerInfo peerInfo = new()
        {
            PeerId = peerId,
            SwarmId = _localSwarmId,
            Role = PeerRole.Member,
            ConnectedAt = DateTime.UtcNow
        };

        _peers[peerId] = peerInfo;
        _ = MeasureLatency(peerId);
        SendSwarmInfo(peerId);
        PeerAdded?.Invoke(peerId, peerInfo);

        if (GetSwarmPeers().Count > MaxPeersPerCluster)
        {
            RebalanceClusters();
        }
    }

    private void OnPeerDisconnected(int peerId)
    {
        if (_peers.TryRemove(peerId, out PeerInfo? peer))
        {
            PT.Print($"[P2P] Peer {peerId} disconnected (was in swarm {peer.SwarmId})");

            if (peer.Role == PeerRole.Bridge)
            {
                AssignBridgePeers();
            }

            PeerRemoved?.Invoke(peerId);
        }
    }

    private void OnMessageReceived(int peerId, byte[] data, TransferMode transferMode)
    {
        if (MessageRouter.IsRoutedMessage(data))
        {
            MessageRouter.HandleRoutedMessage(data, (targetId, payload) =>
            {
                if (targetId != _networkInstance.LocalPeerId)
                {
                    RouteMessage(targetId, payload, transferMode);
                }
            });
        }
        else if (MessageRouter.IsBroadcastMessage(data))
        {
            byte[] payload = MessageRouter.ExtractBroadcastPayload(data);
            BroadcastToSwarm(payload, transferMode, except: [peerId]);
        }
    }

    private async Task MeasureLatency(int peerId)
    {
        while (_peers.ContainsKey(peerId))
        {
            await Task.Delay((int)LatencySampleIntervalMs);

            if (_peers.TryGetValue(peerId, out PeerInfo? peer))
            {
                peer.LatencySamples.Add(50);
                if (peer.LatencySamples.Count > 10)
                {
                    peer.LatencySamples.RemoveAt(0);
                }
            }
        }
    }

    private void SendSwarmInfo(int peerId)
    {
        SwarmInfo? swarm = LocalSwarm;
        if (swarm == null) return;

        byte[] data = SwarmSerializer.SerializeSwarmInfo(swarm);
        _networkInstance.SendMessage(peerId, data, TransferMode.Reliable);
    }

    private void SendClusterAssignment(int peerId, string newSwarmId)
    {
        byte[] data = SwarmSerializer.SerializeClusterAssignment(newSwarmId);
        _networkInstance.SendMessage(peerId, data, TransferMode.Reliable);
    }

    private List<List<PeerInfo>> FindClusterBoundaries(List<PeerInfo> sortedPeers)
    {
        var clusters = new List<List<PeerInfo>>();
        var currentCluster = new List<PeerInfo>();

        for (int i = 0; i < sortedPeers.Count; i++)
        {
            currentCluster.Add(sortedPeers[i]);

            bool shouldSplit = false;

            if (currentCluster.Count >= MaxPeersPerCluster)
            {
                shouldSplit = true;
            }
            else if (i < sortedPeers.Count - 1)
            {
                float latencyGap = sortedPeers[i + 1].AverageLatency - sortedPeers[i].AverageLatency;
                if (latencyGap > 50)
                {
                    shouldSplit = true;
                }
            }

            if (shouldSplit && currentCluster.Count > 0)
            {
                clusters.Add(currentCluster);
                currentCluster = new List<PeerInfo>();
            }
        }

        if (currentCluster.Count > 0)
        {
            clusters.Add(currentCluster);
        }

        return clusters;
    }

    private void AssignBridgePeers()
    {
        foreach (PeerInfo peer in _peers.Values)
        {
            if (peer.Role == PeerRole.Bridge)
            {
                peer.Role = PeerRole.Member;
            }
        }

        List<PeerInfo> farthest = GetFarthestPeers(BridgePeersPerCluster);

        foreach (PeerInfo peer in farthest)
        {
            peer.Role = PeerRole.Bridge;
            PT.Print($"[P2P] Assigned peer {peer.PeerId} as bridge (latency: {peer.AverageLatency:F1}ms)");
            if (LocalSwarm != null)
            {
                BridgePeerAssigned?.Invoke(peer.PeerId, LocalSwarm);
            }
        }
    }

    private PeerInfo? GetBridgePeerForSwarm(string swarmId)
    {
        return _peers.Values.FirstOrDefault(p =>
            p.SwarmId == _localSwarmId &&
            p.Role == PeerRole.Bridge &&
            p.KnownSwarms.Contains(swarmId));
    }
}

public class PeerInfo
{
    public int PeerId { get; set; }
    public string SwarmId { get; set; } = "";
    public PeerRole Role { get; set; } = PeerRole.Member;
    public DateTime ConnectedAt { get; set; }
    public List<float> LatencySamples { get; set; } = [];
    public HashSet<string> KnownSwarms { get; set; } = [];
    public IPEndPoint? PublicEndPoint { get; set; }

    public float AverageLatency => LatencySamples.Count > 0
        ? LatencySamples.Average()
        : float.MaxValue;
}

public enum PeerRole
{
    Member,
    Bridge,
    Coordinator
}

public class SwarmInfo
{
    public string SwarmId { get; set; } = "";
    public List<int> BridgePeers { get; set; } = [];
    public List<int> Members { get; set; } = [];
    public string? CoordinatorPeerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class SwarmSerializer
{
    public static byte[] SerializeSwarmInfo(SwarmInfo swarm)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(swarm);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    public static SwarmInfo? DeserializeSwarmInfo(byte[] data)
    {
        string json = System.Text.Encoding.UTF8.GetString(data);
        return System.Text.Json.JsonSerializer.Deserialize<SwarmInfo>(json);
    }

    public static byte[] SerializeClusterAssignment(string swarmId)
    {
        return System.Text.Encoding.UTF8.GetBytes($"CLUSTER:{swarmId}");
    }

    public static string? DeserializeClusterAssignment(byte[] data)
    {
        string text = System.Text.Encoding.UTF8.GetString(data);
        if (text.StartsWith("CLUSTER:"))
        {
            return text[8..];
        }
        return null;
    }
}

public static class MessageRouter
{
    private const byte ROUTED_PREFIX = 0x01;
    private const byte BROADCAST_PREFIX = 0x02;

    public static bool IsRoutedMessage(byte[] data)
    {
        return data.Length > 0 && data[0] == ROUTED_PREFIX;
    }

    public static bool IsBroadcastMessage(byte[] data)
    {
        return data.Length > 0 && data[0] == BROADCAST_PREFIX;
    }

    public static byte[] WrapMessage(int targetPeerId, byte[] payload)
    {
        byte[] result = new byte[5 + payload.Length];
        result[0] = ROUTED_PREFIX;
        BitConverter.GetBytes(targetPeerId).CopyTo(result, 1);
        payload.CopyTo(result, 5);
        return result;
    }

    public static byte[] WrapBroadcast(byte[] payload)
    {
        byte[] result = new byte[1 + payload.Length];
        result[0] = BROADCAST_PREFIX;
        payload.CopyTo(result, 1);
        return result;
    }

    public static void HandleRoutedMessage(byte[] data, Action<int, byte[]> handler)
    {
        if (data.Length < 5) return;

        int targetId = BitConverter.ToInt32(data, 1);
        byte[] payload = data[5..];
        handler(targetId, payload);
    }

    public static byte[] ExtractBroadcastPayload(byte[] data)
    {
        if (data.Length < 1) return [];
        return data[1..];
    }
}
