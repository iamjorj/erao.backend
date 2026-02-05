import * as signalR from '@microsoft/signalr';

const API_BASE_URL = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:5000';

interface ApiResponse<T> {
  success: boolean;
  message: string;
  data: T;
}

interface AuthTokens {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
}

export interface User {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  subscriptionTier: string;
  queryLimitPerMonth: number;
  queriesUsedThisMonth: number;
  createdAt: string;
}

interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
  user: User;
}

export interface RegisterResponse {
  email: string;
  message: string;
  requiresEmailVerification: boolean;
}

interface RegisterPayload {
  email: string;
  password: string;
  firstName: string;
  lastName: string;
}

interface LoginPayload {
  email: string;
  password: string;
}

// Database connection types
export interface DatabaseConnection {
  id: string;
  name: string;
  databaseType: 'PostgreSQL' | 'MySQL' | 'SQLServer' | 'MongoDB';
  isActive: boolean;
  lastTestedAt: string | null;
  createdAt: string;
}

export interface CreateDatabaseConnectionPayload {
  name: string;
  databaseType: number; // 0=PostgreSQL, 1=MySQL, 2=SQLServer, 3=MongoDB
  host: string;
  port: number;
  databaseName: string;
  username: string;
  password: string;
}

export interface UpdateDatabaseConnectionPayload {
  name?: string;
  host?: string;
  port?: number;
  databaseName?: string;
  username?: string;
  password?: string;
}

export interface SchemaResponse {
  databaseName: string;
  databaseType: string;
  tables: TableSchema[];
  rawSchema: string;
  cachedAt: string | null;
}

export interface TableSchema {
  name: string;
  schema: string | null;
  columns: ColumnSchema[];
  primaryKeys: PrimaryKeyInfo[];
  foreignKeys: ForeignKeyInfo[];
  indexes: IndexInfo[];
  rowCount: number | null;
}

export interface ColumnSchema {
  name: string;
  dataType: string;
  isNullable: boolean;
  defaultValue: string | null;
  isPrimaryKey: boolean;
  isForeignKey: boolean;
  maxLength: number | null;
  precision: number | null;
  scale: number | null;
  isIdentity: boolean;
}

export interface PrimaryKeyInfo {
  name: string;
  columns: string[];
}

export interface ForeignKeyInfo {
  name: string;
  column: string;
  referencedTable: string;
  referencedColumn: string;
  onDelete: string | null;
  onUpdate: string | null;
}

export interface IndexInfo {
  name: string;
  columns: string[];
  isUnique: boolean;
  isClustered: boolean;
}

// Conversation types
export interface Conversation {
  id: string;
  title: string;
  databaseConnectionId: string | null;
  databaseConnectionName: string | null;
  fileDocumentId: string | null;
  fileDocumentName: string | null;
  createdAt: string;
  updatedAt: string;
  lastMessageAt: string | null;
}

export interface ConversationDetail extends Conversation {
  messages: Message[];
}

export interface CreateConversationPayload {
  title?: string;
  databaseConnectionId?: string;
  fileDocumentId?: string;
}

export interface UpdateConversationPayload {
  title?: string;
}

// Message types
export interface Message {
  id: string;
  role: 'User' | 'Assistant' | 'System';
  content: string;
  sqlQuery: string | null;
  queryResult: QueryResult | string | null; // Can be JSON string or parsed object
  tokensUsed: number;
  createdAt: string;
}

export interface QueryResult {
  columns: string[];
  rows: Record<string, unknown>[];
  rowCount: number;
  executionTimeMs: number;
}

export interface ChatRequest {
  conversationId: string;
  message: string;
  executeQuery?: boolean; // Default: true
}

export interface ChatResponse {
  userMessage: Message;
  assistantMessage: Message;
  queryResult: string | null;
  tokensUsed: number;
}

// Usage types
export interface UsageStats {
  queriesUsedThisMonth: number;
  queryLimitPerMonth: number;
  percentageUsed: number;
  daysUntilReset: number;
  billingCycleStart: string;
  billingCycleEnd: string;
}

// Subscription types
export interface SubscriptionPlan {
  tier: number;
  name: string;
  price: number;
  description: string;
  queriesPerMonth: number;
  databaseConnections: number;
  supportLevel: string;
  features: string[];
  isCurrent: boolean;
  isPopular: boolean;
}

export interface SubscriptionResponse {
  currentTier: number;
  tierName: string;
  queriesPerMonth: number;
  queriesUsed: number;
  billingCycleReset: string;
}

export interface UsageLogEntry {
  id: string;
  databaseConnectionName: string;
  queryType: string;
  executionTimeMs: number;
  createdAt: string;
}

// File types
export type FileType = 'Excel' | 'Word' | 'Csv' | 'Xml' | 'Json' | 'Text';
export type FileProcessingStatus = 'Pending' | 'Processing' | 'Completed' | 'Failed';

export interface FileDocument {
  id: string;
  fileName: string;
  originalFileName: string;
  fileType: FileType;
  fileSizeBytes: number;
  rowCount: number | null;
  status: FileProcessingStatus;
  errorMessage: string | null;
  columns: string[] | null;
  createdAt: string;
  updatedAt: string;
}

export interface FileUploadResponse {
  success: boolean;
  message: string;
  file: FileDocument | null;
}

export interface FileListResponse {
  files: FileDocument[];
  totalCount: number;
}

export interface FileSchemaResponse {
  fileId: string;
  fileName: string;
  fileType: FileType;
  columns: ColumnInfo[];
  totalRows: number;
  sampleData: string | null;
}

export interface ColumnInfo {
  name: string;
  dataType: string;
  isNullable: boolean;
  maxLength: number | null;
}

export interface FileContentResponse {
  fileId: string;
  fileName: string;
  fileType: FileType;
  columns: string[];
  data: Record<string, unknown>[];
  totalRows: number;
  pageSize: number;
  currentPage: number;
}

class ApiClient {
  private baseUrl: string;

  constructor(baseUrl: string) {
    this.baseUrl = baseUrl;
  }

  private async request<T>(
    endpoint: string,
    options: RequestInit = {},
    skipAuthRefresh = false
  ): Promise<ApiResponse<T>> {
    const url = `${this.baseUrl}${endpoint}`;

    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      ...(options.headers as Record<string, string>),
    };

    // Add auth token if available (skip for auth endpoints)
    if (typeof window !== 'undefined' && !skipAuthRefresh) {
      const token = localStorage.getItem('accessToken');
      if (token) {
        headers['Authorization'] = `Bearer ${token}`;
      }
    }

    const response = await fetch(url, {
      ...options,
      headers,
    });

    // Handle 401 - try to refresh token (skip for auth endpoints)
    if (response.status === 401 && !skipAuthRefresh) {
      const refreshed = await this.tryRefreshToken();
      if (refreshed) {
        // Retry the request with new token
        const newToken = localStorage.getItem('accessToken');
        headers['Authorization'] = `Bearer ${newToken}`;
        const retryResponse = await fetch(url, { ...options, headers });
        const retryData = await retryResponse.json();
        if (!retryResponse.ok) {
          throw new ApiError(retryData.message || 'Something went wrong', retryResponse.status, retryData);
        }
        return retryData;
      } else {
        // Refresh failed, clear tokens
        auth.clearTokens();
        if (typeof window !== 'undefined') {
          window.location.href = '/login';
        }
        throw new ApiError('Session expired', 401, null);
      }
    }

    // Handle empty responses (204 No Content) for DELETE requests
    if (response.status === 204 || response.headers.get('content-length') === '0') {
      return { success: true, message: 'Success', data: null as T };
    }

    // Check if response has content before parsing
    const text = await response.text();
    if (!text) {
      return { success: true, message: 'Success', data: null as T };
    }

    const data = JSON.parse(text);

    if (!response.ok) {
      throw new ApiError(data.message || 'Something went wrong', response.status, data);
    }

    return data;
  }

  private async tryRefreshToken(): Promise<boolean> {
    if (typeof window === 'undefined') return false;

    const refreshToken = localStorage.getItem('refreshToken');
    if (!refreshToken) return false;

    try {
      const response = await fetch(`${this.baseUrl}/api/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      });

      if (response.ok) {
        const data = await response.json();
        if (data.success && data.data) {
          auth.saveTokens(data.data.accessToken, data.data.refreshToken);
          return true;
        }
      }
      return false;
    } catch {
      return false;
    }
  }

  // ========== Auth endpoints ==========
  async register(payload: RegisterPayload): Promise<ApiResponse<RegisterResponse>> {
    return this.request<RegisterResponse>('/api/auth/register', {
      method: 'POST',
      body: JSON.stringify(payload),
    }, true); // Skip auth refresh for public endpoint
  }

  async verifyEmail(payload: { email: string; otp: string }): Promise<ApiResponse<AuthResponse>> {
    return this.request<AuthResponse>('/api/auth/verify-email', {
      method: 'POST',
      body: JSON.stringify(payload),
    }, true); // Skip auth refresh for public endpoint
  }

  async resendEmailVerification(email: string): Promise<ApiResponse<null>> {
    return this.request<null>('/api/auth/resend-email-verification', {
      method: 'POST',
      body: JSON.stringify({ email }),
    }, true); // Skip auth refresh for public endpoint
  }

  async login(payload: LoginPayload): Promise<ApiResponse<AuthResponse>> {
    return this.request<AuthResponse>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify(payload),
    }, true); // Skip auth refresh for public endpoint
  }

  async logout(): Promise<ApiResponse<null>> {
    return this.request<null>('/api/auth/logout', {
      method: 'POST',
    });
  }

  async refreshToken(refreshToken: string): Promise<ApiResponse<AuthTokens>> {
    return this.request<AuthTokens>('/api/auth/refresh', {
      method: 'POST',
      body: JSON.stringify({ refreshToken }),
    }, true); // Skip auth refresh for public endpoint
  }

  async forgotPassword(email: string): Promise<ApiResponse<null>> {
    return this.request<null>('/api/auth/forgot-password', {
      method: 'POST',
      body: JSON.stringify({ email }),
    }, true); // Skip auth refresh for public endpoint
  }

  async verifyOtp(email: string, otp: string): Promise<ApiResponse<boolean>> {
    return this.request<boolean>('/api/auth/verify-otp', {
      method: 'POST',
      body: JSON.stringify({ email, otp }),
    }, true); // Skip auth refresh for public endpoint
  }

  async resetPassword(email: string, otp: string, newPassword: string): Promise<ApiResponse<null>> {
    return this.request<null>('/api/auth/reset-password', {
      method: 'POST',
      body: JSON.stringify({ email, otp, newPassword }),
    }, true); // Skip auth refresh for public endpoint
  }

  async resendOtp(email: string): Promise<ApiResponse<null>> {
    return this.request<null>('/api/auth/resend-otp', {
      method: 'POST',
      body: JSON.stringify({ email }),
    }, true); // Skip auth refresh for public endpoint
  }

  // ========== Database endpoints ==========
  async getDatabases(): Promise<ApiResponse<DatabaseConnection[]>> {
    return this.request<DatabaseConnection[]>('/api/databases');
  }

  async getDatabase(id: string): Promise<ApiResponse<DatabaseConnection>> {
    return this.request<DatabaseConnection>(`/api/databases/${id}`);
  }

  async createDatabase(payload: CreateDatabaseConnectionPayload): Promise<ApiResponse<DatabaseConnection>> {
    return this.request<DatabaseConnection>('/api/databases', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateDatabase(id: string, payload: UpdateDatabaseConnectionPayload): Promise<ApiResponse<DatabaseConnection>> {
    return this.request<DatabaseConnection>(`/api/databases/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteDatabase(id: string): Promise<ApiResponse<null>> {
    return this.request<null>(`/api/databases/${id}`, {
      method: 'DELETE',
    });
  }

  async testDatabase(id: string): Promise<ApiResponse<boolean>> {
    return this.request<boolean>(`/api/databases/${id}/test`, {
      method: 'POST',
    });
  }

  async getDatabaseSchema(id: string): Promise<ApiResponse<SchemaResponse>> {
    return this.request<SchemaResponse>(`/api/databases/${id}/schema`);
  }

  // ========== Conversation endpoints ==========
  async getConversations(): Promise<ApiResponse<Conversation[]>> {
    return this.request<Conversation[]>('/api/conversations');
  }

  async getConversation(id: string): Promise<ApiResponse<ConversationDetail>> {
    return this.request<ConversationDetail>(`/api/conversations/${id}`);
  }

  async createConversation(payload: CreateConversationPayload): Promise<ApiResponse<Conversation>> {
    return this.request<Conversation>('/api/conversations', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  async updateConversation(id: string, payload: UpdateConversationPayload): Promise<ApiResponse<Conversation>> {
    return this.request<Conversation>(`/api/conversations/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  async deleteConversation(id: string): Promise<ApiResponse<null>> {
    return this.request<null>(`/api/conversations/${id}`, {
      method: 'DELETE',
    });
  }

  // ========== Chat endpoints ==========
  async sendMessage(payload: ChatRequest): Promise<ApiResponse<ChatResponse>> {
    return this.request<ChatResponse>('/api/chat', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  // ========== Usage endpoints ==========
  async getUsage(): Promise<ApiResponse<UsageStats>> {
    return this.request<UsageStats>('/api/usage');
  }

  async getUsageHistory(skip = 0, take = 50): Promise<ApiResponse<UsageLogEntry[]>> {
    return this.request<UsageLogEntry[]>(`/api/usage/history?skip=${skip}&take=${take}`);
  }

  // ========== Account endpoints ==========
  async getAccount(): Promise<ApiResponse<User>> {
    return this.request<User>('/api/account');
  }

  async updateAccount(payload: { firstName?: string; lastName?: string }): Promise<ApiResponse<User>> {
    return this.request<User>('/api/account', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }

  // ========== Subscription endpoints ==========
  async getSubscriptionPlans(): Promise<ApiResponse<SubscriptionPlan[]>> {
    return this.request<SubscriptionPlan[]>('/api/subscriptions/plans');
  }

  async getCurrentSubscription(): Promise<ApiResponse<SubscriptionResponse>> {
    return this.request<SubscriptionResponse>('/api/subscriptions/current');
  }

  async upgradeSubscription(newTier: number): Promise<ApiResponse<SubscriptionResponse>> {
    return this.request<SubscriptionResponse>('/api/subscriptions/upgrade', {
      method: 'PUT',
      body: JSON.stringify({ newTier }),
    });
  }

  // ========== File endpoints ==========
  async uploadFile(file: File): Promise<FileUploadResponse> {
    const formData = new FormData();
    formData.append('file', file);

    const headers: Record<string, string> = {};
    if (typeof window !== 'undefined') {
      const token = localStorage.getItem('accessToken');
      if (token) {
        headers['Authorization'] = `Bearer ${token}`;
      }
    }

    const response = await fetch(`${this.baseUrl}/api/files/upload`, {
      method: 'POST',
      headers,
      body: formData,
    });

    const data = await response.json();

    if (!response.ok) {
      throw new ApiError(data.message || 'Failed to upload file', response.status, data);
    }

    return data;
  }

  async getFiles(): Promise<ApiResponse<FileListResponse>> {
    return this.request<FileListResponse>('/api/files');
  }

  async getFile(fileId: string): Promise<ApiResponse<FileDocument>> {
    return this.request<FileDocument>(`/api/files/${fileId}`);
  }

  async getFileSchema(fileId: string): Promise<ApiResponse<FileSchemaResponse>> {
    return this.request<FileSchemaResponse>(`/api/files/${fileId}/schema`);
  }

  async getFileContent(fileId: string, page = 1, pageSize = 100): Promise<ApiResponse<FileContentResponse>> {
    return this.request<FileContentResponse>(`/api/files/${fileId}/content?page=${page}&pageSize=${pageSize}`);
  }

  async deleteFile(fileId: string): Promise<ApiResponse<null>> {
    return this.request<null>(`/api/files/${fileId}`, {
      method: 'DELETE',
    });
  }
}

export class ApiError extends Error {
  status: number;
  data: unknown;

  constructor(message: string, status: number, data: unknown) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.data = data;
  }
}

export const api = new ApiClient(API_BASE_URL);

// Auth helpers
export const auth = {
  saveTokens(accessToken: string, refreshToken: string) {
    if (typeof window !== 'undefined') {
      localStorage.setItem('accessToken', accessToken);
      localStorage.setItem('refreshToken', refreshToken);
    }
  },

  clearTokens() {
    if (typeof window !== 'undefined') {
      localStorage.removeItem('accessToken');
      localStorage.removeItem('refreshToken');
      localStorage.removeItem('user');
    }
  },

  saveUser(user: User) {
    if (typeof window !== 'undefined') {
      localStorage.setItem('user', JSON.stringify(user));
    }
  },

  getUser(): User | null {
    if (typeof window !== 'undefined') {
      const user = localStorage.getItem('user');
      return user ? JSON.parse(user) : null;
    }
    return null;
  },

  isAuthenticated(): boolean {
    if (typeof window !== 'undefined') {
      return !!localStorage.getItem('accessToken');
    }
    return false;
  },
};

export type { AuthResponse, RegisterPayload, LoginPayload };

// ========== SignalR Chat Connection ==========

export interface StreamChunk {
  conversationId: string;
  chunk: string;
  index: number;
}

export interface StreamCompleted {
  conversationId: string;
  assistantMessage: Message;
  queryResult: string | null;
  tokensUsed: number;
}

export interface StreamError {
  conversationId: string;
  error: string;
}

export interface UserMessageSaved {
  messageId: string;
  content: string;
  createdAt: string;
}

class ChatHubConnection {
  private connection: signalR.HubConnection | null = null;
  private isConnecting = false;

  async getConnection(): Promise<signalR.HubConnection> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      return this.connection;
    }

    if (this.isConnecting) {
      // Wait for existing connection attempt
      await new Promise<void>((resolve) => {
        const checkInterval = setInterval(() => {
          if (!this.isConnecting) {
            clearInterval(checkInterval);
            resolve();
          }
        }, 100);
      });
      // Re-check connection state after waiting - use fresh variable to avoid TS narrowing issues
      const conn = this.connection;
      if (conn && conn.state === signalR.HubConnectionState.Connected) {
        return conn;
      }
    }

    this.isConnecting = true;

    try {
      const token = typeof window !== 'undefined' ? localStorage.getItem('accessToken') : null;

      this.connection = new signalR.HubConnectionBuilder()
        .withUrl(`${API_BASE_URL}/hubs/chat`, {
          accessTokenFactory: () => token || '',
        })
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Debug)
        .build();

      this.connection.onclose((error) => {
        console.log('[SignalR] Connection closed', error);
      });

      this.connection.onreconnecting((error) => {
        console.log('[SignalR] Reconnecting', error);
      });

      this.connection.onreconnected((connectionId) => {
        console.log('[SignalR] Reconnected', connectionId);
      });

      await this.connection.start();
      console.log('[SignalR] Connected, state:', this.connection.state);

      return this.connection;
    } finally {
      this.isConnecting = false;
    }
  }

  async joinConversation(conversationId: string): Promise<void> {
    const connection = await this.getConnection();
    await connection.invoke('JoinConversation', conversationId);
  }

  async leaveConversation(conversationId: string): Promise<void> {
    const connection = await this.getConnection();
    await connection.invoke('LeaveConversation', conversationId);
  }

  async sendMessageStreaming(
    conversationId: string,
    message: string,
    executeQuery = true,
    handlers: {
      onStreamStarted?: () => void;
      onUserMessageSaved?: (data: UserMessageSaved) => void;
      onChunk?: (chunk: string, index: number) => void;
      onQueryExecuting?: () => void;
      onCompleted?: (data: StreamCompleted) => void;
      onError?: (error: string) => void;
    }
  ): Promise<void> {
    const connection = await this.getConnection();

    // Set up handlers with logging
    const streamStartedHandler = (data: { conversationId: string }) => {
      console.log('[SignalR] StreamStarted received:', data);
      console.log('[SignalR] Expected conversationId:', conversationId);
      console.log('[SignalR] Match:', data.conversationId === conversationId);
      if (data.conversationId === conversationId) {
        handlers.onStreamStarted?.();
      }
    };
    const userMessageSavedHandler = (data: UserMessageSaved) => {
      console.log('[SignalR] UserMessageSaved', data);
      handlers.onUserMessageSaved?.(data);
    };
    const chunkHandler = (data: StreamChunk) => {
      if (data.conversationId === conversationId) {
        handlers.onChunk?.(data.chunk, data.index);
      }
    };
    const queryExecutingHandler = (data: { conversationId: string }) => {
      console.log('[SignalR] QueryExecuting', data);
      if (data.conversationId === conversationId) {
        handlers.onQueryExecuting?.();
      }
    };
    const completedHandler = (data: StreamCompleted) => {
      console.log('[SignalR] StreamCompleted received:', data);
      console.log('[SignalR] Expected conversationId:', conversationId);
      console.log('[SignalR] Match:', data.conversationId === conversationId);
      if (data.conversationId === conversationId) {
        console.log('[SignalR] Calling onCompleted handler');
        handlers.onCompleted?.(data);
        cleanup();
      }
    };
    const errorHandler = (data: StreamError) => {
      console.log('[SignalR] StreamError', data);
      if (data.conversationId === conversationId) {
        handlers.onError?.(data.error);
        cleanup();
      }
    };
    const generalErrorHandler = (error: string) => {
      console.log('[SignalR] Error', error);
      handlers.onError?.(error);
      cleanup();
    };

    // Track if we've received any events
    let receivedAnyEvent = false;
    let timeoutId: NodeJS.Timeout | null = null;
    let isCompleted = false;

    // Wrap handlers to track event receipt
    const wrappedStreamStartedHandler = (data: { conversationId: string }) => {
      receivedAnyEvent = true;
      streamStartedHandler(data);
    };
    const wrappedUserMessageSavedHandler = (data: UserMessageSaved) => {
      receivedAnyEvent = true;
      userMessageSavedHandler(data);
    };
    const wrappedChunkHandler = (data: StreamChunk) => {
      receivedAnyEvent = true;
      chunkHandler(data);
    };
    const wrappedQueryExecutingHandler = (data: { conversationId: string }) => {
      receivedAnyEvent = true;
      queryExecutingHandler(data);
    };
    const wrappedCompletedHandler = (data: StreamCompleted) => {
      isCompleted = true;
      completedHandler(data);
    };

    const cleanup = () => {
      if (timeoutId) {
        clearTimeout(timeoutId);
        timeoutId = null;
      }
      connection.off('StreamStarted', wrappedStreamStartedHandler);
      connection.off('UserMessageSaved', wrappedUserMessageSavedHandler);
      connection.off('StreamChunk', wrappedChunkHandler);
      connection.off('QueryExecuting', wrappedQueryExecutingHandler);
      connection.off('StreamCompleted', wrappedCompletedHandler);
      connection.off('StreamError', errorHandler);
      connection.off('Error', generalErrorHandler);
    };

    // Register handlers
    connection.on('StreamStarted', wrappedStreamStartedHandler);
    connection.on('UserMessageSaved', wrappedUserMessageSavedHandler);
    connection.on('StreamChunk', wrappedChunkHandler);
    connection.on('QueryExecuting', wrappedQueryExecutingHandler);
    connection.on('StreamCompleted', wrappedCompletedHandler);
    connection.on('StreamError', errorHandler);
    connection.on('Error', generalErrorHandler);

    // Set a timeout to detect if no events are received (connection issue)
    timeoutId = setTimeout(() => {
      if (!receivedAnyEvent && !isCompleted) {
        console.error('[SignalR] Timeout - no events received after 30s');
        handlers.onError?.('Connection timeout. Please try again.');
        cleanup();
      }
    }, 30000);

    // Send the message
    console.log('[SignalR] Invoking SendMessageStreaming for conversation:', conversationId);
    console.log('[SignalR] Connection state:', connection.state);
    try {
      await connection.invoke('SendMessageStreaming', {
        conversationId,
        message,
        executeQuery,
      });
      console.log('[SignalR] SendMessageStreaming invoke completed');
    } catch (error) {
      console.error('[SignalR] SendMessageStreaming invoke failed:', error);
      cleanup();
      throw error;
    }
  }

  async disconnect(): Promise<void> {
    if (this.connection) {
      await this.connection.stop();
      this.connection = null;
    }
  }
}

export const chatHub = new ChatHubConnection();
