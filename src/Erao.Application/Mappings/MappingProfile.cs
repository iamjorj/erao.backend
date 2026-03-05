using System.Text.Json;
using AutoMapper;
using Erao.Core.DTOs;
using Erao.Core.DTOs.Chat;
using Erao.Core.DTOs.Connector;
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
            .ForMember(dest => dest.AppConnectorName,
                opt => opt.MapFrom(src => src.AppConnector != null ? src.AppConnector.Name : null))
            .ForMember(dest => dest.LastMessageAt,
                opt => opt.MapFrom(src => src.Messages.Any() ? src.Messages.Max(m => m.CreatedAt) : (DateTime?)null))
            .ForMember(dest => dest.HasContextSummary,
                opt => opt.MapFrom(src => !string.IsNullOrEmpty(src.ContextSummary)))
            .ForMember(dest => dest.LastContextMetadata,
                opt => opt.MapFrom(src => string.IsNullOrEmpty(src.LastContextMetadataJson)
                    ? null
                    : JsonSerializer.Deserialize<ContextMetadata>(src.LastContextMetadataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })))
            .ForMember(dest => dest.Messages,
                opt => opt.MapFrom(src => src.Messages.OrderBy(m => m.CreatedAt)));

        // AppConnector mappings
        CreateMap<AppConnector, AppConnectorDto>()
            .ForMember(dest => dest.ConnectorType,
                opt => opt.MapFrom(src => (int)src.ConnectorType))
            .ForMember(dest => dest.SyncStatus,
                opt => opt.MapFrom(src => (int)src.SyncStatus))
            .ForMember(dest => dest.HasSyncedData,
                opt => opt.MapFrom(src => src.SyncStatus == Erao.Core.Enums.ConnectorSyncStatus.Completed
                    && !string.IsNullOrEmpty(src.ParquetStoragePaths)))
            .ForMember(dest => dest.TableRowCounts,
                opt => opt.MapFrom(src => string.IsNullOrEmpty(src.TableRowCounts)
                    ? null
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(src.TableRowCounts, (System.Text.Json.JsonSerializerOptions?)null)));

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
