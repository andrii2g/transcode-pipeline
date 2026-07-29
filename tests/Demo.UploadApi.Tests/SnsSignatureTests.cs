using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Demo.UploadApi.Infrastructure.Aws;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Tests;

public sealed class SnsSignatureTests : IDisposable
{
    private const string Topic = "arn:aws:sns:eu-west-1:111122223333:uploads";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly X509Certificate2 _certificate;
    private readonly DateTimeOffset _now = new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    public SnsSignatureTests()
    {
        var request = new CertificateRequest("CN=sns.amazonaws.com", _rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _certificate = request.CreateSelfSigned(_now.AddDays(-1), _now.AddDays(1));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public async Task Valid_signature_versions_are_accepted(string version)
    {
        var envelope = Sign(Create(version));
        await CreateVerifier().VerifyAsync(envelope, Topic, CancellationToken.None);
    }

    [Fact]
    public async Task Invalid_signature_is_rejected()
    {
        var envelope = Sign(Create("2")) with { Signature = Convert.ToBase64String(new byte[256]) };
        await Assert.ThrowsAsync<CryptographicException>(() =>
            CreateVerifier().VerifyAsync(envelope, Topic, CancellationToken.None));
    }

    [Fact]
    public async Task Unexpected_topic_is_rejected_before_processing()
    {
        var envelope = Sign(Create("2"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CreateVerifier().VerifyAsync(envelope, Topic + "-other", CancellationToken.None));
    }

    [Fact]
    public async Task Stale_message_is_rejected()
    {
        var envelope = Sign(Create("2") with { Timestamp = _now.AddHours(-1) });
        await Assert.ThrowsAsync<CryptographicException>(() =>
            CreateVerifier().VerifyAsync(envelope, Topic, CancellationToken.None));
    }

    [Theory]
    [InlineData("http://sns.eu-west-1.amazonaws.com/SimpleNotificationService-test.pem")]
    [InlineData("https://evil.example.com/SimpleNotificationService-test.pem")]
    [InlineData("https://sns.eu-west-1.amazonaws.com/other.pem")]
    [InlineData("https://sns.eu-west-1.amazonaws.com/SimpleNotificationService-test.pem?redirect=x")]
    public void Invalid_certificate_urls_are_rejected(string url)
    {
        Assert.Throws<CryptographicException>(() => SnsCertificateProvider.ValidateUrl(new Uri(url)));
    }

    [Fact]
    public void Canonical_subscription_confirmation_includes_subscribe_url_and_token()
    {
        var envelope = Create("2") with
        {
            Type = "SubscriptionConfirmation",
            Token = "secret-token",
            SubscribeUrl = new Uri("https://sns.eu-west-1.amazonaws.com/?Action=ConfirmSubscription")
        };
        var canonical = SnsMessageSignatureVerifier.BuildCanonicalString(envelope);
        Assert.Contains("SubscribeURL\n", canonical);
        Assert.Contains("Token\nsecret-token\n", canonical);
    }

    private SnsMessageSignatureVerifier CreateVerifier() => new(
        new StaticCertificateProvider(_certificate), new AcceptCertificateChain(),
        Microsoft.Extensions.Options.Options.Create(new AwsNotificationOptions { MaximumMessageAgeMinutes = 15 }),
        new FixedTimeProvider(_now));

    private SnsEnvelope Create(string version) => new()
    {
        Type = "Notification",
        MessageId = Guid.NewGuid().ToString(),
        TopicArn = Topic,
        Message = "{\"hello\":\"world\"}",
        Subject = "subject",
        Timestamp = _now,
        SignatureVersion = version,
        Signature = string.Empty,
        SigningCertUrl = new Uri("https://sns.eu-west-1.amazonaws.com/SimpleNotificationService-test.pem")
    };

    private SnsEnvelope Sign(SnsEnvelope envelope)
    {
        var algorithm = envelope.SignatureVersion == "1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256;
        var signature = _rsa.SignData(Encoding.UTF8.GetBytes(SnsMessageSignatureVerifier.BuildCanonicalString(envelope)),
            algorithm, RSASignaturePadding.Pkcs1);
        return envelope with { Signature = Convert.ToBase64String(signature) };
    }

    public void Dispose()
    {
        _certificate.Dispose();
        _rsa.Dispose();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
