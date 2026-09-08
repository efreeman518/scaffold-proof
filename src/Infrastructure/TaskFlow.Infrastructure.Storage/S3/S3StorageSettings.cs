namespace TaskFlow.Infrastructure.Storage.S3;

/// <summary>
/// Settings for the S3-compatible object-storage arm (D-037): MinIO locally/on the VPS, any S3-compatible
/// provider in production.
/// </summary>
public class S3StorageSettings
{
    /// <summary>Configuration section this binds to.</summary>
    public const string ConfigSectionName = "Storage:S3";

    /// <summary>
    /// Endpoint the app uses to reach the bucket (e.g. <c>http://minio:9000</c> in-network). Empty targets
    /// real AWS S3, resolved from <see cref="Region"/> instead.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// Endpoint a browser or external client can reach to follow a presigned download URL. Required: SigV4
    /// signs the Host header into a presigned URL's signature, so a URL signed for an in-network-only host
    /// like <c>http://minio:9000</c> would hand a caller a URL it can never resolve, and rewriting the host
    /// afterward would invalidate the signature rather than fix the URL.
    /// </summary>
    public string? PublicServiceUrl { get; set; }

    /// <summary>SigV4 signing region. Not a real AWS region for most S3-compatible servers, but still required for signing.</summary>
    public string Region { get; set; } = "us-east-1";

    public string AccessKeyId { get; set; } = "";

    public string SecretAccessKey { get; set; } = "";

    /// <summary>Path-style bucket addressing (bucket in the URL path, not a subdomain); required by MinIO and most non-AWS S3-compatible servers.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Lifetime of a presigned download URL.</summary>
    public TimeSpan DownloadUrlLifetime { get; set; } = TimeSpan.FromHours(1);
}
