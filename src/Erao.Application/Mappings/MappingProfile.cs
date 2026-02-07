using AutoMapper;
using Erao.Core.DTOs;
using Erao.Core.DTOs.Chat;
using Erao.Core.DTOs.Database;
using Erao.Core.DTOs.Usage;
using Erao.Core.Entities;

namespace Erao.Application.Mappings;

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        // User mappings
        CreateMap<User, UserDto>();

        // Database connection mappings
        CreateMap<DatabaseConnection, DatabaseConnectionDto>();

        // Conversation mappings
        CreateMap<Conversation, ConversationDto>()
            .ForMember(dest => dest.DatabaseConnectionName,
                opt => opt.MapFrom(src => src.DatabaseConnection != null ? src.DatabaseConnection.Name : null))
            .ForMember(dest => dest.FileDocumentName,
                opt => opt.MapFrom(src => src.FileDocument != null ? src.FileDocument.OriginalFileName : null))
            .ForMember(dest => dest.LastMessageAt,
                opt => opt.MapFrom(src => src.Messages.Any() ? src.Messages.Max(m => m.CreatedAt) : (DateTime?)null))
            .ForMember(dest => dest.Messages,
                opt => opt.MapFrom(src => src.Messages.OrderBy(m => m.CreatedAt)));

        // Message mappings
        CreateMap<Message, MessageDto>();

        // Usage mappings
        CreateMap<UsageLog, UsageLogDto>()
            .ForMember(dest => dest.DatabaseConnectionName,
                opt => opt.MapFrom(src => src.DatabaseConnection != null ? src.DatabaseConnection.Name : null))
            .ForMember(dest => dest.QueriesCount,
                opt => opt.MapFrom(src => 1)); // Each log entry represents 1 query
    }
}
