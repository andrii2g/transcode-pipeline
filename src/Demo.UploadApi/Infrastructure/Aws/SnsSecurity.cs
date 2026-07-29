using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Infrastructure.Aws;

public sealed record SnsEnvelope
{
    [JsonPropertyName("Type")] public required string Type { get; init; }
    [JsonPropertyName("MessageId")] public required string MessageId { get; init; }
    [JsonPropertyName("TopicArn")] public required string TopicArn { get; init; }
    [JsonPropertyName("Message")] public required string Message { get; init; }
    [JsonPropertyName("Timestamp")] public DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("SignatureVersion")] public required string SignatureVersion { get; init; }
    [JsonPropertyName("Signature")] public required string Signature { get; init; }
    [JsonPropertyName("SigningCertURL")] public required Uri SigningCertUrl { get; init; }
    [JsonPropertyName("Token")] public string? Token { get; init; }
    [JsonPropertyName("SubscribeURL")] public Uri? SubscribeUrl { get; init; }
    [JsonPropertyName("Subject")] public string? Subject { get; init; }
}

public interface ISnsCertificateProvider
{
    Task<X509Certificate2> GetAsync(Uri uri, CancellationToken cancellationToken);
}

public interface ISnsCertificateChainValidator
{
    bool Validate(X509Certificate2 certificate);
}

public sealed class SnsCertificateChainValidator : ISnsCertificateChainValidator
{
    public bool Validate(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(certificate);
    }
}

public sealed partial class SnsCertificateProvider(
    IHttpClientFactory clientFactory,
    IMemoryCache cache,
    IOptions<AwsNotificationOptions> options) : ISnsCertificateProvider
{
    [GeneratedRegex("^sns\\.[a-z0-9-]+\\.amazonaws\\.com(?:\\.cn)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SnsHostPattern();

    public async Task<X509Certificate2> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        ValidateUrl(uri);
        if (cache.TryGetValue(uri.AbsoluteUri, out byte[]? cached) && cached is not null)
            return X509CertificateLoader.LoadCertificate(cached);
        using var response = await clientFactory.CreateClient("sns-certificates").GetAsync(uri,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 65_536)
            throw new CryptographicException("SNS signing certificate response was too large.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > 65_536) throw new CryptographicException("SNS signing certificate response was too large.");
        var certificate = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(bytes));
        cache.Set(uri.AbsoluteUri, certificate.Export(X509ContentType.Cert),
            TimeSpan.FromMinutes(options.Value.CertificateCacheMinutes));
        return certificate;
    }

    public static void ValidateUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !SnsHostPattern().IsMatch(uri.IdnHost) ||
            !uri.AbsolutePath.StartsWith("/SimpleNotificationService-", StringComparison.Ordinal) ||
            !uri.AbsolutePath.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("..", StringComparison.Ordinal))
            throw new CryptographicException("SigningCertURL is not an allowed Amazon SNS certificate URL.");
    }
}

public interface ISnsMessageSignatureVerifier
{
    Task VerifyAsync(SnsEnvelope envelope, string expectedTopicArn, CancellationToken cancellationToken);
}

public sealed class SnsMessageSignatureVerifier(
    ISnsCertificateProvider certificateProvider,
    ISnsCertificateChainValidator chainValidator,
    IOptions<AwsNotificationOptions> options,
    TimeProvider timeProvider) : ISnsMessageSignatureVerifier
{
    public async Task VerifyAsync(SnsEnvelope envelope, string expectedTopicArn, CancellationToken cancellationToken)
    {
        if (!string.Equals(envelope.TopicArn, expectedTopicArn, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The SNS TopicArn is not allowed for this endpoint.");
        if (envelope.SignatureVersion is not ("1" or "2"))
            throw new CryptographicException("Unsupported SNS signature version.");
        var age = (timeProvider.GetUtcNow() - envelope.Timestamp).Duration();
        if (age > TimeSpan.FromMinutes(options.Value.MaximumMessageAgeMinutes))
            throw new CryptographicException("The SNS message timestamp is outside the accepted window.");
        var certificate = await certificateProvider.GetAsync(envelope.SigningCertUrl, cancellationToken);
        using (certificate)
        {
            if (!chainValidator.Validate(certificate)) throw new CryptographicException("SNS signing certificate chain validation failed.");
            using var rsa = certificate.GetRSAPublicKey() ?? throw new CryptographicException("SNS signing certificate has no RSA public key.");
            byte[] signature;
            try { signature = Convert.FromBase64String(envelope.Signature); }
            catch (FormatException exception) { throw new CryptographicException("SNS signature is not valid Base64.", exception); }
            var algorithm = envelope.SignatureVersion == "1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256;
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(BuildCanonicalString(envelope)), signature, algorithm, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("SNS signature verification failed.");
        }
    }

    public static string BuildCanonicalString(SnsEnvelope envelope)
    {
        var builder = new StringBuilder();
        Add(builder, "Message", envelope.Message);
        Add(builder, "MessageId", envelope.MessageId);
        if (envelope.Type == "Notification" && envelope.Subject is not null) Add(builder, "Subject", envelope.Subject);
        if (envelope.Type is "SubscriptionConfirmation" or "UnsubscribeConfirmation")
        {
            if (envelope.SubscribeUrl is null || envelope.Token is null)
                throw new CryptographicException("SNS confirmation is missing SubscribeURL or Token.");
            Add(builder, "SubscribeURL", envelope.SubscribeUrl.AbsoluteUri);
        }
        Add(builder, "Timestamp", envelope.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        if (envelope.Type is "SubscriptionConfirmation" or "UnsubscribeConfirmation") Add(builder, "Token", envelope.Token!);
        Add(builder, "TopicArn", envelope.TopicArn);
        Add(builder, "Type", envelope.Type);
        return builder.ToString();
    }

    private static void Add(StringBuilder builder, string name, string value) =>
        builder.Append(name).Append('\n').Append(value).Append('\n');
}

public interface ISnsSubscriptionConfirmationService
{
    Task ConfirmAsync(SnsEnvelope envelope, CancellationToken cancellationToken);
}

public sealed class SnsSubscriptionConfirmationService(IAmazonSimpleNotificationService sns) : ISnsSubscriptionConfirmationService
{
    public async Task ConfirmAsync(SnsEnvelope envelope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(envelope.Token)) throw new InvalidOperationException("SNS confirmation token is missing.");
        await sns.ConfirmSubscriptionAsync(new ConfirmSubscriptionRequest
        {
            TopicArn = envelope.TopicArn,
            Token = envelope.Token,
            AuthenticateOnUnsubscribe = "true"
        }, cancellationToken);
    }
}
