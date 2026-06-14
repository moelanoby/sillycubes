// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Datamodel;
using Polytoria.Shared;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Networking.P2P;

/// <summary>
/// Main P2P Manager that orchestrates all P2P networking components.
/// Launches web UI on localhost and manages DHT-based peer discovery.
/// </summary>
public partial class P2PManager : Node
{
    private const int DefaultGamePort = 24221;
    private const int DefaultWebPort = 24222;
    private const string DefaultStunServer = "stun.l.google.com:19302";

    private KademliaDHT? _dht;
    private WebServer? _webServer;
    private SignalClient? _signalClient;
    private P2PNetworkInstance? _networkInstance;
    private Godot.Timer? _heartbeatTimer;
    private bool _isHost = false;
    private string _currentServerCode = "";
    private string _username = "Player";

    public KademliaDHT? DHT => _dht;
    public WebServer? WebServer => _webServer;
    public SignalClient? SignalClient => _signalClient;
    public P2PNetworkInstance? NetworkInstance => _networkInstance;

    public bool IsInitialized => _dht != null;
    public bool IsListening => _networkInstance?.IsListening ?? false;
    public int PeerCount => _networkInstance?.PeerCount ?? 0;
    public string Username
    {
        get => _username;
        set
        {
            _username = value;
            if (_webServer != null) _webServer.LocalUsername = value;
        }
    }

    public event Action? WebUIStarted;
    public event Action? NetworkReady;
    public event Action<string>? ServerCreated;
    public event Action<string>? ServerJoined;
    public event Action<int>? PeerConnected;
    public event Action<int>? PeerDisconnected;

    public override void _Ready()
    {
        base._Ready();
        PT.Print("[P2P] Manager initialized");
    }

    /// <summary>
    /// Initialize the full P2P stack: DHT + Web UI + Network.
    /// Call this once at startup.
    /// </summary>
    public void Initialize(int gamePort = DefaultGamePort, int webPort = DefaultWebPort, string? username = null)
    {
        if (username != null) _username = username;

        // Start Kademlia DHT
        _dht = new KademliaDHT(0); // Random port
        _dht.Start();

        // Bootstrap into DHT network
        _ = Task.Run(async () =>
        {
            await _dht.Bootstrap();
            PT.Print($"[P2P] DHT bootstrapped with {_dht.NodeCount} nodes");
        });

        // Start web server
        _webServer = new WebServer(_dht, webPort);
        _webServer.LocalUsername = _username;
        _webServer.ServerCreated += OnServerCreated;
        _webServer.ServerJoinRequested += OnServerJoinRequested;
        _webServer.Start();

        // Create signal client
        _signalClient = new SignalClient(_dht, webPort);

        // Open browser to web UI
        OS.ShellOpen($"http://localhost:{webPort}");

        PT.Print($"[P2P] Web UI at http://localhost:{webPort}");
        WebUIStarted?.Invoke();
    }

    /// <summary>
    /// Initialize only the DHT for headless server operation (no WebUI).
    /// </summary>
    public void InitializeDHTOnly(int dhtPort = 0, string? username = null)
    {
        if (username != null) _username = username;

        // Start Kademlia DHT
        _dht = new KademliaDHT(dhtPort);
        _dht.Start();

        // Bootstrap into DHT network
        _ = Task.Run(async () =>
        {
            await _dht.Bootstrap();
            PT.Print($"[P2P] DHT bootstrapped with {_dht.NodeCount} nodes");
        });

        // Create signal client (uses DHT directly, no web server)
        _signalClient = new SignalClient(_dht, 0);

        PT.Print($"[P2P] DHT-only mode initialized on port {_dht.Port}");
    }

    /// <summary>
    /// Create a server (host mode).
    /// </summary>
    public async Task<string> CreateServer(int port = DefaultGamePort, bool useNat = false)
    {
        if (_signalClient == null || _dht == null)
        {
            PT.PrintErr("[P2P] Not initialized");
            return "";
        }

        // Create server via signal client
        _currentServerCode = await _signalClient.CreateServer(_username, port);
        _isHost = true;

        // Start P2P network
        _networkInstance = new P2PNetworkInstance("*", port);

        if (useNat)
        {
            await _networkInstance.ConnectWithNatTraversal(DefaultStunServer);
        }
        else
        {
            _networkInstance.StartListening(port);
        }

        _networkInstance.PeerConnected += OnPeerConnected;
        _networkInstance.PeerDisconnected += OnPeerDisconnected;

        // Start heartbeat timer
        _heartbeatTimer = new Godot.Timer();
        _heartbeatTimer.Timeout += OnHeartbeat;
        _heartbeatTimer.WaitTime = 30;
        AddChild(_heartbeatTimer);
        _heartbeatTimer.Start();

        PT.Print($"[P2P] Server created: {_currentServerCode} on port {port}");
        ServerCreated?.Invoke(_currentServerCode);

        return _currentServerCode;
    }

    /// <summary>
    /// Join an existing server by code.
    /// </summary>
    public async Task JoinServer(string code, int gamePort = DefaultGamePort)
    {
        if (_signalClient == null || _dht == null)
        {
            PT.PrintErr("[P2P] Not initialized");
            return;
        }

        ServerInfo? server = await _signalClient.JoinServer(code);
        if (server == null)
        {
            PT.PrintErr($"[P2P] Server not found: {code}");
            return;
        }

        _currentServerCode = code;
        _isHost = false;

        // Connect to host
        if (server.Ip != null && server.Port > 0)
        {
            _networkInstance = new P2PNetworkInstance();
            _networkInstance.PeerConnected += OnPeerConnected;
            _networkInstance.PeerDisconnected += OnPeerDisconnected;
            _networkInstance.ConnectToPeer(server.Ip, server.Port);

            PT.Print($"[P2P] Joining server {code} at {server.Ip}:{server.Port}");
            ServerJoined?.Invoke(code);
        }
        else
        {
            PT.PrintErr($"[P2P] Server {code} has no connection info yet");
        }
    }

    /// <summary>
    /// Leave the current server.
    /// </summary>
    public async Task LeaveServer()
    {
        if (_signalClient != null)
        {
            await _signalClient.LeaveServer();
        }

        _networkInstance?.Shutdown();
        _heartbeatTimer?.Stop();

        _currentServerCode = "";
        _isHost = false;

        PT.Print("[P2P] Left server");
    }

    /// <summary>
    /// Get list of available servers.
    /// </summary>
    public async Task<System.Collections.Generic.List<ServerInfo>> GetServers()
    {
        return await _signalClient?.GetServers() ?? [];
    }

    /// <summary>
    /// Get DHT network stats.
    /// </summary>
    public async Task<(int NodeCount, string NodeId)?> GetDhtStats()
    {
        return await _signalClient?.GetDhtStats() ?? null;
    }

    /// <summary>
    /// Send a message to a specific peer.
    /// </summary>
    public void SendMessage(int targetPeerId, byte[] data, TransferMode transferMode)
    {
        _networkInstance?.SendMessage(targetPeerId, data, transferMode);
    }

    /// <summary>
    /// Broadcast a message to all peers.
    /// </summary>
    public void BroadcastMessage(byte[] data, TransferMode transferMode, int[]? except = null)
    {
        _networkInstance?.BroadcastMessage(data, transferMode, except: except);
    }

    /// <summary>
    /// Get the local peer ID.
    /// </summary>
    public int GetLocalPeerId()
    {
        return _networkInstance?.LocalPeerId ?? 0;
    }

    /// <summary>
    /// Get all connected peer IDs.
    /// </summary>
    public System.Collections.Generic.List<int> GetConnectedPeers()
    {
        return _networkInstance?.GetConnectedPeerIds() ?? [];
    }

    public override void _ExitTree()
    {
        _heartbeatTimer?.Stop();
        _networkInstance?.Shutdown();
        _webServer?.Stop();
        _dht?.Stop();
        _signalClient?.Dispose();
        _webServer?.Dispose();
        _dht?.Dispose();
        base._ExitTree();
    }

    private void OnPeerConnected(int peerId)
    {
        PT.Print($"[P2P] Peer {peerId} connected");
        _webServer?.NotifyPeerConnected($"Peer_{peerId}", peerId);
        PeerConnected?.Invoke(peerId);
    }

    private void OnPeerDisconnected(int peerId)
    {
        PT.Print($"[P2P] Peer {peerId} disconnected");
        _webServer?.NotifyPeerDisconnected($"Peer_{peerId}", peerId);
        PeerDisconnected?.Invoke(peerId);
    }

    private void OnServerCreated(string code, int port)
    {
        PT.Print($"[P2P] WebUI: Server created {code}");

        _currentServerCode = code;
        ServerCreated?.Invoke(_currentServerCode);
    }

    private void OnServerJoinRequested(string code)
    {
        _ = JoinServer(code);
    }

    private async void OnHeartbeat()
    {
        if (_isHost && _signalClient != null && _networkInstance != null)
        {
            await _signalClient.SendHeartbeat(
                "127.0.0.1", // Public IP would be discovered via STUN
                _networkInstance.GetListenAddress()?.Port ?? DefaultGamePort,
                _networkInstance.PeerCount
            );
        }
    }
}
