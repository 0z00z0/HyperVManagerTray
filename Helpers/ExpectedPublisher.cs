using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZeroZero.Update;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Who must have signed a downloaded installer before it is allowed to run.
///
/// <para>The pin is the publisher certificate itself, embedded in this assembly and read at
/// runtime: both the subject and the SHA-256 thumbprint come out of the certificate's bytes, so
/// there is no thumbprint literal in any source or configuration file to drift from the
/// certificate releases are actually signed with. The same file is what the release workflow
/// checks every produced binary against, which makes it one authority rather than two.</para>
///
/// <para>The certificate is self-signed, so a machine that has not imported it does not trust the
/// chain; there the subject alone would accept any self-signed certificate spelling the same name,
/// and the pinned thumbprint is what closes that. Rotating the certificate means embedding the new
/// one a release BEFORE it is first used to sign, so copies already installed pin it — which is why
/// every embedded certificate is a pin rather than only the first.</para>
/// </summary>
internal static class ExpectedPublisher
{
    /// <summary>Every embedded resource whose name ends in this is a pin.</summary>
    internal const string CertificateSuffix = ".cer";

    /// <summary>The signer this build expects, read from its own embedded certificates.</summary>
    public static ExpectedSigner Load() => Load(typeof(ExpectedPublisher).Assembly);

    internal static ExpectedSigner Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var certificates = new List<X509Certificate2>();
        try
        {
            // Ordinal order so the set is the same on every machine; the pins are a set, not a list,
            // but a stable order keeps the subject checked below reported against the same first one.
            foreach (var name in assembly.GetManifestResourceNames()
                                         .Where(IsCertificateResource)
                                         .Order(StringComparer.Ordinal))
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"The embedded resource '{name}' could not be opened.");
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                certificates.Add(X509CertificateLoader.LoadCertificate(bytes.ToArray()));
            }

            return SignerFor(certificates);
        }
        finally
        {
            foreach (var certificate in certificates) certificate.Dispose();
        }
    }

    internal static bool IsCertificateResource(string resourceName) =>
        resourceName.EndsWith(CertificateSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The subject every pinned certificate must share, and one thumbprint per certificate.
    /// </summary>
    /// <exception cref="InvalidOperationException">No certificate is embedded, or the embedded ones
    /// name different subjects — neither of which may be turned into a signer nothing can match,
    /// because a signer nothing can match is a verification that always refuses.</exception>
    internal static ExpectedSigner SignerFor(IReadOnlyList<X509Certificate2> certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);

        if (certificates.Count == 0)
            throw new InvalidOperationException(
                "This build embeds no publisher certificate, so a downloaded installer could not be checked "
              + "against the certificate it must be signed by. Restore the EmbeddedResource entry for "
              + "scripts\\ZeroZeroSoftware.cer.");

        var subject = certificates[0].Subject;
        foreach (var certificate in certificates)
            if (!string.Equals(certificate.Subject, subject, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The embedded publisher certificates name different subjects ('{subject}' and "
                  + $"'{certificate.Subject}'), so there is no single signer to expect. Every certificate "
                  + "pinned here belongs to the same publisher; a rotation changes the key, not the name.");

        return new ExpectedSigner(subject, certificates.Select(Thumbprint).ToArray());
    }

    /// <summary>SHA-256, never the SHA-1 <c>Thumbprint</c> property: a pin is worth no more than the
    /// digest it is taken over, and SHA-1 collisions are constructible.</summary>
    private static string Thumbprint(X509Certificate2 certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);
}
