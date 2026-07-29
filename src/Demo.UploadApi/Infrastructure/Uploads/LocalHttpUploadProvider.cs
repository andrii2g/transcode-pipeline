using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Application;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Infrastructure.Uploads;

public sealed class LocalPathResolver(IOptions<LocalStorageOptions> options)
{
    private readonly LocalStorageOptions _options = options.Value;
    public string TemporaryUploadPath(string videoId) => SafeCombine(_options.TemporaryUploadDirectory, $"{videoId}.uploading");
    public string SourcePath(string relativePath) => SafeCombine(_options.SourceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    public string ProcessingOutputPath(string videoId, string claimId) => SafeCombine(_options.TemporaryOutputDirectory, videoId, $".processing-{claimId}");
    public string PublishedOutputPath(string videoId) => SafeCombine(_options.OutputDirectory, videoId, "published");

    private static string SafeCombine(string root, params string[] parts)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(parts.Aggregate(fullRoot, Path.Combine));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Resolved path escaped its configured root.");
        return candidate;
    }
}

public sealed class LocalHttpUploadProvider(
    IOptions<LocalStorageOptions> options,
    LocalPathResolver paths) : IUploadProvider
{
    private readonly LocalStorageOptions _options = options.Value;
    public UploadProviderKind Kind => UploadProviderKind.LocalHttp;

    public Task<UploadInstruction> CreateInstructionAsync(
        VideoWorkflow workflow, string? oneTimeToken, CancellationToken cancellationToken)
    {
        var url = new Uri(new Uri(_options.PublicBaseUrl.TrimEnd('/') + "/"), $"uploads/{workflow.VideoId}/content");
        var headers = new Dictionary<string, string> { ["X-Upload-Token"] = oneTimeToken! };
        if (workflow.ContentType is not null) headers["Content-Type"] = workflow.ContentType;
        return Task.FromResult(new UploadInstruction(Kind, "PUT", url, headers, new Dictionary<string, string>()));
    }

    public async Task<SourceObjectMetadata> InspectAsync(VideoWorkflow workflow, CancellationToken cancellationToken)
    {
        var path = paths.SourcePath(workflow.Source.LocalRelativePath!);
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The local source file does not exist.", path);
        string checksum;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            checksum = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }
        var source = workflow.Source with { Identity = $"local:{workflow.Source.LocalRelativePath}:{checksum}" };
        return new SourceObjectMetadata(source, file.Length, workflow.ContentType,
            null, checksum, new Dictionary<string, string>());
    }
}
