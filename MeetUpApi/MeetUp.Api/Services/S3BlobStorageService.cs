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
            UseHttp = IsPlainHttpEndpoint,
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
        var url = _client.GetPreSignedURL(request);

        // The SDK's presigner hardcodes https:// for a custom ServiceURL — it honours neither the
        // URL's own scheme nor Config.UseHttp (verified against AWSSDK.S3 3.7 and 4.0). Against a
        // plain-HTTP MinIO that yields links nothing can open, so put the configured scheme back.
        // Only the scheme is touched; the query-string signature stays exactly as signed.
        if (IsPlainHttpEndpoint && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = string.Concat("http://", url.AsSpan("https://".Length));
        }

        return Task.FromResult(url);
    }

    private bool IsPlainHttpEndpoint =>
        _options.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

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

        if (!exists)
        {
            await _client.PutBucketAsync(new PutBucketRequest
            {
                BucketName = _options.BucketName,
                UseClientRegion = true,
            }, ct);

            _logger.LogInformation("Created blob storage bucket {Bucket}", _options.BucketName);
        }

        // Applied on every startup, not only at creation: an existing bucket from before retention
        // was configured — or one whose rule was changed by hand — still ends up correct.
        await ApplyRetentionPolicyAsync(ct);
    }

    /// <summary>
    /// Expires recordings after the configured number of days. The store enforces this itself, so
    /// deletion keeps happening even if the app is down.
    /// </summary>
    private async Task ApplyRetentionPolicyAsync(CancellationToken ct)
    {
        if (_options.RetentionDays <= 0)
        {
            return;
        }

        try
        {
            await _client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
            {
                BucketName = _options.BucketName,
                Configuration = new LifecycleConfiguration
                {
                    Rules =
                    [
                        new LifecycleRule
                        {
                            Id = "expire-recordings",
                            Status = LifecycleRuleStatus.Enabled,
                            Filter = new LifecycleFilter
                            {
                                LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = "recordings/" },
                            },
                            Expiration = new LifecycleRuleExpiration { Days = _options.RetentionDays },
                        },
                    ],
                },
            }, ct);

            _logger.LogInformation(
                "Recording retention set to {Days} day(s) on bucket {Bucket}",
                _options.RetentionDays, _options.BucketName);
        }
        catch (Exception ex)
        {
            // Non-fatal: recordings simply accumulate until this is fixed, which must not stop the
            // app from serving.
            _logger.LogWarning(ex,
                "Could not apply the retention policy to bucket {Bucket}", _options.BucketName);
        }
    }
}
