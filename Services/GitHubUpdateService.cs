using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using L2Launcher.Models;

namespace L2Launcher.Services;

public sealed class GitHubUpdateService
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _owner;
    private readonly string _repository;
    private readonly string _clientRoot;
    private readonly LauncherSignatureService _signatureService;

    public GitHubUpdateService(
        string owner,
        string repository,
        string clientRoot,
        LauncherSignatureService signatureService)
    {
        _owner = owner;
        _repository = repository;
        _clientRoot = Path.GetFullPath(clientRoot);
        _signatureService = signatureService;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_owner) &&
        !string.IsNullOrWhiteSpace(_repository) &&
        Regex.IsMatch(_owner, "^[A-Za-z0-9-]+$") &&
        Regex.IsMatch(_repository, "^[A-Za-z0-9_.-]+$") &&
        !_owner.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase) &&
        !_repository.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase);

    public async Task ApplyLatestReleaseAsync(
        IProgress<UpdateProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ValidateRepositorySettings();
        progress.Report(new UpdateProgress
        {
            Message = "Buscando la versión publicada…",
            Percent = 0
        });

        var releaseUrl = $"https://api.github.com/repos/{_owner}/{_repository}/releases/latest";
        using var releaseResponse = await Http.GetAsync(
            releaseUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();

        await using var releaseStream =
            await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            releaseStream,
            JsonOptions,
            cancellationToken) ?? throw new InvalidDataException(
                "GitHub devolvió una respuesta de Release vacía.");

        if (release.Assets is null)
        {
            throw new InvalidDataException("El Release de GitHub no contiene una lista de activos.");
        }
        var manifestAsset = release.Assets.SingleOrDefault(asset =>
            string.Equals(asset.Name, "client-manifest.json", StringComparison.Ordinal));
        var signatureAsset = release.Assets.SingleOrDefault(asset =>
            string.Equals(asset.Name, "client-manifest.json.sig", StringComparison.Ordinal));
        if (manifestAsset is null || signatureAsset is null)
        {
            throw new InvalidDataException(
                "El último Release no contiene el manifiesto y su firma digital.");
        }

        var manifestContent = await DownloadAssetBytesAsync(manifestAsset, cancellationToken);
        var signatureContent = await DownloadAssetBytesAsync(signatureAsset, cancellationToken);
        if (!_signatureService.Verify(
                manifestContent,
                System.Text.Encoding.ASCII.GetString(signatureContent)))
        {
            throw new InvalidDataException(
                "La firma digital del manifiesto de parches no es válida; no se instalarán archivos.");
        }

        var manifest = DeserializeManifest(manifestContent);
        ValidateManifest(manifest);

        var pendingFiles = new List<ManifestFile>();
        foreach (var file in manifest.Files)
        {
            var targetPath = GetSafeTargetPath(file.Path);
            if (!File.Exists(targetPath) ||
                !await IsHashValidAsync(targetPath, file.Sha256, cancellationToken))
            {
                pendingFiles.Add(file);
            }
        }

        if (pendingFiles.Count == 0)
        {
            progress.Report(new UpdateProgress
            {
                Message = $"Cliente al día ({release.TagName})",
                Percent = 100
            });
            return;
        }

        var assetsByName = release.Assets.ToDictionary(asset => asset.Name, StringComparer.Ordinal);
        var assetIndexes = pendingFiles.Select(file => file.AssetIndex).Distinct().Order().ToArray();
        var completed = 0;

        foreach (var assetIndex in assetIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assetIndex < 0 || assetIndex >= manifest.Assets.Count)
            {
                throw new InvalidDataException(
                    $"El manifiesto contiene un índice de paquete no válido: {assetIndex}.");
            }

            var assetName = manifest.Assets[assetIndex];
            if (!assetsByName.TryGetValue(assetName, out var patchAsset))
            {
                throw new InvalidDataException(
                    $"El Release no incluye el paquete indicado en el manifiesto: {assetName}.");
            }

            progress.Report(new UpdateProgress
            {
                Message = $"Descargando {assetName}…",
                Percent = completed * 100d / pendingFiles.Count
            });
            var packagePath = Path.Combine(
                Path.GetTempPath(),
                $"ascension-{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadAssetAsync(patchAsset, packagePath, cancellationToken);
                using var archive = ZipFile.OpenRead(packagePath);
                var filesInAsset = pendingFiles.Where(file => file.AssetIndex == assetIndex).ToArray();
                foreach (var file in filesInAsset)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ExtractAndVerifyAsync(
                        archive,
                        file,
                        cancellationToken);
                    completed++;
                    progress.Report(new UpdateProgress
                    {
                        Message = $"Verificado: {file.Path}",
                        Percent = completed * 100d / pendingFiles.Count
                    });
                }
            }
            finally
            {
                if (File.Exists(packagePath))
                {
                    File.Delete(packagePath);
                }
            }
        }

        progress.Report(new UpdateProgress
        {
            Message = "Todos los archivos están actualizados",
            Percent = 100
        });
    }

    private async Task<byte[]> DownloadAssetBytesAsync(
        GitHubAsset asset,
        CancellationToken cancellationToken)
    {
        ValidateDownloadUrl(asset.BrowserDownloadUrl);
        using var response = await Http.GetAsync(
            asset.BrowserDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static ClientManifest DeserializeManifest(byte[] content)
    {
        return JsonSerializer.Deserialize<ClientManifest>(content, JsonOptions) ??
            throw new InvalidDataException(
                "No se pudo leer el manifiesto del cliente.");
    }

    private async Task DownloadAssetAsync(
        GitHubAsset asset,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ValidateDownloadUrl(asset.BrowserDownloadUrl);
        using var response = await Http.GetAsync(
            asset.BrowserDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private async Task ExtractAndVerifyAsync(
        ZipArchive archive,
        ManifestFile file,
        CancellationToken cancellationToken)
    {
        var entryName = file.Path.Replace('\\', '/');
        var entry = archive.GetEntry(entryName) ??
            throw new InvalidDataException($"El paquete no contiene {file.Path}.");
        var destinationPath = GetSafeTargetPath(file.Path);
        var destinationDirectory = Path.GetDirectoryName(destinationPath) ??
            throw new InvalidDataException($"La ruta de destino no es válida: {file.Path}");
        Directory.CreateDirectory(destinationDirectory);

        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = entry.Open())
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            if (!await IsHashValidAsync(temporaryPath, file.Sha256, cancellationToken))
            {
                throw new InvalidDataException(
                    $"La verificación SHA-256 falló para {file.Path}; no se instaló el archivo.");
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetSafeTargetPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':') ||
            relativePath.Contains('\0'))
        {
            throw new InvalidDataException($"Ruta insegura en el manifiesto: {relativePath}");
        }

        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_clientRoot, normalizedPath));
        var rootWithSeparator = _clientRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _clientRoot
            : _clientRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"La ruta sale de la carpeta del cliente: {relativePath}");
        }

        return fullPath;
    }

    private static async Task<bool> IsHashValidAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(
            hash,
            Convert.FromHexString(expectedHash));
    }

    private static void ValidateManifest(ClientManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Versión de manifiesto no compatible: {manifest.SchemaVersion}.");
        }

        if (manifest.Assets is null || manifest.Files is null ||
            manifest.Assets.Count == 0 || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("El manifiesto no contiene paquetes ni archivos.");
        }

        if (manifest.Assets.Any(name =>
                string.IsNullOrWhiteSpace(name) ||
                Path.GetFileName(name) != name ||
                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("El manifiesto contiene un nombre de paquete no válido.");
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null)
            {
                throw new InvalidDataException("El manifiesto contiene una entrada vacía.");
            }

            if (string.IsNullOrWhiteSpace(file.Path) ||
                string.IsNullOrWhiteSpace(file.Sha256) ||
                file.AssetIndex < 0 || file.AssetIndex >= manifest.Assets.Count ||
                !Regex.IsMatch(file.Sha256, "^[0-9a-fA-F]{64}$") ||
                !seenPaths.Add(file.Path))
            {
                throw new InvalidDataException(
                    $"La entrada del manifiesto no es válida: {file.Path ?? "(sin ruta)"}");
            }
        }
    }

    private void ValidateRepositorySettings()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Configura GitHubOwner y GitHubRepository en launcher.settings.json.");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AscensionLauncher/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static void ValidateDownloadUrl(string downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub devolvió una URL de descarga no válida.");
        }
    }
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = string.Empty;

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; init; } = [];
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; init; } = string.Empty;
}

public sealed class ClientManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("assets")]
    public List<string> Assets { get; init; } = [];

    [JsonPropertyName("files")]
    public List<ManifestFile> Files { get; init; } = [];
}

public sealed class ManifestFile
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("assetIndex")]
    public int AssetIndex { get; init; }
}
