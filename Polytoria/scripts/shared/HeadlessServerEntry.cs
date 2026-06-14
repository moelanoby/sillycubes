// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Client;
using Polytoria.Client.Settings;
using Polytoria.Client.WebAPI;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Services;
using Polytoria.Networking.P2P;
using Polytoria.Schemas.API;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Polytoria.Shared;

/// <summary>
/// Headless server entry point for running P2P or production servers without GUI.
/// </summary>
public sealed partial class HeadlessServerEntry : Node
{
    private ClientEntry? _clientEntry;
    private P2PManager? _p2pManager;
    private NetworkService? _networkService;

    public override async void _Ready()
    {
        PT.Print("[HeadlessServer] Starting headless server...");

        Dictionary<string, string> cmdargs = Globals.ReadCmdArgs();

        // Force headless mode
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Minimized);

        cmdargs.TryGetValue("token", out string? token);
        cmdargs.TryGetValue("world", out string? worldIdStr);
        cmdargs.TryGetValue("port", out string? portStr);
        cmdargs.TryGetValue("p2p", out string? p2pMode);
        cmdargs.TryGetValue("code", out string? serverCode);
        cmdargs.TryGetValue("username", out string? username);

        int port = 24221;
        if (portStr != null && int.TryParse(portStr, out int parsedPort))
        {
            port = parsedPort;
        }

        username ??= "HeadlessServer";

        // Create ClientEntry for datamodel/networking
        _clientEntry = new ClientEntry { Name = "ClientEntry" };
        AddChild(_clientEntry, true);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // Initialize essential services
        ClientSettingsService settings = new() { Name = "ClientSettings", Entry = _clientEntry };
        _clientEntry.AddChild(settings, true, InternalMode.Front);
        settings.Init();

        _clientEntry.DatamodelBridge = new DatamodelBridge { Name = "DatamodelBridge" };
        _clientEntry.AddChild(_clientEntry.DatamodelBridge, true);

        _networkService = new NetworkService { Name = "NetworkService", Entry = _clientEntry };
        _clientEntry.NetworkService = _networkService;

        _networkService.Attach(_clientEntry.Root);
        _networkService.NetworkParent = _clientEntry.Root;
        _clientEntry.AddChild(_clientEntry.Root.GDNode, true);
        _clientEntry.Root.Root = _clientEntry.Root;
        _clientEntry.Root.Entry = _clientEntry;
        _clientEntry.Root.World3D = _clientEntry.GetWorld3D();
        _clientEntry.Root.InitEntry();

        _clientEntry.DatamodelBridge.Attach(_clientEntry.Root);
        World.Current = _clientEntry.Root;

        if (!string.IsNullOrEmpty(p2pMode))
        {
            await RunP2PServer(username, port, serverCode);
        }
        else if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(worldIdStr))
        {
            await RunProductionServer(token, int.Parse(worldIdStr), port);
        }
        else
        {
            PT.PrintErr("[HeadlessServer] Missing arguments. Use:");
            PT.PrintErr("  P2P: -p2p true -port 24221 [-code SERVER_CODE] [-username NAME]");
            PT.PrintErr("  Prod: -token TOKEN -world WORLD_ID -port 24221");
            GetTree().Quit();
        }
    }

    private async Task RunP2PServer(string username, int port, string? serverCode)
    {
        PT.Print($"[HeadlessServer] Starting P2P server on port {port}...");

        _p2pManager = new P2PManager { Name = "P2PManager" };
        AddChild(_p2pManager);

        // Initialize DHT only (no WebUI)
        _p2pManager.InitializeDHTOnly(port + 1); // DHT on separate port

        if (!string.IsNullOrEmpty(serverCode))
        {
            // Join existing server
            await _p2pManager.JoinServer(serverCode, port);
        }
        else
        {
            // Create new server
            serverCode = await _p2pManager.CreateServer(port);
            PT.Print($"[HeadlessServer] Server code: {serverCode}");
            PT.Print($"[HeadlessServer] Share this code with clients to connect");
        }

        _networkService!.Attach(_clientEntry!.Root);
        _networkService.IsServer = true;
        _networkService.NetworkParent = _clientEntry.Root;
        _networkService.CreateP2P(port);

        PT.Print("[HeadlessServer] P2P server running. Press Ctrl+C to stop.");

        // Keep alive
        await KeepAlive();
    }

    private async Task RunProductionServer(string token, int worldId, int port)
    {
        PT.Print($"[HeadlessServer] Starting production server for world {worldId}...");

        _networkService!.IsProd = true;
        PolyAuthAPI.SetAuthToken(token);
        PolyServerAPI.SetAuthToken(token);
        Engine.MaxFps = 30;

        try
        {
            PT.Print("[HeadlessServer] Authenticating with backend...");
            APIServerListenResponse listenRes = await PolyAuthAPI.SendServerListen();

            PT.Print("[HeadlessServer] Server Info:");
            PT.Print($"  Server ID: {listenRes.ServerID}");
            PT.Print($"  World ID: {listenRes.WorldID}");
            PT.Print($"  Port: {listenRes.Port}");

            _clientEntry!.Root.WorldID = listenRes.WorldID;
            _clientEntry.Root.ServerID = listenRes.ServerID;

            PT.Print("[HeadlessServer] Downloading world...");
            byte[] worldContent = await PolyServerAPI.DownloadWorld(listenRes.WorldID);
            await DatamodelLoader.LoadWorldBytes(_clientEntry.Root, worldContent, listenRes.PlacePath);
            PT.Print("[HeadlessServer] World loaded!");

            int fport = listenRes.Port;
#if PT_DOCKER
            fport = 7777;
#endif

            _networkService.CreateServer(fport);
            PT.Print("[HeadlessServer] Production server running. Press Ctrl+C to stop.");

            await KeepAlive();
        }
        catch (Exception ex)
        {
            PT.PrintErr($"[HeadlessServer] Failed to start: {ex}");
            GetTree().Quit();
        }
    }

    private async Task KeepAlive()
    {
        // Handle signals for graceful shutdown
        if (OS.HasFeature("unix"))
        {
            // On Unix, we can't easily handle signals in C#, 
            // but Godot handles SIGTERM/SIGINT for us
        }

        // Wait indefinitely
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        while (true)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            PT.Print("[HeadlessServer] Shutting down...");
            _p2pManager?.LeaveServer();
            _networkService?.DisconnectSelf();
            GetTree().Quit();
        }
        base._Notification(what);
    }
}