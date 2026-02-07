using AutoMapper;
using Erao.Core.DTOs.Database;
using Erao.Core.Entities;
using Erao.Core.Helpers;
using Erao.Core.Interfaces;

namespace Erao.Application.Services;

public interface IDatabaseConnectionService
{
    Task<IEnumerable<DatabaseConnectionDto>> GetUserConnectionsAsync(Guid userId);
    Task<DatabaseConnectionDto?> GetByIdAsync(Guid id, Guid userId);
    Task<DatabaseConnectionDto> CreateAsync(Guid userId, CreateDatabaseConnectionRequest request);
    Task<DatabaseConnectionDto> UpdateAsync(Guid id, Guid userId, UpdateDatabaseConnectionRequest request);
    Task DeleteAsync(Guid id, Guid userId);
    Task<bool> TestConnectionAsync(Guid id, Guid userId);
    Task<SchemaResponse> GetSchemaAsync(Guid id, Guid userId);
}

public class DatabaseConnectionService : IDatabaseConnectionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryptionService;
    private readonly IDatabaseQueryService _databaseQueryService;
    private readonly IMapper _mapper;

    public DatabaseConnectionService(
        IUnitOfWork unitOfWork,
        IEncryptionService encryptionService,
        IDatabaseQueryService databaseQueryService,
        IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _encryptionService = encryptionService;
        _databaseQueryService = databaseQueryService;
        _mapper = mapper;
    }

    public async Task<IEnumerable<DatabaseConnectionDto>> GetUserConnectionsAsync(Guid userId)
    {
        var connections = await _unitOfWork.DatabaseConnections.GetByUserIdAsync(userId);
        return _mapper.Map<IEnumerable<DatabaseConnectionDto>>(connections);
    }

    public async Task<DatabaseConnectionDto?> GetByIdAsync(Guid id, Guid userId)
    {
        var connection = await _unitOfWork.DatabaseConnections.GetByIdAsync(id);
        if (connection == null || connection.UserId != userId)
        {
            return null;
        }
        return _mapper.Map<DatabaseConnectionDto>(connection);
    }

    public async Task<DatabaseConnectionDto> CreateAsync(Guid userId, CreateDatabaseConnectionRequest request)
    {
        // Check database connection limit based on subscription tier
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        var existingConnections = await _unitOfWork.DatabaseConnections.GetByUserIdAsync(userId);
        var connectionCount = existingConnections.Count();
        var connectionLimit = SubscriptionLimits.GetDatabaseConnectionLimit(user.SubscriptionTier);

        // -1 means unlimited
        if (connectionLimit != -1 && connectionCount >= connectionLimit)
        {
            var tierName = SubscriptionLimits.GetDisplayName(user.SubscriptionTier);
            throw new InvalidOperationException(
                $"Database connection limit reached. Your {tierName} plan allows {connectionLimit} database connection(s). Please upgrade to add more.");
        }

        var connection = new DatabaseConnection
        {
            UserId = userId,
            Name = request.Name,
            DatabaseType = request.DatabaseType,
            EncryptedHost = _encryptionService.Encrypt(request.Host),
            EncryptedPort = _encryptionService.Encrypt(request.Port.ToString()),
            EncryptedDatabaseName = _encryptionService.Encrypt(request.DatabaseName),
            EncryptedUsername = _encryptionService.Encrypt(request.Username),
            EncryptedPassword = _encryptionService.Encrypt(request.Password),
            IsActive = true
        };

        await _unitOfWork.DatabaseConnections.AddAsync(connection);
        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<DatabaseConnectionDto>(connection);
    }

    public async Task<DatabaseConnectionDto> UpdateAsync(Guid id, Guid userId, UpdateDatabaseConnectionRequest request)
    {
        var connection = await _unitOfWork.DatabaseConnections.GetByIdAsync(id);
        if (connection == null || connection.UserId != userId)
        {
            throw new InvalidOperationException("Database connection not found");
        }

        if (!string.IsNullOrEmpty(request.Name))
            connection.Name = request.Name;
        if (request.DatabaseType.HasValue)
            connection.DatabaseType = request.DatabaseType.Value;
        if (!string.IsNullOrEmpty(request.Host))
            connection.EncryptedHost = _encryptionService.Encrypt(request.Host);
        if (request.Port.HasValue)
            connection.EncryptedPort = _encryptionService.Encrypt(request.Port.Value.ToString());
        if (!string.IsNullOrEmpty(request.DatabaseName))
            connection.EncryptedDatabaseName = _encryptionService.Encrypt(request.DatabaseName);
        if (!string.IsNullOrEmpty(request.Username))
            connection.EncryptedUsername = _encryptionService.Encrypt(request.Username);
        if (!string.IsNullOrEmpty(request.Password))
            connection.EncryptedPassword = _encryptionService.Encrypt(request.Password);

        connection.SchemaCache = null; // Invalidate schema cache on update

        await _unitOfWork.DatabaseConnections.UpdateAsync(connection);
        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<DatabaseConnectionDto>(connection);
    }

    public async Task DeleteAsync(Guid id, Guid userId)
    {
        var connection = await _unitOfWork.DatabaseConnections.GetByIdAsync(id);
        if (connection == null || connection.UserId != userId)
        {
            throw new InvalidOperationException("Database connection not found");
        }

        await _unitOfWork.DatabaseConnections.DeleteAsync(connection);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<bool> TestConnectionAsync(Guid id, Guid userId)
    {
        var connection = await _unitOfWork.DatabaseConnections.GetByIdAsync(id);
        if (connection == null || connection.UserId != userId)
        {
            throw new InvalidOperationException("Database connection not found");
        }

        var host = _encryptionService.Decrypt(connection.EncryptedHost);
        var port = int.Parse(_encryptionService.Decrypt(connection.EncryptedPort));
        var database = _encryptionService.Decrypt(connection.EncryptedDatabaseName);
        var username = _encryptionService.Decrypt(connection.EncryptedUsername);
        var password = _encryptionService.Decrypt(connection.EncryptedPassword);

        var success = await _databaseQueryService.TestConnectionAsync(
            connection.DatabaseType, host, port, database, username, password);

        connection.LastTestedAt = DateTime.UtcNow;
        connection.IsActive = success;

        await _unitOfWork.DatabaseConnections.UpdateAsync(connection);
        await _unitOfWork.SaveChangesAsync();

        return success;
    }

    public async Task<SchemaResponse> GetSchemaAsync(Guid id, Guid userId)
    {
        var connection = await _unitOfWork.DatabaseConnections.GetByIdAsync(id);
        if (connection == null || connection.UserId != userId)
        {
            throw new InvalidOperationException("Database connection not found");
        }

        var host = _encryptionService.Decrypt(connection.EncryptedHost);
        var port = int.Parse(_encryptionService.Decrypt(connection.EncryptedPort));
        var database = _encryptionService.Decrypt(connection.EncryptedDatabaseName);
        var username = _encryptionService.Decrypt(connection.EncryptedUsername);
        var password = _encryptionService.Decrypt(connection.EncryptedPassword);

        // Get raw schema for backward compatibility
        var rawSchema = await _databaseQueryService.GetSchemaAsync(
            connection.DatabaseType, host, port, database, username, password);

        // Get structured schema
        var tables = await _databaseQueryService.GetStructuredSchemaAsync(
            connection.DatabaseType, host, port, database, username, password);

        connection.SchemaCache = rawSchema;
        await _unitOfWork.DatabaseConnections.UpdateAsync(connection);
        await _unitOfWork.SaveChangesAsync();

        return new SchemaResponse
        {
            DatabaseName = database,
            DatabaseType = connection.DatabaseType.ToString(),
            Tables = tables,
            RawSchema = rawSchema,
            CachedAt = DateTime.UtcNow
        };
    }
}
