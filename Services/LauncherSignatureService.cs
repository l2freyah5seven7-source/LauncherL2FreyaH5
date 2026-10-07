using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace L2Launcher.Services;

public sealed class LauncherSignatureService : IDisposable
{
    private const string PublicKeyResourceName =
        "L2Launcher.Resources.launcher-signing-public.pem";

    private readonly ECDsa _publicKey;

    public LauncherSignatureService()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(PublicKeyResourceName) ??
            throw new InvalidOperationException(
                "Falta la clave pública de verificación del launcher.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var publicKeyPem = reader.ReadToEnd();
        _publicKey = ECDsa.Create();
        _publicKey.ImportFromPem(publicKeyPem);

        if (_publicKey.KeySize != 256)
        {
            _publicKey.Dispose();
            throw new InvalidDataException(
                "La clave de firma del launcher debe utilizar ECDSA P-256.");
        }
    }

    public bool Verify(byte[] content, string detachedSignatureBase64)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(detachedSignatureBase64);

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(detachedSignatureBase64.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "La firma digital no tiene un formato válido.",
                exception);
        }

        return signature.Length == 64 &&
            _publicKey.VerifyData(
                content,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Dispose()
    {
        _publicKey.Dispose();
    }
}
