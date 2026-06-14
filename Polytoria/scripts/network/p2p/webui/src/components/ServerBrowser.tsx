import { useState } from 'react'
import { ServerInfo } from '../hooks/useDht'

interface Props {
  servers: ServerInfo[]
  onCreateServer: () => void
  onJoinServer: (code: string) => Promise<ServerInfo | null>
  onRefresh: () => void
  onLoadLocalWorld: (file: File) => Promise<void>
}

export default function ServerBrowser({ servers, onCreateServer, onJoinServer, onRefresh, onLoadLocalWorld }: Props) {
  const [joinCode, setJoinCode] = useState('')
  const [joining, setJoining] = useState(false)
  const [loadingLocal, setLoadingLocal] = useState(false)
  const fileInputRef = useState<HTMLInputElement | null>(null)

  const handleJoin = async () => {
    if (!joinCode.trim()) return
    setJoining(true)
    const server = await onJoinServer(joinCode.trim().toUpperCase())
    setJoining(false)
    if (server) {
      console.log('Joining server:', server)
    } else {
      alert('Server not found')
    }
  }

  const handleCreate = () => {
    onCreateServer()
  }

  const handleFileSelect = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    if (file && file.name.endsWith('.poly')) {
      setLoadingLocal(true)
      onLoadLocalWorld(file).finally(() => {
        setLoadingLocal(false)
        event.target.value = ''
      })
    }
  }

  const triggerFileSelect = () => {
    const input = document.createElement('input')
    input.type = 'file'
    input.accept = '.poly'
    input.onchange = (e) => {
      const file = (e.target as HTMLInputElement).files?.[0]
      if (file) {
        setLoadingLocal(true)
        onLoadLocalWorld(file).finally(() => setLoadingLocal(false))
      }
    }
    input.click()
  }

  return (
    <div className="server-browser flex flex-col gap-5">
      {/* ─── Section Header ─── */}
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-polytoria-text">Servers</h1>
        <button id="refresh-servers" className="btn btn-secondary" onClick={onRefresh}>
          <span className="flex items-center gap-2">
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" /></svg>
            Refresh
          </span>
        </button>
      </div>

      {/* ─── Action Bar ─── */}
      <div className="card">
        <div className="flex flex-col sm:flex-row gap-3">
          <div className="flex-1 flex gap-2">
            <input
              id="join-code-input"
              className="input input-mono flex-1"
              placeholder="Enter server code (e.g. ABCD-1234)"
              value={joinCode}
              onChange={(e) => setJoinCode(e.target.value.toUpperCase())}
              onKeyDown={(e) => e.key === 'Enter' && handleJoin()}
              maxLength={9}
              disabled={joining}
            />
            <button
              id="join-server-btn"
              className="btn btn-success whitespace-nowrap"
              onClick={handleJoin}
              disabled={joining || !joinCode.trim()}
            >
              {joining ? 'Joining...' : 'Join'}
            </button>
          </div>
          <div className="flex gap-2">
            <button id="create-server-btn" className="btn btn-primary" onClick={handleCreate} disabled={loadingLocal}>
              <span className="flex items-center gap-2">
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M12 4v16m8-8H4" /></svg>
                Create Server
              </span>
            </button>
            <button id="load-world-btn" className="btn btn-secondary" onClick={triggerFileSelect} disabled={loadingLocal}>
              <span className="flex items-center gap-2">
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M7 16a4 4 0 01-.88-7.903A5 5 0 1115.9 6L16 6a5 5 0 011 9.9M15 13l-3-3m0 0l-3 3m3-3v12" /></svg>
                Load .poly
              </span>
            </button>
          </div>
        </div>
      </div>

      {/* ─── Server List ─── */}
      {servers.length === 0 ? (
        <div className="empty-state">
          <svg className="w-16 h-16 mx-auto mb-4 text-polytoria-border" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1}><path strokeLinecap="round" strokeLinejoin="round" d="M5 12h14M5 12a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v4a2 2 0 01-2 2M5 12a2 2 0 00-2 2v4a2 2 0 002 2h14a2 2 0 002-2v-4a2 2 0 00-2-2m-2-4h.01M17 16h.01" /></svg>
          <h3 className="text-lg font-semibold text-polytoria-text-muted mb-2">No servers found</h3>
          <p className="text-sm">Create a server, enter a code to join one, or load a local .poly world file</p>
        </div>
      ) : (
        <div className="server-list flex flex-col gap-2">
          {servers.map((server) => (
            <div key={server.code} className="card card-hover flex items-center justify-between">
              <div className="flex flex-col gap-1">
                <span className="server-code">{server.code}</span>
                <span className="text-sm text-polytoria-text-muted">Host: {server.hostUsername}</span>
              </div>
              <div className="flex items-center gap-3">
                <span className="text-sm font-medium text-polytoria-success flex items-center gap-1.5">
                  <span className="w-2 h-2 bg-polytoria-success rounded-full inline-block"></span>
                  {server.playerCount} player{server.playerCount !== 1 ? 's' : ''}
                </span>
                <button
                  className="btn btn-success text-sm"
                  onClick={() => onJoinServer(server.code)}
                >
                  <span className="flex items-center gap-1.5">
                    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M14.752 11.168l-3.197-2.132A1 1 0 0010 9.87v4.263a1 1 0 001.555.832l3.197-2.132a1 1 0 000-1.664z" /><path strokeLinecap="round" strokeLinejoin="round" d="M21 12a9 9 0 11-18 0 9 9 0 0118 0z" /></svg>
                    Play
                  </span>
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
