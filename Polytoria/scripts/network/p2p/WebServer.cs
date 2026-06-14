// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Networking.P2P;

/// <summary>
/// Embedded web server that serves the React frontend and provides
/// a JSON API + WebSocket for real-time communication with the game.
/// Runs on localhost:24222.
/// </summary>
public class WebServer : IDisposable
{
    private const int DefaultPort = 24222;

    private readonly HttpListener _listener;
    private readonly int _port;
    private CancellationTokenSource? _cts;
    private bool _running;

    private readonly KademliaDHT _dht;
    private readonly ConcurrentDictionary<string, WebSocketConnection> _webSockets = [];
    private string _localUsername = "Player";
    private readonly string _gamesDir;

    public event Action? ServerStarted;
    public event Action<string>? ServerJoinRequested;
    public event Action<string, int>? ServerCreated;
    public event Action<int, string>? PeerConnectedToGame;

    public int Port => _port;
    public bool IsRunning => _running;
    public string LocalUsername
    {
        get => _localUsername;
        set
        {
            _localUsername = value;
            BroadcastWsMessage(new JsonObject
            {
                ["type"] = "username",
                ["username"] = value
            });
        }
    }

    public WebServer(KademliaDHT dht, int port = DefaultPort)
    {
        _dht = dht;
        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _gamesDir = Path.Combine(AppContext.BaseDirectory, "userdata", "games");
        Directory.CreateDirectory(_gamesDir);
    }

    /// <summary>
    /// Start the web server.
    /// </summary>
    public void Start()
    {
        if (_running) return;

        _cts = new CancellationTokenSource();
        _listener.Start();
        _running = true;

        _ = Task.Run(AcceptLoop);

        PT.Print($"[WebUI] Server started at http://localhost:{_port}");
        ServerStarted?.Invoke();
    }

    /// <summary>
    /// Stop the web server.
    /// </summary>
    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _listener.Stop();

        foreach ((_, WebSocketConnection ws) in _webSockets)
        {
            ws.Close();
        }
        _webSockets.Clear();

        PT.Print("[WebUI] Server stopped");
    }

    /// <summary>
    /// Broadcast a message to all connected WebSocket clients.
    /// </summary>
    public void BroadcastWsMessage(JsonNode message)
    {
        string json = message.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        byte[] data = Encoding.UTF8.GetBytes(json);

        foreach ((string id, WebSocketConnection ws) in _webSockets)
        {
            if (ws.IsConnected)
            {
                _ = Task.Run(() => ws.SendFrame(data));
            }
        }
    }

    /// <summary>
    /// Send a server list update to all clients.
    /// </summary>
    public void BroadcastServerList(List<ServerInfo> servers)
    {
        var arr = new JsonArray();
        foreach (var s in servers)
            arr.Add(JsonSerializer.SerializeToNode(s, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        BroadcastWsMessage(new JsonObject
        {
            ["type"] = "server_list",
            ["servers"] = arr
        });
    }

    /// <summary>
    /// Notify clients about a peer connecting.
    /// </summary>
    public void NotifyPeerConnected(string username, int peerId)
    {
        BroadcastWsMessage(new JsonObject
        {
            ["type"] = "peer_connected",
            ["username"] = username,
            ["peerId"] = peerId
        });
        PeerConnectedToGame?.Invoke(peerId, username);
    }

    /// <summary>
    /// Notify clients about a peer disconnecting.
    /// </summary>
    public void NotifyPeerDisconnected(string username, int peerId)
    {
        BroadcastWsMessage(new JsonObject
        {
            ["type"] = "peer_disconnected",
            ["username"] = username,
            ["peerId"] = peerId
        });
    }

    private async Task AcceptLoop()
    {
        while (_running)
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_running) GD.PushError($"[WebUI] Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        string path = request.Url?.AbsolutePath ?? "/";

        try
        {
            if (path == "/ws")
            {
                await HandleWebSocket(context);
            }
            else if (path == "/api/servers" && request.HttpMethod == "GET")
            {
                await HandleGetServers(response);
            }
            else if (path == "/api/servers" && request.HttpMethod == "POST")
            {
                await HandleCreateServer(request, response);
            }
            else if (path.StartsWith("/api/servers/") && path.EndsWith("/join") && request.HttpMethod == "POST")
            {
                string code = path.Split('/')[3];
                await HandleJoinServer(code, request, response);
            }
            else if (path.StartsWith("/api/servers/") && path.EndsWith("/heartbeat") && request.HttpMethod == "PUT")
            {
                string code = path.Split('/')[3];
                await HandleHeartbeat(code, request, response);
            }
            else if (path.StartsWith("/api/servers/") && request.HttpMethod == "DELETE")
            {
                string code = path.Split('/')[3];
                await HandleDeleteServer(code, response);
            }
            else if (path == "/api/username" && request.HttpMethod == "POST")
            {
                await HandleSetUsername(request, response);
            }
            else if (path == "/api/dht/stats" && request.HttpMethod == "GET")
            {
                HandleDhtStats(response);
            }
            else if (path == "/api/dht/bootstrap" && request.HttpMethod == "POST")
            {
                await HandleDhtBootstrap(request, response);
            }
            else if (path == "/api/peers" && request.HttpMethod == "GET")
            {
                HandleGetPeers(response);
            }
            else if (path == "/api/world/load" && request.HttpMethod == "POST")
            {
                await HandleLoadWorld(request, response);
            }
            else if (path == "/api/games" && request.HttpMethod == "GET")
            {
                HandleGetGames(response);
            }
            else if (path == "/api/games" && request.HttpMethod == "POST")
            {
                await HandleUploadGame(request, response);
            }
            else if (path.StartsWith("/api/games/") && path.EndsWith("/load") && request.HttpMethod == "POST")
            {
                string name = Uri.UnescapeDataString(path.Split('/')[3]);
                await HandleLoadGame(name, response);
            }
            else if (path.StartsWith("/api/games/") && request.HttpMethod == "DELETE")
            {
                string name = Uri.UnescapeDataString(path.Split('/')[3]);
                HandleDeleteGame(name, response);
            }
            else
            {
                await ServeStaticFile(path, response);
            }
        }
        catch (Exception ex)
        {
            GD.PushError($"[WebUI] Request error: {ex.Message}");
            SendJson(response, HttpStatusCode.InternalServerError, new JsonObject { ["error"] = ex.Message });
        }
        finally
        {
            response.Close();
        }
    }

    // === API Handlers ===

    private async Task HandleGetServers(HttpListenerResponse response)
    {
        // Query DHT for servers
        List<ServerInfo> servers = await DiscoverServers();
        SendJson(response, HttpStatusCode.OK, new JsonObject { ["servers"] = JsonSerializer.SerializeToNode(servers, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) });
    }

    private async Task HandleCreateServer(HttpListenerRequest request, HttpListenerResponse response)
    {
        string code = GenerateServerCode();
        string? username = await ReadBodyString(request);

        ServerInfo server = new()
        {
            Code = code,
            HostUsername = username ?? _localUsername,
            CreatedAt = DateTime.UtcNow,
            LastHeartbeat = DateTime.UtcNow
        };

        // Store in DHT
        await _dht.Store($"server:{code}", JsonSerializer.Serialize(server), TimeSpan.FromMinutes(30));

        ServerCreated?.Invoke(code, _port);
        PT.Print($"[WebUI] Server created: {code}");

        SendJson(response, HttpStatusCode.OK, new JsonObject { ["code"] = code, ["server"] = JsonSerializer.SerializeToNode(server, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) });
    }

    private async Task HandleJoinServer(string code, HttpListenerRequest request, HttpListenerResponse response)
    {
        string? json = await _dht.GetString($"server:{code}");

        if (json == null)
        {
            SendJson(response, HttpStatusCode.NotFound, new JsonObject { ["error"] = "Server not found" });
            return;
        }

        ServerInfo? server = JsonSerializer.Deserialize<ServerInfo>(json);
        if (server == null)
        {
            SendJson(response, HttpStatusCode.NotFound, new JsonObject { ["error"] = "Invalid server data" });
            return;
        }

        ServerJoinRequested?.Invoke(code);
        PT.Print($"[WebUI] Joining server: {code}");

        SendJson(response, HttpStatusCode.OK, new JsonObject { ["server"] = JsonSerializer.SerializeToNode(server, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) });
    }

    private async Task HandleHeartbeat(string code, HttpListenerRequest request, HttpListenerResponse response)
    {
        string? body = await ReadBodyString(request);
        if (body == null)
        {
            SendJson(response, HttpStatusCode.BadRequest, new JsonObject { ["error"] = "No body" });
            return;
        }

        HeartbeatData? data = JsonSerializer.Deserialize<HeartbeatData>(body);
        if (data == null)
        {
            SendJson(response, HttpStatusCode.BadRequest, new JsonObject { ["error"] = "Invalid data" });
            return;
        }

        // Update server entry in DHT
        string? json = await _dht.GetString($"server:{code}");
        ServerInfo? server = json != null ? JsonSerializer.Deserialize<ServerInfo>(json) : null;

        if (server != null)
        {
            server.Ip = data.Ip;
            server.Port = data.Port;
            server.LastHeartbeat = DateTime.UtcNow;
            server.PlayerCount = data.PlayerCount;

            await _dht.Store($"server:{code}", JsonSerializer.Serialize(server), TimeSpan.FromMinutes(30));
        }

        SendJson(response, HttpStatusCode.OK, new JsonObject { ["ok"] = true });
    }

    private async Task HandleDeleteServer(string code, HttpListenerResponse response)
    {
        _dht.Remove($"server:{code}");
        PT.Print($"[WebUI] Server deleted: {code}");
        SendJson(response, HttpStatusCode.OK, new JsonObject { ["ok"] = true });
    }

    private async Task HandleSetUsername(HttpListenerRequest request, HttpListenerResponse response)
    {
        string? username = await ReadBodyString(request);
        if (username != null)
        {
            _localUsername = username;
            // Store in DHT
            await _dht.Store($"user:{username}", new JsonObject { ["username"] = username!, ["lastSeen"] = DateTime.UtcNow }.ToJsonString());
        }
        SendJson(response, HttpStatusCode.OK, new JsonObject { ["ok"] = true, ["username"] = _localUsername });
    }

    private void HandleDhtStats(HttpListenerResponse response)
    {
        SendJson(response, HttpStatusCode.OK, new JsonObject
        {
            ["nodeId"] = _dht.NodeIdHex,
            ["nodeCount"] = _dht.NodeCount,
            ["port"] = _dht.Port
        });
    }

    private async Task HandleDhtBootstrap(HttpListenerRequest request, HttpListenerResponse response)
    {
        string? body = await ReadBodyString(request);
        BootstrapRequest? req = body != null ? JsonSerializer.Deserialize<BootstrapRequest>(body) : null;

        if (req?.Host != null && req.Port > 0)
        {
            await _dht.Bootstrap(req.Host, req.Port);
        }

        SendJson(response, HttpStatusCode.OK, new JsonObject { ["ok"] = true, ["nodeCount"] = _dht.NodeCount });
    }

    private void HandleGetPeers(HttpListenerResponse response)
    {
        List<KBucketNode> nodes = _dht.GetAllNodes();
        var peersArr = new JsonArray();
        foreach (var n in nodes.Take(50))
        {
            peersArr.Add(new JsonObject
            {
                ["nodeId"] = Convert.ToHexString(n.NodeId[..8]),
                ["ip"] = n.EndPoint.Address.ToString(),
                ["port"] = n.EndPoint.Port,
                ["lastSeen"] = n.LastSeen
            });
        }

        SendJson(response, HttpStatusCode.OK, new JsonObject { ["peers"] = peersArr });
    }

    private async Task HandleLoadWorld(HttpListenerRequest request, HttpListenerResponse response)
    {
        PT.Print("[WebUI] Loading local world...");
        
        try
        {
            if (!request.HasEntityBody)
            {
                SendJson(response, HttpStatusCode.BadRequest, new JsonObject { ["error"] = "No file uploaded" });
                return;
            }

            string contentType = request.ContentType ?? "";
            if (!contentType.StartsWith("multipart/form-data"))
            {
                SendJson(response, HttpStatusCode.BadRequest, new JsonObject { ["error"] = "Expected multipart/form-data" });
                return;
            }

            string boundary = GetBoundary(contentType);
            if (string.IsNullOrEmpty(boundary))
            {
                SendJson(response, HttpStatusCode.BadRequest, new JsonObject { ["error"] = "Missing boundary" });
                return;
            }

            var (fileData, _) = await ParseMultipartFile(request.InputStream, boundary);
            if (fileData == null || fileData.Length == 0)
            {
                SendJson(response, HttpStatusCode.BadRequest, new JsonObject { ["error"] = "No file data found" });
                return;
            }

            PT.Print($"[WebUI] Received world file: {fileData.Length} bytes");

            string tempDir = Path.Combine(Path.GetTempPath(), "polytoria-webui");
            Directory.CreateDirectory(tempDir);
            string tempPath = Path.Combine(tempDir, "upload_" + Guid.NewGuid() + ".poly");
            await File.WriteAllBytesAsync(tempPath, fileData);

            WorldLoadRequested?.Invoke(tempPath);

            SendJson(response, HttpStatusCode.OK, new JsonObject { ["success"] = true, ["size"] = fileData.Length, ["path"] = tempPath });
        }
        catch (Exception ex)
        {
            GD.PushError($"[WebUI] Load world error: {ex}");
            SendJson(response, HttpStatusCode.InternalServerError, new JsonObject { ["error"] = ex.Message });
        }
    }

    public event Action<string>? WorldLoadRequested;

    // === Game Storage API ===

    private record GameMetadata(string Name, string FileName, long FileSize, DateTime CreatedAt);

    private static string SanitizeGameName(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(raw.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "game" : sanitized.Trim();
    }

    private string GameDir(string id) => Path.Combine(_gamesDir, id);
    private string GamePolyPath(string id) => Path.Combine(GameDir(id), $"{id}.poly");
    private string GameMetaPath(string id) => Path.Combine(GameDir(id), "meta.json");

    private void HandleGetGames(HttpListenerResponse response)
    {
        var games = new List<JsonNode>();

        if (!Directory.Exists(_gamesDir))
        {
            SendJson(response, HttpStatusCode.OK, new JsonObject { ["games"] = new JsonArray() });
            return;
        }

        foreach (string dir in Directory.GetDirectories(_gamesDir))
        {
            string id = Path.GetFileName(dir);
            string metaPath = GameMetaPath(id);
            if (File.Exists(metaPath))
            {
                try
                {
                    string metaJson = File.ReadAllText(metaPath);
                    var metaObj = JsonNode.Parse(metaJson) as JsonObject;
                    if (metaObj != null)
                    {
                        games.Add(new JsonObject
                        {
                            ["id"] = id,
                            ["name"] = metaObj["name"]?.GetValue<string>() ?? "",
                            ["fileName"] = metaObj["fileName"]?.GetValue<string>() ?? "",
                            ["fileSize"] = metaObj["fileSize"]?.GetValue<long>() ?? 0,
                            ["createdAt"] = metaObj["createdAt"]?.GetValue<DateTime>() ?? DateTime.MinValue
                        });
                    }
                }
                catch { }
            }
        }

        games.Sort((a, b) =>
        {
            var aObj = (JsonObject)a;
            var bObj = (JsonObject)b;
            DateTime aTime = aObj["createdAt"]!.GetValue<DateTime>();
            DateTime bTime = bObj["createdAt"]!.GetValue<DateTime>();
            return bTime.CompareTo(aTime);
        });

        var gamesArr = new JsonArray();
        foreach (var g in games) gamesArr.Add(g);
        SendJson(response, HttpStatusCode.OK, new JsonObject { ["games"] = gamesArr });
    }

    private async Task HandleUploadGame(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            if (!request.HasEntityBody)
            {
                SendJson(response, HttpStatusCode.BadRequest, new JsonObject { ["error"] = "No file uploaded" });
                return;
            }

            string contentType = request.ContentType ?? "";
            if (!contentType.StartsWith("multipart/form-data"))
            {
                SendJson(response, HttpStatusCode.BadRequest, new JsonObject { ["error"] = "Expected multipart/form-data" });
                return;
            }

            string boundary = GetBoundary(contentType);
            if (string.IsNullOrEmpty(boundary))
            {
                SendJson(response, HttpStatusCode.BadRequest, new JsonObject { ["error"] = "Missing boundary" });
                return;
            }

            var (fileData, originalName) = await ParseMultipartFile(request.InputStream, boundary);
            if (fileData == null || fileData.Length == 0)
            {
                SendJson(response, HttpStatusCode.BadRequest, new JsonObject { ["error"] = "No file data found" });
                return;
            }

            string baseName = Path.GetFileNameWithoutExtension(originalName ?? "Unnamed");
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Unnamed";

            string id = SanitizeGameName(baseName);
            if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N")[..8];

            string finalId = id;
            int counter = 1;
            while (Directory.Exists(GameDir(finalId)))
            {
                finalId = $"{id}_{counter}";
                counter++;
            }

            Directory.CreateDirectory(GameDir(finalId));
            await File.WriteAllBytesAsync(GamePolyPath(finalId), fileData);

            var meta = new GameMetadata(
                Name: baseName,
                FileName: originalName,
                FileSize: fileData.Length,
                CreatedAt: DateTime.UtcNow
            );
            await File.WriteAllTextAsync(GameMetaPath(finalId), new JsonObject
            {
                ["name"] = meta.Name,
                ["fileName"] = meta.FileName,
                ["fileSize"] = meta.FileSize,
                ["createdAt"] = meta.CreatedAt
            }.ToJsonString());

            PT.Print($"[WebUI] Game saved: {finalId} ({fileData.Length} bytes)");

            SendJson(response, HttpStatusCode.OK, new JsonObject
            {
                ["success"] = true,
                ["game"] = new JsonObject
                {
                    ["id"] = finalId,
                    ["name"] = meta.Name,
                    ["fileName"] = meta.FileName,
                    ["fileSize"] = meta.FileSize,
                    ["createdAt"] = meta.CreatedAt
                }
            });
        }
        catch (Exception ex)
        {
            GD.PushError($"[WebUI] Upload game error: {ex}");
            SendJson(response, HttpStatusCode.InternalServerError, new JsonObject { ["error"] = ex.Message });
        }
    }

    private async Task HandleLoadGame(string id, HttpListenerResponse response)
    {
        string polyPath = GamePolyPath(id);
        if (!File.Exists(polyPath))
        {
            SendJson(response, HttpStatusCode.NotFound, new JsonObject { ["error"] = "Game not found" });
            return;
        }

        WorldLoadRequested?.Invoke(polyPath);

        var fileInfo = new FileInfo(polyPath);
        SendJson(response, HttpStatusCode.OK, new JsonObject { ["success"] = true, ["size"] = fileInfo.Exists ? fileInfo.Length : 0 });
    }

    private void HandleDeleteGame(string id, HttpListenerResponse response)
    {
        string dir = GameDir(id);
        if (!Directory.Exists(dir))
        {
            SendJson(response, HttpStatusCode.NotFound, new JsonObject { ["error"] = "Game not found" });
            return;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
            PT.Print($"[WebUI] Game deleted: {id}");
            SendJson(response, HttpStatusCode.OK, new JsonObject { ["success"] = true });
        }
        catch (Exception ex)
        {
            GD.PushError($"[WebUI] Delete game error: {ex}");
            SendJson(response, HttpStatusCode.InternalServerError, new JsonObject { ["error"] = ex.Message });
        }
    }

    private static string? GetBoundary(string contentType)
    {
        var parts = contentType.Split(';');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("boundary="))
            {
                return trimmed.Substring("boundary=".Length).Trim('"');
            }
        }
        return null;
    }

    private static async Task<(byte[] Data, string? FileName)> ParseMultipartFile(Stream inputStream, string boundary)
    {
        using var ms = new System.IO.MemoryStream();
        await inputStream.CopyToAsync(ms);
        byte[] rawData = ms.ToArray();

        string rawString = System.Text.Encoding.UTF8.GetString(rawData);
        string? fileName = null;
        int filenameIdx = rawString.IndexOf("filename=\"", StringComparison.Ordinal);
        if (filenameIdx != -1)
        {
            int start = filenameIdx + 10;
            int end = rawString.IndexOf('"', start);
            if (end > start)
            {
                fileName = rawString[start..end];
            }
        }

        // The body starts with --boundary, subsequent parts have \r\n--boundary
        byte[] firstDelimiter = System.Text.Encoding.UTF8.GetBytes("--" + boundary);
        byte[] nextDelimiter = System.Text.Encoding.UTF8.GetBytes("\r\n--" + boundary);
        byte[] endDelimiter = System.Text.Encoding.UTF8.GetBytes("\r\n--" + boundary + "--");
        byte[] headerSeparator = System.Text.Encoding.UTF8.GetBytes("\r\n\r\n");

        int startIndex = IndexOfBytes(rawData, firstDelimiter);
        if (startIndex == -1) return ([], null);

        startIndex += firstDelimiter.Length;
        int headerEnd = IndexOfBytes(rawData, headerSeparator, startIndex);
        if (headerEnd == -1) return ([], null);

        int dataStart = headerEnd + headerSeparator.Length;
        int dataEnd = IndexOfBytes(rawData, nextDelimiter, dataStart);
        if (dataEnd == -1)
        {
            dataEnd = IndexOfBytes(rawData, endDelimiter, dataStart);
        }
        if (dataEnd == -1) return ([], null);

        byte[] fileData = new byte[dataEnd - dataStart];
        System.Array.Copy(rawData, dataStart, fileData, 0, fileData.Length);
        return (fileData, fileName);
    }

    private static int IndexOfBytes(byte[] source, byte[] pattern, int startIndex = 0)
    {
        for (int i = startIndex; i <= source.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }


    // === WebSocket ===

    private async Task HandleWebSocket(HttpListenerContext context)
    {
        try
        {
            System.Net.WebSockets.WebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
            System.Net.WebSockets.WebSocket socket = wsContext.WebSocket;

            string connectionId = Guid.NewGuid().ToString("N")[..8];
            WebSocketConnection ws = new(socket);
            _webSockets[connectionId] = ws;

            PT.Print($"[WebUI] WebSocket connected: {connectionId}");

            // Send initial state
            byte[] connectedMsg = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["type"] = "connected",
                ["connectionId"] = connectionId,
                ["username"] = _localUsername,
                ["nodeId"] = _dht.NodeIdHex[..16]
            }.ToJsonString());
            await ws.SendFrame(connectedMsg);

            // Listen for messages
            try
            {
                while (ws.IsConnected)
                {
                    byte[]? data = await ws.ReceiveFrame();
                    if (data == null) break;

                    string message = Encoding.UTF8.GetString(data);
                    await HandleWebSocketMessage(connectionId, message);
                }
            }
            catch { }

            _webSockets.TryRemove(connectionId, out _);
            ws.Close();
            PT.Print($"[WebUI] WebSocket disconnected: {connectionId}");
        }
        catch (Exception ex)
        {
            PT.PrintErr($"[WebUI] WebSocket upgrade failed: {ex.Message}");
        }
    }

    private async Task HandleWebSocketMessage(string connectionId, string message)
    {
        using JsonDocument doc = JsonDocument.Parse(message);
        string? type = doc.RootElement.GetProperty("type").GetString();

        switch (type)
        {
            case "get_servers":
                List<ServerInfo> servers = await DiscoverServers();
                WebSocketConnection? ws = _webSockets.GetValueOrDefault(connectionId);
                if (ws != null)
                {
                    var arr = new JsonArray();
                    foreach (var s in servers)
                        arr.Add(JsonSerializer.SerializeToNode(s, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                    await ws.SendFrame(Encoding.UTF8.GetBytes(new JsonObject
                    {
                        ["type"] = "server_list",
                        ["servers"] = arr
                    }.ToJsonString()));
                }
                break;

            case "create_server":
                string code = GenerateServerCode();
                ServerInfo server = new()
                {
                    Code = code,
                    HostUsername = _localUsername,
                    CreatedAt = DateTime.UtcNow,
                    LastHeartbeat = DateTime.UtcNow
                };
                await _dht.Store($"server:{code}", JsonSerializer.Serialize(server), TimeSpan.FromMinutes(30));
                ServerCreated?.Invoke(code, _port);
                BroadcastWsMessage(new JsonObject
                {
                    ["type"] = "server_created",
                    ["code"] = code,
                    ["server"] = JsonSerializer.SerializeToNode(server, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                });
                break;

            case "set_username":
                if (doc.RootElement.TryGetProperty("username", out JsonElement usernameEl))
                {
                    _localUsername = usernameEl.GetString() ?? "Player";
                    BroadcastWsMessage(new JsonObject
                    {
                        ["type"] = "username",
                        ["username"] = _localUsername
                    });
                }
                break;

            case "ping":
                WebSocketConnection? sender = _webSockets.GetValueOrDefault(connectionId);
                if (sender != null)
                {
                    await sender.SendFrame(Encoding.UTF8.GetBytes("{\"type\":\"pong\"}"));
                }
                break;
        }
    }

    // === Static File Serving ===

    private async Task ServeStaticFile(string path, HttpListenerResponse response)
    {
        if (path == "/") path = "/index.html";

        byte[]? content = GetEmbeddedResource(path);
        if (content == null)
        {
            response.StatusCode = 404;
            byte[] notFound = Encoding.UTF8.GetBytes("Not Found");
            await response.OutputStream.WriteAsync(notFound);
            return;
        }

        string contentType = GetContentType(path);
        response.ContentType = contentType;
        response.ContentLength64 = content.Length;
        await response.OutputStream.WriteAsync(content);
    }

    private byte[]? GetEmbeddedResource(string path)
    {
        // Try to load from embedded resources
        Assembly assembly = Assembly.GetExecutingAssembly();
        string resourceName = $"Polytoria.webui{path.Replace('/', '.')}";

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream != null)
        {
            using MemoryStream ms = new();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        // Fallback: try loading from filesystem (for development)
        string devPath = Path.Combine(AppContext.BaseDirectory, "webui", path.TrimStart('/'));
        if (File.Exists(devPath))
        {
            return File.ReadAllBytes(devPath);
        }

        // Fallback: try loading from source dist directory
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "scripts", "network", "p2p", "webui", "dist", path.TrimStart('/'));
        if (File.Exists(sourcePath))
        {
            return File.ReadAllBytes(sourcePath);
        }

        return null;
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html",
            ".js" => "application/javascript",
            ".css" => "text/css",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".woff" or ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }

    // === Utility ===

    private static string GenerateServerCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        char[] code = new char[9];
        System.Security.Cryptography.RandomNumberGenerator.Fill(MemoryMarshal.AsBytes(code.AsSpan()));
        for (int i = 0; i < 9; i++)
        {
            code[i] = chars[code[i] % chars.Length];
            if (i == 4) code[i] = '-';
        }
        return new string(code);
    }

    private static void SendJson(HttpListenerResponse response, HttpStatusCode status, JsonNode data)
    {
        response.StatusCode = (int)status;
        response.ContentType = "application/json";

        string json = data.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
    }

    private static async Task<string?> ReadBodyString(HttpListenerRequest request)
    {
        using StreamReader reader = new(request.InputStream, request.ContentEncoding);
        return await reader.ReadToEndAsync();
    }

    private async Task<List<ServerInfo>> DiscoverServers()
    {
        List<ServerInfo> servers = [];

        // Query all server keys from local DHT storage
        List<string> serverKeys = _dht.GetKeysByPrefix("server:");

        foreach (string key in serverKeys)
        {
            string? json = await _dht.GetString(key);
            if (json != null)
            {
                ServerInfo? server = JsonSerializer.Deserialize<ServerInfo>(json);
                if (server != null)
                {
                    servers.Add(server);
                }
            }
        }

        return servers;
    }

    public void Dispose()
    {
        Stop();
        ((IDisposable)_listener).Dispose();
        _cts?.Dispose();
    }
}

// === Data Models ===

public class ServerInfo
{
    public string Code { get; set; } = "";
    public string HostUsername { get; set; } = "";
    public string? Ip { get; set; }
    public int Port { get; set; }
    public int PlayerCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
}

public class HeartbeatData
{
    public string? Ip { get; set; }
    public int Port { get; set; }
    public int PlayerCount { get; set; }
}

public class BootstrapRequest
{
    public string? Host { get; set; }
    public int Port { get; set; }
}

// === WebSocket Connection ===

public class WebSocketConnection : IDisposable
{
    private readonly System.Net.WebSockets.WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _connected = true;

    public bool IsConnected => _connected;

    public WebSocketConnection(System.Net.WebSockets.WebSocket socket)
    {
        _socket = socket;
    }

    public async Task SendFrame(byte[] data)
    {
        if (!_connected) return;

        await _sendLock.WaitAsync();
        try
        {
            await _socket.SendAsync(data, System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
            _connected = false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<byte[]?> ReceiveFrame()
    {
        if (!_connected) return null;

        try
        {
            using var ms = new MemoryStream();
            var buffer = new byte[4096];

            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, CancellationToken.None);

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    _connected = false;
                    return null;
                }

                ms.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                    break;
            }

            return ms.ToArray();
        }
        catch
        {
            _connected = false;
            return null;
        }
    }

    public async void Close()
    {
        _connected = false;
        try
        {
            await _socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        }
        catch { }
    }

    public void Dispose()
    {
        _sendLock.Dispose();
        _socket.Dispose();
    }
}
