using Microsoft.Extensions.Configuration;
using Minio;
using Minio.DataModel.Args;

namespace Modules.Helpdesk.Features.Interactions;

public interface IAttachmentStorage
{
    Task PutAsync(string objectKey, Stream content, long size, string contentType, CancellationToken cancellationToken);
    Task<byte[]> GetAsync(string objectKey, CancellationToken cancellationToken);
}

public sealed class MinioAttachmentStorage : IAttachmentStorage
{
    private const string BucketName = "ticket-attachments";
    private readonly IMinioClient _client;
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private bool _bucketReady;

    public MinioAttachmentStorage(IConfiguration configuration)
    {
        var endpoint = configuration.GetConnectionString("minio")
            ?? throw new InvalidOperationException("Connection string 'minio' is required.");
        var endpointUri = new Uri(endpoint);
        var accessKey = configuration["ObjectStorage:AccessKey"]
            ?? throw new InvalidOperationException("ObjectStorage:AccessKey is required.");
        var secretKey = configuration["ObjectStorage:SecretKey"]
            ?? throw new InvalidOperationException("ObjectStorage:SecretKey is required.");
        var builder = new MinioClient().WithEndpoint(endpointUri.Host, endpointUri.Port)
            .WithCredentials(accessKey, secretKey);
        if (endpointUri.Scheme == Uri.UriSchemeHttps)
        {
            builder = builder.WithSSL();
        }

        _client = builder.Build();
    }

    public async Task PutAsync(
        string objectKey, Stream content, long size, string contentType, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(cancellationToken);
        await _client.PutObjectAsync(new PutObjectArgs().WithBucket(BucketName).WithObject(objectKey)
            .WithStreamData(content).WithObjectSize(size).WithContentType(contentType), cancellationToken);
    }

    public async Task<byte[]> GetAsync(string objectKey, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(cancellationToken);
        await using var content = new MemoryStream();
        await _client.GetObjectAsync(new GetObjectArgs().WithBucket(BucketName).WithObject(objectKey)
            .WithCallbackStream(stream => stream.CopyTo(content)), cancellationToken);
        return content.ToArray();
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (_bucketReady)
        {
            return;
        }

        await _bucketLock.WaitAsync(cancellationToken);
        try
        {
            if (!_bucketReady)
            {
                var exists = await _client.BucketExistsAsync(
                    new BucketExistsArgs().WithBucket(BucketName), cancellationToken);
                if (!exists)
                {
                    await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(BucketName), cancellationToken);
                }

                _bucketReady = true;
            }
        }
        finally
        {
            _bucketLock.Release();
        }
    }
}
