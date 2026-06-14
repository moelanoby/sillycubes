import { useState } from 'react'
import { DhtStats } from '../hooks/useDht'

interface Props {
  username: string
  nodeId: string
  dhtStats: DhtStats | null
  onUsernameChange: (name: string) => void
}

export default function Settings({ username, nodeId, dhtStats, onUsernameChange }: Props) {
  const [newUsername, setNewUsername] = useState(username)

  const handleSave = () => {
    if (newUsername.trim()) {
      onUsernameChange(newUsername.trim())
    }
  }

  return (
    <div className="max-w-lg flex flex-col gap-6">
      <h2 className="text-2xl font-bold text-polytoria-text">Settings</h2>

      {/* ─── Username ─── */}
      <div className="card flex flex-col gap-3">
        <label className="text-sm font-semibold text-polytoria-text">Username</label>
        <div className="flex gap-2">
          <input
            id="username-input"
            className="input flex-1"
            value={newUsername}
            onChange={(e) => setNewUsername(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && handleSave()}
            placeholder="Enter your username"
          />
          <button id="save-username-btn" className="btn btn-primary whitespace-nowrap" onClick={handleSave}>
            Save
          </button>
        </div>
      </div>

      {/* ─── Node Info ─── */}
      <div className="card flex flex-col gap-3">
        <label className="text-sm font-semibold text-polytoria-text">Your Node ID</label>
        <div className="px-4 py-2.5 bg-polytoria-surface border border-polytoria-border rounded-lg text-polytoria-text-dim font-mono text-sm cursor-default select-all">
          {nodeId || 'Not connected'}
        </div>
      </div>

      {/* ─── DHT Stats ─── */}
      <div className="flex flex-col gap-3">
        <label className="text-sm font-semibold text-polytoria-text">DHT Network</label>
        <div className="grid grid-cols-3 gap-3">
          <div className="stat-card">
            <div className="stat-value">{dhtStats?.nodeCount || 0}</div>
            <div className="stat-label">Nodes</div>
          </div>
          <div className="stat-card">
            <div className="stat-value">{dhtStats?.port || 0}</div>
            <div className="stat-label">Port</div>
          </div>
          <div className="stat-card">
            <div className="stat-value" style={{ color: '#00b06f' }}>K</div>
            <div className="stat-label">Kademlia</div>
          </div>
        </div>
      </div>
    </div>
  )
}
