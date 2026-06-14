# Project Summary

## Goal
Build and fix the web UI flow for Polytoria: a local web server that lets users browse local .poly game files, host/join game servers, and discover remote Polytoria nodes over a global DHT network.

## Progress

- **Serialization fix**: Added `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` to `BroadcastWsMessage` and `get_servers` handler so `ServerInfo` matches frontend's camelCase.
- **WebSocket upgrade fix**: Replaced custom 101 handshake with `HttpListenerContext.AcceptWebSocketAsync(null)`. Refactored `WebSocketConnection` to wrap `System.Net.WebSockets.WebSocket`.
- **Recursive loop fix**: `SignalClient.JoinServer` was calling HTTP POST → `HandleJoinServer` → `ServerJoinRequested` → `P2PManager.JoinServer` → `SignalClient.JoinServer`. Changed to read DHT directly.
- **Missing Ip/Port**: DHT entry was stored before `CreateP2P` set Ip/Port. Added DHT update after `StartGameAsHost` → `CreateP2P`.
- **UI restructure**: Removed standalone Servers tab; merged into Games tab as "Created" (local `.poly`) and "Other" (DHT-discovered servers).
- **KRPC (BitTorrent Mainline DHT)**: Added `Bencode.cs` for bencoding, KRPC handlers (`ping`, `find_node`, `get_peers`, `announce_peer`) in `KademliaDHT.cs`. Node announces on `SHA1("polytoria-dht-v1")` info_hash and discovers other Polytoria nodes via `get_peers` → token → `announce_peer` exchange. Discovered nodes stored as `server:dht:<ip>:<port>` entries so they appear in the "Other" section.
- **Build script**: Created `build.sh` for exporting standalone executables for Linux and Windows.

## Key Decisions

- **Local-only for now**: DHT only reads from local in-memory storage. Full network DHT lookup via KRPC is implemented but relies on the BitTorrent Mainline DHT for peer discovery.
- **Web UI as Godot overlay**: Web UI runs on localhost:24222, served by a `HttpListener` embedded in the Godot process.
- **Hybrid architecture**: KRPC (BitTorrent DHT protocol) for public peer discovery on a fixed info_hash; custom binary protocol for private server listing/game communication between discovered peers.
- **Token exchange**: Proper `get_peers` → token → `announce_peer` flow per BitTorrent DHT spec.

## Relevant Files

- `Polytoria/scripts/network/p2p/WebServer.cs` — HTTP API and WebSocket server
- `Polytoria/scripts/network/p2p/KademliaDHT.cs` — DHT node with KRPC support
- `Polytoria/scripts/network/p2p/Bencode.cs` — Bencoding encoder/decoder
- `Polytoria/scripts/network/p2p/SignalClient.cs` — HTTP client for server operations
- `Polytoria/scripts/network/p2p/P2PManager.cs` — Orchestrates DHT, WebServer, SignalClient
- `Polytoria/scripts/network/p2p/WebUIEntry.cs` — React SPA entry point with webview
- `Polytoria/scripts/network/p2p/userdata/webui/` — React frontend source
- `Polytoria/export_presets.cfg` — Export presets for Linux/Windows
- `build.sh` — Build script for standalone executables

## Known Limitations

- Export requires Godot 4.6.2 mono + export templates installed locally
- DHT bootstrap will error if no internet or UDP port is blocked
- Token secret rotates hourly; in-flight announce_peer queries between rotation may fail (graceful)
- "Other" section only shows discovered nodes with `(DHT)` as hostname — real server names require querying the discovered node's binary protocol endpoint
