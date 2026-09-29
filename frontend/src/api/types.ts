/** Mirrors the records the API returns. Property names match its camelCase JSON. */
export interface ImageDescriptionUsageFilter {
  from: string
  to: string
  model: string
  userId: string
  attachmentId: string
}
export interface ImageDescriptionUsageTotals {
  calls: number
  unknownUsage: number
  inputTokens: number
  outputTokens: number
  totalTokens: number
}
export interface ImageDescriptionUsageReport {
  summary: ImageDescriptionUsageTotals
  daily: (ImageDescriptionUsageTotals & { day: string })[]
  models: (ImageDescriptionUsageTotals & { modelId: string })[]
  items: {
    id: string
    createdAtUtc: string
    userId: string | null
    userName: string | null
    conversationId: string
    questionId: string
    attachmentId: string
    fileName: string | null
    modelId: string
    systemPrompt: string | null
    prompt: string | null
    description: string | null
    inputTokens: number | null
    outputTokens: number | null
    totalTokens: number | null
  }[]
}
export type AppRole = 'Global Admin' | 'Global Reader Admin' | 'User'
export interface SystemAttachmentStorage {
  fileCount: number
  usedBytes: number
  orphanBytes: number
  unassignedBytes: number
}
export interface AppUser {
  id: string
  email: string
  displayName: string
  roles: AppRole[]
  isActive: boolean
  hasSignedIn: boolean
  createdAtUtc: string
  lastLoginAtUtc: string | null
  concurrencyStamp: string
  attachmentStorageLimitBytes: number | null
  attachmentStorageUsedBytes: number
  monthlyTokenLimit: number | null
  monthlyTokensUsed: number
  tokenUsageResetsAtUtc: string | null
  dailyTokenUsage: { day: number; inputTokens: number; outputTokens: number; totalTokens: number }[] | null
  dailyModelTokenUsage: { day: number; modelId: string | null; inputTokens: number; outputTokens: number; totalTokens: number }[] | null
}
export interface AppUserInput {
  email: string
  displayName: string
  roles: AppRole[]
  isActive: boolean
  concurrencyStamp?: string
}

export interface AppUserStorageInput {
  attachmentStorageLimitBytes: number | null
  concurrencyStamp: string
}

export interface AgentDefinition {
  id: string
  name: string
  modelId: string
  instructions: string
  createdAtUtc: string
  updatedAtUtc: string
}

export interface PagedResult<T> {
  totalCount: number
  items: T[]
}

/** A row of the `SharePointIndexedFiles` table. */
export interface FileSensitivity {
  labelId: string | null
  labelName: string | null
  isLabeled: boolean
  isEncrypted: boolean
  checkedAtUtc: string
}

export interface IndexedFileRow {
  sensitivity: FileSensitivity | null
  driveId: string
  itemId: string
  name: string
  parentPath: string | null
  webUrl: string | null
  mimeType: string | null
  size: number | null
  lastModifiedUtc: string | null
  eTag: string | null
  cTag: string | null
  permissionsHash: string
  indexFingerprint: string
  chunkCount: number
  scanId: string
  indexedAtUtc: string
  embeddingTokenCount: number | null
}

/** A row of the `SharePointDeltaState` table. */
export interface DeltaStateRow {
  driveId: string
  deltaLink: string
  scanId: string
  sweptScanId: string | null
  updatedAtUtc: string
}

export interface MimeTypeCount {
  mimeType: string | null
  fileCount: number
  chunkCount: number
  sizeBytes: number | null
}

export interface IndexStateSummary {
  totalFiles: number
  totalChunks: number
  totalSizeBytes: number | null
  distinctDrives: number
  filesOutsideCurrentScan: number
  distinctIndexFingerprints: number
  oldestIndexedAtUtc: string | null
  newestIndexedAtUtc: string | null
  byMimeType: MimeTypeCount[]
}

export type SortKey =
  | 'name'
  | 'path'
  | 'mimeType'
  | 'size'
  | 'lastModifiedUtc'
  | 'chunkCount'
  | 'embeddingTokenCount'
  | 'indexedAtUtc'

export interface IndexedFileQuery {
  search?: string
  driveId?: string
  sort?: SortKey
  desc?: boolean
  skip?: number
  top?: number
}

/**
 * A named group of conversations that share one agent sandbox. Files the agent downloads or edits in
 * one conversation are still on disk in the next one opened from the same workspace.
 */
export interface ChatWorkspace {
  id: string
  name: string
  /** Rules added to the agent's instructions for every conversation here. Null means none. */
  instructions: string | null
  createdAtUtc: string
  updatedAtUtc: string
  conversationCount: number
}

/**
 * Which sandbox a conversation's next turn will reach. `scope` says which row holds the binding: a
 * workspace shares one across its conversations, a conversation outside one keeps its own. Endpoints
 * are null unless the reader may see administration detail, and in Local mode there is no session.
 */
export interface ChatSandboxSession {
  mode: 'Local' | 'Foundry'
  scope: 'Workspace' | 'Conversation'
  workspaceId: string | null
  workspaceName: string | null
  sharedWithConversations: number
  sessionId: string | null
  boundEndpoint: string | null
  configuredEndpoint: string | null
  /** False when the binding was made against a different endpoint, so the next turn starts anew. */
  reusedOnNextTurn: boolean
}

export interface ChatConversation {
  id: string
  title: string
  /** When set, every search the assistant runs in this conversation is filtered to that user. */
  userId: string | null
  /** The persisted agent whose instructions govern this conversation. Null means the default agent. */
  agentId: string | null
  /** The workspace whose sandbox this conversation shares, chosen when it was created and fixed
   * afterwards. Null means a sandbox of its own. */
  workspaceId: string | null
  createdAtUtc: string
  updatedAtUtc: string
  messageCount: number
  embeddingTokenCount: number
  inputTokenCount: number
  outputTokenCount: number
  totalTokenCount: number
}

/** A document excerpt the assistant retrieved to answer with. */
export interface ChatCitation {
  name: string
  path: string | null
  webUrl: string | null
  chunkNumber: number
  score: number | null
}

export type ChatFeedback = 'Like' | 'Dislike'

export interface ChatMessage {
  id: string
  conversationId: string
  role: 'User' | 'Assistant'
  content: string
  citations: ChatCitation[]
  embeddingTokenCount: number
  inputTokenCount: number
  outputTokenCount: number
  totalTokenCount: number
  modelId: string | null
  /** What the reader thought of the answer, null until they say. */
  feedback: ChatFeedback | null
  attachments: ChatMessageAttachment[]
  createdAtUtc: string
}

export interface ChatMessageAttachment {
  id: string
  fileName: string
  contentType: string | null
  sizeBytes: number
}

export type UploadIndexStatus = 'NotStarted' | 'Indexing' | 'Indexed' | 'Failed'

export interface AttachmentFileRecord {
  id: string
  fileName: string
  contentType: string | null
  sizeBytes: number
  status: UploadIndexStatus
  chunkCount: number
  embeddingTokenCount: number | null
  errorMessage: string | null
  createdAtUtc: string
  updatedAtUtc: string
  indexedAtUtc: string | null
  chatMessageAttachmentId: string | null
  messageId: string | null
  conversationId: string | null
  conversationTitle: string | null
  isOrphan: boolean
}

/** One rated answer, with the question that prompted it. */
export interface FeedbackEntry {
  messageId: string
  conversationId: string
  conversationTitle: string
  feedback: ChatFeedback
  question: string | null
  answer: string
  citations: ChatCitation[]
  inputTokenCount: number
  outputTokenCount: number
  totalTokenCount: number
  modelId: string | null
  createdAtUtc: string
}

export interface FeedbackPage {
  totalCount: number
  /** Counts for the search term, not the page or the selected rating. */
  liked: number
  disliked: number
  items: FeedbackEntry[]
}

export interface ChatThread {
  conversation: ChatConversation
  messages: ChatMessage[]
}

export interface ChatTurnResult {
  question: ChatMessage
  answer: ChatMessage
  /** The conversation title, which the first question replaces "New chat" with. */
  title: string
}

/** Newline-delimited events emitted while an agent turn is running. */
export type ChatStreamEvent =
  | { type: 'started'; question: ChatMessage; title: string }
  | { type: 'status'; message: string }
  | { type: 'delta'; text: string }
  | { type: 'completed'; answer: ChatMessage; title: string }
  | { type: 'error'; message: string }

export type SubscriptionStatus = 'Missing' | 'Active' | 'ExpiringSoon' | 'Expired'

/** A Microsoft Graph webhook subscription. The client state itself is never sent to the browser. */
export interface SubscriptionView {
  id: string | null
  /** Database ID for tracked rows; used to update a tracked row that is currently missing from Graph. */
  databaseId: string | null
  name: string
  resource: string
  notificationUrl: string
  expirationUtc: string | null
  clientStateMatches: boolean
  hasCustomClientState: boolean
  autoRenewEnabled: boolean
  resourceMatches: boolean
  notificationUrlMatches: boolean
  /** Has a database row associated with this Microsoft Graph subscription ID. */
  isTracked: boolean
  isManaged: boolean
  /** Reserved Default record; it cannot be deleted here. */
  isDefault: boolean
  status: SubscriptionStatus
}

export interface UpdateSubscriptionResult {
  subscription: SubscriptionView
  /** True when name, notification URL, or client state changed and Graph required a replacement. */
  replaced: boolean
  warning: string | null
}

export interface SubscriptionOverview {
  expectedResource: string
  expectedNotificationUrl: string
  renewalEnabled: boolean
  lifetimeDays: number
  renewalCheckHours: number
  renewalThresholdDays: number
  items: SubscriptionView[]
}

/** The three retrieval strategies the API exposes over one request body. */
export type SearchMode = 'fulltext' | 'vector' | 'hybrid'

export interface EmbeddingUsageRecord {
  id: string
  createdAtUtc: string
  operation: string
  embeddingModelId: string
  deploymentId: string
  inputTokens: number | null
  totalTokens: number | null
  userId: string | null
  conversationId: string | null
  questionId: string | null
  driveId: string | null
  fileId: string | null
  attachmentId: string | null
  scanId: string | null
  chunkNumber: number | null
  traceId: string | null
}

export interface ChatUsageFilter {
  from: string
  to: string
  model: string
  user: string
  questionId: string
  unknownModel: boolean
}

export interface ContentSafetyUsageReport {
  summary: { requests: number; allowed: number; blocked: number; failed: number; characters: number; estimatedTextRecords: number }
  daily: { day: string; requests: number; blocked: number; characters: number }[]
  items: { id: string; createdAtUtc: string; operation: string; status: string; characterCount: number; estimatedTextRecords: number; userId: string | null; [key: string]: string | number | null }[]
}
export interface ChatUsageTotals {
  /** Distinct questions. A turn is several model requests, so this is not the row count. */
  turns: number
  /** Model requests: one ledger row each. */
  requests: number
  inputTokens: number
  outputTokens: number
  totalTokens: number
}
export interface ChatUsageRecord {
  id: string
  questionId: string
  conversationId: string
  /** Position in the turn's tool-calling loop, or -1 for a whole-turn row. */
  sequence: number
  userId: string | null
  modelId: string | null
  day: number
  month: number
  createdAtUtc: string
  /** Tools this response asked for, comma-separated; null when it answered instead. */
  toolNames: string | null
  skillNames: string | null
  scriptNames: string | null
  /** Null when the provider reported no usage for the request. */
  inputTokens: number | null
  outputTokens: number | null
  totalTokens: number | null
}
export interface ChatUsageReport {
  from: string
  to: string
  summary: ChatUsageTotals & { users: number; unknownModelTurns: number }
  daily: (ChatUsageTotals & { day: number })[]
  models: (ChatUsageTotals & { modelId: string | null })[]
  users: (ChatUsageTotals & { userId: string; name: string })[]
  items: { usage: ChatUsageRecord; userName: string }[]
  modelOptions: (string | null)[]
}
export interface EmbeddingUsageGroup {
  name: string
  calls: number
  tokens: number
  unknownCalls: number
}
export interface EmbeddingUsageReport {
  from: string
  to: string
  summary: { calls: number; tokens: number; inputTokens: number; unknownCalls: number; models: number; users: number }
  daily: { day: string; calls: number; tokens: number; unknownCalls: number }[]
  models: EmbeddingUsageGroup[]
  operations: EmbeddingUsageGroup[]
  users: (EmbeddingUsageGroup & { userId: string | null })[]
  items: { usage: EmbeddingUsageRecord; userName: string | null }[]
  modelOptions: string[]
  operationOptions: string[]
}
export interface EmbeddingUsageFilter {
  from: string
  to: string
  model: string
  operation: string
  user: string
  reference: string
  unattributed: boolean
}

export const SEARCH_MODES: SearchMode[] = ['fulltext', 'vector', 'hybrid']

export const SEARCH_MODE_LABELS: Record<SearchMode, string> = {
  fulltext: 'Full-text',
  vector: 'Vector',
  hybrid: 'Hybrid',
}

export const SEARCH_MODE_DESCRIPTIONS: Record<SearchMode, string> = {
  fulltext: 'Keyword search over the searchable fields.',
  vector: 'Pure k-nearest-neighbour search over contentVector.',
  hybrid: 'Keyword and vector in one request, fused by reciprocal rank.',
}

export interface SearchPayload {
  query: string
  userId?: string | null
  top: number
  skip: number
}

export interface SearchQueryHit {
  id: string
  driveId: string
  itemId: string
  name: string
  path: string | null
  webUrl: string | null
  mimeType: string | null
  size: number | null
  lastModifiedUtc: string | null
  chunkNumber: number
  content: string
  score: number | null
}

export interface SearchQueryResults {
  totalCount: number | null
  items: SearchQueryHit[]
}

/** One strategy's outcome, with the wall-clock time the round trip took. */
export interface TimedSearch {
  mode: SearchMode
  results: SearchQueryResults | null
  error: string | null
  elapsedMs: number
}
