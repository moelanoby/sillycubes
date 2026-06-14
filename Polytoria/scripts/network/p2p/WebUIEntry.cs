// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Client;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Services;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.IO;

namespace Polytoria.Networking.P2P;

public static class WebUIEntry
{
    private static P2PManager? _p2pManager;
    private static ClientEntry? _clientEntry;
    private static readonly List<int> _childProcesses = [];

    public static async void Start(ClientEntry clientEntry, Dictionary<string, string> cmdargs)
    {
        _clientEntry = clientEntry;

        PT.Print("[P2P] Initializing P2P system...");

        await clientEntry.ToSignal(clientEntry.GetTree(), SceneTree.SignalName.ProcessFrame);

        _p2pManager = new P2PManager();
        _p2pManager.Name = "P2PManager";
        clientEntry.AddChild(_p2pManager);

        cmdargs.TryGetValue("username", out string? username);
        cmdargs.TryGetValue("port", out string? portStr);

        int webPort = 24222;
        int gamePort = 24221;

        if (portStr != null)
        {
            int.TryParse(portStr, out gamePort);
        }

        _p2pManager.Initialize(gamePort, webPort, username);

        if (_p2pManager.WebServer != null)
        {
            _p2pManager.WebServer.WorldLoadRequested += OnWorldLoadRequested;
        }

        _p2pManager.ServerCreated += OnServerCreated;
        _p2pManager.ServerJoined += OnServerJoined;

        Globals.BeforeQuit += () =>
        {
            foreach (int pid in _childProcesses)
            {
                if (OS.IsProcessRunning(pid))
                {
                    OS.Kill(pid);
                }
            }
        };

        PT.Print($"[P2P] Web UI launched at http://localhost:{webPort}");
        PT.Print("[P2P] Waiting for user to create or join a server, or load a local world...");
    }

    private static async void OnWorldLoadRequested(string filePath)
    {
        PT.Print($"[P2P] Game launch requested from: {filePath}");

        if (_clientEntry == null || !File.Exists(filePath))
        {
            PT.PrintErr("[P2P] Cannot start game: client not ready or file not found");
            return;
        }

        await _clientEntry.ToSignal(_clientEntry.GetTree(), SceneTree.SignalName.ProcessFrame);

        string exePath = OS.GetExecutablePath();
        int port = _p2pManager?.NetworkInstance?.GetListenAddress()?.Port ?? 24221;

        PT.Print($"[P2P] Spawning headless server...");

        int serverPid = OS.CreateProcess(exePath,
        [
            "--headless",
            "-network", "server",
            "-world", filePath,
            "-port", port.ToString(),
        ]);
        _childProcesses.Add(serverPid);
        PT.Print($"[P2P] Server started (PID {serverPid})");

        await _clientEntry.ToSignal(_clientEntry.GetTree(), SceneTree.SignalName.ProcessFrame);

        PT.Print("[P2P] Spawning client...");

        int clientPid = OS.CreateProcess(exePath,
        [
            "--windowed",
            "-network", "client",
            "-address", "127.0.0.1",
            "-port", port.ToString(),
            "-id", "1",
        ]);
        _childProcesses.Add(clientPid);
        PT.Print($"[P2P] Client started (PID {clientPid})");
        PT.Print("[P2P] Game running as separate processes (server headless, client windowed)");
    }

    private static void OnServerCreated(string code)
    {
        PT.Print($"[P2P] Server created: {code}");
        PT.Print("[P2P] Starting server on main thread...");

        // Defer to main thread (event may fire from WebServer's background thread)
        if (_clientEntry != null && GodotObject.IsInstanceIdValid(_clientEntry.GetInstanceId()))
        {
            Callable.From(() => StartGameAsHost(code)).CallDeferred();
        }
    }

    private static void OnServerJoined(string code)
    {
        PT.Print($"[P2P] Joined server: {code}");
        PT.Print("[P2P] Connecting to host on main thread...");

        if (_clientEntry != null && GodotObject.IsInstanceIdValid(_clientEntry.GetInstanceId()))
        {
            Callable.From(() => StartGameAsClient(code)).CallDeferred();
        }
    }

    private static async void StartGameAsHost(string code)
    {
        if (_clientEntry == null) return;

        PT.Print("[P2P] Starting as host...");

        if (_clientEntry.NetworkService == null)
        {
            _clientEntry.NetworkService = new NetworkService
            {
                Name = "NetworkService",
                Entry = _clientEntry
            };
        }

        _clientEntry.NetworkService.Attach(_clientEntry.Root);
        _clientEntry.NetworkService.IsServer = true;
        _clientEntry.NetworkService.NetworkParent = _clientEntry.Root;

        // Set up services and fire events that are normally done by Entry()
        // after the WebUI branch returns early.
        _clientEntry.DatamodelBridge?.Attach(_clientEntry.Root);
        World.Current = _clientEntry.Root;
        _clientEntry.Root.Setup();
        _clientEntry.SignalNetworkEssentialsReady();

        int port = _p2pManager?.NetworkInstance?.GetListenAddress()?.Port ?? 24221;
        _clientEntry.NetworkService.CreateP2P(port);

        // Update DHT entry with connection info so other clients can join
        if (_p2pManager?.DHT != null)
        {
            string? existing = await _p2pManager.DHT.GetString($"server:{code}");
            if (existing != null)
            {
                ServerInfo? si = System.Text.Json.JsonSerializer.Deserialize<ServerInfo>(existing);
                if (si != null)
                {
                    si.Ip = "127.0.0.1";
                    si.Port = port;
                    si.PlayerCount = 1;
                    si.LastHeartbeat = DateTime.UtcNow;
                    await _p2pManager.DHT.Store($"server:{code}",
                        System.Text.Json.JsonSerializer.Serialize(si),
                        TimeSpan.FromMinutes(30));
                }
            }
        }

        PT.Print("[P2P] Game ready as host");
    }

    private static void StartGameAsClient(string code)
    {
        if (_clientEntry == null || _p2pManager?.NetworkInstance == null) return;

        PT.Print("[P2P] Starting as client...");

        if (_clientEntry.NetworkService == null)
        {
            _clientEntry.NetworkService = new NetworkService
            {
                Name = "NetworkService",
                Entry = _clientEntry
            };
        }

        _clientEntry.NetworkService.Attach(_clientEntry.Root);
        _clientEntry.NetworkService.IsServer = false;
        _clientEntry.NetworkService.NetworkParent = _clientEntry.Root;

        // Set up bridge and current world (services come via replication from server)
        _clientEntry.DatamodelBridge?.Attach(_clientEntry.Root);
        World.Current = _clientEntry.Root;
        _clientEntry.SignalNetworkEssentialsReady();

        PT.Print("[P2P] Game ready as client");
    }
}
