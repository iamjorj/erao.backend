"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState, useRef, useCallback } from "react";
import {
  auth,
  api,
  ApiError,
  User,
  Conversation,
  Message,
  DatabaseConnection,
  CreateDatabaseConnectionPayload,
  QueryResult,
  FileDocument,
  SchemaResponse,
  TableSchema,
} from "@/lib/api";
import { DataChart, ChartType, detectChartType } from "@/components/DataChart";
import { DataViewerModal } from "@/components/DataViewerModal";
import { MarkdownResponse } from "@/components/MarkdownResponse";
import { useVirtualizer } from "@tanstack/react-virtual";

// Helper to strip SQL/JSON code blocks and query result blocks from AI response text
function stripCodeBlocks(content: string): string {
  return content
    .replace(/```sql[\s\S]*?```/gi, "") // Remove SQL code blocks
    .replace(/```json[\s\S]*?```/gi, "") // Remove JSON code blocks
    .replace(/```[\s\S]*?```/g, "") // Remove any other code blocks
    .replace(/\[Query Result:[\s\S]*$/gi, "") // Remove [Query Result: to end of string
    .replace(/\[DATA_CONTEXT:[\s\S]*?\]/gi, "") // Remove [DATA_CONTEXT: ...] tags (including multiline)
    .replace(/\[DATA_CONTEXT:[^\]]*$/gi, "") // Remove unclosed [DATA_CONTEXT: to end
    .replace(/[,{]?"?(columns|rows|rowCount|executionTimeMs)"?[\s\S]*$/gi, "") // Remove partial JSON results
    .replace(/\{"columns":\[[\s\S]*$/gi, "") // Remove JSON starting with columns
    .replace(/\n\|[^\n]*\|(\n\|[^\n]*\|)*/g, "") // Remove markdown tables
    .replace(/\(Query returned[^)\n]*\)?/gi, "") // Remove "(Query returned...)" text
    .replace(/\n{3,}/g, "\n\n") // Clean up extra newlines
    .trim();
}

// Helper to convert array rows to object rows
function normalizeRows(columns: string[], rows: unknown[]): Record<string, unknown>[] {
  if (!rows || rows.length === 0) return [];

  // Check if rows are already objects
  if (rows[0] && typeof rows[0] === 'object' && !Array.isArray(rows[0])) {
    return rows as Record<string, unknown>[];
  }

  // Convert array rows to object rows
  return rows.map(row => {
    if (Array.isArray(row)) {
      const obj: Record<string, unknown> = {};
      columns.forEach((col, idx) => {
        obj[col] = row[idx];
      });
      return obj;
    }
    return row as Record<string, unknown>;
  });
}

// Helper to safely parse queryResult (can be string, object, or null)
// Returns array of QueryResult for multi-table support
function parseQueryResult(queryResult: QueryResult | string | null): QueryResult[] | null {
  if (!queryResult) return null;

  let parsed: unknown = queryResult;
  if (typeof queryResult === "string") {
    try {
      parsed = JSON.parse(queryResult);
    } catch {
      return null;
    }
  }

  // Check for multi-table format: { tables: [...] }
  if (parsed && typeof parsed === "object" && "tables" in parsed) {
    const tablesData = (parsed as { tables: unknown[] }).tables;
    if (Array.isArray(tablesData)) {
      const results: QueryResult[] = [];
      for (const table of tablesData) {
        if (table && typeof table === "object" && "rows" in table && "columns" in table) {
          const t = table as { columns: string[]; rows: unknown[] };
          results.push({
            ...t,
            rows: normalizeRows(t.columns, t.rows),
          } as QueryResult);
        }
      }
      return results.length > 0 ? results : null;
    }
  }

  // Single table format
  if (parsed && typeof parsed === "object" && "rows" in parsed && "columns" in parsed) {
    const p = parsed as { columns: string[]; rows: unknown[] };
    return [{
      ...p,
      rows: normalizeRows(p.columns, p.rows),
    } as QueryResult];
  }

  return null;
}

// Helper to detect requested chart type from user message
function detectRequestedChartType(message: string): ChartType | null {
  const lowerMessage = message.toLowerCase();
  if (lowerMessage.includes("bar chart") || lowerMessage.includes("bar graph")) {
    return "bar";
  }
  if (lowerMessage.includes("line chart") || lowerMessage.includes("line graph")) {
    return "line";
  }
  if (lowerMessage.includes("pie chart") || lowerMessage.includes("pie graph")) {
    return "pie";
  }
  if (lowerMessage.includes("area chart") || lowerMessage.includes("area graph")) {
    return "area";
  }
  return null;
}

// Helper to format relative time
function formatRelativeTime(dateString: string): string {
  const date = new Date(dateString);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);
  const diffHours = Math.floor(diffMs / 3600000);
  const diffDays = Math.floor(diffMs / 86400000);

  if (diffMins < 1) return "Just now";
  if (diffMins < 60) return `${diffMins} minute${diffMins > 1 ? "s" : ""} ago`;
  if (diffHours < 24) return `${diffHours} hour${diffHours > 1 ? "s" : ""} ago`;
  if (diffDays === 1) return "Yesterday";
  if (diffDays < 7) return `${diffDays} days ago`;
  return date.toLocaleDateString();
}

// Virtual Table Component for handling large datasets
function VirtualTable({
  columns,
  rows
}: {
  columns: string[];
  rows: Record<string, unknown>[];
}) {
  const parentRef = useRef<HTMLDivElement>(null);

  const rowVirtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => 40,
    overscan: 10,
  });

  // Calculate minimum width based on columns
  const minTableWidth = Math.max(columns.length * 140 + 60, 400);

  return (
    <div className="p-1 overflow-hidden">
      {/* Scroll container - handles both horizontal and vertical scroll */}
      <div
        ref={parentRef}
        className="max-h-[400px] overflow-auto custom-scrollbar"
      >
        {/* Inner container with minimum width for horizontal scroll */}
        <div style={{ minWidth: `${minTableWidth}px` }}>
          {/* Table Header - sticky top, scrolls horizontally with data */}
          <div
            className="flex items-center gap-2.5 px-3 py-2.5 bg-[#fafafc] dark:bg-gray-800 border-b border-gray-200 dark:border-gray-700 sticky top-0 z-10"
          >
            <span className="w-12 text-xs font-semibold text-gray-500 dark:text-gray-400 flex-shrink-0">#</span>
            {columns.map((col) => (
              <span
                key={col}
                className="min-w-[120px] w-[120px] text-xs font-semibold text-gray-700 dark:text-gray-300 truncate"
                title={col}
              >
                {col}
              </span>
            ))}
          </div>

          {/* Virtual rows container */}
          <div
            style={{
              height: `${rowVirtualizer.getTotalSize()}px`,
              position: 'relative',
            }}
          >
            {rowVirtualizer.getVirtualItems().map((virtualRow) => {
              const row = rows[virtualRow.index];
              return (
                <div
                  key={virtualRow.index}
                  className={`flex items-center gap-2.5 px-3 py-2.5 absolute w-full ${
                    virtualRow.index % 2 === 0 ? "bg-white dark:bg-gray-900" : "bg-gray-50/50 dark:bg-gray-800/50"
                  }`}
                  style={{
                    height: `${virtualRow.size}px`,
                    transform: `translateY(${virtualRow.start}px)`,
                  }}
                >
                  <span className="w-12 text-xs text-gray-400 flex-shrink-0">
                    {virtualRow.index + 1}
                  </span>
                  {columns.map((col) => (
                    <span
                      key={col}
                      className="min-w-[120px] w-[120px] text-sm text-gray-700 dark:text-gray-300 truncate"
                      title={String(row[col] ?? "")}
                    >
                      {String(row[col] ?? "")}
                    </span>
                  ))}
                </div>
              );
            })}
          </div>
        </div>
      </div>

      {/* Row count indicator */}
      <div className="text-xs text-gray-400 text-center py-2 border-t border-gray-100 dark:border-gray-700">
        {rows.length} rows total
      </div>
    </div>
  );
}

export default function AIPage() {
  const router = useRouter();
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const accountMenuRef = useRef<HTMLDivElement>(null);

  // User state
  const [user, setUser] = useState<User | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  // Conversations state
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const [selectedConversationId, setSelectedConversationId] = useState<string | null>(null);
  const [initialLoadComplete, setInitialLoadComplete] = useState(false);
  const initialConversationIdRef = useRef<string | null>(
    typeof window !== "undefined" ? localStorage.getItem("selectedConversationId") : null
  );
  const [messages, setMessages] = useState<Message[]>([]);
  const [loadingConversations, setLoadingConversations] = useState(true);
  const [loadingMessages, setLoadingMessages] = useState(false);

  // Persist selected conversation to localStorage
  useEffect(() => {
    if (selectedConversationId) {
      localStorage.setItem("selectedConversationId", selectedConversationId);
    }
  }, [selectedConversationId]);

  // Database connections state
  const [databases, setDatabases] = useState<DatabaseConnection[]>([]);
  const [selectedDatabaseId, setSelectedDatabaseId] = useState<string | null>(null);
  const [showDatabaseModal, setShowDatabaseModal] = useState(false);
  const [showAddDatabaseModal, setShowAddDatabaseModal] = useState(false);
  const [showSchemaModal, setShowSchemaModal] = useState(false);
  const [schemaViewDatabaseId, setSchemaViewDatabaseId] = useState<string | null>(null);

  // File state
  const [files, setFiles] = useState<FileDocument[]>([]);
  const [selectedFileId, setSelectedFileId] = useState<string | null>(null);
  const [showFilesModal, setShowFilesModal] = useState(false);
  const [isUploadingFile, setIsUploadingFile] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  // Chat input state
  const [inputValue, setInputValue] = useState("");
  const [isSending, setIsSending] = useState(false);

  // Loading phase indicator
  const [currentPhase, setCurrentPhase] = useState<"writing" | "executing" | null>(null);
  const phaseTimeoutsRef = useRef<{ writing?: NodeJS.Timeout; executing?: NodeJS.Timeout }>({});

  // Track pending request's conversation ID to handle background completion
  const pendingConversationRef = useRef<string | null>(null);

  // Track current selected conversation (for async callbacks)
  const selectedConversationIdRef = useRef<string | null>(null);

  // Track all conversations with pending requests (for UI indicator)
  const [pendingConversations, setPendingConversations] = useState<Set<string>>(new Set());

  // Track pending message info per conversation (message content and phase)
  const pendingMessagesRef = useRef<Map<string, { message: string; phase: "writing" | "executing" | null }>>(new Map());

  // Error state
  const [error, setError] = useState<string | null>(null);

  // Account menu state
  const [showAccountMenu, setShowAccountMenu] = useState(false);

  // Search state
  const [showSearchModal, setShowSearchModal] = useState(false);
  const [searchQuery, setSearchQuery] = useState("");

  // Rename conversation state
  const [editingConversationId, setEditingConversationId] = useState<string | null>(null);
  const [editingTitle, setEditingTitle] = useState("");

  // Chat menu state
  const [chatMenuOpen, setChatMenuOpen] = useState<string | null>(null);
  const [chatMenuOpenUp, setChatMenuOpenUp] = useState(false);
  const chatListRef = useRef<HTMLDivElement>(null);

  // Delete confirmation modal state
  const [deleteConfirm, setDeleteConfirm] = useState<{
    type: 'conversation' | 'database';
    id: string;
    name: string;
  } | null>(null);

  // Dark mode state
  const [darkMode, setDarkMode] = useState(false);

  // Chart view state - tracks view mode per message
  const [chartViews, setChartViews] = useState<Record<string, ChartType>>({});

  // Fullscreen data viewer state
  const [dataViewerOpen, setDataViewerOpen] = useState<string | null>(null);
  const [dataViewerData, setDataViewerData] = useState<{
    columns: string[];
    rows: Record<string, unknown>[];
    chartType: ChartType;
  } | null>(null);

  // Scroll to bottom of messages
  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  };

  useEffect(() => {
    scrollToBottom();
  }, [messages]);

  // Keep selectedConversationIdRef in sync with state (for async callbacks)
  useEffect(() => {
    selectedConversationIdRef.current = selectedConversationId;
  }, [selectedConversationId]);

  // Close chat menu when clicking outside
  useEffect(() => {
    const handleClickOutside = () => setChatMenuOpen(null);
    if (chatMenuOpen) {
      document.addEventListener("click", handleClickOutside);
      return () => document.removeEventListener("click", handleClickOutside);
    }
  }, [chatMenuOpen]);


  // Close account menu when clicking outside
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (accountMenuRef.current && !accountMenuRef.current.contains(event.target as Node)) {
        setShowAccountMenu(false);
      }
    };

    if (showAccountMenu) {
      document.addEventListener("mousedown", handleClickOutside);
    }

    return () => {
      document.removeEventListener("mousedown", handleClickOutside);
    };
  }, [showAccountMenu]);

  // Close search modal on Esc key
  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape" && showSearchModal) {
        setShowSearchModal(false);
        setSearchQuery("");
      }
    };

    document.addEventListener("keydown", handleKeyDown);
    return () => document.removeEventListener("keydown", handleKeyDown);
  }, [showSearchModal]);

  // Dark mode effect - sync with localStorage and apply class
  useEffect(() => {
    const savedMode = localStorage.getItem("darkMode");
    if (savedMode === "true") {
      setDarkMode(true);
      document.documentElement.classList.add("dark");
    }
  }, []);

  useEffect(() => {
    if (darkMode) {
      document.documentElement.classList.add("dark");
      localStorage.setItem("darkMode", "true");
    } else {
      document.documentElement.classList.remove("dark");
      localStorage.setItem("darkMode", "false");
    }
  }, [darkMode]);

  // Check auth and load initial data
  useEffect(() => {
    if (!auth.isAuthenticated()) {
      router.push("/login");
      return;
    }

    const currentUser = auth.getUser();
    setUser(currentUser);
    setIsLoading(false);

    // Load conversations, databases, and files
    loadConversations();
    loadDatabases();
    loadFiles();
  }, [router]);

  const loadConversations = async () => {
    try {
      // Set loading state for chat area immediately if we have a saved conversation
      const savedId = initialConversationIdRef.current;
      if (savedId) {
        setSelectedConversationId(savedId);
        setLoadingMessages(true);
      }

      const response = await api.getConversations();
      if (response.success) {
        setConversations(response.data);

        const savedExists = savedId && response.data.some(c => c.id === savedId);

        if (savedExists && savedId) {
          // Load messages for saved conversation
          try {
            const convResponse = await api.getConversation(savedId);
            if (convResponse.success) {
              setMessages(convResponse.data.messages);
              if (convResponse.data.databaseConnectionId) {
                setSelectedDatabaseId(convResponse.data.databaseConnectionId);
                setSelectedFileId(null);
              } else if (convResponse.data.fileDocumentId) {
                setSelectedFileId(convResponse.data.fileDocumentId);
                setSelectedDatabaseId(null);
              }
            }
          } catch {
            console.error("Failed to load saved conversation");
          } finally {
            setLoadingMessages(false);
          }
        } else if (response.data.length > 0) {
          // Clear invalid saved ID and select first
          if (savedId) {
            localStorage.removeItem("selectedConversationId");
          }
          await selectConversation(response.data[0].id);
        } else {
          // No conversations exist - clear loading state
          setLoadingMessages(false);
          if (savedId) {
            localStorage.removeItem("selectedConversationId");
          }
        }
      }
    } catch (err) {
      console.error("Failed to load conversations:", err);
    } finally {
      setLoadingConversations(false);
      setInitialLoadComplete(true);
    }
  };

  const loadDatabases = async () => {
    try {
      const response = await api.getDatabases();
      if (response.success) {
        setDatabases(response.data);
        // Auto-select first active database
        const activeDb = response.data.find((db) => db.isActive);
        if (activeDb) {
          setSelectedDatabaseId(activeDb.id);
        }
      }
    } catch (err) {
      console.error("Failed to load databases:", err);
    }
  };

  const loadFiles = async () => {
    try {
      const response = await api.getFiles();
      // Handle both wrapped { success, data } and direct { files } response formats
      if ('success' in response && response.success && response.data) {
        setFiles(response.data.files);
      } else if ('files' in response) {
        // Direct response format from backend
        setFiles((response as unknown as { files: FileDocument[] }).files);
      }
    } catch (err) {
      console.error("Failed to load files:", err);
    }
  };

  const handleFileUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;

    setIsUploadingFile(true);
    setError(null);

    try {
      const response = await api.uploadFile(file);
      if (response.success && response.file) {
        setFiles((prev) => [response.file!, ...prev]);
        setSelectedFileId(response.file.id);
        // Clear database and conversation when file is uploaded
        setSelectedDatabaseId(null);
        setSelectedConversationId(null);
        setMessages([]);
      } else {
        setError(response.message || "Failed to upload file");
      }
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message);
      } else {
        setError("Failed to upload file");
      }
    } finally {
      setIsUploadingFile(false);
      // Reset file input
      if (fileInputRef.current) {
        fileInputRef.current.value = "";
      }
    }
  };

  const handleDeleteFile = async (fileId: string) => {
    try {
      await api.deleteFile(fileId);
      setFiles((prev) => prev.filter((f) => f.id !== fileId));
      if (selectedFileId === fileId) {
        setSelectedFileId(null);
      }
    } catch (err) {
      console.error("Failed to delete file:", err);
    }
  };

  const formatFileSize = (bytes: number): string => {
    if (bytes < 1024) return bytes + " B";
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + " KB";
    return (bytes / (1024 * 1024)).toFixed(1) + " MB";
  };

  const getFileIcon = (fileType: string) => {
    switch (fileType) {
      case "Excel":
        return (
          <svg className="w-5 h-5 text-green-600" fill="currentColor" viewBox="0 0 24 24">
            <path d="M14.17 2H6c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V7.83L14.17 2zM13 8V3.5L18.5 9H13V8zM6 20V4h5v6h7v10H6z"/>
            <path d="M8.5 11L10.5 14L8.5 17H10L11.25 15L12.5 17H14L12 14L14 11H12.5L11.25 13L10 11H8.5z"/>
          </svg>
        );
      case "Word":
        return (
          <svg className="w-4 h-4 text-blue-600" fill="currentColor" viewBox="0 0 24 24">
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6zM6 20V4h7v5h5v11H6z"/>
            <path d="M8 12h1.5l1 4 1-4h1l1 4 1-4H15l-1.5 6h-1l-1-4-1 4h-1L8 12z"/>
          </svg>
        );
      case "Csv":
        return (
          <svg className="w-4 h-4 text-orange-600" fill="currentColor" viewBox="0 0 24 24">
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6zM6 20V4h7v5h5v11H6z"/>
            <path d="M8 12v6h8v-6H8zm2 2h4v2h-4v-2z"/>
          </svg>
        );
      default:
        return (
          <svg className="w-4 h-4 text-gray-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
          </svg>
        );
    }
  };

  const selectConversation = useCallback(async (conversationId: string) => {
    setSelectedConversationId(conversationId);
    setLoadingMessages(true);
    setError(null);
    // Reset sending state when switching conversations
    setIsSending(false);
    setCurrentPhase(null);
    // Clear any pending phase timers
    if (phaseTimeoutsRef.current.writing) clearTimeout(phaseTimeoutsRef.current.writing);
    if (phaseTimeoutsRef.current.executing) clearTimeout(phaseTimeoutsRef.current.executing);
    phaseTimeoutsRef.current = {};
    // Update pending ref to new conversation - callbacks from old requests will see the mismatch
    pendingConversationRef.current = conversationId;

    try {
      const response = await api.getConversation(conversationId);
      if (response.success) {
        setMessages(response.data.messages);
        // Set the appropriate data source (database or file)
        if (response.data.databaseConnectionId) {
          setSelectedDatabaseId(response.data.databaseConnectionId);
          setSelectedFileId(null);
        } else if (response.data.fileDocumentId) {
          setSelectedFileId(response.data.fileDocumentId);
          setSelectedDatabaseId(null);
        }
      }
    } catch (err) {
      console.error("Failed to load conversation:", err);
      setError("Failed to load conversation");
    } finally {
      setLoadingMessages(false);
    }
  }, []);

  const createNewConversation = async () => {
    // Need either a database or file selected
    if (!selectedDatabaseId && !selectedFileId) {
      setShowDatabaseModal(true);
      return;
    }

    try {
      const response = await api.createConversation({
        databaseConnectionId: selectedDatabaseId || undefined,
        fileDocumentId: selectedFileId || undefined,
      });
      if (response.success) {
        setConversations((prev) => [response.data, ...prev]);
        setSelectedConversationId(response.data.id);
        setMessages([]);
      }
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message);
      }
    }
  };

  const handleSendMessage = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!inputValue.trim() || isSending) return;

    const messageContent = inputValue.trim();
    setInputValue("");
    setIsSending(true);
    setError(null);

    // If no conversation selected, create one first
    let conversationId = selectedConversationId;
    if (!conversationId) {
      // Need either a database or file selected
      if (!selectedDatabaseId && !selectedFileId) {
        setShowDatabaseModal(true);
        setIsSending(false);
        setInputValue(messageContent);
        return;
      }

      try {
        const convResponse = await api.createConversation({
          databaseConnectionId: selectedDatabaseId || undefined,
          fileDocumentId: selectedFileId || undefined,
        });
        if (convResponse.success) {
          conversationId = convResponse.data.id;
          setConversations((prev) => [convResponse.data, ...prev]);
          setSelectedConversationId(conversationId);
        }
      } catch (err) {
        if (err instanceof ApiError) {
          setError(err.message);
        }
        setIsSending(false);
        setInputValue(messageContent);
        return;
      }
    }

    // Add user message optimistically
    const tempUserMessage: Message = {
      id: `temp-${Date.now()}`,
      role: "User",
      content: messageContent,
      sqlQuery: null,
      queryResult: null,
      tokensUsed: 0,
      createdAt: new Date().toISOString(),
    };
    setMessages((prev) => [...prev, tempUserMessage]);

    // Track which conversation this request is for
    const requestConversationId = conversationId!;
    pendingConversationRef.current = requestConversationId;

    // Add to pending conversations set (for UI indicator in sidebar)
    setPendingConversations(prev => new Set(prev).add(requestConversationId));

    // Store pending message info for this conversation
    pendingMessagesRef.current.set(requestConversationId, { message: messageContent, phase: null });

    try {
      // Fake phases - show "writing" after 500ms, "executing" after 2s
      phaseTimeoutsRef.current.writing = setTimeout(() => {
        setCurrentPhase("writing");
        // Update phase in pending messages ref
        const pending = pendingMessagesRef.current.get(requestConversationId);
        if (pending) pendingMessagesRef.current.set(requestConversationId, { ...pending, phase: "writing" });
      }, 500);
      phaseTimeoutsRef.current.executing = setTimeout(() => {
        setCurrentPhase("executing");
        // Update phase in pending messages ref
        const pending = pendingMessagesRef.current.get(requestConversationId);
        if (pending) pendingMessagesRef.current.set(requestConversationId, { ...pending, phase: "executing" });
      }, 2000);

      // Use REST API - this completes even if user switches away
      const response = await api.sendMessage({
        conversationId: requestConversationId,
        message: messageContent,
        executeQuery: true,
      });

      // Clear fake phase timers
      if (phaseTimeoutsRef.current.writing) clearTimeout(phaseTimeoutsRef.current.writing);
      if (phaseTimeoutsRef.current.executing) clearTimeout(phaseTimeoutsRef.current.executing);
      phaseTimeoutsRef.current = {};

      // Check if user is currently viewing the same conversation (using ref for accurate async check)
      const currentlyViewingConversation = selectedConversationIdRef.current === requestConversationId;

      // Clear pending ref if this was the tracked conversation
      if (pendingConversationRef.current === requestConversationId) {
        pendingConversationRef.current = null;
      }

      // Remove from pending conversations set and clean up pending message info
      setPendingConversations(prev => {
        const next = new Set(prev);
        next.delete(requestConversationId);
        return next;
      });
      pendingMessagesRef.current.delete(requestConversationId);

      if (response.success && currentlyViewingConversation) {
        // Reload messages from API to get fresh data
        // This handles both normal completion and when user switched away and back
        try {
          const convResponse = await api.getConversation(requestConversationId);
          if (convResponse.success) {
            setMessages(convResponse.data.messages);
          }
        } catch {
          // Fallback: try to update messages directly
          setMessages((prev) => {
            const filtered = prev.filter(m => m.id !== tempUserMessage.id);
            return [...filtered, response.data.userMessage, response.data.assistantMessage];
          });
        }

        // Check if user requested a specific chart type
        const requestedChartType = detectRequestedChartType(messageContent);
        if (requestedChartType && response.data.assistantMessage.queryResult) {
          setChartViews((prevViews) => ({
            ...prevViews,
            [response.data.assistantMessage.id]: requestedChartType,
          }));
        }
      }

      // Always refresh conversations to get updated title (even if switched away)
      loadConversations();

      // Only update loading state if still on same conversation
      if (currentlyViewingConversation) {
        setCurrentPhase(null);
        setIsSending(false);
      }
    } catch (err) {
      // Clear fake phase timers on error too
      if (phaseTimeoutsRef.current.writing) clearTimeout(phaseTimeoutsRef.current.writing);
      if (phaseTimeoutsRef.current.executing) clearTimeout(phaseTimeoutsRef.current.executing);
      phaseTimeoutsRef.current = {};

      // Check if user is currently viewing the same conversation
      const currentlyViewingConversation = selectedConversationIdRef.current === requestConversationId;

      // Clear pending ref if this was the tracked conversation
      if (pendingConversationRef.current === requestConversationId) {
        pendingConversationRef.current = null;
      }

      // Remove from pending conversations set and clean up pending message info
      setPendingConversations(prev => {
        const next = new Set(prev);
        next.delete(requestConversationId);
        return next;
      });
      pendingMessagesRef.current.delete(requestConversationId);

      // Only update UI if still on same conversation
      if (currentlyViewingConversation) {
        // Reload messages to remove optimistic message
        try {
          const convResponse = await api.getConversation(requestConversationId);
          if (convResponse.success) {
            setMessages(convResponse.data.messages);
          }
        } catch {
          // Fallback: remove optimistic message
          setMessages((prev) => prev.filter((m) => m && m.id !== tempUserMessage.id));
        }
        setCurrentPhase(null);
        setIsSending(false);
        if (err instanceof ApiError) {
          setError(err.message);
        } else {
          setError("Failed to send message");
        }
        setInputValue(messageContent);
      }
    }
  };

  const handleLogout = async () => {
    try {
      await api.logout();
    } catch {
      // Ignore logout errors
    }
    auth.clearTokens();
    router.push("/login");
  };

  // Rename conversation handler
  const handleRenameConversation = async (conversationId: string, newTitle: string) => {
    if (!newTitle.trim()) {
      setEditingConversationId(null);
      return;
    }
    try {
      const response = await api.updateConversation(conversationId, { title: newTitle.trim() });
      if (response.success) {
        setConversations((prev) =>
          prev.map((c) => (c.id === conversationId ? { ...c, title: newTitle.trim() } : c))
        );
      }
    } catch (err) {
      console.error("Failed to rename conversation:", err);
    }
    setEditingConversationId(null);
  };

  // Delete conversation handler
  const handleDeleteConversation = async (conversationId: string) => {
    try {
      await api.deleteConversation(conversationId);
      setConversations((prev) => prev.filter((c) => c.id !== conversationId));
      if (selectedConversationId === conversationId) {
        setSelectedConversationId(null);
        setMessages([]);
      }
    } catch (err) {
      console.error("Failed to delete conversation:", err);
    }
    setDeleteConfirm(null);
  };

  // Delete database handler
  const handleDeleteDatabase = async (databaseId: string) => {
    try {
      await api.deleteDatabase(databaseId);
      setDatabases((prev) => prev.filter((d) => d.id !== databaseId));
      if (selectedDatabaseId === databaseId) {
        setSelectedDatabaseId(null);
      }
    } catch (err) {
      console.error("Failed to delete database:", err);
    }
    setDeleteConfirm(null);
  };

  const selectedConversation = conversations.find(
    (c) => c.id === selectedConversationId
  );
  const selectedDatabase = databases.find((d) => d.id === selectedDatabaseId);
  const selectedFile = files.find((f) => f.id === selectedFileId);

  // Show loading only for auth check
  if (isLoading) {
    return (
      <div className="min-h-screen bg-gray-50 dark:bg-gray-900 flex items-center justify-center">
        <div className="text-gray-500 dark:text-gray-400 text-sm">Loading...</div>
      </div>
    );
  }

  const userInitial = user?.firstName?.[0]?.toUpperCase() || "U";

  return (
    <div className="h-screen bg-gray-50 dark:bg-gray-900 flex transition-colors duration-200">
      {/* Sidebar */}
      <aside className="w-[260px] bg-white dark:bg-gray-800 flex flex-col justify-between border-r border-gray-100/80 dark:border-gray-700/80 transition-colors duration-200">
        {/* Top Section */}
        <div className="flex flex-col">
          {/* Header */}
          <div className="px-4 pt-5 pb-3">
            <span className="font-semibold text-base text-gray-900 dark:text-white">Chats</span>
          </div>

          {/* Action Buttons */}
          <div className="flex flex-col gap-1.5 px-3 pb-3">
            <button
              onClick={createNewConversation}
              className="w-full flex items-center gap-3 px-3 py-2.5 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 hover:text-gray-900 dark:hover:text-white rounded-xl transition-all duration-200 active:scale-[0.98]"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
              </svg>
              New chat
            </button>
            <button
              onClick={() => setShowSearchModal(true)}
              className="w-full flex items-center gap-3 px-3 py-2.5 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 hover:text-gray-900 dark:hover:text-white rounded-xl transition-all duration-200 active:scale-[0.98]"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
              </svg>
              Search chats
            </button>
          </div>

          {/* Chat List */}
          <div ref={chatListRef} className="flex flex-col gap-1 px-3 pb-3 overflow-y-auto max-h-[calc(100vh-280px)] custom-scrollbar">
            {loadingConversations ? (
              <div className="text-center py-4 text-sm text-gray-400 dark:text-gray-500">
                Loading...
              </div>
            ) : conversations.length === 0 ? (
              <div className="text-center py-4 text-sm text-gray-400 dark:text-gray-500">
                No conversations yet
              </div>
            ) : (
              conversations.map((chat) => (
                    <div
                      key={chat.id}
                      className={`group relative w-full text-left rounded-xl px-3.5 py-3 flex flex-col gap-1 cursor-pointer transition-all duration-200 ${
                        chatMenuOpen === chat.id ? "z-50" : ""
                      } ${
                        chat.id === selectedConversationId
                          ? "bg-gray-100/80 dark:bg-gray-700/80 shadow-sm"
                          : "bg-white dark:bg-gray-800 hover:bg-gray-50 dark:hover:bg-gray-700/50 hover:shadow-sm"
                      }`}
                      onClick={() => {
                        if (editingConversationId !== chat.id) {
                          setChatMenuOpen(null);
                          selectConversation(chat.id);
                        }
                      }}
                    >
                      {editingConversationId === chat.id ? (
                        <input
                          type="text"
                          value={editingTitle}
                          onChange={(e) => setEditingTitle(e.target.value)}
                          onBlur={() => handleRenameConversation(chat.id, editingTitle)}
                          onKeyDown={(e) => {
                            if (e.key === "Enter") handleRenameConversation(chat.id, editingTitle);
                            if (e.key === "Escape") setEditingConversationId(null);
                          }}
                          className="text-sm font-medium bg-white dark:bg-gray-700 border border-gray-300 dark:border-gray-600 rounded px-2 py-1 w-full pr-8 dark:text-white"
                          autoFocus
                          onClick={(e) => e.stopPropagation()}
                        />
                      ) : (
                        <div className="flex items-center gap-2 pr-8">
                          <span className="text-sm font-medium truncate text-gray-900 dark:text-white">
                            {chat.title || "New Chat"}
                          </span>
                          {pendingConversations.has(chat.id) && (
                            <div className="flex-shrink-0 flex items-center gap-1">
                              <span className="relative flex h-2.5 w-2.5">
                                <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-gray-400 dark:bg-gray-500 opacity-75"></span>
                                <span className="relative inline-flex rounded-full h-2.5 w-2.5 bg-gray-500 dark:bg-gray-400"></span>
                              </span>
                            </div>
                          )}
                        </div>
                      )}
                      <div className="flex items-center gap-1.5 text-xs text-gray-400 dark:text-gray-500 pr-8">
                        {(chat.databaseConnectionName || chat.fileDocumentName) && (
                          <>
                            <span className="truncate max-w-[80px]">
                              {chat.databaseConnectionName || chat.fileDocumentName}
                            </span>
                            <span>·</span>
                          </>
                        )}
                        <span className="whitespace-nowrap">{formatRelativeTime(chat.updatedAt)}</span>
                      </div>
                      {/* More options button */}
                      {editingConversationId !== chat.id && (
                        <div className={`absolute right-2 top-1/2 -translate-y-1/2 ${chatMenuOpen === chat.id ? "z-[100]" : ""}`}>
                          <button
                            onClick={(e) => {
                              e.stopPropagation();
                              if (chatMenuOpen === chat.id) {
                                setChatMenuOpen(null);
                              } else {
                                // Check if button is in bottom half of chat list
                                const button = e.currentTarget;
                                const container = chatListRef.current;
                                if (container) {
                                  const containerRect = container.getBoundingClientRect();
                                  const buttonRect = button.getBoundingClientRect();
                                  const buttonBottom = buttonRect.bottom - containerRect.top;
                                  const containerHeight = containerRect.height;
                                  // If button is in bottom 100px of visible area, open menu upward
                                  setChatMenuOpenUp(buttonBottom > containerHeight - 100);
                                }
                                setChatMenuOpen(chat.id);
                              }
                            }}
                            className={`p-1.5 rounded-md transition-colors ${
                              chatMenuOpen === chat.id
                                ? "bg-gray-200 dark:bg-gray-600"
                                : "opacity-0 group-hover:opacity-100 hover:bg-gray-200 dark:hover:bg-gray-600"
                            }`}
                          >
                            <svg className="w-4 h-4 text-gray-500 dark:text-gray-400" fill="currentColor" viewBox="0 0 24 24">
                              <circle cx="12" cy="6" r="1.5" />
                              <circle cx="12" cy="12" r="1.5" />
                              <circle cx="12" cy="18" r="1.5" />
                            </svg>
                          </button>
                          {/* Dropdown menu */}
                          {chatMenuOpen === chat.id && (
                            <div
                              className={`absolute right-0 w-36 bg-white dark:bg-gray-800 rounded-lg shadow-lg border border-gray-100 dark:border-gray-700 py-1 ${
                                chatMenuOpenUp ? "bottom-full mb-1" : "top-full mt-1"
                              }`}
                            >
                              <button
                                onClick={(e) => {
                                  e.stopPropagation();
                                  setChatMenuOpen(null);
                                  setEditingConversationId(chat.id);
                                  setEditingTitle(chat.title || "New Chat");
                                }}
                                className="w-full text-left px-3 py-2 text-sm text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-700 flex items-center gap-2"
                              >
                                <svg className="w-4 h-4 text-gray-500 dark:text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15.232 5.232l3.536 3.536m-2.036-5.036a2.5 2.5 0 113.536 3.536L6.5 21.036H3v-3.572L16.732 3.732z" />
                                </svg>
                                Rename
                              </button>
                              <button
                                onClick={(e) => {
                                  e.stopPropagation();
                                  setChatMenuOpen(null);
                                  setDeleteConfirm({
                                    type: 'conversation',
                                    id: chat.id,
                                    name: chat.title || 'New Chat'
                                  });
                                }}
                                className="w-full text-left px-3 py-2 text-sm hover:bg-red-50 dark:hover:bg-red-900/20 text-red-600 dark:text-red-400 flex items-center gap-2"
                              >
                                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                                </svg>
                                Delete
                              </button>
                            </div>
                          )}
                        </div>
                      )}
                    </div>
                ))
            )}
          </div>
        </div>

        {/* User Profile */}
        <div className="p-3 relative border-t border-gray-100/80 dark:border-gray-700/80">
          <button
            onClick={() => setShowAccountMenu(!showAccountMenu)}
            className="w-full bg-gray-50 dark:bg-gray-700/50 rounded-xl p-3 flex items-center gap-3 hover:bg-gray-100 dark:hover:bg-gray-700 transition-all duration-200 active:scale-[0.99]"
          >
            <div className="w-9 h-9 bg-gradient-to-br from-gray-800 to-gray-900 dark:from-gray-600 dark:to-gray-700 rounded-xl flex items-center justify-center shadow-sm">
              <span className="text-white text-xs font-semibold">
                {userInitial}
              </span>
            </div>
            <div className="flex-1 min-w-0 text-left">
              <p className="text-sm font-medium text-gray-900 dark:text-white truncate">
                {user?.firstName} {user?.lastName}
              </p>
              <p className="text-xs text-gray-400 dark:text-gray-500">
                {user?.subscriptionTier || "Starter"} Plan
              </p>
            </div>
            <svg
              className={`w-4 h-4 text-gray-400 dark:text-gray-500 transition-transform duration-200 ${showAccountMenu ? "rotate-180" : ""}`}
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
            </svg>
          </button>

          {/* Account Menu Dropdown */}
          {showAccountMenu && (
            <div className="absolute bottom-full left-3 right-3 mb-2 bg-white dark:bg-gray-800 rounded-xl shadow-lg shadow-gray-200/50 dark:shadow-gray-900/50 border border-gray-100/80 dark:border-gray-700 py-1.5 z-50">
              <button
                onClick={() => {
                  setShowAccountMenu(false);
                  router.push("/profile");
                }}
                className="w-full text-left px-4 py-2.5 text-sm text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 hover:text-black dark:hover:text-white cursor-pointer transition-colors flex items-center gap-3"
              >
                <svg className="w-4 h-4 text-gray-400 dark:text-gray-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z" />
                </svg>
                Profile
              </button>
              <button
                onClick={() => {
                  setShowAccountMenu(false);
                  router.push("/usage");
                }}
                className="w-full text-left px-4 py-2.5 text-sm text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 hover:text-black dark:hover:text-white cursor-pointer transition-colors flex items-center gap-3"
              >
                <svg className="w-4 h-4 text-gray-400 dark:text-gray-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
                </svg>
                Usage
              </button>
              <button
                onClick={() => {
                  setShowAccountMenu(false);
                  router.push("/subscriptions");
                }}
                className="w-full text-left px-4 py-2.5 text-sm text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 hover:text-black dark:hover:text-white cursor-pointer transition-colors flex items-center gap-3"
              >
                <svg className="w-4 h-4 text-gray-400 dark:text-gray-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 10h18M7 15h1m4 0h1m-7 4h12a3 3 0 003-3V8a3 3 0 00-3-3H6a3 3 0 00-3 3v8a3 3 0 003 3z" />
                </svg>
                Subscriptions
              </button>
              <button
                onClick={() => setDarkMode(!darkMode)}
                className="w-full text-left px-4 py-2.5 text-sm text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 hover:text-black dark:hover:text-white cursor-pointer transition-colors flex items-center gap-3"
              >
                {darkMode ? (
                  <svg className="w-4 h-4 text-gray-400 dark:text-gray-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.364 6.364l-.707-.707M6.343 6.343l-.707-.707m12.728 0l-.707.707M6.343 17.657l-.707.707M16 12a4 4 0 11-8 0 4 4 0 018 0z" />
                  </svg>
                ) : (
                  <svg className="w-4 h-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M20.354 15.354A9 9 0 018.646 3.646 9.003 9.003 0 0012 21a9.003 9.003 0 008.354-5.646z" />
                  </svg>
                )}
                {darkMode ? "Light mode" : "Dark mode"}
              </button>
              <hr className="my-1.5 border-gray-100 dark:border-gray-700" />
              <button
                onClick={() => {
                  setShowAccountMenu(false);
                  handleLogout();
                }}
                className="w-full text-left px-4 py-2.5 text-sm text-red-600 dark:text-red-400 hover:bg-red-100 dark:hover:bg-red-900/30 cursor-pointer transition-colors flex items-center gap-3"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1" />
                </svg>
                Sign out
              </button>
            </div>
          )}
        </div>
      </aside>

      {/* Main Content */}
      <main className="flex-1 min-w-0 flex flex-col bg-gray-50/50 dark:bg-gray-900 overflow-hidden transition-colors duration-200">
        {/* Header */}
        <header className="bg-white/80 dark:bg-gray-800/80 backdrop-blur-sm px-6 py-4 flex items-center justify-between border-b border-gray-100/80 dark:border-gray-700/80 sticky top-0 z-10 transition-colors duration-200">
          <div className="flex flex-col gap-1">
            <h1 className="font-semibold text-base text-gray-900 dark:text-white">
              {selectedConversation?.title || "New Chat"}
            </h1>
            <div className="flex items-center gap-2">
              <button
                onClick={() => setShowDatabaseModal(true)}
                className={`text-xs hover:text-gray-700 dark:hover:text-gray-300 text-left transition-all duration-200 hover:underline underline-offset-2 ${
                  selectedDatabase ? "text-gray-600 dark:text-gray-400" : "text-gray-400 dark:text-gray-500"
                }`}
              >
                {selectedDatabase
                  ? `DB: ${selectedDatabase.name}`
                  : "Select database"}
              </button>
              <span className="text-gray-200 dark:text-gray-600">|</span>
              <button
                onClick={() => setShowFilesModal(true)}
                className={`text-xs hover:text-gray-700 dark:hover:text-gray-300 text-left transition-all duration-200 hover:underline underline-offset-2 ${
                  selectedFile ? "text-gray-600 dark:text-gray-400" : "text-gray-400 dark:text-gray-500"
                }`}
              >
                {selectedFile
                  ? `File: ${selectedFile.originalFileName}`
                  : "Select file"}
              </button>
            </div>
          </div>
          {/* Upload indicator */}
          {isUploadingFile && (
            <div className="flex items-center gap-2 text-sm text-gray-500 dark:text-gray-400">
              <div className="w-4 h-4 border-2 border-gray-300 dark:border-gray-600 border-t-gray-600 dark:border-t-gray-300 rounded-full animate-spin" />
              Uploading...
            </div>
          )}
        </header>

        {/* Chat Area */}
        <div className="flex-1 overflow-y-auto overflow-x-hidden px-8 py-6 flex flex-col gap-6 custom-scrollbar">
          {loadingMessages ? (
            <div className="flex items-center justify-center h-full">
              <div className="w-8 h-8 border-2 border-gray-200 dark:border-gray-700 border-t-gray-800 dark:border-t-white rounded-full animate-spin" />
            </div>
          ) : messages.length === 0 ? (
            <div className="flex items-center justify-center h-full">
              <div className="text-center max-w-md">
                <h2 className="text-lg font-semibold mb-2 text-gray-900 dark:text-white">
                  {selectedFile
                    ? `Analyzing ${selectedFile.originalFileName}`
                    : selectedDatabase
                    ? `Connected to ${selectedDatabase.name}`
                    : "Welcome to Erao"}
                </h2>
                <p className="text-sm text-gray-500 dark:text-gray-400">
                  {selectedFile
                    ? `${selectedFile.fileType} file with ${selectedFile.rowCount?.toLocaleString() || 0} rows ready. Ask anything about your data.`
                    : selectedDatabase
                    ? "Ask anything about your data. I can help you analyze, query, and understand your database."
                    : "Connect a database or upload a file to start querying your data with natural language."}
                </p>
                {!selectedDatabase && !selectedFile && (
                  <div className="flex gap-3 justify-center mt-4">
                    <button
                      onClick={() => setShowDatabaseModal(true)}
                      className="px-4 py-2 bg-gray-900 dark:bg-white text-white dark:text-gray-900 text-sm rounded-lg hover:bg-gray-800 dark:hover:bg-gray-100 transition-colors"
                    >
                      Connect Database
                    </button>
                    <button
                      onClick={() => fileInputRef.current?.click()}
                      className="px-4 py-2 bg-white dark:bg-gray-800 text-gray-900 dark:text-white text-sm rounded-lg border border-gray-200 dark:border-gray-700 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors"
                    >
                      Upload File
                    </button>
                  </div>
                )}
              </div>
            </div>
          ) : (
            messages.filter((m) => m && m.role).map((message) => (
              <div key={message.id}>
                {message.role === "Assistant" ? (
                  <div className="w-[70%] max-w-[70%] bg-white dark:bg-gray-800 rounded-2xl p-5 flex flex-col gap-3.5 shadow-sm border border-gray-100/80 dark:border-gray-700/80 overflow-hidden">
                    <span className="font-semibold text-base text-gray-900 dark:text-white">Erao</span>
                    <MarkdownResponse content={stripCodeBlocks(message.content)} />
                    {(() => {
                      const parsedResults = parseQueryResult(message.queryResult);
                      if (!parsedResults || parsedResults.length === 0) return null;

                      // Multiple tables - show each with same UI as single table
                      if (parsedResults.length > 1) {
                        return (
                          <div className="space-y-4 mt-2">
                            {parsedResults.map((result, idx) => {
                              // Use title from result or derive from first column
                              const tableTitle = (result as QueryResult & { title?: string }).title ||
                                result.columns[0]?.replace(/_id$/i, '').replace(/_/g, ' ') ||
                                `Table ${idx + 1}`;
                              const capitalizedTitle = tableTitle.charAt(0).toUpperCase() + tableTitle.slice(1);

                              // Use composite key for chart view state
                              const viewKey = `${message.id}_${idx}`;
                              const currentView = chartViews[viewKey] || "table";

                              // Check if data is chartable
                              const hasNumericData = result.columns.some((col) =>
                                result.rows.some((row) => {
                                  const val = row[col];
                                  if (val === null || val === undefined || val === '') return false;
                                  if (typeof val === "number") return true;
                                  const numVal = Number(val);
                                  return !isNaN(numVal) && isFinite(numVal);
                                })
                              );

                              return (
                                <div key={idx} className="bg-[#fafafc] dark:bg-gray-800 rounded-xl overflow-hidden">
                                  {/* Title */}
                                  <div className="px-3 pt-2 pb-1">
                                    <span className="text-xs font-medium text-gray-500 dark:text-gray-400">{capitalizedTitle}</span>
                                  </div>
                                  {/* View Toggle Buttons */}
                                  <div className="flex items-center justify-between p-2 border-b border-gray-200 dark:border-gray-700">
                                    <div className="flex items-center gap-1">
                                      <button
                                        onClick={() => setChartViews(prev => ({ ...prev, [viewKey]: "table" }))}
                                        className={`px-3 py-1.5 text-xs rounded-md transition-colors ${
                                          currentView === "table"
                                            ? "bg-black dark:bg-white text-white dark:text-gray-900"
                                            : "bg-white dark:bg-gray-700 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
                                        }`}
                                      >
                                        Table
                                      </button>
                                      {hasNumericData && result.rows.length > 1 && (
                                        <>
                                          <button
                                            onClick={() => setChartViews(prev => ({ ...prev, [viewKey]: "bar" }))}
                                            className={`px-3 py-1.5 text-xs rounded-md transition-colors ${
                                              currentView === "bar"
                                                ? "bg-black dark:bg-white text-white dark:text-gray-900"
                                                : "bg-white dark:bg-gray-700 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
                                            }`}
                                          >
                                            Bar
                                          </button>
                                          <button
                                            onClick={() => setChartViews(prev => ({ ...prev, [viewKey]: "line" }))}
                                            className={`px-3 py-1.5 text-xs rounded-md transition-colors ${
                                              currentView === "line"
                                                ? "bg-black dark:bg-white text-white dark:text-gray-900"
                                                : "bg-white dark:bg-gray-700 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
                                            }`}
                                          >
                                            Line
                                          </button>
                                          <button
                                            onClick={() => setChartViews(prev => ({ ...prev, [viewKey]: "pie" }))}
                                            className={`px-3 py-1.5 text-xs rounded-md transition-colors ${
                                              currentView === "pie"
                                                ? "bg-black dark:bg-white text-white dark:text-gray-900"
                                                : "bg-white dark:bg-gray-700 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
                                            }`}
                                          >
                                            Pie
                                          </button>
                                          <button
                                            onClick={() => setChartViews(prev => ({ ...prev, [viewKey]: "area" }))}
                                            className={`px-3 py-1.5 text-xs rounded-md transition-colors ${
                                              currentView === "area"
                                                ? "bg-black dark:bg-white text-white dark:text-gray-900"
                                                : "bg-white dark:bg-gray-700 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
                                            }`}
                                          >
                                            Area
                                          </button>
                                        </>
                                      )}
                                    </div>
                                    {/* Expand Button */}
                                    <button
                                      onClick={() => {
                                        setDataViewerData({
                                          columns: result.columns,
                                          rows: result.rows,
                                          chartType: currentView,
                                        });
                                        setDataViewerOpen(message.id);
                                      }}
                                      className="flex items-center gap-1.5 px-2.5 py-1.5 text-xs text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-200 hover:bg-gray-100 dark:hover:bg-gray-700 rounded-md transition-colors"
                                      title="Open in fullscreen"
                                    >
                                      <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 8V4m0 0h4M4 4l5 5m11-1V4m0 0h-4m4 0l-5 5M4 16v4m0 0h4m-4 0l5-5m11 5l-5-5m5 5v-4m0 4h-4" />
                                      </svg>
                                      Expand
                                    </button>
                                  </div>

                                  {/* Chart View */}
                                  {currentView !== "table" && (
                                    <DataChart
                                      data={result.rows}
                                      columns={result.columns}
                                      chartType={currentView}
                                    />
                                  )}

                                  {/* Table View */}
                                  {currentView === "table" && (
                                    <VirtualTable
                                      columns={result.columns}
                                      rows={result.rows}
                                    />
                                  )}
                                </div>
                              );
                            })}
                          </div>
                        );
                      }

                      // Single table - render normally
                      const parsedResult = parsedResults[0];
                      if (!parsedResult.rows || parsedResult.rows.length === 0) {
                        // Show empty state instead of hiding completely
                        return (
                          <div className="bg-[#fafafc] dark:bg-gray-800 rounded-xl p-4 mt-2 text-center">
                            <p className="text-sm text-gray-500 dark:text-gray-400">No data returned</p>
                          </div>
                        );
                      }

                      // For single row results, show as a clean card instead of a table
                      if (parsedResult.rows.length === 1) {
                        const row = parsedResult.rows[0];
                        const values = parsedResult.columns.map((col) => ({
                          label: col.replace(/_/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').trim(),
                          value: row[col],
                        }));

                        // Find the "main" value (usually numeric, like total, count, amount)
                        const mainValueIdx = values.findIndex(v =>
                          typeof v.value === 'number' ||
                          /total|count|sum|amount|price|spent/i.test(v.label)
                        );
                        const mainValue = mainValueIdx >= 0 ? values[mainValueIdx] : null;
                        const otherValues = values.filter((_, idx) => idx !== mainValueIdx);

                        return (
                          <div className="mt-3 space-y-3">
                            {/* Main value - featured card */}
                            {mainValue && (
                              <div className="relative overflow-hidden rounded-2xl bg-gradient-to-br from-gray-900 to-gray-800 dark:from-gray-100 dark:to-gray-200 p-5">
                                <div className="absolute top-0 right-0 w-32 h-32 bg-white/5 dark:bg-black/5 rounded-full -translate-y-1/2 translate-x-1/2"></div>
                                <div className="absolute bottom-0 left-0 w-24 h-24 bg-white/5 dark:bg-black/5 rounded-full translate-y-1/2 -translate-x-1/2"></div>
                                <div className="relative">
                                  <span className="text-xs font-medium text-gray-400 dark:text-gray-500 uppercase tracking-wider">
                                    {mainValue.label}
                                  </span>
                                  <div className="text-4xl font-bold text-white dark:text-gray-900 mt-1 tracking-tight">
                                    {typeof mainValue.value === "number"
                                      ? mainValue.value.toLocaleString(undefined, { maximumFractionDigits: 2 })
                                      : String(mainValue.value)}
                                  </div>
                                </div>
                              </div>
                            )}
                            {/* Other values - stat cards */}
                            {otherValues.length > 0 && (
                              <div className={`grid gap-2 ${otherValues.length >= 3 ? 'grid-cols-3' : otherValues.length === 2 ? 'grid-cols-2' : 'grid-cols-1'}`}>
                                {otherValues.map((item, idx) => (
                                  <div
                                    key={idx}
                                    className="rounded-xl bg-gray-100 dark:bg-gray-800 px-4 py-3 hover:bg-gray-150 dark:hover:bg-gray-750 transition-colors"
                                  >
                                    <span className="text-[10px] text-gray-500 dark:text-gray-400 uppercase tracking-wider font-medium block">{item.label}</span>
                                    <div className="text-lg font-semibold text-gray-900 dark:text-white mt-1">
                                      {typeof item.value === "number"
                                        ? item.value.toLocaleString(undefined, { maximumFractionDigits: 2 })
                                        : String(item.value)}
                                    </div>
                                  </div>
                                ))}
                              </div>
                            )}
                          </div>
                        );
                      }

                      // Get current view mode for this message, default to table
                      const currentView = chartViews[message.id] || "table";

                      // Check if data is chartable (has at least one numeric column)
                      // Check ALL columns, not just after first - and handle string numbers
                      const hasNumericData = parsedResult.columns.some((col) =>
                        parsedResult.rows.some((row) => {
                          const val = row[col];
                          if (val === null || val === undefined || val === '') return false;
                          if (typeof val === "number") return true;
                          // Check if string can be parsed as number
                          const numVal = Number(val);
                          return !isNaN(numVal) && isFinite(numVal);
                        })
                      );

                      return (
                        <div className="bg-[#fafafc] dark:bg-gray-800 rounded-xl overflow-hidden mt-2">
                          {/* View Toggle Buttons - always show for tables */}
                          <div className="flex items-center justify-between p-2 border-b border-gray-200 dark:border-gray-700">
                            <div className="flex items-center gap-1">
                              <button
                                onClick={() => setChartViews(prev => ({ ...prev, [message.id]: "table" }))}
                                className={`px-3 py-1.5 text-xs rounded-md transition-colors ${
                                  currentView === "table"
                                    ? "bg-black dark:bg-white text-white dark:text-gray-900"
                                    : "bg-white dark:bg-gray-700 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
                                }`}
                              >
                                Table
                              </button>
                              {hasNumericData && parsedResult.rows.length > 1 && (
                                <>
                                  <button
                                    onClick={() => setChartViews(prev => ({ ...prev, [message.id]: "bar" }))}
                                    className={`px-3 py-1.5 text-xs rounded-md transition-colors ${
                                      currentView === "bar"
                                        ? "bg-black dark:bg-white text-white dark:text-gray-900"
                                        : "bg-white dark:bg-gray-700 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
                                    }`}
                                  >
                                    Bar
                                  </button>
                                  <button
                                    onClick={() => setChartViews(prev => ({ ...prev, [message.id]: "line" }))}
                                    className={`px-3 py-1.5 text-xs rounded-md transition-colors ${
                                      currentView === "line"
                                        ? "bg-black dark:bg-white text-white dark:text-gray-900"
                                        : "bg-white dark:bg-gray-700 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
                                    }`}
                                  >
                                    Line
                                  </button>
                                  <button
                                    onClick={() => setChartViews(prev => ({ ...prev, [message.id]: "pie" }))}
                                    className={`px-3 py-1.5 text-xs rounded-md transition-colors ${
                                      currentView === "pie"
                                        ? "bg-black dark:bg-white text-white dark:text-gray-900"
                                        : "bg-white dark:bg-gray-700 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
                                    }`}
                                  >
                                    Pie
                                  </button>
                                  <button
                                    onClick={() => setChartViews(prev => ({ ...prev, [message.id]: "area" }))}
                                    className={`px-3 py-1.5 text-xs rounded-md transition-colors ${
                                      currentView === "area"
                                        ? "bg-black dark:bg-white text-white dark:text-gray-900"
                                        : "bg-white dark:bg-gray-700 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
                                    }`}
                                  >
                                    Area
                                  </button>
                                </>
                              )}
                            </div>
                              {/* Expand Button */}
                              <button
                                onClick={() => {
                                  setDataViewerData({
                                    columns: parsedResult.columns,
                                    rows: parsedResult.rows,
                                    chartType: currentView,
                                  });
                                  setDataViewerOpen(message.id);
                                }}
                                className="flex items-center gap-1.5 px-2.5 py-1.5 text-xs text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-200 hover:bg-gray-100 dark:hover:bg-gray-700 rounded-md transition-colors"
                                title="Open in fullscreen"
                              >
                                <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 8V4m0 0h4M4 4l5 5m11-1V4m0 0h-4m4 0l-5 5M4 16v4m0 0h4m-4 0l5-5m11 5l-5-5m5 5v-4m0 4h-4" />
                                </svg>
                                Expand
                              </button>
                          </div>

                          {/* Chart View */}
                          {currentView !== "table" && (
                            <DataChart
                              data={parsedResult.rows}
                              columns={parsedResult.columns}
                              chartType={currentView}
                            />
                          )}

                          {/* Table View - Virtual Scrolling for performance */}
                          {currentView === "table" && (
                            <VirtualTable
                              columns={parsedResult.columns}
                              rows={parsedResult.rows}
                            />
                          )}
                        </div>
                      );
                    })()}
                  </div>
                ) : (
                  <div className="flex justify-end">
                    <div className="max-w-[70%] bg-gradient-to-br from-gray-800 to-gray-900 rounded-2xl px-[18px] py-3.5 shadow-sm">
                      <p className="text-sm text-white leading-relaxed">
                        {message.content}
                      </p>
                    </div>
                  </div>
                )}
              </div>
            ))
          )}
          {/* Show processing indicator when actively sending OR when this conversation has pending request */}
          {(isSending || (selectedConversationId && pendingConversations.has(selectedConversationId) && !isSending)) && (() => {
            // Get pending message info for background processing
            const pendingInfo = selectedConversationId ? pendingMessagesRef.current.get(selectedConversationId) : null;
            const effectivePhase = isSending ? currentPhase : pendingInfo?.phase ?? null;
            const pendingMessage = !isSending && pendingInfo ? pendingInfo.message : null;

            return (
              <>
                {/* Show user's pending message when returning to conversation */}
                {pendingMessage && (
                  <div className="flex justify-end mb-4">
                    <div className="max-w-[70%] bg-gradient-to-br from-gray-800 to-gray-900 rounded-2xl px-[18px] py-3.5 shadow-sm">
                      <p className="text-sm text-white leading-relaxed">
                        {pendingMessage}
                      </p>
                    </div>
                  </div>
                )}
                <div className="w-[70%] max-w-[70%] bg-white dark:bg-gray-800 rounded-2xl p-5 shadow-sm border border-gray-100/80 dark:border-gray-700/80">
                  <p className="font-semibold text-base text-gray-900 dark:text-white mb-4">Erao</p>
                  <div className="flex items-center gap-5">
                    {/* Robot Animation Container */}
                <div className="relative w-20 h-20 flex-shrink-0">
                  <svg viewBox="0 0 300 300" className="w-full h-full overflow-visible">
                    {/* Thought rings orbiting around */}
                    <ellipse
                      cx="150" cy="150" rx="90" ry="30"
                      className="fill-none stroke-black dark:stroke-white opacity-30"
                      strokeWidth="1.5"
                      style={{ transformOrigin: '150px 150px', animation: 'orbit-ring 4s linear infinite' }}
                    />
                    <ellipse
                      cx="150" cy="150" rx="70" ry="25"
                      className="fill-none stroke-black dark:stroke-white opacity-30"
                      strokeWidth="1.5"
                      style={{ transformOrigin: '150px 150px', animation: 'orbit-ring 4s linear infinite reverse', animationDelay: '-2s' }}
                    />

                    {/* Floating particles */}
                    <circle cx="80" cy="100" r="3" className="fill-black dark:fill-white" style={{ animation: 'particle-float 2s ease-in-out infinite' }} />
                    <circle cx="220" cy="110" r="2" className="fill-black dark:fill-white" style={{ animation: 'particle-float 2s ease-in-out infinite', animationDelay: '0.3s' }} />
                    <circle cx="70" cy="180" r="2.5" className="fill-black dark:fill-white" style={{ animation: 'particle-float 2s ease-in-out infinite', animationDelay: '0.6s' }} />
                    <circle cx="230" cy="190" r="2" className="fill-black dark:fill-white" style={{ animation: 'particle-float 2s ease-in-out infinite', animationDelay: '0.9s' }} />
                    <circle cx="150" cy="60" r="3" className="fill-black dark:fill-white" style={{ animation: 'particle-float 2s ease-in-out infinite', animationDelay: '1.2s' }} />
                    <circle cx="150" cy="240" r="2" className="fill-black dark:fill-white" style={{ animation: 'particle-float 2s ease-in-out infinite', animationDelay: '1.5s' }} />

                    {/* Main robot group with float + sway */}
                    <g style={{ animation: 'robot-float 3s ease-in-out infinite' }}>
                      <g style={{ transformOrigin: '150px 150px', animation: 'robot-sway 4s ease-in-out infinite' }}>

                        {/* Body */}
                        <rect
                          x="100" y="100" width="100" height="100" rx="8"
                          className="fill-none stroke-black dark:stroke-white"
                          strokeWidth="3"
                          strokeLinecap="round"
                          strokeLinejoin="round"
                        />

                        {/* Inner frame */}
                        <rect
                          x="110" y="110" width="80" height="80" rx="4"
                          className="fill-none stroke-black dark:stroke-white"
                          strokeWidth="2"
                          strokeLinecap="round"
                          style={{ animation: 'pulse-glow 2s ease-in-out infinite' }}
                        />

                        {/* Thinking eye/core */}
                        <g style={{ animation: 'pulse-glow 2s ease-in-out infinite' }}>
                          <circle
                            cx="150" cy="150" r="25"
                            className="fill-none stroke-black dark:stroke-white"
                            strokeWidth="2"
                            strokeLinecap="round"
                          />

                          {/* Spinning inner elements */}
                          <g style={{ transformOrigin: '150px 150px', animation: 'think-spin 3s linear infinite' }}>
                            <circle cx="150" cy="130" r="4" className="fill-black dark:fill-white" />
                            <circle cx="170" cy="150" r="3" className="fill-black dark:fill-white" />
                            <circle cx="150" cy="170" r="4" className="fill-black dark:fill-white" />
                            <circle cx="130" cy="150" r="3" className="fill-black dark:fill-white" />
                          </g>
                        </g>

                        {/* Antenna */}
                        <line
                          x1="150" y1="100" x2="150" y2="70"
                          className="stroke-black dark:stroke-white"
                          strokeWidth="3"
                          strokeLinecap="round"
                          strokeLinejoin="round"
                        />
                        <circle
                          cx="150" cy="65" r="6"
                          className="fill-black dark:fill-white"
                          style={{ animation: 'antenna-blink 1s ease-in-out infinite' }}
                        />

                        {/* Arms */}
                        <g style={{ transformOrigin: '90px 135px', animation: 'arm-wave-left 2s ease-in-out infinite' }}>
                          <polygon
                            points="100,120 70,140 70,160 100,150"
                            className="fill-none stroke-black dark:stroke-white"
                            strokeWidth="3"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                          />
                          <circle
                            cx="70" cy="150" r="8"
                            className="fill-none stroke-black dark:stroke-white"
                            strokeWidth="1.5"
                            style={{ animation: 'pulse-glow 2s ease-in-out infinite' }}
                          />
                        </g>

                        <g style={{ transformOrigin: '210px 135px', animation: 'arm-wave-right 2s ease-in-out infinite' }}>
                          <polygon
                            points="200,120 230,140 230,160 200,150"
                            className="fill-none stroke-black dark:stroke-white"
                            strokeWidth="3"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                          />
                          <circle
                            cx="230" cy="150" r="8"
                            className="fill-none stroke-black dark:stroke-white"
                            strokeWidth="1.5"
                            style={{ animation: 'pulse-glow 2s ease-in-out infinite' }}
                          />
                        </g>

                        {/* Legs */}
                        <g style={{ animation: 'leg-float 2.5s ease-in-out infinite' }}>
                          <polygon
                            points="120,200 130,240 110,240"
                            className="fill-none stroke-black dark:stroke-white"
                            strokeWidth="3"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                          />
                          <circle
                            cx="120" cy="245" r="5"
                            className="fill-none stroke-black dark:stroke-white"
                            strokeWidth="1.5"
                          />
                        </g>

                        <g style={{ animation: 'leg-float 2.5s ease-in-out infinite', animationDelay: '-1.25s' }}>
                          <polygon
                            points="180,200 190,240 170,240"
                            className="fill-none stroke-black dark:stroke-white"
                            strokeWidth="3"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                          />
                          <circle
                            cx="180" cy="245" r="5"
                            className="fill-none stroke-black dark:stroke-white"
                            strokeWidth="1.5"
                          />
                        </g>

                      </g>
                    </g>
                  </svg>
                </div>

                {/* Phase Text */}
                    <div className="flex flex-col gap-1.5">
                      <span className="text-sm font-medium text-gray-900 dark:text-white tracking-wide">
                        {effectivePhase === "writing"
                          ? "Writing response"
                          : effectivePhase === "executing"
                          ? "Running query"
                          : "Thinking"}
                      </span>
                      <span className="flex gap-1 items-center">
                        <span className="w-1 h-1 bg-black dark:bg-white rounded-full animate-bounce" />
                        <span className="w-1 h-1 bg-black dark:bg-white rounded-full animate-bounce [animation-delay:0.1s]" />
                        <span className="w-1 h-1 bg-black dark:bg-white rounded-full animate-bounce [animation-delay:0.2s]" />
                      </span>
                    </div>
                  </div>
                </div>
              </>
            );
          })()}
          <div ref={messagesEndRef} />
        </div>

        {/* Error Message */}
        {error && (
          <div className="px-8 pb-2">
            <div className="bg-red-50 dark:bg-red-900/20 text-red-600 dark:text-red-400 text-sm px-4 py-2 rounded-lg">
              {error}
            </div>
          </div>
        )}

        {/* Input Area */}
        <div className="bg-gradient-to-t from-gray-50 dark:from-gray-900 to-transparent px-8 pt-4 pb-6 flex justify-center">
          <form
            onSubmit={handleSendMessage}
            className="w-full max-w-[700px] h-[52px] bg-white dark:bg-gray-800 rounded-2xl px-3 pr-2 flex items-center gap-2 shadow-md shadow-gray-200/50 dark:shadow-gray-900/50 border border-gray-100 dark:border-gray-700 focus-within:border-gray-200 dark:focus-within:border-gray-600 focus-within:shadow-lg focus-within:shadow-gray-200/50 dark:focus-within:shadow-gray-900/50 transition-all duration-200"
          >
            {/* File Upload Button */}
            <input
              type="file"
              ref={fileInputRef}
              onChange={handleFileUpload}
              accept=".xlsx,.xls,.docx,.doc,.csv,.xml,.json,.txt"
              className="hidden"
            />
            <button
              type="button"
              onClick={() => fileInputRef.current?.click()}
              disabled={isUploadingFile}
              className="w-9 h-9 rounded-full flex items-center justify-center flex-shrink-0 hover:bg-gray-100 dark:hover:bg-gray-700 active:scale-95 transition-all duration-200 disabled:opacity-50"
              title="Upload file (Excel, Word, CSV)"
            >
              <svg className="w-5 h-5 text-gray-400 dark:text-gray-500 hover:text-gray-600 dark:hover:text-gray-300 transition-colors" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M15.172 7l-6.586 6.586a2 2 0 102.828 2.828l6.414-6.586a4 4 0 00-5.656-5.656l-6.415 6.585a6 6 0 108.486 8.486L20.5 13" />
              </svg>
            </button>

            <input
              type="text"
              value={inputValue}
              onChange={(e) => setInputValue(e.target.value)}
              placeholder={selectedFile ? `Ask about ${selectedFile.originalFileName}...` : "Ask anything about your data..."}
              disabled={isSending}
              className="flex-1 text-sm outline-none border-none focus:outline-none focus:ring-0 placeholder:text-gray-400 dark:placeholder:text-gray-500 disabled:opacity-50 bg-transparent text-gray-900 dark:text-white"
            />
            <button
              type="submit"
              disabled={isSending || !inputValue.trim()}
              className="w-9 h-9 bg-gradient-to-br from-gray-800 to-gray-900 dark:from-gray-200 dark:to-gray-300 rounded-full flex items-center justify-center flex-shrink-0 hover:from-gray-700 hover:to-gray-800 dark:hover:from-gray-100 dark:hover:to-gray-200 hover:scale-105 transition-all duration-200 disabled:opacity-50 disabled:cursor-not-allowed shadow-sm"
            >
              <svg className="w-4 h-4 text-white dark:text-gray-900" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 10l7-7m0 0l7 7m-7-7v18" />
              </svg>
            </button>
          </form>
        </div>
      </main>

      {/* Database Selection Modal */}
      {showDatabaseModal && (
        <DatabaseModal
          databases={databases}
          selectedDatabaseId={selectedDatabaseId}
          onSelect={(id) => {
            setSelectedDatabaseId(id);
            setSelectedFileId(null); // Clear file when database is selected
            // Clear conversation if changing data source
            if (selectedConversationId) {
              setSelectedConversationId(null);
              setMessages([]);
            }
            setShowDatabaseModal(false);
          }}
          onClose={() => setShowDatabaseModal(false)}
          onAddNew={() => {
            setShowDatabaseModal(false);
            setShowAddDatabaseModal(true);
          }}
          onViewSchema={(id) => {
            setSchemaViewDatabaseId(id);
            setShowSchemaModal(true);
            setShowDatabaseModal(false);
          }}
          onDelete={(id, name) => {
            setDeleteConfirm({
              type: 'database',
              id,
              name
            });
          }}
        />
      )}

      {/* Schema Viewer Modal */}
      {showSchemaModal && schemaViewDatabaseId && (
        <SchemaViewerModal
          databaseId={schemaViewDatabaseId}
          databaseName={databases.find(d => d.id === schemaViewDatabaseId)?.name || "Database"}
          onClose={() => {
            setShowSchemaModal(false);
            setSchemaViewDatabaseId(null);
          }}
        />
      )}

      {/* Add Database Modal */}
      {showAddDatabaseModal && (
        <AddDatabaseModal
          onClose={() => setShowAddDatabaseModal(false)}
          onSuccess={(newDb) => {
            setDatabases((prev) => [...prev, newDb]);
            setSelectedDatabaseId(newDb.id);
            setSelectedFileId(null); // Clear file
            setSelectedConversationId(null); // Start fresh
            setMessages([]);
            setShowAddDatabaseModal(false);
          }}
        />
      )}

      {/* Files Modal */}
      {showFilesModal && (
        <FilesModal
          files={files}
          selectedFileId={selectedFileId}
          onSelect={(id) => {
            setSelectedFileId(id || null);
            setSelectedDatabaseId(null); // Clear database when file is selected
            // Clear conversation if changing data source
            if (selectedConversationId) {
              setSelectedConversationId(null);
              setMessages([]);
            }
            setShowFilesModal(false);
          }}
          onClose={() => setShowFilesModal(false)}
          onUpload={() => {
            setShowFilesModal(false);
            fileInputRef.current?.click();
          }}
          onDelete={handleDeleteFile}
          formatFileSize={formatFileSize}
          getFileIcon={getFileIcon}
        />
      )}

      {/* Fullscreen Data Viewer Modal */}
      {dataViewerOpen && dataViewerData && (
        <DataViewerModal
          isOpen={true}
          onClose={() => {
            setDataViewerOpen(null);
            setDataViewerData(null);
          }}
          columns={dataViewerData.columns}
          rows={dataViewerData.rows}
          initialChartType={dataViewerData.chartType}
        />
      )}

      {/* Delete Confirmation Modal */}
      {deleteConfirm && (
        <div className="fixed inset-0 bg-black/50 dark:bg-black/70 flex items-center justify-center z-[60]">
          <div className="bg-white dark:bg-gray-800 rounded-2xl p-6 w-full max-w-sm mx-4 shadow-xl">
            <div className="flex items-center gap-3 mb-4">
              <div className="w-10 h-10 bg-red-100 dark:bg-red-900/30 rounded-full flex items-center justify-center">
                <svg className="w-5 h-5 text-red-600 dark:text-red-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                </svg>
              </div>
              <div>
                <h3 className="font-semibold text-base text-gray-900 dark:text-white">Delete {deleteConfirm.type === 'conversation' ? 'Chat' : 'Connection'}</h3>
                <p className="text-sm text-gray-500 dark:text-gray-400">This action cannot be undone</p>
              </div>
            </div>
            <p className="text-sm text-gray-600 dark:text-gray-300 mb-6">
              Are you sure you want to delete <span className="font-medium text-gray-900 dark:text-white">&quot;{deleteConfirm.name}&quot;</span>?
            </p>
            <div className="flex gap-3">
              <button
                onClick={() => setDeleteConfirm(null)}
                className="flex-1 h-10 bg-gray-100 dark:bg-gray-700 text-gray-700 dark:text-gray-300 rounded-lg text-sm font-medium hover:bg-gray-200 dark:hover:bg-gray-600 transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={() => {
                  if (deleteConfirm.type === 'conversation') {
                    handleDeleteConversation(deleteConfirm.id);
                  } else {
                    handleDeleteDatabase(deleteConfirm.id);
                  }
                }}
                className="flex-1 h-10 bg-red-600 text-white rounded-lg text-sm font-medium hover:bg-red-700 transition-colors"
              >
                Delete
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Search Modal */}
      {showSearchModal && (
        <div
          className="fixed inset-0 bg-black/50 dark:bg-black/70 flex items-start justify-center pt-[15vh] z-[70]"
          onClick={() => {
            setShowSearchModal(false);
            setSearchQuery("");
          }}
        >
          <div
            className="bg-white dark:bg-gray-800 rounded-2xl w-full max-w-lg mx-4 shadow-2xl overflow-hidden"
            onClick={(e) => e.stopPropagation()}
          >
            {/* Search Input */}
            <div className="p-4 border-b border-gray-100 dark:border-gray-700">
              <div className="relative">
                <svg className="absolute left-3 top-1/2 -translate-y-1/2 w-5 h-5 text-gray-400 dark:text-gray-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                </svg>
                <input
                  type="text"
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  placeholder="Search conversations..."
                  className="w-full h-11 bg-gray-50 dark:bg-gray-700 rounded-xl pl-10 pr-4 text-sm outline-none placeholder:text-gray-400 dark:placeholder:text-gray-500 focus:bg-gray-100 dark:focus:bg-gray-600 transition-colors text-gray-900 dark:text-white"
                  autoFocus
                />
                {searchQuery && (
                  <button
                    onClick={() => setSearchQuery("")}
                    className="absolute right-3 top-1/2 -translate-y-1/2 p-1 hover:bg-gray-200 dark:hover:bg-gray-600 rounded-full"
                  >
                    <svg className="w-4 h-4 text-gray-400 dark:text-gray-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                    </svg>
                  </button>
                )}
              </div>
            </div>

            {/* Search Results */}
            <div className="max-h-[50vh] overflow-y-auto custom-scrollbar">
              {conversations.length === 0 ? (
                <div className="p-8 text-center text-gray-400 dark:text-gray-500 text-sm">
                  No conversations yet
                </div>
              ) : (
                (() => {
                  const filtered = conversations.filter((chat) =>
                    searchQuery === "" ||
                    (chat.title || "New Chat").toLowerCase().includes(searchQuery.toLowerCase()) ||
                    (chat.databaseConnectionName || "").toLowerCase().includes(searchQuery.toLowerCase()) ||
                    (chat.fileDocumentName || "").toLowerCase().includes(searchQuery.toLowerCase())
                  );

                  if (filtered.length === 0) {
                    return (
                      <div className="p-8 text-center text-gray-400 dark:text-gray-500 text-sm">
                        No results found for &quot;{searchQuery}&quot;
                      </div>
                    );
                  }

                  return filtered.map((chat) => (
                    <button
                      key={chat.id}
                      onClick={() => {
                        selectConversation(chat.id);
                        setShowSearchModal(false);
                        setSearchQuery("");
                      }}
                      className={`w-full text-left px-4 py-3 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors border-b border-gray-50 dark:border-gray-700 last:border-b-0 ${
                        chat.id === selectedConversationId ? "bg-gray-50 dark:bg-gray-700" : ""
                      }`}
                    >
                      <p className="text-sm font-medium text-gray-900 dark:text-white truncate">
                        {chat.title || "New Chat"}
                      </p>
                      <div className="flex items-center gap-1.5 mt-1 text-xs text-gray-400 dark:text-gray-500">
                        {(chat.databaseConnectionName || chat.fileDocumentName) && (
                          <>
                            <span className="truncate max-w-[150px]">
                              {chat.databaseConnectionName || chat.fileDocumentName}
                            </span>
                            <span>·</span>
                          </>
                        )}
                        <span className="whitespace-nowrap">{formatRelativeTime(chat.updatedAt)}</span>
                      </div>
                    </button>
                  ));
                })()
              )}
            </div>

            {/* Footer hint */}
            <div className="px-4 py-3 bg-gray-50 dark:bg-gray-700/50 border-t border-gray-100 dark:border-gray-700">
              <p className="text-xs text-gray-400 dark:text-gray-500 text-center">
                Press <kbd className="px-1.5 py-0.5 bg-white dark:bg-gray-600 rounded border border-gray-200 dark:border-gray-500 text-gray-500 dark:text-gray-300">Esc</kbd> to close
              </p>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// Database Selection Modal Component
function DatabaseModal({
  databases,
  selectedDatabaseId,
  onSelect,
  onClose,
  onAddNew,
  onViewSchema,
  onDelete,
}: {
  databases: DatabaseConnection[];
  selectedDatabaseId: string | null;
  onSelect: (id: string) => void;
  onClose: () => void;
  onAddNew: () => void;
  onViewSchema: (id: string) => void;
  onDelete: (id: string, name: string) => void;
}) {
  return (
    <div className="fixed inset-0 bg-black/50 dark:bg-black/70 flex items-center justify-center z-50">
      <div className="bg-white dark:bg-gray-800 rounded-2xl p-6 w-full max-w-md">
        <h2 className="text-lg font-semibold mb-4 text-gray-900 dark:text-white">Select Database</h2>
        <div className="flex flex-col gap-2 max-h-[300px] overflow-y-auto custom-scrollbar">
          {databases.length === 0 ? (
            <p className="text-sm text-gray-500 dark:text-gray-400 text-center py-4">
              No databases connected yet
            </p>
          ) : (
            databases.map((db) => (
              <div
                key={db.id}
                className={`p-3 rounded-xl transition-colors ${
                  db.id === selectedDatabaseId
                    ? "bg-gray-900 dark:bg-white text-white dark:text-gray-900"
                    : "bg-gray-100 dark:bg-gray-700 hover:bg-gray-200 dark:hover:bg-gray-600"
                }`}
              >
                <button
                  onClick={() => onSelect(db.id)}
                  className="w-full text-left flex items-center justify-between"
                >
                  <div>
                    <p className="font-medium text-sm">{db.name}</p>
                    <p
                      className={`text-xs ${
                        db.id === selectedDatabaseId
                          ? "text-gray-300 dark:text-gray-600"
                          : "text-gray-500 dark:text-gray-400"
                      }`}
                    >
                      {db.databaseType}
                    </p>
                  </div>
                  {db.lastTestedAt && (
                    <span
                      className={`text-xs ${
                        db.id === selectedDatabaseId
                          ? db.isActive ? "text-green-300 dark:text-green-600" : "text-red-300 dark:text-red-600"
                          : db.isActive ? "text-green-600 dark:text-green-400" : "text-red-500 dark:text-red-400"
                      }`}
                    >
                      {db.isActive ? "Active" : "Inactive"}
                    </span>
                  )}
                </button>
                <div className="flex gap-2 mt-2">
                  <button
                    onClick={(e) => {
                      e.stopPropagation();
                      onViewSchema(db.id);
                    }}
                    className={`flex-1 text-xs py-1.5 rounded-lg flex items-center justify-center gap-1.5 transition-colors ${
                      db.id === selectedDatabaseId
                        ? "bg-white/20 dark:bg-gray-200 hover:bg-white/30 dark:hover:bg-gray-300 text-white dark:text-gray-700"
                        : "bg-white dark:bg-gray-600 hover:bg-gray-100 dark:hover:bg-gray-500 text-gray-600 dark:text-gray-200"
                    }`}
                  >
                    <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 7v10c0 2.21 3.582 4 8 4s8-1.79 8-4V7M4 7c0 2.21 3.582 4 8 4s8-1.79 8-4M4 7c0-2.21 3.582-4 8-4s8 1.79 8 4m0 5c0 2.21-3.582 4-8 4s-8-1.79-8-4" />
                    </svg>
                    Schema
                  </button>
                  <button
                    onClick={(e) => {
                      e.stopPropagation();
                      onDelete(db.id, db.name);
                    }}
                    className={`text-xs py-1.5 px-3 rounded-lg flex items-center justify-center gap-1.5 transition-colors ${
                      db.id === selectedDatabaseId
                        ? "bg-red-500/30 dark:bg-red-100 hover:bg-red-500/50 dark:hover:bg-red-200 text-white dark:text-red-600"
                        : "bg-red-50 dark:bg-red-900/30 hover:bg-red-100 dark:hover:bg-red-900/50 text-red-600 dark:text-red-400"
                    }`}
                  >
                    <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                    </svg>
                    Delete
                  </button>
                </div>
              </div>
            ))
          )}
        </div>
        <div className="flex gap-3 mt-4">
          <button
            onClick={onAddNew}
            className="flex-1 h-10 bg-gray-100 dark:bg-gray-700 text-gray-900 dark:text-white rounded-lg text-sm font-medium hover:bg-gray-200 dark:hover:bg-gray-600 transition-colors"
          >
            Add New
          </button>
          <button
            onClick={onClose}
            className="flex-1 h-10 bg-gray-900 dark:bg-white text-white dark:text-gray-900 rounded-lg text-sm font-medium hover:bg-gray-800 dark:hover:bg-gray-100 transition-colors"
          >
            Done
          </button>
        </div>
      </div>
    </div>
  );
}

// Add Database Modal Component
function AddDatabaseModal({
  onClose,
  onSuccess,
}: {
  onClose: () => void;
  onSuccess: (db: DatabaseConnection) => void;
}) {
  // Database type mapping with icons
  const databaseTypes = [
    {
      value: 0,
      label: "PostgreSQL",
      port: 5432,
      placeholder: "db.example.com",
      color: "#336791",
      description: "Advanced open-source relational database"
    },
    {
      value: 1,
      label: "MySQL",
      port: 3306,
      placeholder: "mysql.example.com",
      color: "#00758F",
      description: "Popular open-source database"
    },
    {
      value: 2,
      label: "SQL Server",
      port: 1433,
      placeholder: "sqlserver.example.com",
      color: "#CC2927",
      description: "Microsoft enterprise database"
    },
    {
      value: 3,
      label: "MongoDB",
      port: 27017,
      placeholder: "mongo.example.com",
      color: "#47A248",
      description: "NoSQL document database"
    },
  ];

  // Database logo paths
  const databaseLogos: Record<number, string> = {
    0: "/db-logos/postgresql.png",
    1: "/db-logos/mysql.png",
    2: "/db-logos/sql-server.png",
    3: "/db-logos/mongodb.png",
  };


  const [formData, setFormData] = useState<CreateDatabaseConnectionPayload>({
    name: "",
    databaseType: 0,
    host: "",
    port: 5432,
    databaseName: "",
    username: "",
    password: "",
  });
  const [portInput, setPortInput] = useState("5432");
  const [isLoading, setIsLoading] = useState(false);
  const [isTesting, setIsTesting] = useState(false);
  const [error, setError] = useState("");
  const [testResult, setTestResult] = useState<"success" | "error" | null>(null);
  const [testPassed, setTestPassed] = useState(false);

  const currentDbType = databaseTypes.find(t => t.value === formData.databaseType);

  const handlePortChange = (value: string) => {
    setPortInput(value);
    const parsed = parseInt(value, 10);
    if (!isNaN(parsed)) {
      setFormData({ ...formData, port: parsed });
    }
    setTestPassed(false);
    setTestResult(null);
  };

  const handleDatabaseTypeSelect = (typeValue: number) => {
    const dbType = databaseTypes.find((t) => t.value === typeValue);
    if (dbType) {
      setFormData({ ...formData, databaseType: typeValue, port: dbType.port });
      setPortInput(dbType.port.toString());
    }
    setTestPassed(false);
    setTestResult(null);
  };

  const handleFieldChange = (field: string, value: string) => {
    setFormData({ ...formData, [field]: value });
    setTestPassed(false);
    setTestResult(null);
  };

  const handleTest = async () => {
    setIsTesting(true);
    setTestResult(null);
    setError("");
    setTestPassed(false);

    try {
      const createResponse = await api.createDatabase(formData);
      if (createResponse.success) {
        const testResponse = await api.testDatabase(createResponse.data.id);
        if (testResponse.data) {
          setTestResult("success");
          setTestPassed(true);
          setFormData(prev => ({ ...prev, id: createResponse.data.id } as typeof prev & { id: string }));
        } else {
          await api.deleteDatabase(createResponse.data.id);
          setTestResult("error");
          setError("Could not connect. Please verify your credentials and try again.");
        }
      }
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message);
      } else {
        setError("Connection test failed");
      }
      setTestResult("error");
    } finally {
      setIsTesting(false);
    }
  };

  const handleSave = async () => {
    if (!testPassed) return;
    setIsLoading(true);

    try {
      const response = await api.getDatabases();
      if (response.success) {
        const newDb = response.data.find(db => db.name === formData.name);
        if (newDb) {
          onSuccess(newDb);
        } else {
          const lastDb = response.data[response.data.length - 1];
          if (lastDb) {
            onSuccess(lastDb);
          }
        }
      }
    } catch {
      setError("Failed to save connection");
    } finally {
      setIsLoading(false);
    }
  };

  return (
    <div className="fixed inset-0 bg-black/60 dark:bg-black/80 backdrop-blur-sm flex items-center justify-center z-50 p-4">
      <div className="bg-white dark:bg-gray-800 rounded-2xl w-full max-w-xl max-h-[90vh] overflow-y-auto custom-scrollbar shadow-2xl">
        {/* Header */}
        <div className="px-6 py-5 border-b border-gray-100 dark:border-gray-700 flex items-center justify-between">
          <div>
            <h2 className="text-xl font-semibold text-gray-900 dark:text-white">Connect Database</h2>
            <p className="text-sm text-gray-500 dark:text-gray-400 mt-0.5">Add a new database connection to start querying</p>
          </div>
          <button
            onClick={onClose}
            className="w-8 h-8 rounded-lg flex items-center justify-center text-gray-400 dark:text-gray-500 hover:text-gray-600 dark:hover:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors"
          >
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <div className="px-6 py-5">
          {/* Database Type Selection */}
          <div className="mb-6">
            <label className="text-sm font-medium text-gray-700 dark:text-gray-300 mb-3 block">Select Database Type</label>
            <div className="grid grid-cols-4 gap-3">
              {databaseTypes.map((type) => (
                <button
                  key={type.value}
                  type="button"
                  onClick={() => handleDatabaseTypeSelect(type.value)}
                  className={`relative flex flex-col items-center p-4 rounded-xl border-2 transition-all ${
                    formData.databaseType === type.value
                      ? "border-gray-900 dark:border-white bg-gray-50 dark:bg-gray-700 shadow-sm"
                      : "border-gray-200 dark:border-gray-600 hover:border-gray-300 dark:hover:border-gray-500 hover:bg-gray-50 dark:hover:bg-gray-700"
                  }`}
                >
                  {formData.databaseType === type.value && (
                    <div className="absolute top-2 right-2 w-4 h-4 bg-gray-900 dark:bg-white rounded-full flex items-center justify-center">
                      <svg className="w-2.5 h-2.5 text-white dark:text-gray-900" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={3} d="M5 13l4 4L19 7" />
                      </svg>
                    </div>
                  )}
                  <div className="mb-2">
                    <img src={databaseLogos[type.value]} alt={type.label} className="w-9 h-9 object-contain" />
                  </div>
                  <span className="text-xs font-medium text-gray-700 dark:text-gray-300">{type.label}</span>
                </button>
              ))}
            </div>
            {currentDbType && (
              <p className="text-xs text-gray-500 dark:text-gray-400 mt-2 text-center">{currentDbType.description}</p>
            )}
          </div>

          {/* Connection Details */}
          <div className="space-y-4">
            {/* Connection Name */}
            <div>
              <label className="text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5 block">Connection Name</label>
              <input
                type="text"
                value={formData.name}
                onChange={(e) => handleFieldChange("name", e.target.value)}
                placeholder="My Production Database"
                className="w-full h-11 bg-gray-50 dark:bg-gray-700 border border-gray-200 dark:border-gray-600 rounded-xl px-4 text-sm outline-none transition-all focus:border-gray-400 dark:focus:border-gray-500 focus:bg-white dark:focus:bg-gray-600 text-gray-900 dark:text-white placeholder:text-gray-400 dark:placeholder:text-gray-500"
              />
            </div>

            {/* Host & Port */}
            <div className="grid grid-cols-3 gap-3">
              <div className="col-span-2">
                <label className="text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5 block">Host</label>
                <input
                  type="text"
                  value={formData.host}
                  onChange={(e) => handleFieldChange("host", e.target.value)}
                  placeholder={currentDbType?.placeholder || "db.example.com"}
                  className="w-full h-11 bg-gray-50 dark:bg-gray-700 border border-gray-200 dark:border-gray-600 rounded-xl px-4 text-sm outline-none transition-all focus:border-gray-400 dark:focus:border-gray-500 focus:bg-white dark:focus:bg-gray-600 text-gray-900 dark:text-white placeholder:text-gray-400 dark:placeholder:text-gray-500"
                />
              </div>
              <div>
                <label className="text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5 block">Port</label>
                <input
                  type="text"
                  inputMode="numeric"
                  value={portInput}
                  onChange={(e) => handlePortChange(e.target.value)}
                  className="w-full h-11 bg-gray-50 dark:bg-gray-700 border border-gray-200 dark:border-gray-600 rounded-xl px-4 text-sm outline-none transition-all focus:border-gray-400 dark:focus:border-gray-500 focus:bg-white dark:focus:bg-gray-600 text-gray-900 dark:text-white"
                />
              </div>
            </div>

            {/* Database Name */}
            <div>
              <label className="text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5 block">Database Name</label>
              <input
                type="text"
                value={formData.databaseName}
                onChange={(e) => handleFieldChange("databaseName", e.target.value)}
                placeholder="production_db"
                className="w-full h-11 bg-gray-50 dark:bg-gray-700 border border-gray-200 dark:border-gray-600 rounded-xl px-4 text-sm outline-none transition-all focus:border-gray-400 dark:focus:border-gray-500 focus:bg-white dark:focus:bg-gray-600 text-gray-900 dark:text-white placeholder:text-gray-400 dark:placeholder:text-gray-500"
              />
            </div>

            {/* Credentials */}
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5 block">Username</label>
                <input
                  type="text"
                  value={formData.username}
                  onChange={(e) => handleFieldChange("username", e.target.value)}
                  placeholder="admin"
                  className="w-full h-11 bg-gray-50 dark:bg-gray-700 border border-gray-200 dark:border-gray-600 rounded-xl px-4 text-sm outline-none transition-all focus:border-gray-400 dark:focus:border-gray-500 focus:bg-white dark:focus:bg-gray-600 text-gray-900 dark:text-white placeholder:text-gray-400 dark:placeholder:text-gray-500"
                />
              </div>
              <div>
                <label className="text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5 block">Password</label>
                <input
                  type="password"
                  value={formData.password}
                  onChange={(e) => handleFieldChange("password", e.target.value)}
                  placeholder="••••••••"
                  className="w-full h-11 bg-gray-50 dark:bg-gray-700 border border-gray-200 dark:border-gray-600 rounded-xl px-4 text-sm outline-none transition-all focus:border-gray-400 dark:focus:border-gray-500 focus:bg-white dark:focus:bg-gray-600 text-gray-900 dark:text-white placeholder:text-gray-400 dark:placeholder:text-gray-500"
                />
              </div>
            </div>
          </div>

          {/* Feedback */}
          {(testResult || error) && (
            <div className={`mt-5 p-4 rounded-xl flex items-center gap-3 ${
              testResult === "success"
                ? "bg-green-50 dark:bg-green-900/30 border border-green-200 dark:border-green-800"
                : "bg-red-50 dark:bg-red-900/30 border border-red-200 dark:border-red-800"
            }`}>
              {testResult === "success" ? (
                <>
                  <div className="w-10 h-10 bg-green-100 dark:bg-green-900/50 rounded-full flex items-center justify-center flex-shrink-0">
                    <svg className="w-5 h-5 text-green-600 dark:text-green-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                    </svg>
                  </div>
                  <div>
                    <p className="text-sm font-medium text-green-800 dark:text-green-300">Connection successful!</p>
                    <p className="text-xs text-green-600 dark:text-green-400">Ready to connect to your {currentDbType?.label} database</p>
                  </div>
                </>
              ) : (
                <>
                  <div className="w-10 h-10 bg-red-100 dark:bg-red-900/50 rounded-full flex items-center justify-center flex-shrink-0">
                    <svg className="w-5 h-5 text-red-600 dark:text-red-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                    </svg>
                  </div>
                  <div>
                    <p className="text-sm font-medium text-red-800 dark:text-red-300">Connection failed</p>
                    <p className="text-xs text-red-600 dark:text-red-400">{error || "Please check your credentials"}</p>
                  </div>
                </>
              )}
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="px-6 py-4 bg-gray-50 dark:bg-gray-900 border-t border-gray-100 dark:border-gray-700 rounded-b-2xl flex gap-3">
          <button
            type="button"
            onClick={onClose}
            className="px-5 h-11 text-gray-600 dark:text-gray-400 text-sm font-medium hover:text-gray-800 dark:hover:text-gray-200 transition-colors"
          >
            Cancel
          </button>
          <div className="flex-1" />
          <button
            type="button"
            onClick={handleTest}
            disabled={isTesting || !formData.host || !formData.databaseName || !formData.username || !formData.name}
            className="px-6 h-11 bg-white dark:bg-gray-700 border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 rounded-xl text-sm font-medium hover:bg-gray-50 dark:hover:bg-gray-600 transition-colors disabled:opacity-50 disabled:cursor-not-allowed flex items-center justify-center gap-2"
          >
            {isTesting ? (
              <>
                <div className="w-4 h-4 border-2 border-gray-300 dark:border-gray-500 border-t-gray-600 dark:border-t-gray-300 rounded-full animate-spin" />
                Testing...
              </>
            ) : (
              "Test Connection"
            )}
          </button>
          <button
            type="button"
            onClick={handleSave}
            disabled={!testPassed || isLoading}
            className={`px-6 h-11 rounded-xl text-sm font-medium transition-all flex items-center justify-center gap-2 ${
              testPassed
                ? "bg-gray-900 dark:bg-white text-white dark:text-gray-900 hover:bg-gray-800 dark:hover:bg-gray-100"
                : "bg-gray-200 dark:bg-gray-700 text-gray-400 dark:text-gray-500 cursor-not-allowed"
            }`}
          >
            {isLoading ? (
              <>
                <div className="w-4 h-4 border-2 border-gray-400 border-t-white rounded-full animate-spin" />
                Saving...
              </>
            ) : (
              "Save Connection"
            )}
          </button>
        </div>
      </div>
    </div>
  );
}

// Files Modal Component
function FilesModal({
  files,
  selectedFileId,
  onSelect,
  onClose,
  onUpload,
  onDelete,
  formatFileSize,
  getFileIcon,
}: {
  files: FileDocument[];
  selectedFileId: string | null;
  onSelect: (id: string) => void;
  onClose: () => void;
  onUpload: () => void;
  onDelete: (id: string) => void;
  formatFileSize: (bytes: number) => string;
  getFileIcon: (fileType: string) => React.ReactNode;
}) {
  return (
    <div className="fixed inset-0 bg-black/50 dark:bg-black/70 flex items-center justify-center z-50">
      <div className="bg-white dark:bg-gray-800 rounded-2xl p-6 w-full max-w-lg">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold text-gray-900 dark:text-white">Your Files</h2>
          <button
            onClick={onUpload}
            className="flex items-center gap-2 px-3 py-1.5 bg-gray-900 dark:bg-white text-white dark:text-gray-900 text-sm rounded-lg hover:bg-gray-800 dark:hover:bg-gray-100 transition-colors"
          >
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
            </svg>
            Upload
          </button>
        </div>

        <div className="flex flex-col gap-2 max-h-[400px] overflow-y-auto custom-scrollbar">
          {files.length === 0 ? (
            <div className="text-center py-8">
              <svg className="w-12 h-12 text-gray-300 dark:text-gray-600 mx-auto mb-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
              </svg>
              <p className="text-sm text-gray-500 dark:text-gray-400 mb-2">No files uploaded yet</p>
              <p className="text-xs text-gray-400 dark:text-gray-500">
                Upload Excel, Word, CSV, or other files to analyze
              </p>
            </div>
          ) : (
            files.map((file) => (
              <div
                key={file.id}
                className={`p-3 rounded-xl flex items-center gap-3 transition-colors cursor-pointer ${
                  file.id === selectedFileId
                    ? "bg-gray-900 dark:bg-white text-white dark:text-gray-900"
                    : "bg-gray-100 dark:bg-gray-700 hover:bg-gray-200 dark:hover:bg-gray-600"
                }`}
                onClick={() => onSelect(file.id)}
              >
                <div className={`w-10 h-10 rounded-lg flex items-center justify-center ${
                  file.id === selectedFileId ? "bg-white/20 dark:bg-gray-900/20" : "bg-white dark:bg-gray-600"
                }`}>
                  {getFileIcon(file.fileType)}
                </div>
                <div className="flex-1 min-w-0">
                  <p className="font-medium text-sm truncate">
                    {file.originalFileName}
                  </p>
                  <div className={`flex items-center gap-2 text-xs ${
                    file.id === selectedFileId ? "text-gray-300 dark:text-gray-600" : "text-gray-500 dark:text-gray-400"
                  }`}>
                    <span>{file.fileType}</span>
                    <span>•</span>
                    <span>{formatFileSize(file.fileSizeBytes)}</span>
                    {file.rowCount && (
                      <>
                        <span>•</span>
                        <span>{file.rowCount.toLocaleString()} rows</span>
                      </>
                    )}
                  </div>
                </div>
                <div className="flex items-center gap-2">
                  {file.status === "Completed" ? (
                    <span className={`text-xs ${
                      file.id === selectedFileId ? "text-green-300 dark:text-green-600" : "text-green-600 dark:text-green-400"
                    }`}>
                      Ready
                    </span>
                  ) : file.status === "Processing" ? (
                    <span className={`text-xs ${
                      file.id === selectedFileId ? "text-yellow-300 dark:text-yellow-600" : "text-yellow-600 dark:text-yellow-400"
                    }`}>
                      Processing...
                    </span>
                  ) : file.status === "Failed" ? (
                    <span className={`text-xs ${
                      file.id === selectedFileId ? "text-red-300 dark:text-red-600" : "text-red-600 dark:text-red-400"
                    }`}>
                      Failed
                    </span>
                  ) : null}
                  <button
                    onClick={(e) => {
                      e.stopPropagation();
                      onDelete(file.id);
                    }}
                    className={`p-1 rounded hover:bg-black/10 dark:hover:bg-white/10 transition-colors ${
                      file.id === selectedFileId ? "text-white/70 dark:text-gray-600 hover:text-white dark:hover:text-gray-900" : "text-gray-400 dark:text-gray-500 hover:text-gray-600 dark:hover:text-gray-300"
                    }`}
                  >
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                    </svg>
                  </button>
                </div>
              </div>
            ))
          )}
        </div>

        <div className="flex gap-3 mt-4">
          <button
            onClick={() => onSelect("")}
            className="flex-1 h-10 bg-gray-100 dark:bg-gray-700 text-gray-900 dark:text-white rounded-lg text-sm font-medium hover:bg-gray-200 dark:hover:bg-gray-600 transition-colors"
          >
            Clear Selection
          </button>
          <button
            onClick={onClose}
            className="flex-1 h-10 bg-gray-900 dark:bg-white text-white dark:text-gray-900 rounded-lg text-sm font-medium hover:bg-gray-800 dark:hover:bg-gray-100 transition-colors"
          >
            Done
          </button>
        </div>
      </div>
    </div>
  );
}

// Schema Viewer Modal Component
function SchemaViewerModal({
  databaseId,
  databaseName,
  onClose,
}: {
  databaseId: string;
  databaseName: string;
  onClose: () => void;
}) {
  const [schema, setSchema] = useState<SchemaResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedTables, setExpandedTables] = useState<Set<string>>(new Set());
  const [searchQuery, setSearchQuery] = useState("");
  const [viewMode, setViewMode] = useState<"list" | "diagram">("diagram");
  const [isFullscreen, setIsFullscreen] = useState(false);

  useEffect(() => {
    const fetchSchema = async () => {
      try {
        setLoading(true);
        const response = await api.getDatabaseSchema(databaseId);
        if (response.success && response.data) {
          setSchema(response.data);
          // Auto-expand first 3 tables
          const tables = response.data.tables || [];
          const firstTables = tables.slice(0, 3).map(t => t.name);
          setExpandedTables(new Set(firstTables));
        }
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to load schema");
      } finally {
        setLoading(false);
      }
    };
    fetchSchema();
  }, [databaseId]);

  const toggleTable = (tableName: string) => {
    setExpandedTables(prev => {
      const next = new Set(prev);
      if (next.has(tableName)) {
        next.delete(tableName);
      } else {
        next.add(tableName);
      }
      return next;
    });
  };

  const filteredTables = (schema?.tables || []).filter(table =>
    table.name.toLowerCase().includes(searchQuery.toLowerCase()) ||
    table.columns.some(col => col.name.toLowerCase().includes(searchQuery.toLowerCase()))
  );

  return (
    <div className={`fixed inset-0 bg-black/50 dark:bg-black/70 flex items-center justify-center z-50 ${isFullscreen ? 'p-0' : 'p-4'}`}>
      <div className={`bg-white dark:bg-gray-800 flex flex-col transition-all duration-300 ${
        isFullscreen
          ? 'w-full h-full rounded-none'
          : 'rounded-2xl w-full max-w-6xl max-h-[90vh]'
      }`}>
        {/* Header */}
        <div className={`border-b border-gray-100 dark:border-gray-700 ${isFullscreen ? 'p-4' : 'p-6'}`}>
          <div className="flex items-center justify-between mb-4">
            <div>
              <h2 className="text-lg font-semibold text-gray-900 dark:text-white">{databaseName}</h2>
              {schema && (
                <p className="text-sm text-gray-500 dark:text-gray-400">
                  {schema.databaseType} • {(schema.tables || []).length} tables
                </p>
              )}
            </div>
            <div className="flex items-center gap-3">
              {/* View Mode Toggle */}
              <div className="flex bg-gray-100 dark:bg-gray-700 rounded-lg p-1">
                <button
                  onClick={() => setViewMode("diagram")}
                  className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${
                    viewMode === "diagram" ? "bg-white dark:bg-gray-600 text-gray-900 dark:text-white shadow-sm" : "text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-300"
                  }`}
                >
                  Diagram
                </button>
                <button
                  onClick={() => setViewMode("list")}
                  className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${
                    viewMode === "list" ? "bg-white dark:bg-gray-600 text-gray-900 dark:text-white shadow-sm" : "text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-300"
                  }`}
                >
                  List
                </button>
              </div>
              {/* Fullscreen Toggle */}
              <button
                onClick={() => setIsFullscreen(!isFullscreen)}
                className="w-8 h-8 flex items-center justify-center rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors"
                title={isFullscreen ? "Exit fullscreen" : "Open sandbox mode"}
              >
                {isFullscreen ? (
                  <svg className="w-5 h-5 text-gray-500 dark:text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 9V4.5M9 9H4.5M9 9L3.75 3.75M9 15v4.5M9 15H4.5M9 15l-5.25 5.25M15 9h4.5M15 9V4.5M15 9l5.25-5.25M15 15h4.5M15 15v4.5m0-4.5l5.25 5.25" />
                  </svg>
                ) : (
                  <svg className="w-5 h-5 text-gray-500 dark:text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.75 3.75v4.5m0-4.5h4.5m-4.5 0L9 9M3.75 20.25v-4.5m0 4.5h4.5m-4.5 0L9 15M20.25 3.75h-4.5m4.5 0v4.5m0-4.5L15 9m5.25 11.25h-4.5m4.5 0v-4.5m0 4.5L15 15" />
                  </svg>
                )}
              </button>
              <button
                onClick={onClose}
                className="w-8 h-8 flex items-center justify-center rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors"
              >
                <svg className="w-5 h-5 text-gray-500 dark:text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>
          </div>
          {viewMode === "list" && (
            <div className="relative">
              <input
                type="text"
                placeholder="Search tables and columns..."
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                className="w-full h-10 bg-gray-50 dark:bg-gray-700 border border-gray-200 dark:border-gray-600 rounded-xl pl-10 pr-4 text-sm outline-none focus:border-gray-400 dark:focus:border-gray-500 focus:bg-white dark:focus:bg-gray-600 transition-all text-gray-900 dark:text-white placeholder:text-gray-400 dark:placeholder:text-gray-500"
              />
              <svg className="w-4 h-4 text-gray-400 dark:text-gray-500 absolute left-3 top-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
              </svg>
            </div>
          )}
        </div>

        {/* Content */}
        <div className={`flex-1 ${viewMode === "diagram" ? "overflow-hidden" : "overflow-y-auto custom-scrollbar"}`}>
          {loading ? (
            <div className="flex items-center justify-center py-12">
              <div className="w-8 h-8 border-2 border-gray-200 dark:border-gray-600 border-t-gray-800 dark:border-t-white rounded-full animate-spin" />
            </div>
          ) : error ? (
            <div className="text-center py-12">
              <p className="text-red-500 dark:text-red-400">{error}</p>
            </div>
          ) : (schema?.tables || []).length === 0 ? (
            <div className="text-center py-12 text-gray-500 dark:text-gray-400">
              No tables found
            </div>
          ) : viewMode === "diagram" ? (
            <ERDiagramView tables={schema?.tables || []} />
          ) : (
            <div className="p-6">
              {filteredTables.length === 0 ? (
                <div className="text-center py-12 text-gray-500 dark:text-gray-400">
                  No tables match your search
                </div>
              ) : (
                <div className="space-y-3">
                  {filteredTables.map((table) => (
                    <TableCard
                      key={table.name}
                      table={table}
                      isExpanded={expandedTables.has(table.name)}
                      onToggle={() => toggleTable(table.name)}
                      searchQuery={searchQuery}
                    />
                  ))}
                </div>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

// ERD Diagram View Component
function ERDiagramView({ tables }: { tables: TableSchema[] }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [positions, setPositions] = useState<Record<string, { x: number; y: number }>>({});
  const [dragging, setDragging] = useState<string | null>(null);
  const [dragOffset, setDragOffset] = useState({ x: 0, y: 0 });
  const [scale, setScale] = useState(0.8);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const [isPanning, setIsPanning] = useState(false);
  const [panStart, setPanStart] = useState({ x: 0, y: 0 });

  // Initialize table positions in a grid layout
  useEffect(() => {
    const cols = Math.ceil(Math.sqrt(tables.length));
    const initialPositions: Record<string, { x: number; y: number }> = {};
    tables.forEach((table, index) => {
      const col = index % cols;
      const row = Math.floor(index / cols);
      initialPositions[table.name] = {
        x: 80 + col * 300,
        y: 80 + row * 280,
      };
    });
    setPositions(initialPositions);
    // Auto-fit scale based on table count
    if (tables.length > 10) {
      setScale(0.6);
    } else if (tables.length > 5) {
      setScale(0.75);
    } else {
      setScale(0.9);
    }
  }, [tables]);

  // Build FK relationships
  const relationships = tables.flatMap(table =>
    table.foreignKeys.map(fk => ({
      fromTable: table.name,
      fromColumn: fk.column,
      toTable: fk.referencedTable,
      toColumn: fk.referencedColumn,
    }))
  );

  const handleMouseDown = (tableName: string, e: React.MouseEvent) => {
    e.stopPropagation();
    const rect = (e.target as HTMLElement).closest('.erd-table')?.getBoundingClientRect();
    if (rect) {
      setDragging(tableName);
      setDragOffset({
        x: e.clientX - rect.left,
        y: e.clientY - rect.top,
      });
    }
  };

  const handleCanvasMouseDown = (e: React.MouseEvent) => {
    // Only pan if clicking on the canvas background (not on a table)
    if ((e.target as HTMLElement).closest('.erd-table')) return;
    setIsPanning(true);
    setPanStart({ x: e.clientX - pan.x, y: e.clientY - pan.y });
  };

  const handleMouseMove = (e: React.MouseEvent) => {
    if (dragging && containerRef.current) {
      const containerRect = containerRef.current.getBoundingClientRect();
      setPositions(prev => ({
        ...prev,
        [dragging]: {
          x: (e.clientX - containerRect.left - dragOffset.x - pan.x) / scale,
          y: (e.clientY - containerRect.top - dragOffset.y - pan.y) / scale,
        },
      }));
    } else if (isPanning) {
      setPan({
        x: e.clientX - panStart.x,
        y: e.clientY - panStart.y,
      });
    }
  };

  const handleMouseUp = () => {
    setDragging(null);
    setIsPanning(false);
  };

  // Handle wheel/touchpad events
  const handleWheel = (e: React.WheelEvent) => {
    // Pinch-to-zoom (ctrlKey is true for pinch gestures on touchpad)
    if (e.ctrlKey) {
      e.preventDefault();
      const rect = containerRef.current?.getBoundingClientRect();
      if (!rect) return;

      const mouseX = e.clientX - rect.left;
      const mouseY = e.clientY - rect.top;

      // Slower zoom speed for smoother touchpad experience
      const zoomSpeed = 0.008;
      const delta = -e.deltaY * zoomSpeed;
      const newScale = Math.min(Math.max(scale + delta, 0.3), 2);
      const scaleChange = newScale / scale;

      // Zoom towards mouse position
      setPan(prev => ({
        x: mouseX - (mouseX - prev.x) * scaleChange,
        y: mouseY - (mouseY - prev.y) * scaleChange,
      }));
      setScale(newScale);
    } else {
      // Regular two-finger scroll = pan
      e.preventDefault();
      setPan(prev => ({
        x: prev.x - e.deltaX,
        y: prev.y - e.deltaY,
      }));
    }
  };

  // Zoom towards center
  const zoomIn = () => {
    if (!containerRef.current) return;
    const rect = containerRef.current.getBoundingClientRect();
    const centerX = rect.width / 2;
    const centerY = rect.height / 2;

    const newScale = Math.min(scale + 0.1, 2);
    const scaleChange = newScale / scale;

    setPan(prev => ({
      x: centerX - (centerX - prev.x) * scaleChange,
      y: centerY - (centerY - prev.y) * scaleChange,
    }));
    setScale(newScale);
  };

  const zoomOut = () => {
    if (!containerRef.current) return;
    const rect = containerRef.current.getBoundingClientRect();
    const centerX = rect.width / 2;
    const centerY = rect.height / 2;

    const newScale = Math.max(scale - 0.1, 0.3);
    const scaleChange = newScale / scale;

    setPan(prev => ({
      x: centerX - (centerX - prev.x) * scaleChange,
      y: centerY - (centerY - prev.y) * scaleChange,
    }));
    setScale(newScale);
  };

  const getColumnYOffset = (table: TableSchema, columnName: string) => {
    const headerHeight = 36;
    const columnHeight = 28;
    const colIndex = table.columns.findIndex(c => c.name === columnName);
    return headerHeight + (colIndex + 0.5) * columnHeight;
  };

  // Check for dark mode
  const [isDark, setIsDark] = useState(false);
  useEffect(() => {
    const checkDarkMode = () => {
      setIsDark(document.documentElement.classList.contains("dark"));
    };
    checkDarkMode();
    const observer = new MutationObserver(checkDarkMode);
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["class"],
    });
    return () => observer.disconnect();
  }, []);

  return (
    <div
      ref={containerRef}
      className={`relative w-full h-full overflow-hidden bg-gradient-to-br from-gray-50 to-gray-100 dark:from-gray-900 dark:to-gray-800 ${
        isPanning ? 'cursor-grabbing' : dragging ? 'cursor-grabbing' : 'cursor-grab'
      }`}
      onMouseDown={handleCanvasMouseDown}
      onMouseMove={handleMouseMove}
      onMouseUp={handleMouseUp}
      onMouseLeave={handleMouseUp}
      onWheel={handleWheel}
      style={{ minHeight: "500px" }}
    >
      {/* Grid pattern background */}
      <div
        className="absolute inset-0 pointer-events-none opacity-30"
        style={{
          backgroundImage: `radial-gradient(circle, ${isDark ? '#4b5563' : '#cbd5e1'} 1px, transparent 1px)`,
          backgroundSize: `${20 * scale}px ${20 * scale}px`,
          backgroundPosition: `${pan.x}px ${pan.y}px`,
        }}
      />

      {/* Zoom Controls */}
      <div className="absolute top-4 right-4 z-10 flex gap-2 bg-white/80 dark:bg-gray-800/80 backdrop-blur-sm rounded-xl p-2 shadow-lg border border-gray-200 dark:border-gray-700">
        <button
          onClick={zoomIn}
          className="w-8 h-8 bg-white dark:bg-gray-700 rounded-lg shadow-sm border border-gray-200 dark:border-gray-600 flex items-center justify-center hover:bg-gray-50 dark:hover:bg-gray-600 transition-colors text-gray-700 dark:text-gray-200"
          title="Zoom in"
        >
          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
          </svg>
        </button>
        <button
          onClick={zoomOut}
          className="w-8 h-8 bg-white dark:bg-gray-700 rounded-lg shadow-sm border border-gray-200 dark:border-gray-600 flex items-center justify-center hover:bg-gray-50 dark:hover:bg-gray-600 transition-colors text-gray-700 dark:text-gray-200"
          title="Zoom out"
        >
          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M20 12H4" />
          </svg>
        </button>
        <div className="w-px bg-gray-200 dark:bg-gray-600" />
        <span className="flex items-center px-2 text-xs text-gray-500 dark:text-gray-400 font-medium min-w-[50px] justify-center">
          {Math.round(scale * 100)}%
        </span>
        <div className="w-px bg-gray-200 dark:bg-gray-600" />
        <button
          onClick={() => { setScale(0.8); setPan({ x: 0, y: 0 }); }}
          className="px-3 h-8 bg-white dark:bg-gray-700 rounded-lg shadow-sm border border-gray-200 dark:border-gray-600 flex items-center justify-center hover:bg-gray-50 dark:hover:bg-gray-600 text-xs font-medium transition-colors text-gray-700 dark:text-gray-200"
          title="Reset view"
        >
          Fit
        </button>
      </div>

      {/* Instructions hint */}
      <div className="absolute top-4 left-4 z-10 text-xs text-gray-500 dark:text-gray-400 bg-white/80 dark:bg-gray-800/80 backdrop-blur-sm rounded-lg px-3 py-2 shadow-sm border border-gray-200 dark:border-gray-700">
        <span className="font-medium">Tip:</span> Drag tables to arrange • Scroll to pan • Pinch to zoom
      </div>

      {/* Canvas */}
      <div
        className="absolute"
        style={{
          transform: `translate(${pan.x}px, ${pan.y}px) scale(${scale})`,
          transformOrigin: "0 0",
          left: 0,
          top: 0,
          width: "6000px",
          height: "4000px",
        }}
      >
        {/* SVG for relationship lines */}
        <svg
          className="absolute pointer-events-none"
          style={{
            left: "-1000px",
            top: "-1000px",
            width: "8000px",
            height: "6000px",
            overflow: "visible"
          }}
        >
          <defs>
            <marker
              id="arrowhead"
              markerWidth="10"
              markerHeight="7"
              refX="9"
              refY="3.5"
              orient="auto"
            >
              <polygon points="0 0, 10 3.5, 0 7" fill="#6366f1" />
            </marker>
          </defs>
          {relationships.map((rel, idx) => {
            const fromPos = positions[rel.fromTable];
            const toPos = positions[rel.toTable];
            const fromTable = tables.find(t => t.name === rel.fromTable);
            const toTable = tables.find(t => t.name === rel.toTable);

            if (!fromPos || !toPos || !fromTable || !toTable) return null;

            // SVG offset compensation
            const svgOffset = 1000;
            const tableWidth = 240;
            const fromY = fromPos.y + getColumnYOffset(fromTable, rel.fromColumn) + svgOffset;
            const toY = toPos.y + getColumnYOffset(toTable, rel.toColumn) + svgOffset;

            // Determine which side to connect from
            const fromRight = fromPos.x + tableWidth + svgOffset;
            const fromLeft = fromPos.x + svgOffset;
            const toLeft = toPos.x + svgOffset;
            const toRight = toPos.x + tableWidth + svgOffset;

            let startX: number, endX: number;
            if (fromRight < toLeft) {
              // from is to the left of to
              startX = fromRight;
              endX = toLeft;
            } else if (fromLeft > toRight) {
              // from is to the right of to
              startX = fromLeft;
              endX = toRight;
            } else {
              // overlapping horizontally, use right side
              startX = fromRight;
              endX = toRight;
            }

            const midX = (startX + endX) / 2;
            const path = `M ${startX} ${fromY} C ${midX} ${fromY}, ${midX} ${toY}, ${endX} ${toY}`;

            return (
              <g key={idx}>
                <path
                  d={path}
                  fill="none"
                  stroke="#6366f1"
                  strokeWidth="2"
                  markerEnd="url(#arrowhead)"
                  className="opacity-60"
                />
                {/* FK indicator dot */}
                <circle cx={startX} cy={fromY} r="4" fill="#6366f1" />
              </g>
            );
          })}
        </svg>

        {/* Table Cards */}
        {tables.map(table => {
          const pos = positions[table.name] || { x: 0, y: 0 };
          const pkColumns = new Set(table.primaryKeys.flatMap(pk => pk.columns));
          const fkColumns = new Set(table.foreignKeys.map(fk => fk.column));

          return (
            <div
              key={table.name}
              className="erd-table absolute bg-white dark:bg-gray-800 rounded-xl shadow-lg border border-gray-200 dark:border-gray-700 overflow-hidden select-none"
              style={{
                left: pos.x,
                top: pos.y,
                width: 240,
                cursor: dragging === table.name ? "grabbing" : "grab",
              }}
              onMouseDown={(e) => handleMouseDown(table.name, e)}
            >
              {/* Table Header */}
              <div className="bg-gray-900 dark:bg-gray-700 text-white px-3 py-2 flex items-center gap-2">
                <svg className="w-4 h-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 10h18M3 14h18m-9-4v8m-7 0h14a2 2 0 002-2V8a2 2 0 00-2-2H5a2 2 0 00-2 2v8a2 2 0 002 2z" />
                </svg>
                <span className="font-medium text-sm truncate">{table.name}</span>
                {table.rowCount !== null && (
                  <span className="ml-auto text-xs text-gray-400">{table.rowCount.toLocaleString()}</span>
                )}
              </div>
              {/* Columns */}
              <div
                className="divide-y divide-gray-100 dark:divide-gray-700 max-h-48 overflow-y-auto erd-scrollbar bg-white dark:bg-gray-800"
                onWheel={(e) => e.stopPropagation()}
              >
                {table.columns.map(column => (
                  <div
                    key={column.name}
                    className="px-3 py-1.5 flex items-center gap-2 text-xs hover:bg-gray-50 dark:hover:bg-gray-700"
                  >
                    <div className="w-4 flex justify-center">
                      {pkColumns.has(column.name) ? (
                        <svg className="w-3.5 h-3.5 text-amber-500" fill="currentColor" viewBox="0 0 24 24">
                          <path d="M12.65 10A5.99 5.99 0 007 6c-3.31 0-6 2.69-6 6s2.69 6 6 6a5.99 5.99 0 005.65-4H17v4h4v-4h2v-4H12.65zM7 14c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z"/>
                        </svg>
                      ) : fkColumns.has(column.name) ? (
                        <svg className="w-3.5 h-3.5 text-indigo-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13.828 10.172a4 4 0 00-5.656 0l-4 4a4 4 0 105.656 5.656l1.102-1.101m-.758-4.899a4 4 0 005.656 0l4-4a4 4 0 00-5.656-5.656l-1.1 1.1" />
                        </svg>
                      ) : (
                        <div className="w-1.5 h-1.5 rounded-full bg-gray-300 dark:bg-gray-500" />
                      )}
                    </div>
                    <span className="font-medium text-gray-700 dark:text-gray-200 truncate flex-1">{column.name}</span>
                    <span className="text-gray-400 font-mono text-[10px]">{column.dataType}</span>
                  </div>
                ))}
              </div>
            </div>
          );
        })}
      </div>

      {/* Legend */}
      <div className="absolute bottom-4 left-4 bg-white/90 dark:bg-gray-800/90 backdrop-blur-sm rounded-lg shadow-sm border border-gray-200 dark:border-gray-700 px-3 py-2">
        <div className="flex gap-4 text-xs">
          <div className="flex items-center gap-1.5">
            <svg className="w-3.5 h-3.5 text-amber-500" fill="currentColor" viewBox="0 0 24 24">
              <path d="M12.65 10A5.99 5.99 0 007 6c-3.31 0-6 2.69-6 6s2.69 6 6 6a5.99 5.99 0 005.65-4H17v4h4v-4h2v-4H12.65zM7 14c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z"/>
            </svg>
            <span className="text-gray-600 dark:text-gray-300">Primary Key</span>
          </div>
          <div className="flex items-center gap-1.5">
            <svg className="w-3.5 h-3.5 text-indigo-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13.828 10.172a4 4 0 00-5.656 0l-4 4a4 4 0 105.656 5.656l1.102-1.101m-.758-4.899a4 4 0 005.656 0l4-4a4 4 0 00-5.656-5.656l-1.1 1.1" />
            </svg>
            <span className="text-gray-600 dark:text-gray-300">Foreign Key</span>
          </div>
          <div className="flex items-center gap-1.5">
            <div className="w-6 h-0.5 bg-indigo-500 rounded" />
            <span className="text-gray-600 dark:text-gray-300">Relationship</span>
          </div>
        </div>
      </div>
    </div>
  );
}

// Table Card Component for Schema Viewer
function TableCard({
  table,
  isExpanded,
  onToggle,
  searchQuery,
}: {
  table: TableSchema;
  isExpanded: boolean;
  onToggle: () => void;
  searchQuery: string;
}) {
  const pkColumns = new Set(table.primaryKeys.flatMap(pk => pk.columns));
  const fkColumns = new Set(table.foreignKeys.map(fk => fk.column));

  const highlightMatch = (text: string) => {
    if (!searchQuery) return text;
    const regex = new RegExp(`(${searchQuery})`, 'gi');
    const parts = text.split(regex);
    return parts.map((part, i) =>
      regex.test(part) ? <mark key={i} className="bg-yellow-200 dark:bg-yellow-500/30 dark:text-yellow-200 rounded px-0.5">{part}</mark> : part
    );
  };

  return (
    <div className="border border-gray-200 dark:border-gray-700 rounded-xl overflow-hidden bg-white dark:bg-gray-800">
      {/* Table Header */}
      <button
        onClick={onToggle}
        className="w-full px-4 py-3 bg-gray-50 dark:bg-gray-800 flex items-center justify-between hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors"
      >
        <div className="flex items-center gap-3">
          <svg
            className={`w-4 h-4 text-gray-400 transition-transform ${isExpanded ? 'rotate-90' : ''}`}
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
          </svg>
          <div className="flex items-center gap-2">
            <svg className="w-4 h-4 text-gray-500 dark:text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 10h18M3 14h18m-9-4v8m-7 0h14a2 2 0 002-2V8a2 2 0 00-2-2H5a2 2 0 00-2 2v8a2 2 0 002 2z" />
            </svg>
            <span className="font-medium text-sm text-gray-900 dark:text-white">{highlightMatch(table.name)}</span>
          </div>
        </div>
        <div className="flex items-center gap-3 text-xs text-gray-500 dark:text-gray-400">
          <span>{table.columns.length} columns</span>
          {table.rowCount !== null && (
            <span>{table.rowCount.toLocaleString()} rows</span>
          )}
          {table.primaryKeys.length > 0 && (
            <span className="px-1.5 py-0.5 bg-amber-100 dark:bg-amber-900/30 text-amber-700 dark:text-amber-400 rounded">
              {table.primaryKeys.length} PK
            </span>
          )}
          {table.foreignKeys.length > 0 && (
            <span className="px-1.5 py-0.5 bg-blue-100 dark:bg-blue-900/30 text-blue-700 dark:text-blue-400 rounded">
              {table.foreignKeys.length} FK
            </span>
          )}
          {table.indexes.length > 0 && (
            <span className="px-1.5 py-0.5 bg-purple-100 dark:bg-purple-900/30 text-purple-700 dark:text-purple-400 rounded">
              {table.indexes.length} IDX
            </span>
          )}
        </div>
      </button>

      {/* Table Content */}
      {isExpanded && (
        <div className="border-t border-gray-200 dark:border-gray-700">
          {/* Columns */}
          <div className="divide-y divide-gray-100 dark:divide-gray-700">
            {table.columns.map((column) => (
              <div
                key={column.name}
                className="px-4 py-2.5 flex items-center justify-between hover:bg-gray-50 dark:hover:bg-gray-700"
              >
                <div className="flex items-center gap-3">
                  <div className="w-5 flex justify-center">
                    {pkColumns.has(column.name) ? (
                      <svg className="w-4 h-4 text-amber-500" fill="currentColor" viewBox="0 0 24 24">
                        <path d="M12.65 10A5.99 5.99 0 007 6c-3.31 0-6 2.69-6 6s2.69 6 6 6a5.99 5.99 0 005.65-4H17v4h4v-4h2v-4H12.65zM7 14c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z"/>
                      </svg>
                    ) : fkColumns.has(column.name) ? (
                      <svg className="w-4 h-4 text-blue-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13.828 10.172a4 4 0 00-5.656 0l-4 4a4 4 0 105.656 5.656l1.102-1.101m-.758-4.899a4 4 0 005.656 0l4-4a4 4 0 00-5.656-5.656l-1.1 1.1" />
                      </svg>
                    ) : (
                      <div className="w-1.5 h-1.5 rounded-full bg-gray-300 dark:bg-gray-500" />
                    )}
                  </div>
                  <span className="text-sm font-medium text-gray-900 dark:text-white">{highlightMatch(column.name)}</span>
                </div>
                <div className="flex items-center gap-2">
                  <span className="text-xs text-gray-500 dark:text-gray-400 font-mono bg-gray-100 dark:bg-gray-700 px-2 py-0.5 rounded">
                    {column.dataType}
                    {column.maxLength && `(${column.maxLength})`}
                  </span>
                  {column.isIdentity && (
                    <span className="text-xs text-green-600 dark:text-green-400 bg-green-50 dark:bg-green-900/30 px-1.5 py-0.5 rounded">AUTO</span>
                  )}
                  {!column.isNullable && (
                    <span className="text-xs text-red-600 dark:text-red-400 bg-red-50 dark:bg-red-900/30 px-1.5 py-0.5 rounded">NOT NULL</span>
                  )}
                </div>
              </div>
            ))}
          </div>

          {/* Foreign Keys Section */}
          {table.foreignKeys.length > 0 && (
            <div className="border-t border-gray-200 dark:border-gray-700 bg-blue-50/50 dark:bg-blue-900/20 px-4 py-3">
              <p className="text-xs font-medium text-blue-700 dark:text-blue-400 mb-2">Foreign Keys</p>
              <div className="space-y-1.5">
                {table.foreignKeys.map((fk, idx) => (
                  <div key={idx} className="flex items-center gap-2 text-xs text-gray-600 dark:text-gray-300">
                    <span className="font-mono bg-white dark:bg-gray-800 px-1.5 py-0.5 rounded border border-blue-200 dark:border-blue-700">
                      {fk.column}
                    </span>
                    <svg className="w-3 h-3 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 7l5 5m0 0l-5 5m5-5H6" />
                    </svg>
                    <span className="font-mono bg-white dark:bg-gray-800 px-1.5 py-0.5 rounded border border-blue-200 dark:border-blue-700">
                      {fk.referencedTable}.{fk.referencedColumn}
                    </span>
                    {(fk.onDelete || fk.onUpdate) && (
                      <span className="text-gray-400">
                        ({fk.onDelete && `ON DELETE ${fk.onDelete}`}
                        {fk.onDelete && fk.onUpdate && ', '}
                        {fk.onUpdate && `ON UPDATE ${fk.onUpdate}`})
                      </span>
                    )}
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Indexes Section */}
          {table.indexes.length > 0 && (
            <div className="border-t border-gray-200 dark:border-gray-700 bg-purple-50/50 dark:bg-purple-900/20 px-4 py-3">
              <p className="text-xs font-medium text-purple-700 dark:text-purple-400 mb-2">Indexes</p>
              <div className="space-y-1.5">
                {table.indexes.map((index, idx) => (
                  <div key={idx} className="flex items-center gap-2 text-xs text-gray-600 dark:text-gray-300">
                    <span className="font-medium">{index.name}</span>
                    <span className="text-gray-400">on</span>
                    <span className="font-mono bg-white dark:bg-gray-800 px-1.5 py-0.5 rounded border border-purple-200 dark:border-purple-700">
                      {index.columns.join(', ')}
                    </span>
                    {index.isUnique && (
                      <span className="text-purple-600 dark:text-purple-400 bg-purple-100 dark:bg-purple-900/30 px-1.5 py-0.5 rounded">UNIQUE</span>
                    )}
                    {index.isClustered && (
                      <span className="text-purple-600 dark:text-purple-400 bg-purple-100 dark:bg-purple-900/30 px-1.5 py-0.5 rounded">CLUSTERED</span>
                    )}
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
