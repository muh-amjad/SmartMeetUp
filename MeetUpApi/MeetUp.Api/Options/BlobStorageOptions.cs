namespace MeetUp.Api.Options;

public sealed class BlobStorageOptions
{
    public const string SectionName = "BlobStorage";

    // Endpoint the .NET API process itself uses (bucket bootstrap, presigned URLs).
    // Dev today: the API runs on the host while MinIO runs in Docker with its port published,
    // so this is "http://localhost:9000". Once the API is containerized on the same compose
    // network (Phase 10), this becomes "http://minio:9000" like EgressServiceUrl below.
    public string ServiceUrl { get; set; } = string.Empty;

    // Endpoint the LiveKit Egress *worker container* uses to upload recordings. Egress always
    // runs in Docker, so this must be the compose-network address (e.g. "http://minio:9000"),
    // even while the API itself still runs on the host in dev.
    public string EgressServiceUrl { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";

    // Required for MinIO (bucket-in-path instead of bucket-as-subdomain)
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// Days before a recording is deleted automatically. Recordings are the only personal data the
    /// demo stores in bulk, and the free tier has finite disk, so they expire rather than accumulate.
    /// Zero disables the rule.
    /// </summary>
    public int RetentionDays { get; set; } = 30;
}
