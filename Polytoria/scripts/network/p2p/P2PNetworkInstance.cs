// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Networking.P2P;

/// <summary>
/// P2P mesh network instance using ENet.
/// Each peer uses a single ENetConnection that both listens and connects,
/// forming a full mesh topology.
/// </summary>
public class P2PNetworkInstance
{
    private const float SilenceTimeoutSeconds = 10.0f;
    private const int DefaultCapacity = 67;
    private const int DefaultPort = 21441;
    private const int MinimumTimeout = 5;
    private const ENetConnection.CompressionMode CompressionMode = ENetConnection.CompressionMode.Zlib;
    private const int BandwidthInLimit = 0;
    private const int BandwidthOutLimit = 0;

    private readonly ENetConnection _peer;
    private readonly ConcurrentDictionary<int, ENetPacketPeer> _connectedPeers = [];
    private readonly ConcurrentDictionary<ENetPacketPeer, int> _peerToId = [];
    private int _peerCounter = 0;
    private readonly string _listenAddress;
    private readonly int _listenPort;
    private bool _isListening = false;
    private bool _shutdownd = false;

    private long _lastMessageTicks = DateTime.UtcNow.Ticks;
    private readonly ConcurrentQueue<Action> _actionQueue = new();
    private readonly ConcurrentQueue<DeferredP2PEvent> _mainThreadEventQueue = new();
    private int _mainThreadDrainScheduled = 0;

    public event Action<int>? PeerConnected;
    public event Action<int>? PeerDisconnected;
    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<P2PErrorEnum>? P2PError;
    public event P2PMessageReceivedHandler? MessageReceived;

    public bool IsListening => _isListening;
    public bool IsSilence { get; private set; } = false;
    public int LocalPeerId { get; private set; } = 0;
    public int PeerCount => _connectedPeers.Count;

    public NatTraversal? NatTraversal { get; set; }

    public P2PNetworkInstance(string address = "*", int port = DefaultPort)
    {
        _listenAddress = address;
        _listenPort = port;
        _peer = new();
    }

    /// <summary>
    /// Start listening for incoming connections.
    /// </summary>
    public Godot.Error StartListening(int maxChannels = 3)
    {
        if (_isListening)
        {
            GD.PushWarning("[P2P] Already listening");
            return Godot.Error.AlreadyInUse;
        }

        Godot.Error e = _peer.CreateHostBound(_listenAddress, _listenPort, DefaultCapacity, maxChannels);
        _peer.Compress(CompressionMode);
        _peer.BandwidthLimit(BandwidthInLimit, BandwidthOutLimit);

        if (e != Godot.Error.Ok)
        {
            GD.PushError("[P2P] Couldn't create host: ", e);
            return e;
        }

        _isListening = true;
        LocalPeerId = 1;
        _ = Task.Run(NetworkLoop);
        return Godot.Error.Ok;
    }

    /// <summary>
    /// Connect to a remote peer. Uses the same ENetConnection (mesh topology).
    /// </summary>
    public void ConnectToPeer(string address, int port, int maxChannels = 3)
    {
        if (!_isListening)
        {
            // Create host first if not listening yet
            Godot.Error e = _peer.CreateHost(DefaultCapacity, maxChannels);
            _peer.Compress(CompressionMode);
            _peer.BandwidthLimit(BandwidthInLimit, BandwidthOutLimit);

            if (e != Godot.Error.Ok)
            {
                GD.PushError("[P2P] Couldn't create host: ", e);
                P2PError?.Invoke(P2PErrorEnum.ConnectionFailure);
                return;
            }

            _isListening = true;
            LocalPeerId = 1;
            _ = Task.Run(NetworkLoop);
        }

        _peer.ConnectToHost(address, port);
        PT.Print($"[P2P] Connecting to {address}:{port}");
    }

    /// <summary>
    /// Connect using NAT traversal.
    /// </summary>
    public async Task ConnectWithNatTraversal(string stunServer, int maxChannels = 3)
    {
        NatTraversal ??= new NatTraversal();

        var (publicAddress, publicPort) = await NatTraversal.DiscoverPublicAddress(stunServer);
        PT.Print($"[NAT] Discovered public address: {publicAddress}:{publicPort}");

        Godot.Error e = _peer.CreateHostBound("*", publicPort, DefaultCapacity, maxChannels);
        _peer.Compress(CompressionMode);
        _peer.BandwidthLimit(BandwidthInLimit, BandwidthOutLimit);

        if (e != Godot.Error.Ok)
        {
            GD.PushError("[P2P] Couldn't create NAT host: ", e);
            return;
        }

        _isListening = true;
        LocalPeerId = 1;
        _ = Task.Run(NetworkLoop);
    }

    /// <summary>
    /// Send a message to a specific peer.
    /// </summary>
    public void SendMessage(int targetPeerId, byte[] data, TransferMode transferMode, int transferChannel = 0)
    {
        _actionQueue.Enqueue(() =>
        {
            ENetPacketPeer? peer = GetPeerById(targetPeerId);
            if (peer == null)
            {
                GD.PushWarning($"[P2P] Peer {targetPeerId} doesn't exist");
                return;
            }
            Godot.Error err = peer.Send(transferChannel, data, (int)transferMode);
            if (err != Godot.Error.Ok)
            {
                GD.PushError("[P2P] Send error: ", err);
            }
        });
    }

    /// <summary>
    /// Broadcast a message to all connected peers.
    /// </summary>
    public void BroadcastMessage(byte[] data, TransferMode transferMode, int transferChannel = 0, int[]? except = null)
    {
        _actionQueue.Enqueue(() =>
        {
            foreach ((int id, ENetPacketPeer? peer) in _connectedPeers)
            {
                if (!peer.IsActive()) continue;
                if (except != null && except.Contains(id)) continue;
                peer?.Send(transferChannel, data, (int)transferMode);
            }
        });
    }

    /// <summary>
    /// Disconnect a specific peer.
    /// </summary>
    public void DisconnectPeer(int peerId, bool force = false)
    {
        _actionQueue.Enqueue(() =>
        {
            ENetPacketPeer? peer = GetPeerById(peerId);
            if (peer == null) return;
            if (force)
            {
                peer.PeerDisconnectNow();
            }
            else
            {
                peer.PeerDisconnect();
            }
        });
    }

    /// <summary>
    /// Shutdown the network instance.
    /// </summary>
    public void Shutdown()
    {
        if (_shutdownd) return;
        _shutdownd = true;

        foreach ((_, ENetPacketPeer pk) in _connectedPeers)
        {
            pk.PeerDisconnect();
        }

        _peer.Flush();
        _peer.Destroy();
    }

    /// <summary>
    /// Get all connected peer IDs.
    /// </summary>
    public List<int> GetConnectedPeerIds()
    {
        return [.. _connectedPeers.Keys];
    }

    public IPEndPoint? GetListenAddress()
    {
        if (!_isListening) return null;
        return new IPEndPoint(IPAddress.Parse(_listenAddress == "*" ? "127.0.0.1" : _listenAddress), _listenPort);
    }

    public P2PNetworkStats GetStats()
    {
        return new P2PNetworkStats
        {
            PeerCount = PeerCount,
            IsListening = _isListening
        };
    }

    private ENetPacketPeer? GetPeerById(int peerId)
    {
        _connectedPeers.TryGetValue(peerId, out ENetPacketPeer? peer);
        return peer;
    }

    private void NetworkLoop()
    {
        while (true)
        {
            if (_shutdownd) return;
            if (!GodotObject.IsInstanceValid(_peer)) return;

            try
            {
                ProcessActionQueue();
                ProcessNetwork();
                CheckSilence();
                _peer.Flush();
            }
            catch (Exception ex)
            {
                GD.PushError(ex);
            }

            Thread.Sleep(1);
        }
    }

    private void ProcessNetwork()
    {
        Godot.Collections.Array serviceData = _peer.Service(MinimumTimeout);

        while (true)
        {
            ENetConnection.EventType eventType = (ENetConnection.EventType)(int)serviceData[0];
            if (eventType == ENetConnection.EventType.None)
                break;

            ENetPacketPeer? fromPeer = (ENetPacketPeer?)serviceData[1];

            if (fromPeer != null)
            {
                int peerID = 0;
                if (_peerToId.TryGetValue(fromPeer, out int p))
                {
                    peerID = p;
                }

                switch (eventType)
                {
                    case ENetConnection.EventType.Connect:
                        if (peerID == 0)
                        {
                            _peerCounter++;
                            peerID = _peerCounter;
                            _connectedPeers[peerID] = fromPeer;
                            _peerToId[fromPeer] = peerID;
                        }

                        EnqueueEvent(new P2PPeerConnectedEvent(peerID));
                        break;

                    case ENetConnection.EventType.Disconnect:
                        _connectedPeers.TryRemove(peerID, out _);
                        _peerToId.TryRemove(fromPeer, out _);
                        EnqueueEvent(new P2PPeerDisconnectedEvent(peerID));
                        break;

                    case ENetConnection.EventType.Receive:
                        Interlocked.Exchange(ref _lastMessageTicks, DateTime.UtcNow.Ticks);
                        while (fromPeer.GetAvailablePacketCount() > 0)
                        {
                            int pkf = fromPeer.GetPacketFlags();
                            TransferMode m = pkf switch
                            {
                                (int)ENetPacketPeer.FlagReliable => TransferMode.Reliable,
                                (int)ENetPacketPeer.FlagUnreliableFragment => TransferMode.UnreliableOrdered,
                                (int)ENetPacketPeer.FlagUnsequenced => TransferMode.Unreliable,
                                _ => TransferMode.Unreliable,
                            };
                            byte[] data = fromPeer.GetPacket();
                            EnqueueEvent(new P2PMessageReceivedEvent(peerID, data, m));
                        }
                        break;

                    case ENetConnection.EventType.Error:
                        GD.PushError("[P2P] Network error");
                        EnqueueEvent(new P2PErrorEvent(P2PErrorEnum.NetworkError));
                        break;
                }
            }

            serviceData = _peer.Service(0);
        }
    }

    private void CheckSilence()
    {
        long lastTicks = Interlocked.Read(ref _lastMessageTicks);
        double elapsedSeconds = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastTicks).TotalSeconds;

        bool currentlySilent = elapsedSeconds > SilenceTimeoutSeconds;

        if (currentlySilent != IsSilence)
        {
            IsSilence = currentlySilent;
            if (IsSilence)
            {
                PT.PrintErr("[!] P2P network has gone silent");
            }
            else
            {
                PT.Print("[i] P2P network resumed");
            }
        }
    }

    private void ProcessActionQueue()
    {
        while (_actionQueue.TryDequeue(out Action? action))
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                GD.PushError("[P2P] Error processing action: ", ex);
            }
        }
    }

    private void EnqueueEvent(DeferredP2PEvent e)
    {
        _mainThreadEventQueue.Enqueue(e);
        if (Interlocked.CompareExchange(ref _mainThreadDrainScheduled, 1, 0) == 0)
        {
            Callable.From(DrainEvents).CallDeferred();
        }
    }

    private void DrainEvents()
    {
        try
        {
            while (_mainThreadEventQueue.TryDequeue(out DeferredP2PEvent? e))
            {
                switch (e)
                {
                    case P2PPeerConnectedEvent connected:
                        PeerConnected?.Invoke(connected.PeerID);
                        break;
                    case P2PPeerDisconnectedEvent disconnected:
                        PeerDisconnected?.Invoke(disconnected.PeerID);
                        break;
                    case P2PErrorEvent error:
                        P2PError?.Invoke(error.Error);
                        break;
                    case P2PMessageReceivedEvent msg:
                        MessageReceived?.Invoke(msg.PeerID, msg.Data, msg.TransferMode);
                        break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _mainThreadDrainScheduled, 0);

            if (!_mainThreadEventQueue.IsEmpty && Interlocked.CompareExchange(ref _mainThreadDrainScheduled, 1, 0) == 0)
            {
                Callable.From(DrainEvents).CallDeferred();
            }
        }
    }

    public enum P2PErrorEnum
    {
        ConnectionFailure,
        ConnectionTimeout,
        NetworkError,
        NatTraversalFailure,
        DhtError
    }

    public delegate void P2PMessageReceivedHandler(int peerID, byte[] data, TransferMode transferMode);

    private abstract record DeferredP2PEvent;
    private record P2PPeerConnectedEvent(int PeerID) : DeferredP2PEvent;
    private record P2PPeerDisconnectedEvent(int PeerID) : DeferredP2PEvent;
    private record P2PErrorEvent(P2PErrorEnum Error) : DeferredP2PEvent;
    private record P2PMessageReceivedEvent(int PeerID, byte[] Data, TransferMode TransferMode) : DeferredP2PEvent;
}

public class P2PNetworkStats
{
    public int PeerCount { get; set; }
    public bool IsListening { get; set; }
    public double BytesReceived { get; set; }
    public double BytesSent { get; set; }
    public double PacketsReceived { get; set; }
    public double PacketsSent { get; set; }
}
