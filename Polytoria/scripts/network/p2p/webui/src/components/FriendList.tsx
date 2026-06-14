import { PeerInfo } from '../hooks/useDht'

interface Props {
  username: string
  peers: PeerInfo[]
}

const AVATAR_COLORS = [
  '#00a2ff', '#00b06f', '#7c3aed', '#f59e0b', '#ef4444', '#06b6d4', '#ec4899', '#8b5cf6'
]

function getAvatarColor(id: string): string {
  let hash = 0
  for (let i = 0; i < id.length; i++) {
    hash = id.charCodeAt(i) + ((hash << 5) - hash)
  }
  return AVATAR_COLORS[Math.abs(hash) % AVATAR_COLORS.length]
}

export default function FriendList({ username, peers }: Props) {
  return (
    <div className="flex flex-col gap-5">
      <div className="flex items-center justify-between">
        <h2 className="text-2xl font-bold text-polytoria-text">Friends</h2>
        <span className="text-sm font-medium text-polytoria-text-dim">{peers.length} peer{peers.length !== 1 ? 's' : ''} online</span>
      </div>

      {peers.length === 0 ? (
        <div className="empty-state">
          <svg className="w-16 h-16 mx-auto mb-4 text-polytoria-border" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1}><path strokeLinecap="round" strokeLinejoin="round" d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z" /></svg>
          <h3 className="text-lg font-semibold text-polytoria-text-muted mb-2">No peers discovered</h3>
          <p className="text-sm">Other players will appear here once they join the DHT network</p>
        </div>
      ) : (
        <div className="flex flex-col gap-2">
          {peers.map((peer) => (
            <div key={peer.nodeId} className="card card-hover flex items-center justify-between">
              <div className="flex items-center gap-3">
                <div
                  className="w-10 h-10 rounded-full flex items-center justify-center text-sm font-bold text-white"
                  style={{ backgroundColor: getAvatarColor(peer.nodeId) }}
                >
                  {peer.nodeId.charAt(0).toUpperCase()}
                </div>
                <div>
                  <div className="text-sm font-semibold text-polytoria-text">Peer {peer.nodeId.slice(0, 8)}</div>
                  <div className="text-xs text-polytoria-text-dim font-mono">{peer.ip}:{peer.port}</div>
                </div>
              </div>
              <button id={`invite-${peer.nodeId.slice(0, 8)}`} className="btn btn-primary text-sm">
                <span className="flex items-center gap-1.5">
                  <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M12 4v16m8-8H4" /></svg>
                  Invite
                </span>
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
