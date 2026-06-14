// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Client.WebAPI.Interfaces;
using Polytoria.Schemas.API;
using Polytoria.Shared;
using Polytoria.Utils;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Polytoria.Client.WebAPI;

internal sealed class PolyClientConnector : IClientConnector
{
    private string _token = "";
    private readonly PTHttpClient _client = new();

    public void SetToken(string token)
    {
        _token = token;
        _client.DefaultRequestHeaders.Remove("Authorization");
        _client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
    }

    public async Task<APIServerStatus> CheckServerStatus()
    {
        return await _client.GetFromJsonAsync(
            Globals.ApiEndpoint.PathJoin("/v1/game/server/status"),
            AuthAPIGenerationContext.Default.APIServerStatus
        );
    }

    public async Task<APIClientAuthResponseMessage> Connect()
    {
        return await _client.GetFromJsonAsync(
            Globals.ApiEndpoint.PathJoin("/v1/game/client/connect"),
            AuthAPIGenerationContext.Default.APIClientAuthResponseMessage
        );
    }
}

internal sealed class PolyServerListener : IServerListener
{
    private string _token = "";
    private readonly PTHttpClient _client = new();

    public void SetToken(string token)
    {
        _token = token;
        _client.DefaultRequestHeaders.Remove("Authorization");
        _client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
    }

    public async Task<APIServerListenResponse> Listen()
    {
        using HttpResponseMessage response = await _client.PostAsync(
            Globals.ApiEndpoint.PathJoin("/v1/game/server/listen"),
            new StringContent("{}", Encoding.UTF8, "application/json")
        );
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(json, AuthAPIGenerationContext.Default.APIServerListenResponse)!;
    }
}

internal sealed class PolyServerInterface : IServerInterface
{
    private string _token = "";
    private readonly PTHttpClient _client = new();

    public void SetToken(string token)
    {
        _token = token;
        _client.DefaultRequestHeaders.Remove("Authorization");
        _client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
    }

    public async Task<byte[]> DownloadWorld(int worldID)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            Globals.ApiEndpoint.PathJoin($"/v1/places/{worldID}/download")
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task<APIHeartbeatResponse> Heartbeat(int[] playerIDs)
    {
        var request = new { playerIDs };
        string json = JsonSerializer.Serialize(request);
        using HttpResponseMessage response = await _client.PostAsync(
            Globals.ApiEndpoint.PathJoin("/v1/game/server/heartbeat"),
            new StringContent(json, Encoding.UTF8, "application/json")
        );
        response.EnsureSuccessStatusCode();
        string respJson = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(respJson, ServerAPIGenerationContext.Default.APIHeartbeatResponse)!;
    }

    public async Task<APIValidateResponse> ValidatePlayer(string token)
    {
        _client.DefaultRequestHeaders.Remove("Authorization");
        _client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);

        return await _client.GetFromJsonAsync(
            Globals.ApiEndpoint.PathJoin("/v1/game/server/validate"),
            ServerAPIGenerationContext.Default.APIValidateResponse
        );
    }

    public async Task LogEvent(ServerEventType eventType, Dictionary<string, string>? data = null)
    {
        var request = new { eventType = eventType.ToString(), data };
        string json = JsonSerializer.Serialize(request);
        using HttpResponseMessage response = await _client.PostAsync(
            Globals.ApiEndpoint.PathJoin("/v1/game/server/log"),
            new StringContent(json, Encoding.UTF8, "application/json")
        );
        response.EnsureSuccessStatusCode();
    }
}