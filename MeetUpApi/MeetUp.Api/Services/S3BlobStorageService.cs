using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using MeetUp.Api.Options;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Services;

/// <summary>
/// Talks to any S3-compatible object store (MinIO locally/self-hosted, or AWS S3 / Cloudflare R2
/// in production — swapping is just a ServiceUrl + credentials change, see BlobStorageOptions).
/// Uploads themselves are done by LiveKit Egress directly to the bucket; this service only
/// handles signed-URL generation, delete, and bucket bootstrap.
/// </summary>
public sealed class S3BlobStorageService : IBlobStorageService
{
    private readonly BlobStorageOptions _options;
    private readonly ILogger<S3BlobStorageService> _logger;
    private readonly AmazonS3Client _client;

    public S3BlobStorageService(IOptions<BlobStorageOptions> options, ILogger<S3BlobStorageService> logger)
    {
        _options = options.Value;
        _logger = logger;

        var config = new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl,
            ForcePathStyle = _options.ForcePathStyle,
            AuthenticationRegion = _options.Region,
        };

        _client = new AmazonS3Client(_options.AccessKey, _options.SecretKey, config);
    }

    public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry),
        };

        // GetPreSignedURL is a local signature computation (no network call), so this stays sync-fast.
        return Task.FromResult(_client.GetPreSignedURL(request));
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.DeleteObjectAsync(_options.BucketName, key, ct);
            _logger.LogInformation("Deleted blob {Key} from bucket {Bucket}", key, _options.BucketName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete blob {Key} from bucket {Bucket}", key, _options.BucketName);
        }
    }

    public async Task EnsureBucketExistsAsync(CancellationToken ct)
    {
        var exists = await AmazonS3Util.DoesS3BucketExistV2Async(_client, _options.BucketName);
        if (exists)
        {
            return;
        }

        await _client.PutBucketAsync(new PutBucketRequest
        {
            BucketName = _options.BucketName,
            UseClientRegion = true,
        }, ct);

        _logger.LogInformation("Created blob storage bucket {Bucket}", _options.BucketName);
    }
}
