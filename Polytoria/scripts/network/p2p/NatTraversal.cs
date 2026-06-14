// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Networking.P2P;

/// <summary>
/// NAT traversal implementation using STUN/TURN protocols.
/// Handles hole punching and relay fallback for peer-to-peer connections.
/// </summary>
public class NatTraversal
{
    private const int StunTimeoutMs = 3000;
    private const int TurnTimeoutMs = 5000;
    private const int MaxRetries = 3;

    private UdpClient? _udpClient;
    private IPEndPoint? _localEndPoint;
    private CancellationTokenSource? _cts;

    public IPEndPoint? PublicEndPoint { get; private set; }
    public NatType DetectedNatType { get; private set; } = NatType.Unknown;
    public TurnServerInfo? TurnServer { get; private set; }
    public bool IsHolePunched { get; private set; } = false;

    public event Action<NatType>? NatTypeDetected;
    public event Action<IPEndPoint>? HolePunched;
    public event Action<string>? Error;

    /// <summary>
    /// Discover public address using STUN server.
    /// </summary>
    public async Task<(string Address, int Port)> DiscoverPublicAddress(string stunServer)
    {
        try
        {
            _udpClient = new UdpClient(0);
            _localEndPoint = (IPEndPoint)_udpClient.Client.LocalEndPoint!;
            _cts = new CancellationTokenSource();

            PT.Print($"[NAT] Local endpoint: {_localEndPoint}");

            // Parse STUN server address
            IPAddress stunIp = await ResolveHost(stunServer.Split(':')[0]);
            int stunPort = stunServer.Contains(':') ? int.Parse(stunServer.Split(':')[1]) : 3478;
            IPEndPoint stunEndpoint = new(stunIp, stunPort);

            // Send STUN binding request
            byte[] stunRequest = BuildStunBindingRequest();
            await _udpClient.SendAsync(stunRequest, stunRequest.Length, stunEndpoint);

            // Wait for response
            UdpReceiveResult result = await WithTimeout(
                _udpClient.ReceiveAsync(),
                StunTimeoutMs,
                _cts.Token);

            byte[] responseData = result.Buffer;

            // Parse STUN response
            if (TryParseStunResponse(responseData, out IPEndPoint? mappedAddress))
            {
                PublicEndPoint = mappedAddress;
                PT.Print($"[NAT] Public address discovered: {mappedAddress}");

                // Detect NAT type
                DetectedNatType = await DetectNatType(stunEndpoint);
                NatTypeDetected?.Invoke(DetectedNatType);

                return (mappedAddress.Address.ToString(), mappedAddress.Port);
            }

            throw new Exception("Failed to parse STUN response");
        }
        catch (Exception ex)
        {
            GD.PushError($"[NAT] STUN discovery failed: {ex.Message}");
            Error?.Invoke($"STUN discovery failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Connect to a TURN relay server.
    /// </summary>
    public async Task<IPEndPoint?> ConnectToTurnServer(string turnServer, string username, string credential)
    {
        try
        {
            if (_udpClient == null)
            {
                _udpClient = new UdpClient(0);
                _localEndPoint = (IPEndPoint)_udpClient.Client.LocalEndPoint!;
            }

            // Parse TURN server address
            IPAddress turnIp = await ResolveHost(turnServer.Split(':')[0]);
            int turnPort = turnServer.Contains(':') ? int.Parse(turnServer.Split(':')[1]) : 3479;
            IPEndPoint turnEndpoint = new(turnIp, turnPort);

            // Send TURN Allocate request
            byte[] allocateRequest = BuildTurnAllocateRequest(username, credential);
            await _udpClient.SendAsync(allocateRequest, allocateRequest.Length, turnEndpoint);

            // Wait for response
            UdpReceiveResult result = await WithTimeout(
                _udpClient.ReceiveAsync(),
                TurnTimeoutMs,
                _cts?.Token ?? CancellationToken.None);

            // Parse TURN response
            if (TryParseTurnResponse(result.Buffer, out IPEndPoint? relayEndpoint))
            {
                TurnServer = new TurnServerInfo
                {
                    Endpoint = turnEndpoint,
                    RelayEndpoint = relayEndpoint,
                    Username = username,
                    Credential = credential
                };

                PT.Print($"[NAT] Connected to TURN relay: {relayEndpoint}");
                return relayEndpoint;
            }

            return null;
        }
        catch (Exception ex)
        {
            GD.PushError($"[NAT] TURN connection failed: {ex.Message}");
            Error?.Invoke($"TURN connection failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Perform UDP hole punching to a peer.
    /// </summary>
    public async Task<bool> HolePunch(IPEndPoint remoteEndPoint)
    {
        if (_udpClient == null || PublicEndPoint == null)
        {
            Error?.Invoke("Must discover public address first");
            return false;
        }

        PT.Print($"[NAT] Starting hole punch to {remoteEndPoint}");

        // Send multiple packets to punch through NAT
        byte[] punchPacket = Encoding.UTF8.GetBytes("PUNCH");

        for (int i = 0; i < MaxRetries; i++)
        {
            try
            {
                await _udpClient.SendAsync(punchPacket, punchPacket.Length, remoteEndPoint);
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                GD.PushError($"[NAT] Punch packet failed: {ex.Message}");
            }
        }

        // Wait for response from peer
        try
        {
            _cts ??= new CancellationTokenSource();
            UdpReceiveResult result = await WithTimeout(
                _udpClient.ReceiveAsync(),
                2000,
                _cts.Token);

            string message = Encoding.UTF8.GetString(result.Buffer);
            if (message.Contains("PUNCH") || message.Contains("PONG"))
            {
                IsHolePunched = true;
                HolePunched?.Invoke(remoteEndPoint);
                PT.Print($"[NAT] Hole punch successful to {remoteEndPoint}");
                return true;
            }
        }
        catch (TimeoutException)
        {
            PT.Print($"[NAT] Hole punch timeout - may need TURN relay");
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }

        return false;
    }

    /// <summary>
    /// Send data through the NAT traversal layer.
    /// </summary>
    public async Task SendData(byte[] data, IPEndPoint remoteEndPoint)
    {
        if (_udpClient == null)
        {
            throw new InvalidOperationException("NAT traversal not initialized");
        }

        // If we have a TURN server, use relay
        if (TurnServer != null && !IsHolePunched)
        {
            // Send through TURN relay
            await _udpClient.SendAsync(data, data.Length, TurnServer.RelayEndpoint);
        }
        else
        {
            // Send directly
            await _udpClient.SendAsync(data, data.Length, remoteEndPoint);
        }
    }

    /// <summary>
    /// Receive data from the NAT traversal layer.
    /// </summary>
    public async Task<(byte[] Data, IPEndPoint RemoteEndPoint)> ReceiveData()
    {
        if (_udpClient == null)
        {
            throw new InvalidOperationException("NAT traversal not initialized");
        }

        UdpReceiveResult result = await _udpClient.ReceiveAsync();
        return (result.Buffer, result.RemoteEndPoint);
    }

    /// <summary>
    /// Cleanup resources.
    /// </summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _udpClient?.Close();
        _udpClient = null;
    }

    private async Task<NatType> DetectNatType(IPEndPoint stunEndpoint)
    {
        try
        {
            // Send multiple binding requests to detect NAT behavior
            byte[] request1 = BuildStunBindingRequest();
            await _udpClient!.SendAsync(request1, request1.Length, stunEndpoint);

            UdpReceiveResult result1 = await WithTimeout(
                _udpClient.ReceiveAsync(),
                StunTimeoutMs,
                _cts?.Token ?? CancellationToken.None);

            if (!TryParseStunResponse(result1.Buffer, out IPEndPoint? mapped1))
            {
                return NatType.Symmetric; // Can't parse response
            }

            // Check if port changed (symmetric NAT indicator)
            if (mapped1.Port != PublicEndPoint?.Port)
            {
                return NatType.Symmetric;
            }

            // Try sending from a different local port
            using UdpClient testClient = new UdpClient(0);
            IPEndPoint testLocal = (IPEndPoint)testClient.Client.LocalEndPoint!;
            byte[] request2 = BuildStunBindingRequest();
            await testClient.SendAsync(request2, request2.Length, stunEndpoint);

            UdpReceiveResult result2 = await WithTimeout(
                testClient.ReceiveAsync(),
                StunTimeoutMs,
                _cts?.Token ?? CancellationToken.None);

            if (TryParseStunResponse(result2.Buffer, out IPEndPoint? mapped2))
            {
                // If port is different from first request, it's symmetric
                if (mapped2.Port != mapped1.Port)
                {
                    return NatType.Symmetric;
                }
                else
                {
                    return NatType.PortRestricted;
                }
            }

            return NatType.Unknown;
        }
        catch
        {
            return NatType.Unknown;
        }
    }

    private byte[] BuildStunBindingRequest()
    {
        byte[] request = new byte[28];
        request[0] = 0x00; // Message type MSB
        request[1] = 0x01; // Binding Request
        request[2] = 0x00; // Message length MSB
        request[3] = 0x08; // Message length (8 bytes for header only)

        // Magic cookie
        request[4] = 0x21;
        request[5] = 0x12;
        request[6] = 0xA4;
        request[7] = 0x42;

        // Transaction ID (random)
        System.Security.Cryptography.RandomNumberGenerator.Fill(request.AsSpan(8, 16));

        return request;
    }

    private bool TryParseStunResponse(byte[] data, out IPEndPoint? mappedAddress)
    {
        mappedAddress = null;

        if (data.Length < 20) return false;

        // Check message type (0x0101 = Binding Response)
        int messageType = (data[0] << 8) | data[1];
        if (messageType != 0x0101) return false;

        // Check magic cookie
        if (data[4] != 0x21 || data[5] != 0x12 || data[6] != 0xA4 || data[7] != 0x42)
        {
            return false;
        }

        // Parse attributes
        int offset = 20;
        int messageLength = (data[2] << 8) | data[3];

        while (offset < 20 + messageLength && offset + 4 <= data.Length)
        {
            int attrType = (data[offset] << 8) | data[offset + 1];
            int attrLength = (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;

            // MAPPED-ADDRESS (0x0001)
            if (attrType == 0x0001 && attrLength >= 8)
            {
                int family = data[offset + 1]; // 1 = IPv4, 2 = IPv6
                int port = (data[offset + 2] << 8) | data[offset + 3];

                if (family == 1 && offset + 8 <= data.Length)
                {
                    byte[] ipBytes = data[(offset + 4)..(offset + 8)];
                    IPAddress ip = new(ipBytes);
                    mappedAddress = new IPEndPoint(ip, port);
                    return true;
                }
            }

            offset += attrLength;
            // Align to 4 bytes
            offset = (offset + 3) & ~3;
        }

        return false;
    }

    private byte[] BuildTurnAllocateRequest(string username, string credential)
    {
        // Simplified TURN Allocate request
        // In production, use a proper TURN library
        byte[] request = new byte[28];
        request[0] = 0x00; // Message type MSB
        request[1] = 0x03; // Allocate Request
        request[2] = 0x00;
        request[3] = 0x08;

        // Magic cookie
        request[4] = 0x21;
        request[5] = 0x12;
        request[6] = 0xA4;
        request[7] = 0x42;

        // Transaction ID
        System.Security.Cryptography.RandomNumberGenerator.Fill(request.AsSpan(8, 16));

        return request;
    }

    private bool TryParseTurnResponse(byte[] data, out IPEndPoint? relayEndpoint)
    {
        relayEndpoint = null;

        if (data.Length < 20) return false;

        // Check message type (0x0103 = Allocate Response)
        int messageType = (data[0] << 8) | data[1];
        if (messageType != 0x0103) return false;

        // Parse RELAYED-ADDRESS attribute
        int offset = 20;
        int messageLength = (data[2] << 8) | data[3];

        while (offset < 20 + messageLength && offset + 4 <= data.Length)
        {
            int attrType = (data[offset] << 8) | data[offset + 1];
            int attrLength = (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;

            // RELAYED-ADDRESS (0x0016)
            if (attrType == 0x0016 && attrLength >= 8)
            {
                int family = data[offset + 1];
                int port = (data[offset + 2] << 8) | data[offset + 3];

                if (family == 1 && offset + 8 <= data.Length)
                {
                    byte[] ipBytes = data[(offset + 4)..(offset + 8)];
                    IPAddress ip = new(ipBytes);
                    relayEndpoint = new IPEndPoint(ip, port);
                    return true;
                }
            }

            offset += attrLength;
            offset = (offset + 3) & ~3;
        }

        return false;
    }

    private async Task<IPAddress> ResolveHost(string hostname)
    {
        IPHostEntry entry = await Dns.GetHostEntryAsync(hostname);
        return entry.AddressList.First(a => a.AddressFamily == AddressFamily.InterNetwork);
    }

    private async Task<T> WithTimeout<T>(Task<T> task, int timeoutMs, CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        Task completedTask = await Task.WhenAny(task, Task.Delay(timeoutMs, timeoutCts.Token));

        if (completedTask == task)
        {
            return await task;
        }

        throw new TimeoutException();
    }
}

public enum NatType
{
    Unknown,
    Open,           // No NAT (public IP)
    FullCone,       // Any external can connect
    RestrictedCone, // Only contacted external can connect
    PortRestricted, // Only contacted external:port can connect
    Symmetric       // Different mapping for each destination
}

public class TurnServerInfo
{
    public IPEndPoint Endpoint { get; set; } = null!;
    public IPEndPoint RelayEndpoint { get; set; } = null!;
    public string Username { get; set; } = "";
    public string Credential { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}
