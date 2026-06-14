import { useState, useEffect } from 'react'
import { ServerInfo } from '../hooks/useDht'

interface GameInfo {
  id: string
  name: string
  fileName: string
  fileSize: number
  createdAt: string
}

interface Props {
  servers: ServerInfo[]
  onCreateServer: () => void
  onJoinServer: (code: string) => Promise<ServerInfo | null>
  username: string
}

export default function GamesBrowser({ servers, onCreateServer, onJoinServer, username }: Props) {
  const [games, setGames] = useState<GameInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [uploading, setUploading] = useState(false)
  const [selectedGame, setSelectedGame] = useState<GameInfo | null>(null)
  const [actionLoading, setActionLoading] = useState<string | null>(null)

  const fetchGames = async () => {
    try {
      const res = await fetch('/api/games')
      const data = await res.json()
      setGames(data.games || [])
    } catch (e) {
      console.error('Failed to fetch games:', e)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchGames()
  }, [])

  const handleUpload = () => {
    const input = document.createElement('input')
    input.type = 'file'
    input.accept = '.poly'
    input.onchange = async (e) => {
      const file = (e.target as HTMLInputElement).files?.[0]
      if (!file) return

      setUploading(true)
      try {
        const formData = new FormData()
        formData.append('world', file)

        const res = await fetch('/api/games', {
          method: 'POST',
          body: formData
        })

        const result = await res.json()
        if (result.success) {
          await fetchGames()
        } else {
          alert('Failed to upload game: ' + (result.error || 'Unknown error'))
        }
      } catch (error) {
        console.error('Failed to upload game:', error)
        alert('Failed to upload game: ' + error)
      } finally {
        setUploading(false)
      }
    }
    input.click()
  }

  const handleDelete = async (id: string, name: string) => {
    if (!confirm(`Delete "${name}"?`)) return

    try {
      const res = await fetch(`/api/games/${encodeURIComponent(id)}`, {
        method: 'DELETE'
      })
      const result = await res.json()
      if (result.success) {
        setGames(prev => prev.filter(g => g.id !== id))
      } else {
        alert('Failed to delete game: ' + (result.error || 'Unknown error'))
      }
    } catch (error) {
      console.error('Failed to delete game:', error)
      alert('Failed to delete game: ' + error)
    }
  }

  const handlePlay = async (id: string) => {
    setActionLoading('play')
    try {
      const res = await fetch(`/api/games/${encodeURIComponent(id)}/load`, {
        method: 'POST'
      })
      const result = await res.json()
      if (!result.success) {
        alert('Failed to load game: ' + (result.error || 'Unknown error'))
      }
      setSelectedGame(null)
    } catch (error) {
      console.error('Failed to load game:', error)
      alert('Failed to load game: ' + error)
    } finally {
      setActionLoading(null)
    }
  }

  const handleHostServer = async (id: string) => {
    setActionLoading('host')
    try {
      onCreateServer()

      const res = await fetch(`/api/games/${encodeURIComponent(id)}/load`, {
        method: 'POST'
      })
      const result = await res.json()
      if (!result.success) {
        alert('Failed to load game: ' + (result.error || 'Unknown error'))
      }
      setSelectedGame(null)
    } catch (error) {
      console.error('Failed to host server:', error)
      alert('Failed to host server: ' + error)
    } finally {
      setActionLoading(null)
    }
  }

  const handlePlayLocally = async (id: string) => {
    setActionLoading('local')
    try {
      const res = await fetch(`/api/games/${encodeURIComponent(id)}/load`, {
        method: 'POST'
      })
      const result = await res.json()
      if (!result.success) {
        alert('Failed to load game: ' + (result.error || 'Unknown error'))
      }
      setSelectedGame(null)
    } catch (error) {
      console.error('Failed to load game:', error)
      alert('Failed to load game: ' + error)
    } finally {
      setActionLoading(null)
    }
  }

  const handleJoinServer = async (code: string) => {
    setActionLoading('join-' + code)
    try {
      const server = await onJoinServer(code)
      if (!server) {
        alert('Server not found')
      }
    } catch (error) {
      console.error('Failed to join server:', error)
      alert('Failed to join server: ' + error)
    } finally {
      setActionLoading(null)
    }
  }

  const formatSize = (bytes: number) => {
    if (bytes < 1024) return bytes + ' B'
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB'
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB'
  }

  const formatDate = (dateStr: string) => {
    try {
      const d = new Date(dateStr)
      return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })
    } catch {
      return dateStr
    }
  }

  const gameColor = (name: string) => {
    let hash = 0
    for (let i = 0; i < name.length; i++) {
      hash = name.charCodeAt(i) + ((hash << 5) - hash)
    }
    const hue = Math.abs(hash) % 360
    return `hsl(${hue}, 55%, 45%)`
  }

  return (
    <div className="games-browser flex flex-col gap-6">
      {/* ─── Header ─── */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-polytoria-text">Games</h1>
          <p className="text-sm text-polytoria-text-muted mt-1">
            {games.length} game{games.length !== 1 ? 's' : ''} saved &middot; {servers.length} server{servers.length !== 1 ? 's' : ''} available
          </p>
        </div>
        <div className="flex gap-2">
          <button className="btn btn-primary" onClick={handleUpload} disabled={uploading}>
            <span className="flex items-center gap-2">
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M4 16v2a2 2 0 002 2h12a2 2 0 002-2v-2M7 10l5 5 5-5M12 15V4" />
              </svg>
              {uploading ? 'Uploading...' : 'Upload Game'}
            </span>
          </button>
          <button className="btn btn-secondary" onClick={fetchGames} disabled={loading}>
            <span className="flex items-center gap-2">
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
              </svg>
              Refresh
            </span>
          </button>
        </div>
      </div>

      {/* ─── Created Games Section ─── */}
      <section>
        <h2 className="text-lg font-semibold text-polytoria-text mb-4 flex items-center gap-2">
          <svg className="w-5 h-5 text-polytoria-primary" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
          </svg>
          Created
        </h2>

        {loading ? (
          <div className="empty-state py-8">
            <h3 className="text-lg font-semibold text-polytoria-text-muted mb-2">Loading games...</h3>
          </div>
        ) : games.length === 0 ? (
          <div className="empty-state py-8">
            <svg className="w-12 h-12 mx-auto mb-3 text-polytoria-border" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M15 10l4.553-2.276A1 1 0 0121 8.618v6.764a1 1 0 01-1.447.894L15 14M5 18h8a2 2 0 002-2V8a2 2 0 00-2-2H5a2 2 0 00-2 2v8a2 2 0 002 2z" />
            </svg>
            <h3 className="text-lg font-semibold text-polytoria-text-muted mb-2">No games yet</h3>
            <p className="text-sm">Upload a .poly world file to get started</p>
          </div>
        ) : (
          <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
            {games.map((game) => (
              <div
                key={game.id}
                className="bg-white border border-polytoria-border-light rounded-xl overflow-hidden transition-all duration-200 hover:shadow-lg hover:-translate-y-0.5 group cursor-pointer"
                onClick={() => setSelectedGame(game)}
              >
                <div className="relative h-32 flex items-center justify-center overflow-hidden" style={{ backgroundColor: gameColor(game.name) }}>
                  <div className="absolute inset-0 bg-black/10 group-hover:bg-black/5 transition-colors" />
                  <svg className="w-12 h-12 text-white/50" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M14.752 11.168l-3.197-2.132A1 1 0 0010 9.87v4.263a1 1 0 001.555.832l3.197-2.132a1 1 0 000-1.664z" />
                    <path strokeLinecap="round" strokeLinejoin="round" d="M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <div className="absolute inset-0 bg-black/0 group-hover:bg-black/30 flex items-center justify-center transition-all duration-200">
                    <span className="opacity-0 group-hover:opacity-100 transition-opacity duration-200 text-white text-sm font-medium">
                      Click to play
                    </span>
                  </div>
                </div>
                <div className="p-3">
                  <h3 className="font-semibold text-polytoria-text truncate" title={game.name}>{game.name}</h3>
                  <div className="flex items-center justify-between mt-2">
                    <span className="text-xs text-polytoria-text-dim">{formatSize(game.fileSize)}</span>
                    <span className="text-xs text-polytoria-text-dim">{formatDate(game.createdAt)}</span>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </section>

      {/* ─── Other Games Section ─── */}
      <section>
        <h2 className="text-lg font-semibold text-polytoria-text mb-4 flex items-center gap-2">
          <svg className="w-5 h-5 text-polytoria-secondary" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M5 12h14M5 12a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v4a2 2 0 01-2 2M5 12a2 2 0 00-2 2v4a2 2 0 002 2h14a2 2 0 002-2v-4a2 2 0 00-2-2" />
          </svg>
          Other
        </h2>

        {servers.length === 0 ? (
          <div className="empty-state py-8">
            <svg className="w-12 h-12 mx-auto mb-3 text-polytoria-border" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M5 12h14M5 12a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v4a2 2 0 01-2 2M5 12a2 2 0 00-2 2v4a2 2 0 002 2h14a2 2 0 002-2v-4a2 2 0 00-2-2" />
            </svg>
            <h3 className="text-lg font-semibold text-polytoria-text-muted mb-2">No other games found</h3>
            <p className="text-sm">Other players' servers will appear here</p>
          </div>
        ) : (
          <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
            {servers.map((server) => {
              const code = server.code
              return (
                <div
                  key={code}
                  className="bg-white border border-polytoria-border-light rounded-xl overflow-hidden transition-all duration-200 hover:shadow-lg hover:-translate-y-0.5 group cursor-pointer"
                  onClick={() => handleJoinServer(code)}
                >
                  <div className="relative h-32 flex items-center justify-center overflow-hidden bg-gradient-to-br from-polytoria-primary/30 to-polytoria-secondary/30">
                    <svg className="w-12 h-12 text-polytoria-primary/40" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z" />
                    </svg>
                    <div className="absolute inset-0 bg-black/0 group-hover:bg-black/20 flex items-center justify-center transition-all duration-200">
                      <span className="opacity-0 group-hover:opacity-100 transition-opacity duration-200 text-white text-sm font-medium">
                        Join game
                      </span>
                    </div>
                  </div>
                  <div className="p-3">
                    <h3 className="font-semibold text-polytoria-text truncate font-mono text-sm">{code}</h3>
                    <div className="flex items-center justify-between mt-2">
                      <span className="text-xs text-polytoria-text-dim">Host: {server.hostUsername}</span>
                      <span className="text-xs font-medium text-polytoria-success">{server.playerCount ?? 0} online</span>
                    </div>
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </section>

      {/* ─── Game Detail Modal ─── */}
      {selectedGame && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm"
          onClick={() => setSelectedGame(null)}
        >
          <div
            className="bg-white rounded-2xl shadow-2xl w-full max-w-sm mx-4 overflow-hidden"
            onClick={(e) => e.stopPropagation()}
          >
            {/* Thumbnail */}
            <div
              className="relative h-36 flex items-center justify-center overflow-hidden"
              style={{ backgroundColor: gameColor(selectedGame.name) }}
            >
              <svg className="w-16 h-16 text-white/40" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M14.752 11.168l-3.197-2.132A1 1 0 0010 9.87v4.263a1 1 0 001.555.832l3.197-2.132a1 1 0 000-1.664z" />
                <path strokeLinecap="round" strokeLinejoin="round" d="M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
            </div>

            {/* Info */}
            <div className="p-5">
              <h2 className="text-xl font-bold text-polytoria-text mb-1">{selectedGame.name}</h2>
              <p className="text-sm text-polytoria-text-muted mb-5">
                {formatSize(selectedGame.fileSize)} &middot; {formatDate(selectedGame.createdAt)} &middot; by You
              </p>

              {/* Action Buttons */}
              <div className="flex flex-col gap-3">
                <button
                  className="w-full btn btn-primary py-3 text-base font-semibold"
                  onClick={() => handlePlay(selectedGame.id)}
                  disabled={actionLoading === 'play'}
                >
                  {actionLoading === 'play' ? 'Loading...' : (
                    <span className="flex items-center justify-center gap-2">
                      <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M14.752 11.168l-3.197-2.132A1 1 0 0010 9.87v4.263a1 1 0 001.555.832l3.197-2.132a1 1 0 000-1.664z" />
                        <path strokeLinecap="round" strokeLinejoin="round" d="M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                      </svg>
                      Play
                    </span>
                  )}
                </button>

                <button
                  className="w-full btn btn-secondary py-3 text-base font-semibold"
                  onClick={() => handleHostServer(selectedGame.id)}
                  disabled={actionLoading === 'host'}
                >
                  {actionLoading === 'host' ? 'Loading...' : (
                    <span className="flex items-center justify-center gap-2">
                      <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M5 12h14M5 12a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v4a2 2 0 01-2 2M5 12a2 2 0 00-2 2v4a2 2 0 002 2h14a2 2 0 002-2v-4a2 2 0 00-2-2" />
                      </svg>
                      Host Server
                    </span>
                  )}
                </button>

                <button
                  className="w-full btn btn-secondary py-3 text-base font-semibold"
                  onClick={() => handlePlayLocally(selectedGame.id)}
                  disabled={actionLoading === 'local'}
                >
                  {actionLoading === 'local' ? 'Loading...' : (
                    <span className="flex items-center justify-center gap-2">
                      <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M12 18h.01M8 21h8a2 2 0 002-2V5a2 2 0 00-2-2H8a2 2 0 00-2 2v14a2 2 0 002 2z" />
                      </svg>
                      Play Locally
                    </span>
                  )}
                </button>
              </div>

              {/* Delete */}
              <button
                className="w-full mt-4 text-sm text-polytoria-text-dim hover:text-polytoria-danger transition-colors py-2"
                onClick={() => {
                  handleDelete(selectedGame.id, selectedGame.name)
                  setSelectedGame(null)
                }}
              >
                Delete game
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
