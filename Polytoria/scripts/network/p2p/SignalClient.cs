// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Polytoria.Networking.P2P;

/// <summary>
/// Client that communicates with the local embedded web server or DHT directly.
/// Used by the game to create/join servers and interact with the DHT.
/// </summary>
public class SignalClient : IDisposable
{
    private readonly System.Net.Http.HttpClient? _http;
    private readonly string _baseUrl;
    private readonly KademliaDHT _dht;
    private string _serverCode = "";
    private bool _isHost = false;
    private readonly bool _useDhtDirect;

    public string ServerCode => _serverCode;
    public bool IsHost => _isHost;

    public event Action<string, ServerInfo>? ServerJoined;
    public event Action<string>? ServerCreated;
    public event Action? ServerLeft;
    public event Action<int, string>? PeerConnected;

    /// <summary>
    /// Creates a SignalClient that uses the WebServer (webPort > 0) or DHT directly (webPort = 0).
    /// </summary>
    public SignalClient(KademliaDHT dht, int webPort = 24222)
    {
        _dht = dht;
        _useDhtDirect = webPort == 0;

        if (!_useDhtDirect)
        {
            _baseUrl = $"http://localhost:{webPort}";
            _http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }
        else
        {
            _baseUrl = "";
            _http = null;
            PT.Print("[Signal] DHT-direct mode enabled");
        }
    }

    /// <summary>
    /// Create a new server and get a code.
    /// </summary>
    public async Task<string> CreateServer(string username, int gamePort = 24221)
    {
        try
        {
            if (!_useDhtDirect)
            {
                string json = JsonSerializer.Serialize(new { username });
                System.Net.Http.StringContent content = new(json, System.Text.Encoding.UTF8, "application/json");

                System.Net.Http.HttpResponseMessage response = await _http!.PostAsync($"{_baseUrl}/api/servers", content);
                string body = await response.Content.ReadAsStringAsync();

                using JsonDocument doc = JsonDocument.Parse(body);
                _serverCode = doc.RootElement.GetProperty("code").GetString() ?? "";
            }
            else
            {
                // Generate code directly and store in DHT
                _serverCode = GenerateServerCode();
            }

            _isHost = true;

            // Store server info in DHT
            ServerInfo server = new()
            {
                Code = _serverCode,
                HostUsername = username,
                Port = gamePort,
                CreatedAt = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow
            };

            await _dht.Store($"server:{_serverCode}", JsonSerializer.Serialize(server), TimeSpan.FromMinutes(30));

            ServerCreated?.Invoke(_serverCode);
            PT.Print($"[Signal] Server created: {_serverCode}");

            return _serverCode;
        }
        catch (Exception ex)
        {
            GD.PushError($"[Signal] Failed to create server: {ex.Message}");
            return "";
        }
    }

    private static string GenerateServerCode()
    {
        // Generate a 6-character alphanumeric code
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        return new string(Enumerable.Range(0, 6).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    /// <summary>
    /// Join a server by code.
    /// </summary>
    public async Task<ServerInfo?> JoinServer(string code)
    {
        try
        {
            // Read from DHT directly (avoids recursive HTTP loop through WebServer)
            string? existing = await _dht.GetString($"server:{code}");
            if (existing == null)
            {
                PT.PrintErr($"[Signal] Server not found in DHT: {code}");
                return null;
            }

            ServerInfo? server = JsonSerializer.Deserialize<ServerInfo>(existing);
            if (server == null)
            {
                PT.PrintErr($"[Signal] Failed to parse server info from DHT: {code}");
                return null;
            }

            _serverCode = code;
            _isHost = false;

            ServerJoined?.Invoke(code, server);
            PT.Print($"[Signal] Joined server: {code}");

            return server;
        }
        catch (Exception ex)
        {
            GD.PushError($"[Signal] Failed to join server: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Send heartbeat to keep server alive.
    /// </summary>
    public async Task SendHeartbeat(string ip, int port, int playerCount)
    {
        if (string.IsNullOrEmpty(_serverCode)) return;

        try
        {
            string json = JsonSerializer.Serialize(new { ip, port, playerCount });
            System.Net.Http.StringContent content = new(json, System.Text.Encoding.UTF8, "application/json");

            await _http.PutAsync($"{_baseUrl}/api/servers/{_serverCode}/heartbeat", content);

            // Also update DHT
            string? existing = await _dht.GetString($"server:{_serverCode}");
            if (existing != null)
            {
                ServerInfo? server = JsonSerializer.Deserialize<ServerInfo>(existing);
                if (server != null)
                {
                    server.Ip = ip;
                    server.Port = port;
                    server.PlayerCount = playerCount;
                    server.LastHeartbeat = DateTime.UtcNow;
                    await _dht.Store($"server:{_serverCode}", JsonSerializer.Serialize(server), TimeSpan.FromMinutes(30));
                }
            }
        }
        catch (Exception ex)
        {
            GD.PushError($"[Signal] Heartbeat failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Leave the current server.
    /// </summary>
    public async Task LeaveServer()
    {
        if (string.IsNullOrEmpty(_serverCode)) return;

        try
        {
            await _http.DeleteAsync($"{_baseUrl}/api/servers/{_serverCode}");
            _dht.Remove($"server:{_serverCode}");

            PT.Print($"[Signal] Left server: {_serverCode}");
            _serverCode = "";
            _isHost = false;
            ServerLeft?.Invoke();
        }
        catch (Exception ex)
        {
            GD.PushError($"[Signal] Failed to leave server: {ex.Message}");
        }
    }

    /// <summary>
    /// Get DHT network stats.
    /// </summary>
    public async Task<(int NodeCount, string NodeId)?> GetDhtStats()
    {
        try
        {
            System.Net.Http.HttpResponseMessage response = await _http.GetAsync($"{_baseUrl}/api/dht/stats");
            string body = await response.Content.ReadAsStringAsync();

            using JsonDocument doc = JsonDocument.Parse(body);
            int nodeCount = doc.RootElement.GetProperty("nodeCount").GetInt32();
            string nodeId = doc.RootElement.GetProperty("nodeId").GetString() ?? "";

            return (nodeCount, nodeId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get list of servers from DHT.
    /// </summary>
    public async Task<List<ServerInfo>> GetServers()
    {
        try
        {
            System.Net.Http.HttpResponseMessage response = await _http.GetAsync($"{_baseUrl}/api/servers");
            string body = await response.Content.ReadAsStringAsync();

            using JsonDocument doc = JsonDocument.Parse(body);
            List<ServerInfo> servers = [];

            if (doc.RootElement.TryGetProperty("servers", out JsonElement serversArray))
            {
                foreach (JsonElement serverEl in serversArray.EnumerateArray())
                {
                    servers.Add(new ServerInfo
                    {
                        Code = serverEl.GetProperty("code").GetString() ?? "",
                        HostUsername = serverEl.GetProperty("hostUsername").GetString() ?? "",
                        Ip = serverEl.TryGetProperty("ip", out JsonElement ipEl) ? ipEl.GetString() : null,
                        Port = serverEl.GetProperty("port").GetInt32(),
                        PlayerCount = serverEl.GetProperty("playerCount").GetInt32()
                    });
                }
            }

            return servers;
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
