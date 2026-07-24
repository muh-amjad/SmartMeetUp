namespace MeetUp.Api.Services;

public interface IBlobStorageService
{
    /// <summary>Generates a time-limited URL a client can use to download/play back an object directly from storage.</summary>
    Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Creates the configured bucket if it doesn't already exist. MinIO does not auto-create buckets — call once at startup.</summary>
    Task EnsureBucketExistsAsync(CancellationToken ct);
}
