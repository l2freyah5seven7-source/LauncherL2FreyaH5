using System.ComponentModel;
using System.Security.AccessControl;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace LauncherSigningTool;

internal static class Program
{
    private const string AppDirectoryName = "AscensionLauncher";
    private const string KeyFileName = "launcher-signing-key.dpapi";
    private const string PublicKeyFileName = "launcher-signing-public.pem";
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("Ascension.LineageII.Launcher.SigningKey.v1");

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                throw new ArgumentException(
                    "Comandos: init, sign <archivo> [firma], sign-settings, export-public <archivo>, verify <archivo> <firma> [clave-publica].");
            }

            return args[0] switch
            {
                "init" when args.Length == 1 => InitializeKey(),
                "sign" when args.Length is 2 or 3 => SignFile(args[1], args.ElementAtOrDefault(2)),
                "sign-settings" when args.Length == 1 => SignSettings(),
                "export-public" when args.Length == 2 => ExportPublicKey(args[1]),
                "verify" when args.Length is 3 or 4 =>
                    VerifyFile(args[1], args[2], args.ElementAtOrDefault(3)),
                _ => throw new ArgumentException("El comando de firma o la cantidad de argumentos no son válidos.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int InitializeKey()
    {
        var keyPath = GetPrivateKeyPath();
        var publicKeyPath = GetProjectPublicKeyPath();
        if (File.Exists(keyPath) || File.Exists(publicKeyPath))
        {
            throw new IOException(
                "Ya hay una clave de firma. No se reemplazó para conservar la cadena de confianza.");
        }

        var keyDirectory = Path.GetDirectoryName(keyPath)!;
        Directory.CreateDirectory(keyDirectory);
        RestrictDirectoryAcl(keyDirectory);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateBytes = key.ExportPkcs8PrivateKey();
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectForCurrentUser(privateBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
        }

        try
        {
            using var keyFile = new FileStream(
                keyPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            keyFile.Write(protectedBytes);
            keyFile.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }

        SetPrivateFileAcl(keyPath);
        var publicPem = key.ExportSubjectPublicKeyInfoPem();
        var publicDirectory = Path.GetDirectoryName(publicKeyPath)!;
        Directory.CreateDirectory(publicDirectory);
        File.WriteAllText(
            publicKeyPath,
            publicPem,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(publicPem)));
        Console.WriteLine($"ECDSA P-256 configurada para el usuario Windows {WindowsIdentity.GetCurrent().Name}.");
        Console.WriteLine($"Clave privada cifrada solo para este usuario: {keyPath}");
        Console.WriteLine($"Clave pública para el launcher: {publicKeyPath}");
        Console.WriteLine($"Huella pública SHA-256: {fingerprint}");
        return 0;
    }

    private static int SignSettings()
    {
        var settingsPath = Path.Combine(GetSigningWorkspace(), "launcher.settings.json");
        if (!File.Exists(settingsPath))
        {
            throw new FileNotFoundException(
                "No se encuentra launcher.settings.json junto a la herramienta de firma.",
                settingsPath);
        }

        return SignFile(settingsPath, null);
    }

    private static int SignFile(string filePath, string? signaturePath)
    {
        var resolvedFile = Path.GetFullPath(filePath);
        if (!File.Exists(resolvedFile))
        {
            throw new FileNotFoundException("No se encuentra el archivo que se quiere firmar.", resolvedFile);
        }

        using var key = LoadPrivateKey();
        var contents = File.ReadAllBytes(resolvedFile);
        var signature = key.SignData(
            contents,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var resultPath = signaturePath is null
            ? resolvedFile + ".sig"
            : Path.GetFullPath(signaturePath);
        File.WriteAllText(
            resultPath,
            Convert.ToBase64String(signature) + Environment.NewLine,
            Encoding.ASCII);
        Console.WriteLine($"Archivo firmado (ECDSA P-256 / SHA-256): {resultPath}");
        return 0;
    }

    private static int ExportPublicKey(string destinationPath)
    {
        var sourcePath = GetProjectPublicKeyPath();
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                "Todavía no existe una clave pública. Inicializa primero la clave de firma.",
                sourcePath);
        }

        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        File.Copy(sourcePath, fullDestination, overwrite: false);
        Console.WriteLine($"Clave pública exportada: {fullDestination}");
        return 0;
    }

    private static int VerifyFile(
        string filePath,
        string signaturePath,
        string? publicKeyPath)
    {
        var trustedKeyPath = Path.GetFullPath(publicKeyPath ?? GetProjectPublicKeyPath());
        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(File.ReadAllText(trustedKeyPath));
        var contents = File.ReadAllBytes(Path.GetFullPath(filePath));
        var signature = Convert.FromBase64String(
            File.ReadAllText(Path.GetFullPath(signaturePath)).Trim());
        var verified = signature.Length == 64 && verifier.VerifyData(
            contents,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!verified)
        {
            throw new CryptographicException("La firma no corresponde al archivo o no es de confianza.");
        }

        Console.WriteLine($"Firma válida: {Path.GetFullPath(filePath)}");
        return 0;
    }

    private static ECDsa LoadPrivateKey()
    {
        var keyPath = GetPrivateKeyPath();
        if (!File.Exists(keyPath))
        {
            throw new FileNotFoundException(
                "No existe una clave de firma para este usuario. Primero ejecuta -InitializeKey.",
                keyPath);
        }

        var encryptedBytes = File.ReadAllBytes(keyPath);
        var privateBytes = UnprotectForCurrentUser(encryptedBytes);
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(privateBytes, out var bytesRead);
            if (bytesRead != privateBytes.Length || key.KeySize != 256)
            {
                throw new CryptographicException("La clave privada ECDSA P-256 no es válida.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedBytes);
            CryptographicOperations.ZeroMemory(privateBytes);
        }
    }

    private static string GetPrivateKeyPath()
    {
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            throw new InvalidOperationException("Windows no devolvió una carpeta AppData local.");
        }

        return Path.Combine(appData, AppDirectoryName, KeyFileName);
    }

    private static string GetProjectPublicKeyPath() =>
        Path.Combine(GetSigningWorkspace(), "Resources", PublicKeyFileName);

    private static string GetSigningWorkspace()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "launcher.settings.json")) ||
                File.Exists(Path.Combine(current.FullName, "L2Launcher.csproj")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "No se encontró launcher.settings.json en la carpeta del launcher.");
    }

    private static byte[] ProtectForCurrentUser(byte[] data) =>
        TransformDpapiData(data, encrypt: true);

    private static byte[] UnprotectForCurrentUser(byte[] data) =>
        TransformDpapiData(data, encrypt: false);

    private static byte[] TransformDpapiData(byte[] data, bool encrypt)
    {
        var inputPointer = Marshal.AllocHGlobal(data.Length);
        var entropyPointer = Marshal.AllocHGlobal(Entropy.Length);
        try
        {
            Marshal.Copy(data, 0, inputPointer, data.Length);
            Marshal.Copy(Entropy, 0, entropyPointer, Entropy.Length);
            var input = new DataBlob { Size = data.Length, Data = inputPointer };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entropyPointer };
            DataBlob output;

            var success = encrypt
                ? NativeMethods.CryptProtectData(
                    ref input, "Ascension launcher signing key", ref entropy,
                    IntPtr.Zero, IntPtr.Zero, NativeMethods.CryptProtectUiForbidden, out output)
                : NativeMethods.CryptUnprotectData(
                    ref input, IntPtr.Zero, ref entropy,
                    IntPtr.Zero, IntPtr.Zero, NativeMethods.CryptProtectUiForbidden, out output);

            if (!success)
            {
                throw new CryptographicException(
                    $"Windows no pudo {(encrypt ? "proteger" : "desproteger")} la clave de firma.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                NativeMethods.LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.Copy(new byte[data.Length], 0, inputPointer, data.Length);
            Marshal.Copy(new byte[Entropy.Length], 0, entropyPointer, Entropy.Length);
            Marshal.FreeHGlobal(inputPointer);
            Marshal.FreeHGlobal(entropyPointer);
        }
    }

    private static void RestrictDirectoryAcl(string directory)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("No se pudo identificar al usuario Windows actual.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var rights = FileSystemRights.FullControl;
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            rights,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            rights,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void SetPrivateFileAcl(string filePath)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("No se pudo identificar al usuario Windows actual.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(filePath).SetAccessControl(security);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private static class NativeMethods
    {
        internal const int CryptProtectUiForbidden = 0x1;

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string description,
            ref DataBlob optionalEntropy,
            IntPtr reserved,
            IntPtr prompt,
            int flags,
            out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            IntPtr description,
            ref DataBlob optionalEntropy,
            IntPtr reserved,
            IntPtr prompt,
            int flags,
            out DataBlob dataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
