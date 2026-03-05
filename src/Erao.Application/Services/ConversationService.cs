using AutoMapper;
using Erao.Core.DTOs.Chat;
using Erao.Core.Entities;
using Erao.Core.Enums;
using Erao.Core.Interfaces;

namespace Erao.Application.Services;

public interface IConversationService
{
    Task<IEnumerable<ConversationDto>> GetUserConversationsAsync(Guid userId);
    Task<ConversationDto?> GetByIdAsync(Guid id, Guid userId);
    Task<ConversationDto> CreateAsync(Guid userId, CreateConversationRequest request);
    Task<ConversationDto> UpdateAsync(Guid id, Guid userId, UpdateConversationRequest request);
    Task DeleteAsync(Guid id, Guid userId);
    Task<ConversationDto> GetOrCreateBySourceAsync(Guid userId, DataSourceType sourceType, Guid sourceId);
}

public class ConversationService : IConversationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;

    public ConversationService(IUnitOfWork unitOfWork, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _mapper = mapper;
    }

    public async Task<IEnumerable<ConversationDto>> GetUserConversationsAsync(Guid userId)
    {
        var conversations = await _unitOfWork.Conversations.GetByUserIdAsync(userId);
        return _mapper.Map<IEnumerable<ConversationDto>>(conversations);
    }

    public async Task<ConversationDto?> GetByIdAsync(Guid id, Guid userId)
    {
        var conversation = await _unitOfWork.Conversations.GetWithMessagesAsync(id);
        if (conversation == null || conversation.UserId != userId)
        {
            return null;
        }
        return _mapper.Map<ConversationDto>(conversation);
    }

    public async Task<ConversationDto> CreateAsync(Guid userId, CreateConversationRequest request)
    {
        // Validate database connection if provided
        if (request.DatabaseConnectionId.HasValue)
        {
            var dbConnection = await _unitOfWork.DatabaseConnections.GetByIdAsync(request.DatabaseConnectionId.Value);
            if (dbConnection == null || dbConnection.UserId != userId)
            {
                throw new InvalidOperationException("Database connection not found");
            }
        }

        // Validate file document if provided
        if (request.FileDocumentId.HasValue)
        {
            var fileDoc = await _unitOfWork.FileDocuments.GetByIdAsync(request.FileDocumentId.Value);
            if (fileDoc == null || fileDoc.UserId != userId)
            {
                throw new InvalidOperationException("File document not found");
            }
        }

        // Validate app connector if provided
        if (request.AppConnectorId.HasValue)
        {
            var connector = await _unitOfWork.AppConnectors.GetByIdAsync(request.AppConnectorId.Value);
            if (connector == null || connector.UserId != userId)
            {
                throw new InvalidOperationException("App connector not found");
            }
        }

        var conversation = new Conversation
        {
            UserId = userId,
            Title = request.Title ?? "New Chat",
            DatabaseConnectionId = request.DatabaseConnectionId,
            FileDocumentId = request.FileDocumentId,
            AppConnectorId = request.AppConnectorId
        };

        await _unitOfWork.Conversations.AddAsync(conversation);
        await _unitOfWork.SaveChangesAsync();

        // Reload conversation to get navigation properties
        var savedConversation = await _unitOfWork.Conversations.GetWithMessagesAsync(conversation.Id);
        return _mapper.Map<ConversationDto>(savedConversation);
    }

    public async Task<ConversationDto> UpdateAsync(Guid id, Guid userId, UpdateConversationRequest request)
    {
        var conversation = await _unitOfWork.Conversations.GetByIdAsync(id);
        if (conversation == null || conversation.UserId != userId)
        {
            throw new InvalidOperationException("Conversation not found");
        }

        if (!string.IsNullOrEmpty(request.Title))
        {
            conversation.Title = request.Title;
        }

        if (request.CustomInstructions != null)
        {
            conversation.CustomInstructions = request.CustomInstructions.Length > 2000
                ? request.CustomInstructions[..2000]
                : request.CustomInstructions;
        }

        if (request.ContextSummary != null)
        {
            conversation.ContextSummary = request.ContextSummary;
        }

        await _unitOfWork.Conversations.UpdateAsync(conversation);
        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<ConversationDto>(conversation);
    }

    public async Task DeleteAsync(Guid id, Guid userId)
    {
        var conversation = await _unitOfWork.Conversations.GetByIdAsync(id);
        if (conversation == null || conversation.UserId != userId)
        {
            throw new InvalidOperationException("Conversation not found");
        }

        await _unitOfWork.Conversations.DeleteAsync(conversation);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<ConversationDto> GetOrCreateBySourceAsync(Guid userId, DataSourceType sourceType, Guid sourceId)
    {
        // Try to find existing conversation for this source
        var existing = await _unitOfWork.Conversations.FindBySourceAsync(userId, sourceType, sourceId);
        if (existing != null)
        {
            return _mapper.Map<ConversationDto>(existing);
        }

        // Validate source ownership and get name for title
        string sourceName;
        switch (sourceType)
        {
            case DataSourceType.Database:
                var db = await _unitOfWork.DatabaseConnections.GetByIdAsync(sourceId);
                if (db == null || db.UserId != userId)
                    throw new InvalidOperationException("Database connection not found");
                sourceName = db.Name;
                break;
            case DataSourceType.File:
                var file = await _unitOfWork.FileDocuments.GetByIdAsync(sourceId);
                if (file == null || file.UserId != userId)
                    throw new InvalidOperationException("File document not found");
                sourceName = file.OriginalFileName;
                break;
            case DataSourceType.Connector:
                var connector = await _unitOfWork.AppConnectors.GetByIdAsync(sourceId);
                if (connector == null || connector.UserId != userId)
                    throw new InvalidOperationException("App connector not found");
                sourceName = connector.Name;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sourceType));
        }

        // Create new conversation
        var conversation = new Conversation
        {
            UserId = userId,
            Title = $"Chat - {sourceName}",
            DatabaseConnectionId = sourceType == DataSourceType.Database ? sourceId : null,
            FileDocumentId = sourceType == DataSourceType.File ? sourceId : null,
            AppConnectorId = sourceType == DataSourceType.Connector ? sourceId : null,
        };

        await _unitOfWork.Conversations.AddAsync(conversation);
        await _unitOfWork.SaveChangesAsync();

        // Reload with navigation properties
        var saved = await _unitOfWork.Conversations.GetWithMessagesAsync(conversation.Id);
        return _mapper.Map<ConversationDto>(saved);
    }
}
