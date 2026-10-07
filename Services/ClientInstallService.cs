using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using L2Launcher.Models;

namespace L2Launcher.Services;

public sealed class ClientInstallService
{
    private const long MaximumExpandedBytes = 40L * 1024 * 1024 * 1024;
    private const long FreeSpaceReserveBytes = 1024L * 1024 * 1024;
    private const int CopyBufferSize = 128 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _clientUrl;
    private readonly string _clientRoot;
    private readonly string _launcherRoot;

    public ClientInstallService(string clientUrl, string launcherRoot, string clientRoot)
    {
        if (!Uri.TryCreate(clientUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "La dirección de descarga del cliente debe usar HTTPS.");
        }

        _clientUrl = clientUrl;
        _launcherRoot = Path.GetFullPath(launcherRoot);
        _clientRoot = Path.GetFullPath(clientRoot);
    }

    public bool IsInstalled =>
        File.Exists(Path.Combine(_clientRoot, "system", "l2.exe"));

    public async Task InstallAsync(
        IProgress<ClientInstallProgress> progress,
        CancellationToken cancellationToken = default)
    {
        if (IsInstalled)
        {
            progress.Report(new ClientInstallProgress
            {
                Message = "El cliente ya está instalado.",
                Percent = 100
            });
            return;
        }

        if (Directory.Exists(_clientRoot) &&
            Directory.EnumerateFileSystemEntries(_clientRoot).Any())
        {
            throw new IOException(
                $"La carpeta de instalación ya contiene archivos pero no se encuentra system\\l2.exe: {_clientRoot}");
        }

        var stageRoot = Path.Combine(_launcherRoot, ".Client.installing");
        if (!IsChildPath(_launcherRoot, stageRoot))
        {
            throw new InvalidOperationException("La carpeta temporal de instalación no es válida.");
        }

        if (Directory.Exists(stageRoot))
        {
            Directory.Delete(stageRoot, recursive: true);
        }

        Directory.CreateDirectory(stageRoot);
        try
        {
            var archivePath = Path.Combine(stageRoot, "client-download.zip");
            await DownloadArchiveAsync(archivePath, progress, cancellationToken);

            var payloadPath = Path.Combine(stageRoot, "Client");
            Directory.CreateDirectory(payloadPath);
            await ExtractArchiveAsync(
                archivePath,
                payloadPath,
                progress,
                cancellationToken);

            var extractedGameExe = Path.Combine(payloadPath, "system", "l2.exe");
            if (!File.Exists(extractedGameExe))
            {
                throw new InvalidDataException(
                    "El archivo descargado no contiene system\\l2.exe; la instalación se canceló.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(_clientRoot) &&
                Directory.EnumerateFileSystemEntries(_clientRoot).Any())
            {
                throw new IOException(
                    $"La carpeta de destino ya contiene archivos; no se sobrescribieron: {_clientRoot}");
            }

            if (Directory.Exists(_clientRoot))
            {
                Directory.Delete(_clientRoot);
            }

            Directory.Move(payloadPath, _clientRoot);
            progress.Report(new ClientInstallProgress
            {
                Message = "Cliente base instalado correctamente. El cliente conserva sus archivos originales.",
                Percent = 100
            });
        }
        finally
        {
            if (Directory.Exists(stageRoot))
            {
                Directory.Delete(stageRoot, recursive: true);
            }
        }
    }

    private async Task DownloadArchiveAsync(
        string archivePath,
        IProgress<ClientInstallProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new ClientInstallProgress
        {
            Message = "Conectando con la descarga oficial del cliente High Five…",
            Percent = 0
        });

        using var response = await Http.GetAsync(
            _clientUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.RequestMessage?.RequestUri is not { Scheme: "https" })
        {
            throw new InvalidDataException(
                "La descarga del cliente no terminó en una dirección HTTPS segura.");
        }

        var reportedLength = response.Content.Headers.ContentLength;
        if (reportedLength is null or <= 0)
        {
            throw new InvalidDataException(
                "El servidor no informó el tamaño del archivo del cliente.");
        }

        var totalBytes = reportedLength.Value;
        var destinationRoot = Path.GetPathRoot(_launcherRoot) ??
            throw new IOException("No se pudo determinar el disco de instalación.");
        var requiredDownloadSpace = checked(totalBytes + FreeSpaceReserveBytes);
        if (new DriveInfo(destinationRoot).AvailableFreeSpace < requiredDownloadSpace)
        {
            throw new IOException(
                "No hay espacio libre suficiente para guardar el archivo de instalación y continuar.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[CopyBufferSize];
        long bytesDownloaded = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            bytesDownloaded = checked(bytesDownloaded + count);
            progress.Report(new ClientInstallProgress
            {
                Message = $"Descargando cliente: {FormatBytes(bytesDownloaded)} de {FormatBytes(totalBytes)}",
                Percent = 60d * bytesDownloaded / totalBytes
            });
        }

        await destination.FlushAsync(cancellationToken);
        if (bytesDownloaded != totalBytes)
        {
            throw new InvalidDataException(
                $"La descarga quedó incompleta: se recibieron {bytesDownloaded} de {totalBytes} bytes.");
        }
    }

    private static async Task ExtractArchiveAsync(
        string archivePath,
        string payloadPath,
        IProgress<ClientInstallProgress> progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var files = new List<(ZipArchiveEntry Entry, string RelativePath)>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var relativePath = entry.FullName
                .Replace('\\', '/')
                .TrimEnd('/');
            if (string.IsNullOrEmpty(relativePath) ||
                Path.IsPathRooted(relativePath) ||
                relativePath.Contains(':') ||
                relativePath.Contains('\0') ||
                relativePath.Split('/').Any(component =>
                    component is "" or "." or ".."))
            {
                throw new InvalidDataException(
                    $"El archivo contiene una ruta no segura: {entry.FullName}");
            }

            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000)
            {
                throw new InvalidDataException(
                    $"El archivo de instalación contiene un enlace no permitido: {entry.FullName}");
            }

            var target = Path.GetFullPath(Path.Combine(
                payloadPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsChildPath(payloadPath, target))
            {
                throw new InvalidDataException(
                    $"La ruta de instalación se sale del cliente: {entry.FullName}");
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (!targets.Add(relativePath))
            {
                throw new InvalidDataException(
                    $"El archivo ZIP repite una ruta: {entry.FullName}");
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException(
                    "El archivo ZIP supera el límite de seguridad de 40 GiB extraídos.");
            }

            files.Add((entry, relativePath));
        }

        var installDrivePath = Path.GetPathRoot(payloadPath) ??
            throw new IOException("No se pudo determinar el disco de instalación.");
        var requiredSpace = checked(
            expandedBytes + new FileInfo(archivePath).Length + FreeSpaceReserveBytes);
        if (new DriveInfo(installDrivePath).AvailableFreeSpace < requiredSpace)
        {
            throw new IOException(
                $"Se necesitan al menos {FormatBytes(requiredSpace)} libres para extraer el cliente. " +
                $"Espacio disponible: {FormatBytes(new DriveInfo(installDrivePath).AvailableFreeSpace)}.");
        }

        var extractedBytes = 0L;
        var buffer = new byte[CopyBufferSize];
        progress.Report(new ClientInstallProgress
        {
            Message = $"Preparando instalación: {files.Count:N0} archivos…",
            Percent = 60
        });

        foreach (var (entry, relativePath) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(
                payloadPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var source = entry.Open();
            await using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }

            extractedBytes = checked(extractedBytes + entry.Length);
            progress.Report(new ClientInstallProgress
            {
                Message = $"Instalando cliente: {Path.GetFileName(relativePath)}",
                Percent = expandedBytes == 0
                    ? 100
                    : 60d + 39d * extractedBytes / expandedBytes
            });
        }
    }

    private static bool IsChildPath(string root, string path)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var canonicalPath = Path.GetFullPath(path);
        return canonicalPath.StartsWith(
            canonicalRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        const double gibibyte = 1024d * 1024 * 1024;
        return bytes >= gibibyte
            ? $"{bytes / gibibyte:0.0} GiB"
            : $"{bytes / (1024d * 1024):0} MiB";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromHours(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AscensionLauncher/1.0");
        return client;
    }
}
