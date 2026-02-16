using Erao.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;

namespace Erao.Infrastructure.Services;

public class MinioService : IMinioService
{
    private readonly IMinioClient _minioClient;
    private readonly ILogger<MinioService> _logger;
    private readonly string _bucketName;
    private bool _bucketVerified;

    public MinioService(IConfiguration configuration, ILogger<MinioService> logger)
    {
        _logger = logger;

        var endpoint = configuration["Minio:Endpoint"] ?? "localhost:9000";
        var accessKey = configuration["Minio:AccessKey"] ?? "minioadmin";
        var secretKey = configuration["Minio:SecretKey"] ?? "minioadmin";
        var useSSL = configuration.GetValue<bool>("Minio:UseSSL", false);
        var region = configuration["Minio:Region"] ?? "";
        _bucketName = configuration["Minio:BucketName"] ?? "erao-files";

        var clientBuilder = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey);

        if (useSSL)
            clientBuilder = clientBuilder.WithSSL();

        // Region is required for AWS S3 and Cloudflare R2
        if (!string.IsNullOrEmpty(region))
            clientBuilder = clientBuilder.WithRegion(region);

        _minioClient = clientBuilder.Build();

        _logger.LogInformation("Storage initialized: endpoint={Endpoint}, bucket={Bucket}, ssl={SSL}, region={Region}",
            endpoint, _bucketName, useSSL, string.IsNullOrEmpty(region) ? "(default)" : region);
    }

    public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, Guid userId)
    {
        return await UploadFileAsync(fileStream, fileName, contentType, userId, prefix: null);
    }

    public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, Guid userId, string? prefix)
    {
        await EnsureBucketExistsAsync();

        var objectName = string.IsNullOrEmpty(prefix)
            ? GetObjectName(userId, fileName)
            : $"{prefix}/{userId}/{fileName}";

        var putObjectArgs = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectName)
            .WithStreamData(fileStream)
            .WithObjectSize(fileStream.Length)
            .WithContentType(contentType);

        await _minioClient.PutObjectAsync(putObjectArgs);

        _logger.LogInformation("File uploaded: {ObjectName}", objectName);

        return objectName;
    }

    public async Task<Stream> DownloadFileAsync(string objectName)
    {
        var memoryStream = new MemoryStream();

        var getObjectArgs = new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectName)
            .WithCallbackStream(stream => stream.CopyTo(memoryStream));

        await _minioClient.GetObjectAsync(getObjectArgs);

        memoryStream.Position = 0;
        return memoryStream;
    }

    public async Task DeleteFileAsync(string objectName)
    {
        var removeObjectArgs = new RemoveObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectName);

        await _minioClient.RemoveObjectAsync(removeObjectArgs);

        _logger.LogInformation("File deleted: {ObjectName}", objectName);
    }

    public async Task<bool> FileExistsAsync(string objectName)
    {
        try
        {
            var statObjectArgs = new StatObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName);

            await _minioClient.StatObjectAsync(statObjectArgs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string GetObjectName(Guid userId, string fileName)
    {
        return $"{userId}/{fileName}";
    }

    private async Task EnsureBucketExistsAsync()
    {
        // Only check once per app lifetime (singleton service)
        if (_bucketVerified) return;

        try
        {
            var bucketExistsArgs = new BucketExistsArgs().WithBucket(_bucketName);
            var exists = await _minioClient.BucketExistsAsync(bucketExistsArgs);

            if (!exists)
            {
                var makeBucketArgs = new MakeBucketArgs().WithBucket(_bucketName);
                await _minioClient.MakeBucketAsync(makeBucketArgs);
                _logger.LogInformation("Created bucket: {BucketName}", _bucketName);
            }

            _bucketVerified = true;
        }
        catch (Exception ex)
        {
            // R2/S3 may not support ListBuckets - bucket might already exist
            _logger.LogWarning(ex, "Could not verify bucket exists. If using R2/S3, create the bucket manually in the dashboard.");
            _bucketVerified = true;
        }
    }
}
