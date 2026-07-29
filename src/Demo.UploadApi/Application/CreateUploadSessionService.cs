using System.Security.Cryptography;
using System.Text;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Options;
using Demo.UploadApi.Persistence;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Application;

public interface ICreateUploadSessionService
{
    Task<CreatedUpload> CreateAsync(string fileName, string? contentType, long sizeBytes, string? profile,
        CancellationToken cancellationToken);
}

public sealed class CreateUploadSessionService(
    IWorkflowStore store,
    IUploadProvider uploadProvider,
    IUploadLimitResolver limitResolver,
    IOptions<MediaPipelineOptions> pipelineOptions,
    IOptions<UploadPolicyOptions> policyOptions,
    IOptions<S3StorageOptions> s3Options,
    TimeProvider timeProvider) : ICreateUploadSessionService
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv" };
    private readonly MediaPipelineOptions _pipeline = pipelineOptions.Value;
    private readonly UploadPolicyOptions _policy = policyOptions.Value;
    private readonly S3StorageOptions _s3 = s3Options.Value;

    public async Task<CreatedUpload> CreateAsync(
        string fileName, string? contentType, long sizeBytes, string? profile, CancellationToken cancellationToken)
    {
        var normalized = NormalizeFileName(fileName);
        var extension = Path.GetExtension(normalized).ToLowerInvariant();
        if (!Extensions.Contains(extension))
            throw new UploadRequestException("UnsupportedExtension", "Only .mp4, .mov, and .mkv files are supported.", 400);
        if (sizeBytes <= 0)
            throw new UploadRequestException("InvalidSize", "sizeBytes must be greater than zero.", 400);
        var selectedProfile = string.IsNullOrWhiteSpace(profile) ? _pipeline.DefaultProfile : profile.Trim();
        if (!_policy.Profiles.Contains(selectedProfile, StringComparer.OrdinalIgnoreCase))
            throw new UploadRequestException("UnknownProfile", $"The profile '{selectedProfile}' is not configured.", 400);
        var maximum = limitResolver.Resolve();
        if (sizeBytes > maximum)
            throw new UploadRequestException("FileSizeExceeded", "Video file is too large.", StatusCodes.Status413PayloadTooLarge);

        var now = timeProvider.GetUtcNow();
        var videoId = Guid.CreateVersion7().ToString();
        var expiresAt = now.AddMinutes(_policy.SessionExpirationMinutes);
        string? token = null;
        byte[]? tokenHash = null;
        SourceLocator source;
        if (_pipeline.UploadProvider == UploadProviderKind.LocalHttp)
        {
            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            token = Base64UrlEncode(tokenBytes);
            tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var relative = Path.Combine(videoId, $"source{extension}").Replace('\\', '/');
            source = new SourceLocator(UploadProviderKind.LocalHttp, $"local:{relative}", LocalRelativePath: relative);
        }
        else
        {
            var key = $"{_s3.UploadPrefix.Trim('/')}/{videoId}/source/source{extension}";
            source = new SourceLocator(UploadProviderKind.S3PresignedPost, $"s3://{_s3.InputBucket}/{key}", _s3.InputBucket, key);
        }

        var workflow = new VideoWorkflow
        {
            VideoId = videoId,
            OriginalFileName = normalized,
            ContentType = NormalizeContentType(contentType),
            DeclaredSizeBytes = sizeBytes,
            MaximumSizeBytes = maximum,
            UploadProvider = _pipeline.UploadProvider,
            TranscodeProvider = _pipeline.TranscodeProvider,
            ProfileName = selectedProfile,
            Status = TranscodeJobStatus.UploadPending,
            Source = source,
            CreatedAtUtc = now,
            UploadExpiresAtUtc = expiresAt
        };
        await store.CreateAsync(workflow, new UploadSession(videoId, tokenHash, expiresAt, maximum, sizeBytes), cancellationToken);
        try
        {
            var instruction = await uploadProvider.CreateInstructionAsync(workflow, token, cancellationToken);
            return new CreatedUpload(workflow, instruction);
        }
        catch
        {
            await store.RejectUploadAsync(videoId, "UploadInstructionFailed", "Upload instructions could not be generated.", cancellationToken);
            throw;
        }
    }

    public static string NormalizeFileName(string fileName)
    {
        var normalized = Path.GetFileName(fileName ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(normalized)) throw new UploadRequestException("FileNameRequired", "fileName is required.", 400);
        if (normalized.Length > 200) normalized = normalized[..200];
        return normalized;
    }

    private static string? NormalizeContentType(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Base64UrlEncode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
