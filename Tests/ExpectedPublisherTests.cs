using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Who a downloaded installer must have been signed by.
///
/// <para><b>Why the pin is a certificate and not a constant.</b> The signing certificate is
/// self-signed, so on a machine that has not imported it Windows does not trust the chain and the
/// subject alone would accept any self-signed certificate spelling the same name —
/// <see cref="AnImpostorSpellingTheSameNameIsRefused"/> is that hole, closed. The pin is therefore a
/// thumbprint, and it is read out of the certificate embedded in the build rather than written down
/// beside it, so the app and the release workflow that verifies every produced binary against the same
/// file cannot drift apart. Nothing here writes a thumbprint: the assertions compare two values both
/// computed at run time.</para>
///
/// <para>This test assembly embeds the same certificate under the same resource name as the app, so
/// <see cref="ExpectedPublisher.Load()"/> is exercised exactly as it runs in production.</para>
/// </summary>
public class ExpectedPublisherTests
{
    // ── The embedded pin ─────────────────────────────────────────────────────────

    /// <summary>The certificate is in the build. A csproj that dropped the resource would leave the
    /// app unable to accept any installer at all, which is a failure worth failing loudly on.</summary>
    [Fact]
    public void TheBuildEmbedsExactlyOnePublisherCertificate()
    {
        var signer = ExpectedPublisher.Load();

        Assert.Single(signer.CertificateThumbprints);
        Assert.False(string.IsNullOrWhiteSpace(signer.Subject));
    }

    /// <summary>The pin is taken over SHA-256, not the SHA-1 <c>Thumbprint</c> property: SHA-1
    /// collisions are constructible, and a pin is worth no more than its digest.</summary>
    [Fact]
    public void ThePinIsASha256Thumbprint()
    {
        var pin = Assert.Single(ExpectedPublisher.Load().CertificateThumbprints);

        Assert.Equal(64, pin.Length);
        Assert.All(pin, c => Assert.True(char.IsAsciiHexDigit(c)));
    }

    /// <summary>
    /// The pin is the embedded certificate's own SHA-256, computed here rather than compared against a
    /// value written down — which is the property that makes a hard-coded thumbprint unnecessary.
    /// </summary>
    [Fact]
    public void ThePinIsTheEmbeddedCertificatesOwnThumbprint()
    {
        using var embedded = EmbeddedCertificate();

        Assert.Equal(embedded.GetCertHashString(HashAlgorithmName.SHA256),
                     Assert.Single(ExpectedPublisher.Load().CertificateThumbprints));
    }

    // ── What the signer accepts and refuses ──────────────────────────────────────

    /// <summary>The certificate releases are signed with, on a machine that does not trust the chain —
    /// which is every machine the certificate has not been imported on.</summary>
    [Fact]
    public void ThePublishersOwnCertificateIsAcceptedUnderAnUntrustedChain()
    {
        using var embedded = EmbeddedCertificate();

        Assert.Equal(ZeroZero.Update.SignerMatch.Matched, SignerMatchOf(embedded, chainTrusted: false));
    }

    /// <summary>
    /// The hole a subject-only rule leaves: anyone can mint a self-signed certificate spelling the
    /// publisher's name. Under an untrusted chain it must be refused for not being pinned.
    /// </summary>
    [Fact]
    public void AnImpostorSpellingTheSameNameIsRefused()
    {
        using var embedded = EmbeddedCertificate();
        using var impostor = SelfSigned(embedded.Subject);

        Assert.Equal(ZeroZero.Update.SignerMatch.CertificateNotPinned,
                     SignerMatchOf(impostor, chainTrusted: false));
    }

    [Fact]
    public void AnotherPublisherIsRefusedOnItsSubject()
    {
        using var stranger = SelfSigned("CN=Somebody Else, O=Somewhere, C=NO");

        Assert.Equal(ZeroZero.Update.SignerMatch.SubjectDiffers, SignerMatchOf(stranger, chainTrusted: false));
    }

    // ── Building the signer ──────────────────────────────────────────────────────

    /// <summary>
    /// A build embedding no certificate must fail loudly rather than produce a signer nothing can
    /// match, which would be a verification that silently refuses every release for ever.
    /// </summary>
    [Fact]
    public void NoCertificateAtAllIsRefusedRatherThanTurnedIntoASignerNothingMatches()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => ExpectedPublisher.SignerFor([]));

        Assert.Contains("embeds no publisher certificate", thrown.Message);
    }

    /// <summary>
    /// Several certificates may be pinned at once — that is how a rotation reaches installed copies a
    /// release before the new key is first used. They must all name one publisher, because there is one
    /// subject to expect and picking one of two silently would decide which.
    /// </summary>
    [Fact]
    public void CertificatesNamingDifferentSubjectsAreRefused()
    {
        using var mine = SelfSigned("CN=ZeroZero Software");
        using var theirs = SelfSigned("CN=Somebody Else");

        var thrown = Assert.Throws<InvalidOperationException>(() => ExpectedPublisher.SignerFor([mine, theirs]));

        Assert.Contains("different subjects", thrown.Message);
    }

    /// <summary>Two keys, one name: both are pins, and each is accepted on its own.</summary>
    [Fact]
    public void EveryCertificateOfTheOnePublisherIsAPin()
    {
        using var current = SelfSigned("CN=ZeroZero Software");
        using var next = SelfSigned("CN=ZeroZero Software");

        var signer = ExpectedPublisher.SignerFor([current, next]);

        Assert.Equal(2, signer.CertificateThumbprints.Count);
        Assert.Equal(ZeroZero.Update.SignerMatch.Matched, signer.Match(current, chainTrusted: false, out _));
        Assert.Equal(ZeroZero.Update.SignerMatch.Matched, signer.Match(next, chainTrusted: false, out _));
    }

    [Theory]
    [InlineData("ZeroZeroSoftware.cer", true)]
    [InlineData("Something.CER", true)]
    [InlineData("ZeroZeroSoftware.pfx", false)]
    [InlineData("config.json", false)]
    public void OnlyCertificateResourcesAreRead(string resourceName, bool expected) =>
        Assert.Equal(expected, ExpectedPublisher.IsCertificateResource(resourceName));

    // ── Fixtures ─────────────────────────────────────────────────────────────────

    private static ZeroZero.Update.SignerMatch SignerMatchOf(X509Certificate2 certificate, bool chainTrusted) =>
        ExpectedPublisher.Load().Match(certificate, chainTrusted, out _);

    /// <summary>The publisher certificate this build embeds, read the same way the app reads it.</summary>
    private static X509Certificate2 EmbeddedCertificate()
    {
        var assembly = typeof(ExpectedPublisherTests).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(), ExpectedPublisher.IsCertificateResource);

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return X509CertificateLoader.LoadCertificate(bytes.ToArray());
    }

    /// <summary>A certificate of this test's own making — the stranger, and the impostor spelling the
    /// publisher's name with a key nobody pinned.</summary>
    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
