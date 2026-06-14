import { useState, useEffect } from 'react'
import { useWebSocket } from './hooks/useDht'
import FriendList from './components/FriendList'
import Settings from './components/Settings'
import GamesBrowser from './components/GamesBrowser'
import './App.css'

type Tab = 'games' | 'friends' | 'settings'

function App() {
  const [tab, setTab] = useState<Tab>('games')
  const {
    connected,
    servers,
    username,
    nodeId,
    dhtStats,
    peers,
    createServer,
    refreshServers,
    setUsername,
    fetchDhtStats,
    fetchPeers,
    joinServer
  } = useWebSocket()

  useEffect(() => {
    refreshServers()
    fetchDhtStats()
    fetchPeers()

    const interval = setInterval(() => {
      refreshServers()
      fetchPeers()
    }, 5000)

    return () => clearInterval(interval)
  }, [])

  return (
    <div className="min-h-screen flex flex-col bg-polytoria-surface">
      {/* ─── Header ─── */}
      <header className="flex items-center justify-between px-6 py-3 bg-polytoria-header" style={{ boxShadow: '0 1px 4px rgba(0,0,0,0.1)' }}>
        <div className="flex items-center gap-3">
          <div className="w-9 h-9 bg-white rounded-lg flex items-center justify-center font-extrabold text-polytoria-primary text-lg" style={{ boxShadow: '0 1px 3px rgba(0,0,0,0.15)' }}>
            P
          </div>
          <span className="text-xl font-bold text-white tracking-tight">Polytoria</span>
        </div>
        <div className="flex items-center gap-2 text-sm text-white/70">
          <span className={`status-dot ${connected ? 'status-online' : 'status-offline'}`} />
          <span>{connected ? 'Connected' : 'Connecting...'}</span>
        </div>
      </header>

      {/* ─── Tab Navigation ─── */}
      <nav className="flex gap-0 bg-white border-b border-polytoria-border px-6" style={{ boxShadow: '0 1px 2px rgba(0,0,0,0.04)' }}>
        {(['games', 'friends', 'settings'] as Tab[]).map((t) => (
          <button
            key={t}
            id={`tab-${t}`}
            className={`tab ${tab === t ? 'tab-active' : ''}`}
            onClick={() => setTab(t)}
          >
            {t === 'games' && (
              <span className="flex items-center gap-2">
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M15 10l4.553-2.276A1 1 0 0121 8.618v6.764a1 1 0 01-1.447.894L15 14M5 18h8a2 2 0 002-2V8a2 2 0 00-2-2H5a2 2 0 00-2 2v8a2 2 0 002 2z" /></svg>
                Games
              </span>
            )}
            {t === 'friends' && (
              <span className="flex items-center gap-2">
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z" /></svg>
                Friends
              </span>
            )}
            {t === 'settings' && (
              <span className="flex items-center gap-2">
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.573-1.066z" /><path strokeLinecap="round" strokeLinejoin="round" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" /></svg>
                Settings
              </span>
            )}
          </button>
        ))}
      </nav>

      {/* ─── Main Content ─── */}
      <main className="flex-1 p-6 max-w-6xl mx-auto w-full">
        {tab === 'games' && (
          <GamesBrowser
            servers={servers}
            onCreateServer={createServer}
            onJoinServer={joinServer}
            username={username}
          />
        )}
        {tab === 'friends' && (
          <FriendList
            username={username}
            peers={peers}
          />
        )}
        {tab === 'settings' && (
          <Settings
            username={username}
            nodeId={nodeId}
            dhtStats={dhtStats}
            onUsernameChange={setUsername}
          />
        )}
      </main>

      {/* ─── Footer ─── */}
      <footer className="flex items-center justify-between px-6 py-3 bg-white border-t border-polytoria-border text-xs text-polytoria-text-dim">
        <span>{dhtStats ? `${dhtStats.nodeCount} nodes in DHT` : 'Connecting to DHT...'}</span>
        <span>Node: {nodeId ? nodeId.slice(0, 12) + '...' : '...'}</span>
      </footer>
    </div>
  )
}

export default App
