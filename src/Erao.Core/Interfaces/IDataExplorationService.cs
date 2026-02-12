using Erao.Core.DTOs.DataExploration;
using Erao.Core.DTOs.SmartFeatures;

namespace Erao.Core.Interfaces;

public interface IDataExplorationService
{
    // Database Preview & Stats
    Task<PreviewResultDto> GetDatabaseTablePreviewAsync(Guid databaseId, Guid userId, string tableName, int limit = 50);
    Task<ColumnStatsDto> GetDatabaseColumnStatsAsync(Guid databaseId, Guid userId, string tableName, string columnName);

    // File Preview & Stats
    Task<PreviewResultDto> GetFilePreviewAsync(Guid fileId, Guid userId, int limit = 50);
    Task<ColumnStatsDto> GetFileColumnStatsAsync(Guid fileId, Guid userId, string columnName);

    // Smart Features - Suggestions
    Task<List<SuggestedQueryDto>> GetDatabaseSuggestionsAsync(Guid databaseId, Guid userId, string? tableName = null);
    Task<List<SuggestedQueryDto>> GetFileSuggestionsAsync(Guid fileId, Guid userId);

    // Smart Features - Insights
    Task<List<InsightDto>> GetDatabaseInsightsAsync(Guid databaseId, Guid userId, string tableName);
    Task<List<InsightDto>> GetFileInsightsAsync(Guid fileId, Guid userId);

    // AI-Powered Visualization Recommendation
    Task<VisualizationRecommendationDto> AnalyzeForVisualizationAsync(AnalyzeVisualizationRequest request);
}
