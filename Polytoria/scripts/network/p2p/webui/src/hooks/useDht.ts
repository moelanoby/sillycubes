import { useState, useEffect, useCallback, useRef } from 'react'

export interface ServerInfo {
  code: string
  hostUsername: string
  ip?: string
  port: number
  playerCount: number
  createdAt: string
  lastHeartbeat: string
}

export interface PeerInfo {
  nodeId: string
  ip: string
  port: number
  lastSeen: string
}

export interface DhtStats {
  nodeId: string
  nodeCount: number
  port: number
}

interface WebSocketMessage {
  type: string
  [key: string]: any
}

export function useWebSocket() {
  const [connected, setConnected] = useState(false)
  const [servers, setServers] = useState<ServerInfo[]>([])
  const [username, setUsername] = useState('Player')
  const [nodeId, setNodeId] = useState('')
  const [dhtStats, setDhtStats] = useState<DhtStats | null>(null)
  const [peers, setPeers] = useState<PeerInfo[]>([])
  const wsRef = useRef<WebSocket | null>(null)
  const reconnectTimer = useRef<number | null>(null)
  const [reconnectCount, setReconnectCount] = useState(0)

  useEffect(() => {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
    const ws = new WebSocket(`${protocol}//${window.location.host}/ws`)
    wsRef.current = ws

    ws.onopen = () => {
      setConnected(true)
      console.log('WebSocket connected')
    }

    ws.onclose = () => {
      setConnected(false)
      console.log('WebSocket disconnected, will retry in 5s')
      // Reconnect by creating a new WebSocket (not a full page reload)
      reconnectTimer.current = window.setTimeout(() => {
        if (wsRef.current === ws) {
          // Re-run the effect by bumping reconnect count
          setReconnectCount(c => c + 1)
        }
      }, 5000)
    }

    ws.onmessage = (event) => {
      try {
        const msg: WebSocketMessage = JSON.parse(event.data)
        handleMessage(msg)
      } catch (e) {
        console.error('Failed to parse message:', e)
      }
    }

    return () => {
      if (reconnectTimer.current) clearTimeout(reconnectTimer.current)
      ws.close()
    }
  }, [reconnectCount])

  const handleMessage = useCallback((msg: WebSocketMessage) => {
    switch (msg.type) {
      case 'connected':
        setUsername(msg.username || 'Player')
        setNodeId(msg.nodeId || '')
        break
      case 'server_list':
        setServers(msg.servers || [])
        break
      case 'server_created':
        setServers(prev => [...prev, msg.server])
        break
      case 'username':
        setUsername(msg.username)
        break
      case 'peer_connected':
      case 'peer_disconnected':
        // Handle peer events
        break
      case 'pong':
        break
    }
  }, [])

  const sendMessage = useCallback((msg: object) => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      wsRef.current.send(JSON.stringify(msg))
    }
  }, [])

  const createServer = useCallback(() => {
    sendMessage({ type: 'create_server' })
  }, [sendMessage])

  const refreshServers = useCallback(() => {
    sendMessage({ type: 'get_servers' })
  }, [sendMessage])

  const setUsernameAction = useCallback((name: string) => {
    sendMessage({ type: 'set_username', username: name })
    setUsername(name)
  }, [sendMessage])

  // REST API calls
  const fetchDhtStats = useCallback(async () => {
    try {
      const res = await fetch('/api/dht/stats')
      const data = await res.json()
      setDhtStats(data)
    } catch (e) {
      console.error('Failed to fetch DHT stats:', e)
    }
  }, [])

  const fetchPeers = useCallback(async () => {
    try {
      const res = await fetch('/api/peers')
      const data = await res.json()
      setPeers(data.peers || [])
    } catch (e) {
      console.error('Failed to fetch peers:', e)
    }
  }, [])

  const joinServer = useCallback(async (code: string) => {
    try {
      const res = await fetch(`/api/servers/${code}/join`, { method: 'POST' })
      if (res.ok) {
        const data = await res.json()
        return data.server
      }
      return null
    } catch (e) {
      console.error('Failed to join server:', e)
      return null
    }
  }, [])

  return {
    connected,
    servers,
    username,
    nodeId,
    dhtStats,
    peers,
    createServer,
    refreshServers,
    setUsername: setUsernameAction,
    fetchDhtStats,
    fetchPeers,
    joinServer
  }
}
