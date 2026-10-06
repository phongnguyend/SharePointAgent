import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useSearchParams } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import {
  Bot,
  Check,
  ChevronDown,
  Copy,
  Cpu,
  RefreshCw,
  ExternalLink,
  Eye,
  Folder,
  FolderPlus,
  GitBranch,
  ArrowUp,
  ChevronRight,
  FolderOpen,
  HardDrive,
  MessageSquare,
  Pencil,
  ScrollText,
  TriangleAlert,
  Plus,
  Paperclip,
  Download,
  Upload,
  SendHorizontal,
  Sparkles,
  ThumbsDown,
  ThumbsUp,
  Trash2,
  User,
  X,
} from 'lucide-react'
import {
  branchConversation,
  createConversation,
  deleteConversation,
  getThread,
  listAgents,
  listConversations,
  sendChatMessage,
  setMessageFeedback,
  uploadAttachmentFile,
  getAttachmentOptions,
  getCurrentUser,
  downloadAttachmentFile,
  listWorkspaces,
  getConversationSession,
  listConversationFiles,
  downloadConversationFile,
  createWorkspace,
  updateWorkspace,
  deleteWorkspace,
} from '../api/client'
import type {
  ChatConversation,
  ChatFeedback,
  ChatMessage,
  ChatMessageAttachment,
  ChatSandboxSession,
  ChatWorkspace,
  FileSystemEntry,
} from '../api/types'
import { CopyButton, Empty, ErrorBanner, Field, LoadingBar, Modal } from '../components/ui'
import { FileTypeIcon } from '../components/FileTypeIcon'
import { useSandboxFileManagement } from '../components/SandboxFileManagement'
import { AttachmentDownload } from '../components/AttachmentDownload'
import { MonthlyTokenUsage } from '../components/MonthlyTokenUsage'
import { OfficeViewer } from '../components/OfficeViewer'
import { ImageViewer } from '../components/ImageViewer'
import { MarkdownViewer } from '../components/MarkdownViewer'
import { isPreviewableOfficeFile } from '../lib/officeFiles'
import {
  folderLabel,
  formatBytes,
  formatDateTime,
  formatMessageTime,
  formatRelative,
  formatScore,
  formatTokenUsage,
} from '../lib/format'
import { copyText } from '../lib/clipboard'
import { useAsync } from '../lib/useAsync'

/** Match the API's caps, so a box stops where the request would be rejected. */
const WORKSPACE_NAME_LIMIT = 200
const WORKSPACE_RULES_LIMIT = 8000

export default function ChatPage() {
  const readOnly = !canManageOwnContent(useAppUser())
  const conversations = useAsync((signal) => listConversations(signal), [])
  const workspaces = useAsync((signal) => listWorkspaces(signal), [])
  const agents = useAsync((signal) => listAgents(signal), [])
  const attachmentOptions = useAsync((signal) => getAttachmentOptions(signal), [])
  const tokenUsage = useAsync(signal => getCurrentUser(signal), [])
  useEffect(() => {
    const timer = window.setInterval(tokenUsage.reload, 60_000)
    window.addEventListener('focus', tokenUsage.reload)
    return () => { window.clearInterval(timer); window.removeEventListener('focus', tokenUsage.reload) }
  }, [tokenUsage.reload])
  // The open conversation is in the URL, so a link from elsewhere — the Feedback page — can open the
  // one it is pointing at rather than dropping the reader into whichever is most recent.
  const [params, setParams] = useSearchParams()
  const [activeId, setActiveId] = useState<string | null>(params.get('conversation'))
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [draft, setDraft] = useState('')
  const [sending, setSending] = useState(false)
  const [streamingText, setStreamingText] = useState('')
  const [agentStatus, setAgentStatus] = useState('Thinking…')
  const [error, setError] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)
  const [creatingConversation, setCreatingConversation] = useState(false)
  const [agentMenuOpen, setAgentMenuOpen] = useState(false)
  // Which workspace the sidebar is showing: '' is all of them, 'none' the conversations in none.
  const [workspaceFilter, setWorkspaceFilter] = useState('')
  // The inline editor. A null id is a new workspace, an id an edit of that one.
  const [workspaceDraft, setWorkspaceDraft] =
    useState<{ id: string | null; name: string; instructions: string } | null>(null)
  const [confirmDeleteWorkspace, setConfirmDeleteWorkspace] = useState(false)
  const [conversationTab, setConversationTab] = useState<'workspace' | 'chat'>('chat')
  const [workspaceDetailsOpen, setWorkspaceDetailsOpen] = useState(false)
  const [copiedSession, setCopiedSession] = useState(false)
  const [filesPath, setFilesPath] = useState('.')
  const [fileSort, setFileSort] = useState<{ key: FileSortKey; desc: boolean }>({ key: 'name', desc: false })
  const [previewFile, setPreviewFile] = useState<string | null>(null)
  const [attachments, setAttachments] = useState<ChatMessageAttachment[]>([])
  const [uploading, setUploading] = useState(false)
  const uploadInProgress = useRef(false)
  const threadRef = useRef<HTMLDivElement>(null)
  const composerRef = useRef<HTMLTextAreaElement>(null)
  const wasSendingRef = useRef(false)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const agentMenuRef = useRef<HTMLDivElement>(null)
  const agentMenuButtonRef = useRef<HTMLButtonElement>(null)

  const list = conversations.data ?? []
  const workspaceList = workspaces.data ?? []
  const selectedWorkspace = workspaceList.find((item) => item.id === workspaceFilter) ?? null

  // A new conversation joins whichever workspace the sidebar is filtered to, which is what makes
  // "open a workspace, then start a chat in it" reach the same files as the chats already there.
  const targetWorkspaceId = selectedWorkspace?.id ?? null
  const visible = workspaceFilter === ''
    ? list
    : list.filter((item) => (workspaceFilter === 'none' ? item.workspaceId === null : item.workspaceId === workspaceFilter))

  // Headings only earn their space once there is a workspace to distinguish conversations by.
  const grouped = workspaceFilter === '' && workspaceList.length > 0
  const groups: { workspace: ChatWorkspace | null; items: ChatConversation[] }[] = grouped
    ? [
        ...workspaceList.map((workspace) => ({
          workspace,
          items: list.filter((item) => item.workspaceId === workspace.id),
        })),
        // Anything whose workspace is not in the list lands here rather than disappearing from it.
        {
          workspace: null,
          items: list.filter((item) => !workspaceList.some((workspace) => workspace.id === item.workspaceId)),
        },
      ].filter((group) => group.items.length > 0)
    : [{ workspace: null, items: visible }]

  const active = list.find((item) => item.id === activeId) ?? null
  const activeWorkspace = workspaceList.find((item) => item.id === active?.workspaceId) ?? null
  const activeAgent = active
    ? (agents.data ?? []).find((item) =>
        active.agentId ? item.id === active.agentId : item.name.toLowerCase() === 'default',
      )
    : null

  useEffect(() => {
    // Wait for the render that re-enables the textbox before restoring keyboard focus.
    if (wasSendingRef.current && !sending) {
      composerRef.current?.focus({ preventScroll: true })
    }
    wasSendingRef.current = sending
  }, [sending])

  const open = (id: string | null) => {
    setActiveId(id)
    setParams(id ? { conversation: id } : {}, { replace: true })
  }

  const requested = params.get('conversation')
  const targetMessageId = params.get('message')
  useEffect(() => {
    if (targetMessageId) {
      setConversationTab('chat')
    }
  }, [targetMessageId])
  const jumpedTo = useRef<string | null>(null)
  const [highlighted, setHighlighted] = useState<string | null>(null)
  useEffect(() => {
    if (requested && requested !== activeId) {
      setActiveId(requested)
    }
    // Only a change to the URL should move the selection; selecting in the page writes the URL itself.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [requested])

  // Picks a conversation only when none is selected — on first load, and after a delete clears the
  // selection. It must not correct a selection that is merely newer than the list, or creating a
  // conversation would snap straight back to the most recent one already in it.
  useEffect(() => {
    if (activeId === null && list.length > 0) {
      open(list[0].id)
    }
  }, [list, activeId])

  const thread = useAsync(
    async (signal) => (activeId ? getThread(activeId, signal) : null),
    [activeId],
  )

  const session = useAsync(
    async (signal) => (activeId && conversationTab === 'workspace'
      ? { conversationId: activeId, value: await getConversationSession(activeId, signal) } : null),
    [activeId, conversationTab],
  )

  const sandboxFiles = useAsync(
    async (signal) => (activeId && conversationTab === 'workspace'
      ? { conversationId: activeId, value: await listConversationFiles(activeId, filesPath === '.' ? null : filesPath, false, signal) }
      : null),
    [activeId, filesPath, conversationTab],
  )

  // Switching conversations empties the thread at once rather than leaving the previous one on
  // screen until the new fetch lands, and the guard keeps an in-flight response for the conversation
  // just left from overwriting the new one.
  useEffect(() => {
    setMessages([])
    setAttachments([])
    setFilesPath('.')
    setPreviewFile(null)
    setCopiedSession(false)
    setWorkspaceDetailsOpen(false)
  }, [activeId])

  useEffect(() => {
    if (thread.data && thread.data.conversation.id === activeId) {
      setMessages(thread.data.messages)
    }
  }, [thread.data, activeId])

  /**
   * A chat is read from the bottom, so that is where every new turn and every conversation opened
   * lands — unless the URL names a message to jump to, which a link from the Feedback page does. The
   * jump happens once per target: later turns in the same conversation scroll to the bottom again.
   */
  useEffect(() => {
    const element = threadRef.current
    if (!element || conversationTab !== 'chat') {
      return
    }

    if (targetMessageId && jumpedTo.current !== targetMessageId) {
      // The thread has not arrived yet; stay put rather than flashing to the bottom first.
      if (messages.length === 0) return

      jumpedTo.current = targetMessageId
      const node = element.querySelector(`[data-message-id="${CSS.escape(targetMessageId)}"]`)
      if (node) {
        node.scrollIntoView({ block: 'center' })
        setHighlighted(targetMessageId)
        return
      }
    }

    element.scrollTop = element.scrollHeight
  }, [messages, sending, targetMessageId, conversationTab])

  // The highlight is a hint, not a state: it fades once the reader has had a chance to see it.
  useEffect(() => {
    if (!highlighted) return
    const timer = setTimeout(() => setHighlighted(null), 2600)
    return () => clearTimeout(timer)
  }, [highlighted])

  useEffect(() => {
    if (!agentMenuOpen) return
    agentMenuRef.current?.querySelector<HTMLButtonElement>('.chat-agent-menu button')?.focus()
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      setAgentMenuOpen(false)
      agentMenuButtonRef.current?.focus()
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [agentMenuOpen])

  // The tick on a copied session ID is a hint, not a state: it goes away on its own.
  useEffect(() => {
    if (!copiedSession) return
    const timer = setTimeout(() => setCopiedSession(false), 1800)
    return () => clearTimeout(timer)
  }, [copiedSession])

  const newChat = async (agentId: string | null = null) => {
    if (creatingConversation) return
    setAgentMenuOpen(false)
    setError(null)
    setCreatingConversation(true)
    try {
      const created = await createConversation({ agentId, workspaceId: targetWorkspaceId })
      conversations.reload()
      workspaces.reload()
      open(created.id)
      setConversationTab('chat')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setCreatingConversation(false)
    }
  }

  /**
   * The reaction is applied to the thread immediately and rolled back if the write fails, because a
   * thumbs-up that waits on a round trip feels broken.
   */
  const react = async (messageId: string, feedback: ChatFeedback | null) => {
    const previous = messages.find((message) => message.id === messageId)?.feedback ?? null
    setMessages((current) =>
      current.map((message) => (message.id === messageId ? { ...message, feedback } : message)),
    )

    try {
      await setMessageFeedback(messageId, feedback)
    } catch (cause) {
      setMessages((current) =>
        current.map((message) =>
          message.id === messageId ? { ...message, feedback: previous } : message,
        ),
      )
      setError(cause instanceof Error ? cause.message : String(cause))
    }
  }

  const branch = async (messageId: string) => {
    if (!activeId) return

    setError(null)
    try {
      const created = await branchConversation(activeId, messageId)
      conversations.reload()
      open(created.id)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    }
  }

  const remove = async (id: string) => {
    setError(null)
    setConfirmDelete(null)
    try {
      await deleteConversation(id)
      if (id === activeId) open(null)
      conversations.reload()
      workspaces.reload()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    }
  }

  const saveWorkspace = async () => {
    const name = workspaceDraft?.name.trim() ?? ''
    if (name === '') return

    const rules = workspaceDraft!.instructions.trim() || null
    setError(null)
    try {
      const saved = workspaceDraft!.id === null
        ? await createWorkspace(name, rules)
        : await updateWorkspace(workspaceDraft!.id, name, rules)
      setWorkspaceDraft(null)
      workspaces.reload()
      // A workspace is made in order to be used, so the sidebar switches to it.
      setWorkspaceFilter(saved.id)
      // The open conversation may be in it, and it is now working under different rules.
      session.reload()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    }
  }

  const removeWorkspace = async () => {
    if (!selectedWorkspace) return

    setError(null)
    setConfirmDeleteWorkspace(false)
    try {
      await deleteWorkspace(selectedWorkspace.id)
      setWorkspaceFilter('')
      workspaces.reload()
      // Its conversations are still there, ungrouped, so the list has to be read again.
      conversations.reload()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    }
  }

  const addFiles = async (files: FileList | File[] | null) => {
    if (!files?.length || readOnly || sending || uploadInProgress.current) {
      return
    }
    if (files.length + attachments.length > 10) {
      setError('You can attach up to 10 files per message. Remove an attachment before adding more.')
      return
    }
    const allowed = attachmentOptions.data?.allowedFileExtensions ?? []
    const invalid = Array.from(files).find((file) => !allowed.includes(file.name.slice(file.name.lastIndexOf('.')).toLowerCase()))
    if (invalid) {
      setError(`${invalid.name}: file type is not allowed. Allowed extensions: ${allowed.join(', ')}.`)
      if (fileInputRef.current) {
        fileInputRef.current.value = ''
      }
      return
    }
    uploadInProgress.current = true
    setUploading(true)
    setError(null)
    try {
      for (const file of Array.from(files)) {
        const uploaded = await uploadAttachmentFile(file)
        if (uploaded.status !== 'Indexed') {
          throw new Error(uploaded.errorMessage ?? `${uploaded.fileName} could not be indexed.`)
        }
        setAttachments((current) => [...current, {
          id: uploaded.id,
          fileName: uploaded.fileName,
          contentType: uploaded.contentType,
          sizeBytes: uploaded.sizeBytes,
        }])
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      uploadInProgress.current = false
      setUploading(false)
      if (fileInputRef.current) {
        fileInputRef.current.value = ''
      }
    }
  }

  /**
   * The question is shown immediately with a local id and replaced by the stored one when the turn
   * returns, so the thread does not sit empty while the agent searches.
   */
  const send = async () => {
    const content = draft.trim()
    if (!content || sending || uploadInProgress.current || readOnly) {
      return
    }

    let conversationId = activeId
    setError(null)
    setSending(true)
    setStreamingText('')
    setAgentStatus('Thinking…')
    setDraft('')

    const pending: ChatMessage = {
      id: `pending-${Date.now()}`,
      conversationId: conversationId ?? '',
      role: 'User',
      content,
      citations: [],
      inputTokenCount: 0,
      outputTokenCount: 0,
      totalTokenCount: 0,
      embeddingTokenCount: 0,
      modelId: null,
      feedback: null,
      attachments,
      createdAtUtc: new Date().toISOString(),
    }
    setMessages((current) => [...current, pending])

    try {
      // Typing into an empty page starts a conversation rather than making the user press New chat.
      if (conversationId === null) {
        const created = await createConversation({ workspaceId: targetWorkspaceId })
        conversationId = created.id
        open(created.id)
      }

      const result = await sendChatMessage(conversationId, content, attachments.map((file) => file.id), (event) => {
        if (event.type === 'started') {
          setMessages((current) => [
            ...current.filter((message) => message.id !== pending.id),
            event.question,
          ])
        } else if (event.type === 'status') {
          setAgentStatus(event.message)
        } else if (event.type === 'delta') {
          setStreamingText((current) => current + event.text)
          setAgentStatus('Writing the answer…')
        }
      })
      setMessages((current) => [
        ...current.filter(
          (message) => message.id !== pending.id && message.id !== result.question.id,
        ),
        result.question,
        result.answer,
      ])
      conversations.reload()
      setAttachments([])
    } catch (cause) {
      setMessages((current) => current.filter((message) => message.id !== pending.id))
      setDraft(content)
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setSending(false)
      tokenUsage.reload()
      setStreamingText('')
      setAgentStatus('Thinking…')
    }
  }

  return (
    <div className="stack chat-page" style={{ gap: 14 }}>
      {error ? <ErrorBanner message={error} /> : null}
      {agents.error ? <ErrorBanner message={agents.error} onRetry={agents.reload} /> : null}

      <div className="chat-shell">
        <aside className="card chat-sidebar">
          <div className="card-head">
            <h2>
              <MessageSquare size={15} />
              Conversations
            </h2>
            <button className="ghost icon-only" title="Refresh conversations and token usage" aria-label="Refresh conversations and token usage"
              disabled={conversations.loading || workspaces.loading || tokenUsage.loading}
              onClick={() => {
                conversations.reload()
                workspaces.reload()
                tokenUsage.reload()
              }}><RefreshCw size={14} /></button>
            <div
              className="chat-new-actions"
              ref={agentMenuRef}
            >
              <button
                disabled={readOnly || creatingConversation}
                title={selectedWorkspace ? `New chat in ${selectedWorkspace.name}` : 'New chat, with files of its own'}
                onClick={() => void newChat()}
              >
                <Plus size={14} />
                New
              </button>
              <span className="chat-agent-picker">
                <button
                  ref={agentMenuButtonRef}
                  type="button"
                  aria-label="Choose agent for new conversation"
                  title="Choose agent for new conversation"
                  aria-expanded={agentMenuOpen}
                  disabled={readOnly || creatingConversation || agents.loading || !!agents.error}
                  onClick={() => setAgentMenuOpen((open) => !open)}
                >
                  <ChevronDown size={14} aria-hidden="true" />
                </button>
              </span>
              {agentMenuOpen ? (
                <div className="chat-agent-menu" role="group" aria-label="Choose an agent">
                  <span className="chat-agent-menu-label">
                    {selectedWorkspace ? `Start a conversation in ${selectedWorkspace.name}, with` : 'Start a conversation with'}
                  </span>
                  <button type="button" onClick={() => void newChat()}><Bot size={14} />Default agent</button>
                  {(agents.data ?? [])
                    .filter((agent) => agent.name.toLowerCase() !== 'default')
                    .map((agent) => (
                      <button key={agent.id} type="button" title={agent.name} onClick={() => void newChat(agent.id)}>
                        <Bot size={14} /><span>{agent.name}</span>
                      </button>
                    ))}
                </div>
              ) : null}
            </div>
          </div>
          <div className="chat-workspace-bar">
            <select
              aria-label="Show conversations in"
              value={workspaceFilter}
              onChange={(event) => {
                setWorkspaceFilter(event.target.value)
                setWorkspaceDraft(null)
                setConfirmDeleteWorkspace(false)
              }}
            >
              <option value="">All conversations</option>
              <option value="none">No workspace</option>
              {workspaceList.map((workspace) => (
                <option key={workspace.id} value={workspace.id}>
                  {workspace.name} ({workspace.conversationCount}){workspace.instructions ? ' · rules' : ''}
                </option>
              ))}
            </select>
            <button
              className="ghost icon-only"
              disabled={readOnly}
              title="New workspace"
              aria-label="New workspace"
              onClick={() => {
                setConfirmDeleteWorkspace(false)
                setWorkspaceDraft({ id: null, name: '', instructions: '' })
              }}
            >
              <FolderPlus size={14} />
            </button>
            {selectedWorkspace ? (
              <>
                <button
                  className="ghost icon-only"
                  disabled={readOnly}
                  title={`Edit ${selectedWorkspace.name} and its rules`}
                  aria-label="Edit workspace"
                  onClick={() => {
                    setConfirmDeleteWorkspace(false)
                    setWorkspaceDraft({
                      id: selectedWorkspace.id,
                      name: selectedWorkspace.name,
                      instructions: selectedWorkspace.instructions ?? '',
                    })
                  }}
                >
                  <Pencil size={14} />
                </button>
                <button
                  className="ghost icon-only"
                  disabled={readOnly}
                  title={`Delete ${selectedWorkspace.name}`}
                  aria-label="Delete workspace"
                  onClick={() => {
                    setWorkspaceDraft(null)
                    setConfirmDeleteWorkspace(true)
                  }}
                >
                  <Trash2 size={14} />
                </button>
              </>
            ) : null}
          </div>
          {confirmDeleteWorkspace && selectedWorkspace ? (
            <div className="chat-workspace-draft">
              <span className="hint">
                Delete {selectedWorkspace.name}? Its conversations stay, each with files of its own again.
              </span>
              <button className="ghost" onClick={() => setConfirmDeleteWorkspace(false)}>No</button>
              <button className="danger" onClick={() => void removeWorkspace()}>Delete</button>
            </div>
          ) : null}
          <LoadingBar active={conversations.loading || workspaces.loading} />
          <div className="chat-conversations">
            {visible.length === 0 && !conversations.loading ? (
              <Empty
                title={workspaceFilter === '' ? 'No conversations' : 'No conversations here'}
                icon={<MessageSquare size={24} strokeWidth={1.5} />}
                detail={workspaceFilter === '' ? 'Use New or choose an agent.' : 'New starts one in this workspace.'}
              />
            ) : (
              groups.map((group) => (
                <div key={group.workspace?.id ?? 'none'}>
                  {grouped ? (
                    <div className="chat-workspace-heading">
                      <Folder size={12} />
                      {group.workspace?.name ?? 'No workspace'}
                    </div>
                  ) : null}
                  {group.items.map((item) => (
                    <ConversationRow
                      key={item.id}
                      item={item}
                      active={item.id === activeId}
                      confirming={confirmDelete === item.id}
                      onOpen={() => open(item.id)}
                      onAskDelete={() => setConfirmDelete(item.id)}
                      onCancelDelete={() => setConfirmDelete(null)}
                      onDelete={() => remove(item.id)}
                    />
                  ))}
                </div>
              ))
            )}
          </div>
          <section className="chat-sidebar-usage" aria-label="My model token usage this month">
            {tokenUsage.data && <MonthlyTokenUsage user={tokenUsage.data} showDetails />}
            {tokenUsage.error && <ErrorBanner message={tokenUsage.error} onRetry={tokenUsage.reload} />}
          </section>
        </aside>

        <section className="card chat-panel">
          <div className="card-head">
            <h2>
              <Bot size={15} />
              {active?.title ?? 'New chat'}
            </h2>
            <div className="row" style={{ gap: 8 }}>
              {active ? (
                <span className="badge accent" title="Agent assigned to this conversation">
                  <Bot size={12} />
                  {activeAgent?.name ?? 'Agent unavailable'}
                </span>
              ) : null}
              {active ? (
                <span className="badge" title="Model ID used by the current agent">
                  <Cpu size={12} />
                  {activeAgent?.modelId ?? (agents.loading ? 'Loading model…' : 'Model unavailable')}
                </span>
              ) : null}
              {active?.userId ? (
                <span className="badge accent" title="Searches are filtered to this user's permissions">
                  as {active.userId}
                </span>
              ) : (
                <span className="hint">Searching the whole index, unfiltered</span>
              )}
            </div>
          </div>

          <div className="conversation-tabs" role="tablist" aria-label="Conversation views"
            onKeyDown={event => {
              if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
                event.preventDefault()
                const next = event.key === 'Home' ? 'workspace' : event.key === 'End' ? 'chat'
                  : conversationTab === 'chat' ? 'workspace' : 'chat'
                setConversationTab(next)
                document.getElementById(`conversation-${next}-tab`)?.focus()
              }
            }}>
            <button type="button" role="tab" id="conversation-workspace-tab" aria-controls="conversation-workspace-panel"
              aria-selected={conversationTab === 'workspace'} tabIndex={conversationTab === 'workspace' ? 0 : -1}
              onClick={() => setConversationTab('workspace')}><FolderOpen size={15} />Workspace</button>
            <button type="button" role="tab" id="conversation-chat-tab" aria-controls="conversation-chat-panel"
              aria-selected={conversationTab === 'chat'} tabIndex={conversationTab === 'chat' ? 0 : -1}
              onClick={() => setConversationTab('chat')}><MessageSquare size={15} />Chat</button>
          </div>
          <div id="conversation-workspace-panel" role="tabpanel" aria-labelledby="conversation-workspace-tab"
            className="conversation-workspace-panel" hidden={conversationTab !== 'workspace'}>
            {conversationTab === 'workspace' && (activeId ? <>
              <Modal open={workspaceDetailsOpen} title="Workspace details" icon={<Folder size={16} />}
                onClose={() => setWorkspaceDetailsOpen(false)}
                footer={<>
                  <button onClick={() => {
                    session.reload()
                    workspaces.reload()
                  }} disabled={session.loading}><RefreshCw size={14} />Refresh details</button>
                  <button onClick={() => setWorkspaceDetailsOpen(false)}>Close</button>
                </>}>
              <div className="conversation-workspace-info">
                <div className="conversation-workspace-heading">
                  <h3><Folder size={16} />{activeWorkspace?.name ?? 'No workspace'}</h3>
                  <span className="hint">{activeWorkspace
                    ? 'Shared workspace rules and sandbox files.'
                    : 'This conversation is not assigned to a workspace.'}</span>
                </div>
                {activeWorkspace && <details className="chat-workspace-rules" open>
                  <summary><ScrollText size={13} />Workspace instructions</summary>
                  <p>{activeWorkspace.instructions || 'No workspace instructions.'}</p>
                </details>}
                <SandboxDetails session={session.data?.conversationId === activeId ? session.data.value : null}
                  loading={session.loading} error={session.error} copied={copiedSession}
                  onCopy={async value => setCopiedSession(await copyText(value))} />
              </div>
              </Modal>
              <section className="conversation-workspace-files" aria-label="Files in the sandbox">
                <div className="row" style={{ justifyContent: 'space-between' }}>
                  <h3><FolderOpen size={16} />Files in the sandbox</h3>
                  <div className="row" style={{ gap: 6 }}>
                    <button onClick={() => setWorkspaceDetailsOpen(true)} aria-haspopup="dialog"><Folder size={14} />Workspace details</button>
                    <button onClick={sandboxFiles.reload} disabled={sandboxFiles.loading}><RefreshCw size={14} />Refresh files</button>
                  </div>
                </div>
                <SandboxFiles
                  key={activeId ?? 'none'}
                  conversationId={activeId ?? ''}
                  writable={!readOnly && !!activeId}
                  onReload={sandboxFiles.reload}
                  path={filesPath}
                  listing={sandboxFiles.data?.conversationId === activeId ? sandboxFiles.data.value : null}
                  loading={sandboxFiles.loading || sandboxFiles.data?.conversationId !== activeId}
                  error={sandboxFiles.error}
                  sort={fileSort}
                  onSort={setFileSort}
                  onOpen={setFilesPath}
                  onPreview={setPreviewFile}
                  onDownload={async (entryPath) => {
                    if (!activeId) {
                      return
                    }
                    setError(null)
                    try {
                      await saveBlob(await downloadConversationFile(activeId, entryPath, true), entryPath)
                    } catch (cause) {
                      setError(cause instanceof Error ? cause.message : String(cause))
                    }
                  }}
                />
              </section>
            </> : <Empty title="Select a conversation" detail="Select or create a conversation to see its workspace and sandbox files." />)}
          </div>
          <div id="conversation-chat-panel" role="tabpanel" aria-labelledby="conversation-chat-tab"
            className="conversation-chat-panel" hidden={conversationTab !== 'chat'}>
          <div className="chat-thread" ref={threadRef}>
            {messages.length === 0 && !thread.loading ? (
              <Empty
                title="Ask a question"
                icon={<Sparkles size={26} strokeWidth={1.5} />}
                detail="For example: which documents describe the logging requirements?"
              />
            ) : (
              messages.map((message) => (
                <MessageBubble
                  key={message.id}
                  message={message}
                  highlighted={message.id === highlighted}
                  onFeedback={react}
                  onBranch={branch}
                />
              ))
            )}
            {sending ? <StreamingAnswer text={streamingText} status={agentStatus} /> : null}
          </div>

          <div className="chat-composer">
            <input
              ref={fileInputRef}
              className="visually-hidden"
              type="file"
              accept={attachmentOptions.data?.allowedFileExtensions.join(',')}
              multiple
              onChange={(event) => void addFiles(event.target.files)}
            />
            <button
              className="chat-composer-action"
              disabled={readOnly || sending || uploading || attachments.length >= 10 || !attachmentOptions.data?.allowedFileExtensions.length}
              onClick={() => fileInputRef.current?.click()}
              title={attachmentOptions.error ?? `Attach files (${attachmentOptions.data?.allowedFileExtensions.join(', ') ?? 'loading allowed types…'})`}
            >
              <Paperclip size={15} />
              {uploading ? 'Uploading…' : 'Attach'}
            </button>
            <div className="chat-compose-main">
              {attachments.length > 0 ? (
                <div className="chat-attachment-list">
                  {attachments.map((file) => (
                    <span className="chat-attachment-chip" key={file.id}>
                      <FileTypeIcon name={file.fileName} mimeType={file.contentType} size={14} />
                      <span title={file.fileName}>{file.fileName}</span>
                      <button
                        className="ghost icon-only"
                        aria-label={`Remove ${file.fileName}`}
                        onClick={() => setAttachments((current) => current.filter((item) => item.id !== file.id))}
                      ><X size={12} /></button>
                    </span>
                  ))}
                </div>
              ) : null}
              <textarea
                ref={composerRef}
                rows={2}
                placeholder="Ask about the indexed documents…   (Enter to send, Shift+Enter for a new line)"
                value={draft}
                disabled={readOnly || sending}
                onChange={(event) => setDraft(event.target.value)}
                onPaste={(event) => {
                  const images = Array.from(event.clipboardData.files).filter(file => file.type.startsWith('image/'))
                  if (images.length === 0) {
                    return
                  }
                  event.preventDefault()
                  const usedNames = new Set(attachments.map(file => file.fileName.toLowerCase()))
                  const files = images.map(file => {
                    const hasExtension = /\.[a-z0-9]+$/i.test(file.name)
                    const extension = hasExtension
                      ? file.name.slice(file.name.lastIndexOf('.') + 1)
                      : file.type === 'image/jpeg' ? 'jpg' : file.type.slice('image/'.length)
                    const baseName = hasExtension ? file.name.slice(0, file.name.lastIndexOf('.')) : 'pasted-image'
                    let name = `${baseName}.${extension}`
                    let suffix = 2
                    while (usedNames.has(name.toLowerCase())) {
                      name = `${baseName} (${suffix}).${extension}`
                      suffix += 1
                    }
                    usedNames.add(name.toLowerCase())
                    return new File([file], name, { type: file.type, lastModified: file.lastModified })
                  })
                  void addFiles(files)
                }}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' && !event.shiftKey) {
                    event.preventDefault()
                    void send()
                  }
                }}
              />
            </div>
            <button
              className="primary chat-composer-action"
              disabled={readOnly || sending || uploading || draft.trim() === ''}
              onClick={send}
            >
              <SendHorizontal size={15} />
              {sending ? 'Thinking…' : 'Send'}
            </button>
          </div>
          </div>
        </section>
      </div>

      {previewFile && activeId ? (
        <SandboxPreview
          conversationId={activeId}
          path={previewFile}
          onClose={() => setPreviewFile(null)}
        />
      ) : null}

      <Modal
        open={workspaceDraft !== null}
        title={workspaceDraft?.id === null ? 'New workspace' : 'Edit workspace'}
        icon={<Folder size={18} aria-hidden="true" />}
        onClose={() => setWorkspaceDraft(null)}
        footer={
          <>
            <button onClick={() => setWorkspaceDraft(null)}>
              <X size={14} aria-hidden="true" />
              Cancel
            </button>
            <button
              className="primary"
              disabled={(workspaceDraft?.name.trim() ?? '') === ''}
              onClick={() => void saveWorkspace()}
            >
              <Check size={14} aria-hidden="true" />
              {workspaceDraft?.id === null ? 'Create' : 'Save'}
            </button>
          </>
        }
      >
        {workspaceDraft ? (
          <div className="workspace-editor">
            <Field label="Name" help="What the sidebar lists it as. Maximum 200 characters.">
              <input
                autoFocus
                type="text"
                maxLength={WORKSPACE_NAME_LIMIT}
                value={workspaceDraft.name}
                placeholder="For example: Quarterly report"
                onChange={(event) => setWorkspaceDraft({ ...workspaceDraft, name: event.target.value })}
                onKeyDown={(event) => {
                  // Enter submits from the name box; the rules box below needs it for new lines.
                  if (event.key === 'Enter' && workspaceDraft.name.trim() !== '') {
                    event.preventDefault()
                    void saveWorkspace()
                  }
                }}
              />
            </Field>
            <Field
              label="Rules"
              help="Added to the agent's instructions for every conversation here, from its next question. Leave empty for none."
            >
              <textarea
                className="workspace-rules-editor"
                maxLength={WORKSPACE_RULES_LIMIT}
                value={workspaceDraft.instructions}
                placeholder={"For example:\nAlways cite the file name you took an answer from.\nNever upload a change back to SharePoint without being asked."}
                onChange={(event) => setWorkspaceDraft({ ...workspaceDraft, instructions: event.target.value })}
              />
            </Field>
            <span className="hint">
              {workspaceDraft.instructions.length.toLocaleString()} of{' '}
              {WORKSPACE_RULES_LIMIT.toLocaleString()} characters. Conversations already in this
              workspace pick up a change on their next question; what has been answered is not revisited.
            </span>
          </div>
        ) : null}
      </Modal>
    </div>
  )
}

type FileSortKey = 'name' | 'size' | 'modified'

const PREVIEWABLE_TEXT = /\.(md|markdown|txt|csv|json|log|ya?ml|xml|html?|ts|tsx|js|jsx|css|py|cs|sql|sh|ps1)$/i
const PREVIEWABLE_IMAGE = /\.(png|jpe?g|gif|webp|bmp|avif)$/i

/** Whether one of the viewers the app already has can show this file. */
function isSandboxPreviewable(name: string): boolean {
  return isPreviewableOfficeFile(name) || PREVIEWABLE_TEXT.test(name) || PREVIEWABLE_IMAGE.test(name)
}

/** Hands a fetched blob to the browser as a file to save. */
async function saveBlob(blob: Blob, path: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = path.slice(path.lastIndexOf('/') + 1)
  document.body.appendChild(link)
  link.click()
  link.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/**
 * Shows a sandbox file in whichever of the app's existing viewers suits it. They all take the same
 * shape — a name, a cache key, and a loader — so the only decision here is which one.
 */
function SandboxPreview({
  conversationId,
  path,
  onClose,
}: {
  conversationId: string
  path: string
  onClose: () => void
}) {
  const name = fileName(path)
  const sourceKey = `${conversationId}:${path}`
  const load = (signal: AbortSignal) => downloadConversationFile(conversationId, path, false, signal)

  if (isPreviewableOfficeFile(name)) {
    return <OfficeViewer name={name} sourceKey={sourceKey} load={load} onClose={onClose} />
  }

  if (PREVIEWABLE_IMAGE.test(name)) {
    return <ImageViewer name={name} sourceKey={sourceKey} load={load} onClose={onClose} />
  }

  // Everything else previewable here is text. The Markdown viewer renders it and offers the raw text,
  // which is the right treatment for a .md file and a serviceable one for .csv or .json.
  return (
    <MarkdownViewer
      name={name}
      sourceKey={sourceKey}
      load={async (signal) => ({ markdown: await (await load(signal)).text() })}
      onClose={onClose}
    />
  )
}

/** The last segment of a path, which is the name a reader recognises. */
function fileName(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1)
}

/**
 * The path split into the crumbs that lead to it, each with the path it navigates to.
 */
function breadcrumbs(path: string): { name: string; path: string }[] {
  if (path === '.') {
    return []
  }

  const parts = path.split('/')
  return parts.map((name, index) => ({ name, path: parts.slice(0, index + 1).join('/') }))
}

/**
 * The sandbox as a file explorer: a breadcrumb trail to where you are, a way back up, and a sortable
 * table of what is here. Directories sort above files whichever column is chosen, because a listing
 * that interleaves them is harder to scan than one that does not.
 */
export function SandboxFiles({
  conversationId,
  writable,
  onReload,
  path,
  listing,
  loading,
  error,
  sort,
  onSort,
  onOpen,
  onPreview,
  onDownload,
}: {
  conversationId: string
  writable: boolean
  onReload: () => void
  path: string
  listing: { path: string; entries: FileSystemEntry[]; truncated: boolean; sandboxStarted: boolean } | null
  loading: boolean
  error: string | null
  sort: { key: FileSortKey; desc: boolean }
  onSort: (sort: { key: FileSortKey; desc: boolean }) => void
  onOpen: (path: string) => void
  onPreview: (path: string) => void
  onDownload: (path: string) => void
}) {
  const currentPath = !loading && !error && listing ? listing.path : path
  const [address, setAddress] = useState(currentPath)
  const [editingAddress, setEditingAddress] = useState(false)
  const management = useSandboxFileManagement(conversationId, currentPath,
    writable && !loading && !error && !!listing?.sandboxStarted, onReload)

  useEffect(() => {
    setAddress(currentPath)
  }, [currentPath])

  const parent = currentPath === '.' ? null : currentPath.includes('/') ? currentPath.slice(0, currentPath.lastIndexOf('/')) || '.' : '.'

  if (listing && !listing.sandboxStarted) {
    return (
      <Empty
        title="No sandbox yet"
        icon={<HardDrive size={24} strokeWidth={1.5} />}
        detail="This conversation gets one on its first question. Nothing has been downloaded or written."
      />
    )
  }

  const entries = [...(listing?.entries ?? [])].sort((left, right) => {
    if (left.isDirectory !== right.isDirectory) {
      return left.isDirectory ? -1 : 1
    }

    const order =
      sort.key === 'size'
        ? (left.sizeBytes ?? -1) - (right.sizeBytes ?? -1)
        : sort.key === 'modified'
          ? Date.parse(left.modifiedUtc) - Date.parse(right.modifiedUtc)
          : fileName(left.path).localeCompare(fileName(right.path), undefined, { sensitivity: 'base' })
    return sort.desc ? -order : order
  })

  const header = (key: FileSortKey, label: string) => (
    <button
      type="button"
      className={sort.key === key ? 'sandbox-column active' : 'sandbox-column'}
      aria-sort={sort.key === key ? (sort.desc ? 'descending' : 'ascending') : 'none'}
      onClick={() => onSort({ key, desc: sort.key === key ? !sort.desc : false })}
    >
      {label}
      {sort.key === key ? <ChevronDown size={12} className={sort.desc ? '' : 'flipped'} aria-hidden="true" /> : null}
    </button>
  )

  return (
    <div className={`sandbox-explorer${management.dragging ? ' sandbox-dragging' : ''}`} {...management.dropHandlers}>
      {management.dragging && <div className="sandbox-drop-overlay" role="status"><Upload size={24} /><strong>Drop files to upload</strong><span>Into {currentPath === '.' ? 'the working directory' : currentPath}</span></div>}
      {management.toolbar}
      <fieldset className="sandbox-management-content" disabled={management.busy}>
      <div className="sandbox-address-bar">
        <button
          type="button"
          className="ghost icon-only"
          disabled={parent === null}
          title="Up one level"
          aria-label="Up one level"
          onClick={() => parent !== null && onOpen(parent)}
        >
          <ArrowUp size={14} />
        </button>
        {!editingAddress ? <div className="sandbox-crumbs" onClick={(event) => {
          if (event.target === event.currentTarget) {
            setEditingAddress(true)
          }
        }}>
        <button type="button" className="sandbox-crumb" onClick={() => onOpen('.')}>
          <HardDrive size={13} aria-hidden="true" />
          Working directory
        </button>
        {breadcrumbs(currentPath).map((crumb, index, all) => (
          <span className="sandbox-crumb-step" key={crumb.path}>
            <ChevronRight size={12} aria-hidden="true" />
            <button
              type="button"
              className="sandbox-crumb"
              onClick={() => {
                if (index === all.length - 1) {
                  setEditingAddress(true)
                } else {
                  onOpen(crumb.path)
                }
              }}
            >
              {crumb.name}
            </button>
          </span>
        ))}
        <button type="button" className="ghost icon-only sandbox-edit-address" title="Edit folder path" aria-label="Edit folder path" onClick={() => setEditingAddress(true)}>
          <Pencil size={13} />
        </button>
        </div> : <form className="sandbox-address" onSubmit={(event) => {
        event.preventDefault()
        const destination = address.trim().replace(/^"(.*)"$/, '$1').replace(/\\/g, '/')
        onOpen(destination || '.')
        setEditingAddress(false)
      }} onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget)) {
          setAddress(currentPath)
          setEditingAddress(false)
        }
      }}>
        <input
          autoFocus
          type="text"
          aria-label="Folder path"
          placeholder="Paste a folder path"
          value={address}
          onChange={(event) => setAddress(event.target.value)}
          onFocus={(event) => event.currentTarget.select()}
          onKeyDown={(event) => {
            if (event.key === 'Escape') {
              event.preventDefault()
              event.stopPropagation()
              setAddress(currentPath)
              setEditingAddress(false)
            }
          }}
          spellCheck={false}
        />
        <button type="submit" disabled={loading}>
          <ChevronRight size={14} aria-hidden="true" />Go
        </button>
      </form>}
      </div>

      <LoadingBar active={loading} />
      {error ? <ErrorBanner message={error} /> : null}

      {!error && (entries.length > 0 || !loading) ? (
        <div className="sandbox-table" role="table">
          <div className="sandbox-row sandbox-head" role="row">
            {header('name', 'Name')}
            {header('size', 'Size')}
            {header('modified', 'Modified')}
            <span />
          </div>
          {entries.length === 0 ? (
            <Empty
              title="Nothing here"
              icon={<Folder size={24} strokeWidth={1.5} />}
              detail={path === '.'
                ? 'Downloads and anything the agent writes will show up here.'
                : 'This folder is empty.'}
            />
          ) : (
            entries.map((entry) => {
              const name = fileName(entry.path)
              const cells = (
                <>
                  <span className="sandbox-name" title={entry.path}>
                    {entry.isDirectory
                      ? <FolderOpen size={14} aria-hidden="true" />
                      : <FileTypeIcon name={name} mimeType={null} size={14} />}
                    {name}
                  </span>
                  <span className="sandbox-size">{entry.isDirectory ? '—' : formatBytes(entry.sizeBytes)}</span>
                  <span className="sandbox-modified" title={formatDateTime(entry.modifiedUtc)}>
                    {formatRelative(entry.modifiedUtc)}
                  </span>
                </>
              )
              if (entry.isDirectory) {
                return (
                  <div className="sandbox-row" role="row" key={entry.path}>
                    <button type="button" className="sandbox-open" onClick={() => onOpen(entry.path)}>{cells}</button>
                    <div className="sandbox-actions">{management.actions(entry)}</div>
                  </div>
                )
              }

              return (
                <div className="sandbox-row" role="row" key={entry.path}>
                  {isSandboxPreviewable(name) ? (
                    <button
                      type="button"
                      className="sandbox-open"
                      title={`Preview ${name}`}
                      onClick={() => onPreview(entry.path)}
                    >
                      {cells}
                    </button>
                  ) : (
                    cells
                  )}
                  <div className="sandbox-actions">
                    {management.actions(entry)}
                    {isSandboxPreviewable(name) ? (
                      <button
                        type="button"
                        className="ghost icon-only"
                        title={`Preview ${name}`}
                        aria-label={`Preview ${name}`}
                        onClick={() => onPreview(entry.path)}
                      >
                        <Eye size={14} />
                      </button>
                    ) : null}
                    <button
                      type="button"
                      className="ghost icon-only"
                      title={`Download ${name}`}
                      aria-label={`Download ${name}`}
                      onClick={() => onDownload(entry.path)}
                    >
                      <Download size={14} />
                    </button>
                  </div>
                </div>
              )
            })
          )}
        </div>
      ) : null}

      {listing?.truncated ? (
        <span className="hint">Only the first 500 entries are shown. Open a folder to narrow it.</span>
      ) : null}
      </fieldset>
      {management.dialog}
    </div>
  )
}

/**
 * What the next turn will actually run against. It answers the two questions a shared sandbox raises —
 * which one am I in, and who else is in it — and shows plainly when a recorded binding is stale, since
 * that is the case where the files someone expects to still be there will not be.
 */
function SandboxDetails({
  session,
  loading,
  error,
  copied,
  onCopy,
}: {
  session: ChatSandboxSession | null
  loading: boolean
  error: string | null
  copied: boolean
  onCopy: (value: string) => void
}) {
  return (
    <div className="chat-sandbox">
      <span className="chat-agent-menu-label">
        <HardDrive size={12} aria-hidden="true" /> Sandbox
      </span>
      {error ? <span className="hint">{error}</span> : null}
      {!error && (loading || !session) ? <span className="hint">Reading the binding…</span> : null}
      {session ? (
        <>
          <span className="hint">
            {session.mode === 'Local'
              ? 'Running in the API, which uses one local directory for every conversation.'
              : session.scope === 'Workspace'
                ? `Shared with ${session.sharedWithConversations} ${session.sharedWithConversations === 1 ? 'conversation' : 'conversations'} in this workspace.`
                : 'Private to this conversation.'}
          </span>
          {session.mode === 'Foundry' ? (
            session.sessionId ? (
              <div className="chat-sandbox-id">
                <code title={session.sessionId}>{session.sessionId}</code>
                <button
                  className="ghost icon-only"
                  aria-label="Copy session ID"
                  title="Copy session ID"
                  onClick={() => onCopy(session.sessionId!)}
                >
                  {copied ? <Check size={13} /> : <Copy size={13} />}
                </button>
              </div>
            ) : (
              <span className="hint">No session yet. The next turn starts one.</span>
            )
          ) : null}
          {session.sessionId && !session.reusedOnNextTurn ? (
            <span className="chat-sandbox-stale">
              <TriangleAlert size={13} aria-hidden="true" />
              Recorded against another endpoint, so the next turn starts a new sandbox and these files
              will not be there.
            </span>
          ) : null}
          {session.boundEndpoint ? (
            <span className="hint" title={session.boundEndpoint}>Bound: {session.boundEndpoint}</span>
          ) : null}
          {session.configuredEndpoint && session.configuredEndpoint !== session.boundEndpoint ? (
            <span className="hint" title={session.configuredEndpoint}>
              Configured: {session.configuredEndpoint}
            </span>
          ) : null}

        </>
      ) : null}
    </div>
  )
}

function ConversationRow({
  item,
  active,
  confirming,
  onOpen,
  onAskDelete,
  onCancelDelete,
  onDelete,
}: {
  item: ChatConversation
  active: boolean
  confirming: boolean
  onOpen: () => void
  onAskDelete: () => void
  onCancelDelete: () => void
  onDelete: () => void
}) {
  const readOnly = !canManageOwnContent(useAppUser())
  return (
    <div className={active ? 'chat-conversation active' : 'chat-conversation'}>
      <button className="chat-conversation-open" onClick={onOpen} title={item.title}>
        <span className="chat-conversation-title">{item.title}</span>
        <span className="chat-conversation-meta">
          <span className="chat-conversation-meta-line">
            {formatRelative(item.updatedAtUtc)} · {item.messageCount}{' '}
            {item.messageCount === 1 ? 'message' : 'messages'}
          </span>
          <span className="chat-conversation-token-usage">
            {formatTokenUsage(item.totalTokenCount, item.inputTokenCount, item.outputTokenCount)}
          </span>
          <span className="chat-conversation-token-usage">
            {(item.embeddingTokenCount ?? 0).toLocaleString()} embedding tokens
          </span>
        </span>
      </button>
      {confirming ? (
        <div className="row" style={{ gap: 4, flexWrap: 'nowrap' }}>
          <button className="ghost" onClick={onCancelDelete}>
            No
          </button>
          <button className="danger" onClick={onDelete}>
            Delete
          </button>
        </div>
      ) : (
        <button disabled={readOnly} className="ghost icon-only chat-conversation-delete" onClick={onAskDelete} title="Delete">
          <Trash2 size={14} />
        </button>
      )}
    </div>
  )
}

function MessageBubble({
  message,
  highlighted,
  onFeedback,
  onBranch,
}: {
  message: ChatMessage
  highlighted: boolean
  onFeedback: (id: string, feedback: ChatFeedback | null) => void
  onBranch: (id: string) => Promise<void>
}) {
  const [preview, setPreview] = useState<ChatMessageAttachment | null>(null)
  const isUser = message.role === 'User'
  const classes = [
    'chat-message',
    isUser ? 'user' : 'assistant',
    highlighted ? 'highlighted' : '',
  ].join(' ')

  return (
    <div className={classes.trim()} data-message-id={message.id}>
      <div className="chat-avatar">{isUser ? <User size={14} /> : <Bot size={14} />}</div>
      <div className="chat-body">
        {isUser ? (
          <p className="chat-text">{message.content}</p>
        ) : (
          <div className="chat-text markdown">
            <ReactMarkdown remarkPlugins={[remarkGfm]}>{message.content}</ReactMarkdown>
          </div>
        )}
        {message.attachments.length > 0 ? (
          <div className="message-attachments">
            {message.attachments.map((file) => (
              isPreviewableOfficeFile(file.fileName) ? (
                <button key={file.id} onClick={() => setPreview(file)} title={`Preview ${file.fileName}`}>
                  <FileTypeIcon name={file.fileName} mimeType={file.contentType} size={14} />
                  <span>{file.fileName}</span>
                  <Eye size={12} />
                </button>
              ) : (
                <AttachmentDownload id={file.id} name={file.fileName} key={file.id}>
                  <FileTypeIcon name={file.fileName} mimeType={file.contentType} size={14} />
                  <span>{file.fileName}</span>
                  <Download size={12} />
                </AttachmentDownload>
              )
            ))}
          </div>
        ) : null}
        <div className="chat-meta">
          {isUser ? (
            <div className="chat-actions">
              <MessageCopyButton content={message.content} label="Copy the question" />
              <MessageTraceAction traceId={message.traceId} />
            </div>
          ) : null}
          <span className="chat-time" title={formatDateTime(message.createdAtUtc)}>
            {formatMessageTime(message.createdAtUtc)}
          </span>
          {!isUser && message.totalTokenCount > 0 ? (
            <span className="chat-time">
              {formatTokenUsage(
                message.totalTokenCount,
                message.inputTokenCount,
                message.outputTokenCount,
              )}
              {message.modelId ? ` · ${message.modelId}` : ''}
            </span>
          ) : null}
          {!isUser && <span className="chat-time">{(message.embeddingTokenCount ?? 0).toLocaleString()} embedding tokens</span>}
          {!isUser ? (
            <MessageActions message={message} onFeedback={onFeedback} onBranch={onBranch} />
          ) : null}
        </div>
        {message.citations.length > 0 ? <Citations citations={message.citations} /> : null}
      </div>
      {preview ? (
        <OfficeViewer
          key={preview.id}
          name={preview.fileName}
          sourceKey={preview.id}
          load={(signal) => downloadAttachmentFile(preview.id, signal)}
          onClose={() => setPreview(null)}
        />
      ) : null}
    </div>
  )
}

function MessageTraceAction({ traceId }: { traceId?: string | null }) {
  const [open, setOpen] = useState(false)
  const available = Boolean(traceId?.trim())

  return (
    <>
      <button
        type="button"
        className="ghost icon-only"
        title={available ? 'View trace ID' : 'No trace ID recorded'}
        aria-label={available ? 'View trace ID' : 'No trace ID recorded'}
        aria-haspopup="dialog"
        disabled={!available}
        onClick={() => setOpen(true)}
      >
        <ScrollText size={13} />
      </button>
      {open && traceId ? createPortal(
        <Modal open={open} title="Message trace ID" icon={<ScrollText size={16} />}
          onClose={() => setOpen(false)}
          footer={<CopyButton value={traceId} label="Copy trace ID" />}>
          <p>Use this ID to find the chat turn in distributed tracing.</p>
          <code style={{ overflowWrap: 'anywhere', userSelect: 'text' }}>{traceId}</code>
        </Modal>,
        document.body,
      ) : null}
    </>
  )
}

function MessageCopyButton({ content, label }: { content: string; label: string }) {
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    if (!copied) {
      return
    }
    const timer = setTimeout(() => setCopied(false), 1400)
    return () => clearTimeout(timer)
  }, [copied])

  return (
    <button
      type="button"
      className="ghost icon-only"
      title={copied ? 'Copied' : label}
      aria-label={copied ? 'Copied' : label}
      onClick={() => {
        void copyText(content).then((ok) => setCopied(ok))
      }}
    >
      {copied ? <Check size={13} color="var(--good)" /> : <Copy size={13} />}
    </button>
  )
}

function MessageActions({
  message,
  onFeedback,
  onBranch,
}: {
  message: ChatMessage
  onFeedback: (id: string, feedback: ChatFeedback | null) => void
  onBranch: (id: string) => Promise<void>
}) {
  const readOnly = !canManageOwnContent(useAppUser())
  const [branching, setBranching] = useState(false)

  return (
    <div className="chat-actions">
      <MessageCopyButton content={message.content} label="Copy the answer" />
      <MessageTraceAction traceId={message.traceId} />
      <button
        className={message.feedback === 'Like' ? 'ghost icon-only liked' : 'ghost icon-only'}
        title="Good answer"
        aria-label="Good answer"
        disabled={readOnly}
        aria-pressed={message.feedback === 'Like'}
        onClick={() => onFeedback(message.id, message.feedback === 'Like' ? null : 'Like')}
      >
        <ThumbsUp size={13} />
      </button>
      <button
        className={message.feedback === 'Dislike' ? 'ghost icon-only disliked' : 'ghost icon-only'}
        title="Bad answer"
        aria-label="Bad answer"
        disabled={readOnly}
        aria-pressed={message.feedback === 'Dislike'}
        onClick={() => onFeedback(message.id, message.feedback === 'Dislike' ? null : 'Dislike')}
      >
        <ThumbsDown size={13} />
      </button>
      <button
        className="ghost icon-only"
        title="Branch in new chat"
        aria-label="Branch in new chat"
        disabled={readOnly || branching}
        onClick={() => {
          setBranching(true)
          void onBranch(message.id).finally(() => setBranching(false))
        }}
      >
        <GitBranch size={13} />
      </button>
    </div>
  )
}

function Citations({ citations }: { citations: ChatMessage['citations'] }) {
  return (
    <details className="chat-citations">
      <summary>
        {citations.length} {citations.length === 1 ? 'source' : 'sources'}
      </summary>
      <ul>
        {citations.map((citation, index) => (
          <li key={`${citation.name}:${citation.chunkNumber}:${index}`}>
            <FileTypeIcon name={citation.name} size={14} />
            <span className="chat-citation-name">
              {citation.webUrl ? (
                <a href={citation.webUrl} target="_blank" rel="noreferrer">
                  {citation.name}
                  <ExternalLink size={11} style={{ marginLeft: 4, verticalAlign: -1 }} />
                </a>
              ) : (
                citation.name
              )}
            </span>
            <span className="chat-citation-meta" title={citation.path ?? undefined}>
              {folderLabel(citation.path)} · chunk {citation.chunkNumber} · {formatScore(citation.score)}
            </span>
          </li>
        ))}
      </ul>
    </details>
  )
}

function StreamingAnswer({ text, status }: { text: string; status: string }) {
  return (
    <div className="chat-message assistant">
      <div className="chat-avatar">
        <Bot size={14} />
      </div>
      <div className="chat-body">
        {text ? (
          <div className="chat-text markdown">
            <ReactMarkdown remarkPlugins={[remarkGfm]}>{text}</ReactMarkdown>
          </div>
        ) : null}
        <div className="chat-thinking" role="status" aria-live="polite">
          {status}
          <span className="dots" aria-hidden="true">
            <i />
            <i />
            <i />
          </span>
        </div>
      </div>
    </div>
  )
}
import { useAppUser, canManageOwnContent } from '../components/AppUserContext'
