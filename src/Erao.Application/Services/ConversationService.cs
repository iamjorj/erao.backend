using AutoMapper;
using Erao.Core.DTOs.Chat;
using Erao.Core.Entities;
using Erao.Core.Interfaces;

namespace Erao.Application.Services;

public interface IConversationService
{
    Task<IEnumerable<ConversationDto>> GetUserConversationsAsync(Guid userId);
    Task<ConversationDto?> GetByIdAsync(Guid id, Guid userId);
    Task<ConversationDto> CreateAsync(Guid userId, CreateConversationRequest request);
    Task<ConversationDto> UpdateAsync(Guid id, Guid userId, UpdateConversationRequest request);
    Task DeleteAsync(Guid id, Guid userId);
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

        var conversation = new Conversation
        {
            UserId = userId,
            Title = request.Title ?? "New Chat",
            DatabaseConnectionId = request.DatabaseConnectionId,
            FileDocumentId = request.FileDocumentId
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
}
