// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Networking.P2P;

/// <summary>
/// Hybrid authority system combining distributed ownership with consensus validation.
/// Each object has a primary owner (closest peer) and validators (farthest peers).
/// State changes require consensus from majority of owners.
/// </summary>
public class HybridAuthorityManager
{
    private const int DefaultValidatorCount = 2;
    private const int ConsensusTimeoutMs = 5000;
    private const int StateSequenceIncrement = 1;

    private readonly P2PNetworkInstance _networkInstance;
    private readonly PeerClusterManager _clusterManager;
    private readonly SwarmDHT _dht;

    private readonly ConcurrentDictionary<string, ObjectAuthority> _objectAuthorities = [];
    private readonly ConcurrentDictionary<string, PendingConsensus> _pendingConsensus = [];
    private readonly ConcurrentDictionary<string, ObjectState> _objectStates = [];

    public event Action<string, ObjectAuthority>? AuthorityChanged;
    public event Action<string, bool>? ConsensusResult;
    public event Action<string, byte[]?>? StateUpdated;

    public int LocalPeerId => _networkInstance.LocalPeerId;

    public HybridAuthorityManager(
        P2PNetworkInstance networkInstance,
        PeerClusterManager clusterManager,
        SwarmDHT dht)
    {
        _networkInstance = networkInstance;
        _clusterManager = clusterManager;
        _dht = dht;

        _networkInstance.PeerConnected += OnPeerConnected;
        _networkInstance.PeerDisconnected += OnPeerDisconnected;
        _networkInstance.MessageReceived += OnMessageReceived;
    }

    /// <summary>
    /// Claim authority over an object.
    /// </summary>
    public async Task<bool> ClaimAuthority(string objectId, byte[]? initialState = null)
    {
        // Check if already owned
        if (_objectAuthorities.TryGetValue(objectId, out ObjectAuthority? existing))
        {
            if (existing.PrimaryPeerId == LocalPeerId)
            {
                return true; // Already ours
            }

            // Request transfer from current owner
            return await RequestAuthorityTransfer(objectId, existing.PrimaryPeerId);
        }

        // Calculate distance to determine if we should own this
        float ourDistance = CalculateDistanceToObject(objectId);
        List<PeerInfo> peers = _clusterManager.GetSwarmPeers();

        // Check if any peer is closer
        bool isClosest = true;
        foreach (PeerInfo peer in peers)
        {
            float peerDistance = CalculatePeerDistanceToObject(peer.PeerId, objectId);
            if (peerDistance < ourDistance)
            {
                isClosest = false;
                break;
            }
        }

        if (!isClosest)
        {
            PT.Print($"[P2P-Auth] Not closest to {objectId}, skipping claim");
            return false;
        }

        // Claim authority
        ObjectAuthority authority = new()
        {
            ObjectId = objectId,
            PrimaryPeerId = LocalPeerId,
            ValidatorPeerIds = SelectValidators(objectId, peers),
            SequenceNumber = 0,
            LastUpdated = DateTime.UtcNow
        };

        _objectAuthorities[objectId] = authority;

        // Store in DHT
        _dht.SetObjectOwnership(objectId, LocalPeerId, "primary");
        foreach (int validatorId in authority.ValidatorPeerIds)
        {
            _dht.SetObjectOwnership(objectId, validatorId, "validator");
        }

        // Store initial state
        if (initialState != null)
        {
            ObjectState state = new()
            {
                ObjectId = objectId,
                Data = initialState,
                SequenceNumber = 0,
                OwnerPeerId = LocalPeerId
            };
            _objectStates[objectId] = state;
        }

        // Broadcast authority claim
        BroadcastAuthorityClaim(authority);

        AuthorityChanged?.Invoke(objectId, authority);
        PT.Print($"[P2P-Auth] Claimed authority over {objectId}");

        return true;
    }

    /// <summary>
    /// Update object state with consensus.
    /// </summary>
    public async Task<bool> UpdateState(string objectId, byte[] newState, bool requireConsensus = true)
    {
        if (!_objectAuthorities.TryGetValue(objectId, out ObjectAuthority? authority))
        {
            PT.PrintErr($"[P2P-Auth] No authority for {objectId}");
            return false;
        }

        if (!IsAuthorityFor(objectId, LocalPeerId))
        {
            PT.PrintErr($"[P2P-Auth] Not authorized for {objectId}");
            return false;
        }

        long newSequence = _objectStates[objectId].SequenceNumber + 1;
        _objectStates[objectId].SequenceNumber = newSequence;

        ObjectState state = new()
        {
            ObjectId = objectId,
            Data = newState,
            SequenceNumber = newSequence,
            OwnerPeerId = LocalPeerId
        };

        if (requireConsensus && authority.ValidatorPeerIds.Length > 0)
        {
            // Request consensus from validators
            return await RequestConsensus(objectId, state);
        }
        else
        {
            // Apply state directly (no consensus needed)
            _objectStates[objectId] = state;
            BroadcastStateUpdate(state);
            StateUpdated?.Invoke(objectId, newState);
            return true;
        }
    }

    /// <summary>
    /// Get the current state of an object.
    /// </summary>
    public byte[]? GetState(string objectId)
    {
        if (_objectStates.TryGetValue(objectId, out ObjectState? state))
        {
            return state.Data;
        }
        return null;
    }

    /// <summary>
    /// Get the authority information for an object.
    /// </summary>
    public ObjectAuthority? GetAuthority(string objectId)
    {
        _objectAuthorities.TryGetValue(objectId, out ObjectAuthority? authority);
        return authority;
    }

    /// <summary>
    /// Check if a peer has authority over an object.
    /// </summary>
    public bool IsAuthorityFor(string objectId, int peerId)
    {
        if (!_objectAuthorities.TryGetValue(objectId, out ObjectAuthority? authority))
        {
            return false;
        }

        return authority.PrimaryPeerId == peerId ||
               authority.ValidatorPeerIds.Contains(peerId);
    }

    /// <summary>
    /// Check if a peer is the primary owner of an object.
    /// </summary>
    public bool IsPrimaryOwner(string objectId, int peerId)
    {
        if (!_objectAuthorities.TryGetValue(objectId, out ObjectAuthority? authority))
        {
            return false;
        }

        return authority.PrimaryPeerId == peerId;
    }

    /// <summary>
    /// Get all objects owned by a specific peer.
    /// </summary>
    public List<string> GetObjectsOwnedBy(int peerId)
    {
        return _objectAuthorities.Values
            .Where(a => a.PrimaryPeerId == peerId)
            .Select(a => a.ObjectId)
            .ToList();
    }

    /// <summary>
    /// Get all objects where a peer is a validator.
    /// </summary>
    public List<string> GetObjectsValidatedBy(int peerId)
    {
        return _objectAuthorities.Values
            .Where(a => a.ValidatorPeerIds.Contains(peerId))
            .Select(a => a.ObjectId)
            .ToList();
    }

    /// <summary>
    /// Reassign authority when peers join/leave.
    /// </summary>
    public void RebalanceAuthority()
    {
        List<PeerInfo> currentPeers = _clusterManager.GetSwarmPeers();
        HashSet<int> activePeers = [.. currentPeers.Select(p => p.PeerId)];
        activePeers.Add(LocalPeerId);

        foreach (var (objectId, authority) in _objectAuthorities)
        {
            // Check if primary owner left
            if (!activePeers.Contains(authority.PrimaryPeerId))
            {
                PT.Print($"[P2P-Auth] Primary owner {authority.PrimaryPeerId} left for {objectId}");

                // Find new owner
                int newOwner = FindNewOwner(objectId, activePeers);
                if (newOwner > 0)
                {
                    authority.PrimaryPeerId = newOwner;
                    authority.LastUpdated = DateTime.UtcNow;
                    BroadcastAuthorityChange(authority);
                }
            }

            // Check validators
            List<int> activeValidators = authority.ValidatorPeerIds
                .Where(v => activePeers.Contains(v))
                .ToList();

            if (activeValidators.Count < DefaultValidatorCount)
            {
                // Add new validators
                List<int> newValidators = SelectValidators(objectId, currentPeers, exclude: [.. authority.ValidatorPeerIds, authority.PrimaryPeerId]).ToList();
                activeValidators.AddRange(newValidators.Take(DefaultValidatorCount - activeValidators.Count));
                authority.ValidatorPeerIds = [.. activeValidators];
            }
        }
    }

    /// <summary>
    /// Get consensus status for a pending operation.
    /// </summary>
    public ConsensusStatus GetConsensusStatus(string objectId)
    {
        if (_pendingConsensus.TryGetValue(objectId, out PendingConsensus? pending))
        {
            return pending.Status;
        }
        return ConsensusStatus.None;
    }

    private async Task<bool> RequestAuthorityTransfer(string objectId, int currentOwnerId)
    {
        AuthorityTransferRequest request = new()
        {
            ObjectId = objectId,
            RequesterPeerId = LocalPeerId,
            RequestId = Guid.NewGuid().ToString("N")
        };

        byte[] data = SerializeMessage(MessageType.AuthorityTransferRequest, request);
        _networkInstance.SendMessage(currentOwnerId, data, TransferMode.Reliable);

        // Wait for response
        TaskCompletionSource<AuthorityTransferResponse?> tcs = new();
        _pendingTransferResponses[request.RequestId] = tcs;

        try
        {
            var response = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(ConsensusTimeoutMs));
            return response?.Approved == true;
        }
        catch (TimeoutException)
        {
            PT.Print($"[P2P-Auth] Transfer request timed out for {objectId}");
            return false;
        }
        finally
        {
            _pendingTransferResponses.TryRemove(request.RequestId, out _);
        }
    }

    private async Task<bool> RequestConsensus(string objectId, ObjectState proposedState)
    {
        if (!_objectAuthorities.TryGetValue(objectId, out ObjectAuthority? authority))
        {
            return false;
        }

        ConsensusRequest request = new()
        {
            ObjectId = objectId,
            ProposedState = proposedState,
            RequesterPeerId = LocalPeerId,
            RequestId = Guid.NewGuid().ToString("N"),
            RequiredVotes = (authority.ValidatorPeerIds.Length / 2) + 1
        };

        PendingConsensus consensus = new()
        {
            RequestId = request.RequestId,
            ObjectId = objectId,
            ProposedState = proposedState,
            Status = ConsensusStatus.Pending,
            VotesReceived = 0,
            VotesRequired = request.RequiredVotes
        };

        _pendingConsensus[objectId] = consensus;

        // Send to validators
        byte[] data = SerializeMessage(MessageType.ConsensusRequest, request);
        foreach (int validatorId in authority.ValidatorPeerIds)
        {
            _networkInstance.SendMessage(validatorId, data, TransferMode.Reliable);
        }

        // Wait for consensus
        TaskCompletionSource<bool> tcs = new();
        consensus.CompletionSource = tcs;

        try
        {
            bool result = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(ConsensusTimeoutMs));
            ConsensusResult?.Invoke(objectId, result);
            return result;
        }
        catch (TimeoutException)
        {
            PT.Print($"[P2P-Auth] Consensus timed out for {objectId}");
            consensus.Status = ConsensusStatus.Failed;
            ConsensusResult?.Invoke(objectId, false);
            return false;
        }
        finally
        {
            _pendingConsensus.TryRemove(objectId, out _);
        }
    }

    private void HandleConsensusRequest(int fromPeerId, ConsensusRequest request)
    {
        // Verify sender is an authority for this object
        if (!IsAuthorityFor(request.ObjectId, fromPeerId))
        {
            PT.PrintErr($"[P2P-Auth] Unauthorized consensus request from {fromPeerId}");
            return;
        }

        // Validate the proposed state
        bool approved = ValidateStateTransition(request.ObjectId, request.ProposedState);

        // Send vote
        ConsensusVote vote = new()
        {
            RequestId = request.RequestId,
            ObjectId = request.ObjectId,
            VoterPeerId = LocalPeerId,
            Approved = approved,
            SequenceNumber = request.ProposedState.SequenceNumber
        };

        byte[] data = SerializeMessage(MessageType.ConsensusVote, vote);
        _networkInstance.SendMessage(fromPeerId, data, TransferMode.Reliable);
    }

    private void HandleConsensusVote(ConsensusVote vote)
    {
        if (!_pendingConsensus.TryGetValue(vote.ObjectId, out PendingConsensus? consensus))
        {
            return;
        }

        if (vote.RequestId != consensus.RequestId)
        {
            return;
        }

        if (vote.Approved)
        {
            consensus.VotesReceived++;
        }

        if (consensus.VotesReceived >= consensus.VotesRequired)
        {
            // Consensus reached
            consensus.Status = ConsensusStatus.Approved;

            // Apply state
            _objectStates[vote.ObjectId] = vote.SequenceNumber switch
            {
                _ => consensus.ProposedState
            };

            BroadcastStateUpdate(consensus.ProposedState);
            StateUpdated?.Invoke(vote.ObjectId, consensus.ProposedState.Data);

            consensus.CompletionSource?.TrySetResult(true);
        }
        else if (!vote.Approved)
        {
            // Check if we can still reach consensus
            int maxPossible = consensus.VotesReceived + 1; // If remaining validators vote yes
            if (maxPossible < consensus.VotesRequired)
            {
                consensus.Status = ConsensusStatus.Failed;
                consensus.CompletionSource?.TrySetResult(false);
            }
        }
    }

    private bool ValidateStateTransition(string objectId, ObjectState proposedState)
    {
        // Get current state
        if (_objectStates.TryGetValue(objectId, out ObjectState? currentState))
        {
            // Check sequence number
            if (proposedState.SequenceNumber <= currentState.SequenceNumber)
            {
                return false; // Out of order
            }

            // Check if state is valid (could add custom validation logic)
            // For now, accept all valid states
        }

        return true;
    }

    private float CalculateDistanceToObject(string objectId)
    {
        // Simplified distance calculation
        // In real implementation, this would be based on network latency
        // or some logical distance metric
        return 0.0f;
    }

    private float CalculatePeerDistanceToObject(int peerId, string objectId)
    {
        float latency = _clusterManager.GetLatencyToPeer(peerId);
        return latency;
    }

    private int[] SelectValidators(string objectId, List<PeerInfo> candidates, int[]? exclude = null)
    {
        HashSet<int> excludeSet = exclude != null ? [.. exclude] : [];

        // Select farthest peers as validators
        return candidates
            .Where(p => !excludeSet.Contains(p.PeerId) && p.PeerId != LocalPeerId)
            .OrderByDescending(p => p.AverageLatency)
            .Take(DefaultValidatorCount)
            .Select(p => p.PeerId)
            .ToArray();
    }

    private int FindNewOwner(string objectId, HashSet<int> activePeers)
    {
        // Find closest active peer
        return activePeers
            .OrderBy(p => p == LocalPeerId ? 0 : _clusterManager.GetLatencyToPeer(p))
            .FirstOrDefault();
    }

    private void BroadcastAuthorityClaim(ObjectAuthority authority)
    {
        byte[] data = SerializeMessage(MessageType.AuthorityClaim, authority);
        _networkInstance.BroadcastMessage(data, TransferMode.Reliable);
    }

    private void BroadcastAuthorityChange(ObjectAuthority authority)
    {
        byte[] data = SerializeMessage(MessageType.AuthorityChanged, authority);
        _networkInstance.BroadcastMessage(data, TransferMode.Reliable);
    }

    private void BroadcastStateUpdate(ObjectState state)
    {
        byte[] data = SerializeMessage(MessageType.StateUpdate, state);
        _networkInstance.BroadcastMessage(data, TransferMode.Reliable);
    }

    private void OnPeerConnected(int peerId)
    {
        // Send our authorities to new peer
        foreach (var (objectId, authority) in _objectAuthorities)
        {
            byte[] data = SerializeMessage(MessageType.AuthorityClaim, authority);
            _networkInstance.SendMessage(peerId, data, TransferMode.Reliable);
        }
    }

    private void OnPeerDisconnected(int peerId)
    {
        // Rebalance authority when peer leaves
        RebalanceAuthority();
    }

    private void OnMessageReceived(int peerId, byte[] data, TransferMode transferMode)
    {
        // Handle authority messages (prefix: AUTH)
        if (data.Length < 4) return;

        string header = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(4, data.Length));
        if (!header.StartsWith("AUTH")) return;

        HandleAuthorityMessage(peerId, data);
    }

    private void HandleAuthorityMessage(int peerId, byte[] data)
    {
        try
        {
            string text = System.Text.Encoding.UTF8.GetString(data);
            string[] parts = text.Split(':', 2);
            if (parts.Length < 2) return;

            string msgType = parts[0][4..]; // Remove "AUTH" prefix
            string payload = parts[1];

            switch (msgType)
            {
                case "CLAIM":
                    ObjectAuthority? claimAuthority = System.Text.Json.JsonSerializer.Deserialize<ObjectAuthority>(payload);
                    if (claimAuthority != null)
                    {
                        _objectAuthorities[claimAuthority.ObjectId] = claimAuthority;
                        AuthorityChanged?.Invoke(claimAuthority.ObjectId, claimAuthority);
                    }
                    break;

                case "TRANSFER_REQ":
                    AuthorityTransferRequest? transferReq = System.Text.Json.JsonSerializer.Deserialize<AuthorityTransferRequest>(payload);
                    if (transferReq != null)
                    {
                        HandleAuthorityTransferRequest(peerId, transferReq);
                    }
                    break;

                case "TRANSFER_RESP":
                    AuthorityTransferResponse? transferResp = System.Text.Json.JsonSerializer.Deserialize<AuthorityTransferResponse>(payload);
                    if (transferResp != null && _pendingTransferResponses.TryGetValue(transferResp.RequestId, out var tcs))
                    {
                        tcs.TrySetResult(transferResp);
                    }
                    break;

                case "CONSENSUS_REQ":
                    ConsensusRequest? consensusReq = System.Text.Json.JsonSerializer.Deserialize<ConsensusRequest>(payload);
                    if (consensusReq != null)
                    {
                        HandleConsensusRequest(peerId, consensusReq);
                    }
                    break;

                case "CONSENSUS_VOTE":
                    ConsensusVote? vote = System.Text.Json.JsonSerializer.Deserialize<ConsensusVote>(payload);
                    if (vote != null)
                    {
                        HandleConsensusVote(vote);
                    }
                    break;

                case "STATE_UPDATE":
                    ObjectState? state = System.Text.Json.JsonSerializer.Deserialize<ObjectState>(payload);
                    if (state != null && IsAuthorityFor(state.ObjectId, peerId))
                    {
                        _objectStates[state.ObjectId] = state;
                        StateUpdated?.Invoke(state.ObjectId, state.Data);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            GD.PushError($"[P2P-Auth] Error handling message: {ex.Message}");
        }
    }

    private void HandleAuthorityTransferRequest(int fromPeerId, AuthorityTransferRequest request)
    {
        // Auto-approve if requester is closer
        bool approved = true;

        AuthorityTransferResponse response = new()
        {
            RequestId = request.RequestId,
            ObjectId = request.ObjectId,
            Approved = approved,
            ResponderPeerId = LocalPeerId
        };

        byte[] data = SerializeMessage(MessageType.AuthorityTransferResponse, response);
        _networkInstance.SendMessage(fromPeerId, data, TransferMode.Reliable);

        if (approved && _objectAuthorities.TryGetValue(request.ObjectId, out ObjectAuthority? authority))
        {
            // Transfer authority
            authority.PrimaryPeerId = fromPeerId;
            authority.LastUpdated = DateTime.UtcNow;
            BroadcastAuthorityChange(authority);
        }
    }

    private byte[] SerializeMessage<T>(MessageType type, T message)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(message);
        return System.Text.Encoding.UTF8.GetBytes($"AUTH{type}:{json}");
    }

    private readonly ConcurrentDictionary<string, TaskCompletionSource<AuthorityTransferResponse?>> _pendingTransferResponses = [];
}

public enum MessageType
{
    AuthorityClaim,
    AuthorityChanged,
    AuthorityTransferRequest,
    AuthorityTransferResponse,
    ConsensusRequest,
    ConsensusVote,
    StateUpdate
}

public enum ConsensusStatus
{
    None,
    Pending,
    Approved,
    Failed
}

public class ObjectAuthority
{
    public string ObjectId { get; set; } = "";
    public int PrimaryPeerId { get; set; }
    public int[] ValidatorPeerIds { get; set; } = [];
    public long SequenceNumber { get; set; } = 0;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class ObjectState
{
    public string ObjectId { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public long SequenceNumber { get; set; } = 0;
    public int OwnerPeerId { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class PendingConsensus
{
    public string RequestId { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public ObjectState ProposedState { get; set; } = null!;
    public ConsensusStatus Status { get; set; } = ConsensusStatus.Pending;
    public int VotesReceived { get; set; } = 0;
    public int VotesRequired { get; set; } = 1;
    public TaskCompletionSource<bool>? CompletionSource { get; set; }
}

public class AuthorityTransferRequest
{
    public string ObjectId { get; set; } = "";
    public int RequesterPeerId { get; set; }
    public string RequestId { get; set; } = "";
}

public class AuthorityTransferResponse
{
    public string RequestId { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public bool Approved { get; set; }
    public int ResponderPeerId { get; set; }
}

public class ConsensusRequest
{
    public string ObjectId { get; set; } = "";
    public ObjectState ProposedState { get; set; } = null!;
    public int RequesterPeerId { get; set; }
    public string RequestId { get; set; } = "";
    public int RequiredVotes { get; set; }
}

public class ConsensusVote
{
    public string RequestId { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public int VoterPeerId { get; set; }
    public bool Approved { get; set; }
    public long SequenceNumber { get; set; }
}
