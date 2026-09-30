import { useEffect, useState } from 'react'
import { NavLink, Navigate, Route, Routes } from 'react-router-dom'
import {
  FileText,
  FolderOpen,
  Bot,
  GitBranch,
  LayoutDashboard,
  Monitor,
  Moon,
  Scale,
  Search,
  Sparkles,
  Sun,
  Webhook,
  Upload,
  Users,
  ChartNoAxesCombined,
} from 'lucide-react'
import OverviewPage from './pages/OverviewPage'
import IndexedFilesPage from './pages/IndexedFilesPage'
import DeltaStatePage from './pages/DeltaStatePage'
import SearchPage from './pages/SearchPage'
import SubscriptionsPage from './pages/SubscriptionsPage'
import ChatPage from './pages/ChatPage'
import FeedbackPage from './pages/FeedbackPage'
import AgentsPage from './pages/AgentsPage'
import AttachmentFilesPage from './pages/AttachmentFilesPage'
import { AccountMenu } from './components/AuthGate'
import { useAppUser, canReadAdministration } from './components/AppUserContext'
import UsersPage from './pages/UsersPage'
import TokenUsagePage from './pages/TokenUsagePage'
import BrowsePage from './pages/BrowsePage'

type Theme = 'system' | 'light' | 'dark'

const THEME_KEY = 'sp-viewer-theme'
const THEME_ORDER: Theme[] = ['system', 'light', 'dark']
const THEME_LABELS: Record<Theme, string> = { system: 'System', light: 'Light', dark: 'Dark' }
const THEME_ICONS: Record<Theme, typeof Monitor> = { system: Monitor, light: Sun, dark: Moon }

function readTheme(): Theme {
  const stored = localStorage.getItem(THEME_KEY)
  return stored === 'light' || stored === 'dark' ? stored : 'system'
}

export default function App() {
  const user = useAppUser()
  const canReadAdmin = canReadAdministration(user)
  const home = canReadAdmin ? '/overview' : '/chat'
  const [theme, setTheme] = useState<Theme>(readTheme)

  useEffect(() => {
    if (theme === 'system') {
      document.documentElement.removeAttribute('data-theme')
      localStorage.removeItem(THEME_KEY)
    } else {
      document.documentElement.setAttribute('data-theme', theme)
      localStorage.setItem(THEME_KEY, theme)
    }
  }, [theme])

  const ThemeIcon = THEME_ICONS[theme]

  return (
    <div className="shell">
      <header className="topbar">
        <div className="brand">
          <strong>SharePoint Agent</strong>
        </div>
        <nav className="nav">
          {canReadAdmin && <><NavLink to="/overview">
            <LayoutDashboard size={16} />
            Overview
          </NavLink>
          <NavLink to="/files">
            <FileText size={16} />
            Indexed files
          </NavLink>
          <NavLink to="/browse"><FolderOpen size={16} />Browse</NavLink>
          <NavLink to="/delta">
            <GitBranch size={16} />
            Delta state
          </NavLink>
          <NavLink to="/subscriptions">
            <Webhook size={16} />
            Subscriptions
          </NavLink>
          </>}
          <NavLink to="/search">
            <Search size={16} />
            Search
          </NavLink>
          <NavLink to="/chat">
            <Sparkles size={16} />
            Chat
          </NavLink>
          {canReadAdmin && <NavLink to="/feedback">
            <Scale size={16} />
            Feedback
          </NavLink>}
          <NavLink to="/attachment-files">
            <Upload size={16} />
            Attachment files
          </NavLink>
          {canReadAdmin && <NavLink to="/agents">
            <Bot size={16} />
            Agents
          </NavLink>}
          {canReadAdmin && <NavLink to="/users"><Users size={16} />Users</NavLink>}
          {canReadAdmin && <NavLink to="/token-usage"><ChartNoAxesCombined size={16} />Token usage</NavLink>}
        </nav>
        <button
          className="ghost"
          title="Switch between system, light, and dark"
          onClick={() => setTheme(THEME_ORDER[(THEME_ORDER.indexOf(theme) + 1) % THEME_ORDER.length])}
        >
          <ThemeIcon size={15} />
          {THEME_LABELS[theme]}
        </button>
        <AccountMenu />
      </header>

      <main className="main">
        <Routes>
          <Route path="/" element={<Navigate to={home} replace />} />
          <Route path="/overview" element={canReadAdmin ? <OverviewPage /> : <Navigate to={home} replace />} />
          <Route path="/files" element={canReadAdmin ? <IndexedFilesPage /> : <Navigate to={home} replace />} />
          <Route path="/browse" element={canReadAdmin ? <BrowsePage /> : <Navigate to={home} replace />} />
          <Route path="/attachment-files" element={<AttachmentFilesPage />} />
          <Route path="/delta" element={canReadAdmin ? <DeltaStatePage /> : <Navigate to={home} replace />} />
          <Route path="/subscriptions" element={canReadAdmin ? <SubscriptionsPage /> : <Navigate to={home} replace />} />
          <Route path="/search" element={<SearchPage />} />
          <Route path="/chat" element={<ChatPage />} />
          <Route path="/feedback" element={canReadAdmin ? <FeedbackPage /> : <Navigate to={home} replace />} />
          <Route path="/agents" element={canReadAdmin ? <AgentsPage /> : <Navigate to={home} replace />} />
          <Route path="/users" element={canReadAdmin ? <UsersPage /> : <Navigate to={home} replace />} />
          <Route path="/token-usage" element={canReadAdmin ? <TokenUsagePage /> : <Navigate to={home} replace />} />
          <Route path="/embedding-usage" element={<Navigate to="/token-usage?tab=embeddings" replace />} />
          <Route path="*" element={<Navigate to={home} replace />} />
        </Routes>
      </main>
    </div>
  )
}
