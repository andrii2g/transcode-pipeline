using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Application;

public interface IUploadLimitResolver
{
    long Resolve(string? serverPolicyName = null);
}

public sealed class UploadLimitResolver(IOptions<UploadPolicyOptions> options) : IUploadLimitResolver
{
    private readonly UploadPolicyOptions _options = options.Value;

    public long Resolve(string? serverPolicyName = null)
    {
        var resolved = !string.IsNullOrWhiteSpace(serverPolicyName) &&
            _options.NamedLimits.TryGetValue(serverPolicyName, out var named)
                ? named
                : _options.DefaultMaxSizeBytes;
        if (resolved <= 0 || resolved > _options.AbsoluteMaxSizeBytes)
            throw new InvalidOperationException("The resolved upload limit is outside the configured absolute policy.");
        return resolved;
    }
}
