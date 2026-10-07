using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using L2Launcher.Models;
using L2Launcher.Services;

namespace L2Launcher;

public partial class MainWindow : Window
{
    private readonly string _launcherRoot = Path.GetFullPath(AppContext.BaseDirectory);
    private readonly string _clientRoot;
    private readonly GitHubUpdateService _updateService;
    private readonly ClientInstallService _clientInstallService;
    private readonly LauncherSignatureService _signatureService;
    private readonly CancellationTokenSource _windowCancellation = new();
    private readonly Random _particleRandom = new();
    private readonly List<AmbientParticle> _particles = [];
    private readonly long _particleStartTimestamp = Stopwatch.GetTimestamp();
    private long _lastParticleFrame;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();
        _signatureService = new LauncherSignatureService();
        var settings = LoadSettings(_signatureService);
        _clientRoot = GetSafeClientRoot(settings.ClientDirectory);
        _updateService = new GitHubUpdateService(
            settings.GitHubOwner,
            settings.GitHubRepository,
            _clientRoot,
            _signatureService);
        _clientInstallService = new ClientInstallService(
            settings.ClientDownloadUrl,
            _launcherRoot,
            _clientRoot);

    }

    private static LauncherSettings LoadSettings(LauncherSignatureService signatureService)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "launcher.settings.json");
        var signaturePath = path + ".sig";
        if (!File.Exists(path) || !File.Exists(signaturePath))
        {
            throw new InvalidDataException(
                "Falta launcher.settings.json o su firma digital. Copia el paquete completo del launcher.");
        }

        var content = File.ReadAllBytes(path);
        var signature = File.ReadAllText(signaturePath, System.Text.Encoding.ASCII);
        if (!signatureService.Verify(content, signature))
        {
            throw new InvalidDataException(
                "La configuración del launcher no tiene una firma válida; no se iniciará con ajustes modificados.");
        }

        var settings = JsonSerializer.Deserialize<LauncherSettings>(
            content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return settings ?? throw new InvalidDataException(
            $"No se pudieron leer los ajustes del launcher: {path}");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetBusy(false);
        if (!_clientInstallService.IsInstalled)
        {
            StatusText.Text = "Cliente sin instalar. Pulsa ACTUALIZAR cuando quieras descargarlo.";
        }
        else if (!_updateService.IsConfigured)
        {
            StatusText.Text = "Cliente instalado. Las actualizaciones de GitHub se activarán al crear tu repositorio.";
        }

        StartAmbientParticles();
    }

    private static string GetSafeClientRoot(string configuredDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory) ||
            Path.IsPathRooted(configuredDirectory) ||
            configuredDirectory.Contains(':') ||
            configuredDirectory.Contains('\0'))
        {
            throw new InvalidDataException(
                "ClientDirectory en launcher.settings.json debe ser una carpeta relativa al launcher.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            configuredDirectory));
        var launcherRoot = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fullPath.StartsWith(
                launcherRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "ClientDirectory no puede salir de la carpeta del launcher.");
        }

        return fullPath;
    }

    private void StartAmbientParticles()
    {
        var width = ParticleCanvas.ActualWidth;
        var height = ParticleCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_particles.Count > 0)
        {
            return;
        }

        for (var index = 0; index < 42; index++)
        {
            var isEmber = index < 14;
            var size = isEmber
                ? 2.2 + _particleRandom.NextDouble() * 2.2
                : 1.0 + _particleRandom.NextDouble() * 1.1;
            var element = CreateParticleElement(isEmber, size);
            var particle = new AmbientParticle
            {
                Element = element,
                Width = isEmber ? size * 4 : size,
                Height = isEmber ? size * 4 : size,
                X = _particleRandom.NextDouble() * width,
                Y = _particleRandom.NextDouble() * height,
                Speed = isEmber
                    ? 10 + _particleRandom.NextDouble() * 20
                    : 5 + _particleRandom.NextDouble() * 12,
                Drift = -5 + _particleRandom.NextDouble() * 10,
                WobbleAmplitude = 4 + _particleRandom.NextDouble() * 15,
                WobbleSpeed = 0.25 + _particleRandom.NextDouble() * 0.65,
                Phase = _particleRandom.NextDouble() * Math.PI * 2,
                Opacity = isEmber
                    ? 0.28 + _particleRandom.NextDouble() * 0.38
                    : 0.12 + _particleRandom.NextDouble() * 0.23
            };
            _particles.Add(particle);
            ParticleCanvas.Children.Add(element);
            PositionParticle(particle, 0);
        }

        _lastParticleFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += ParticleCanvas_Rendering;
    }

    private UIElement CreateParticleElement(bool isEmber, double size)
    {
        if (!isEmber)
        {
            return new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromRgb(205, 196, 180)),
                IsHitTestVisible = false
            };
        }

        var glowSize = size * 4;
        var glow = new Ellipse
        {
            Width = glowSize,
            Height = glowSize,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(210, 255, 190, 92), 0),
                    new GradientStop(Color.FromArgb(100, 231, 112, 39), 0.38),
                    new GradientStop(Color.FromArgb(0, 231, 112, 39), 1)
                ]
            },
            IsHitTestVisible = false
        };
        var core = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(Color.FromRgb(255, 209, 130)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        return new Grid
        {
            Width = glowSize,
            Height = glowSize,
            Children = { glow, core },
            IsHitTestVisible = false
        };
    }

    private void ParticleCanvas_Rendering(object? sender, EventArgs e)
    {
        if (!IsVisible || _particles.Count == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Clamp(
            Stopwatch.GetElapsedTime(_lastParticleFrame, now).TotalSeconds,
            0,
            0.1);
        _lastParticleFrame = now;
        var width = ParticleCanvas.ActualWidth;
        var height = ParticleCanvas.ActualHeight;
        var time = Stopwatch.GetElapsedTime(_particleStartTimestamp, now).TotalSeconds;

        foreach (var particle in _particles)
        {
            particle.Y -= particle.Speed * elapsed;
            particle.X += particle.Drift * elapsed;
            if (particle.Y < -20)
            {
                particle.Y = height + _particleRandom.NextDouble() * 30;
                particle.X = _particleRandom.NextDouble() * width;
            }
            else if (particle.X < -20)
            {
                particle.X = width + 10;
            }
            else if (particle.X > width + 20)
            {
                particle.X = -10;
            }

            PositionParticle(particle, time);
        }
    }

    private static void PositionParticle(AmbientParticle particle, double time)
    {
        var element = particle.Element;
        var wobble = Math.Sin(time * particle.WobbleSpeed + particle.Phase) *
            particle.WobbleAmplitude;
        Canvas.SetLeft(element, particle.X + wobble - particle.Width / 2);
        Canvas.SetTop(element, particle.Y - particle.Height / 2);
        element.Opacity = particle.Opacity *
            (0.72 + 0.28 * (0.5 + 0.5 * Math.Sin(time * 1.4 + particle.Phase)));
    }

    private sealed class AmbientParticle
    {
        public required UIElement Element { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Speed { get; init; }
        public double Drift { get; init; }
        public double WobbleAmplitude { get; init; }
        public double WobbleSpeed { get; init; }
        public double Phase { get; init; }
        public double Opacity { get; init; }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_clientInstallService.IsInstalled)
        {
            await InstallBaseClientAsync();
            return;
        }

        if (_updateService.IsConfigured)
        {
            await UpdateClientAsync(launchWhenReady: false);
            return;
        }

        StatusText.Text = "Cliente instalado; falta crear tu repositorio público de parches en GitHub.";
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_clientInstallService.IsInstalled)
        {
            await InstallBaseClientAsync();
            return;
        }

        if (!_updateService.IsConfigured)
        {
            LaunchGame();
            return;
        }

        await UpdateClientAsync(launchWhenReady: true);
    }

    private async Task UpdateClientAsync(bool launchWhenReady)
    {
        SetBusy(true);
        try
        {
            var progress = new Progress<UpdateProgress>(value =>
            {
                StatusText.Text = value.Message;
                UpdateProgressBar.Value = value.Percent;
                ProgressPercent.Text = $"{value.Percent:0}%";
            });

            await _updateService.ApplyLatestReleaseAsync(progress);
            StatusText.Text = "Cliente actualizado y verificado";
            UpdateProgressBar.Value = 100;
            ProgressPercent.Text = "100%";

            if (launchWhenReady)
            {
                LaunchGame();
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(
                this,
                exception.Message,
                "Ascension Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        var clientIsInstalled = _clientInstallService.IsInstalled;
        PlayButton.Content = isBusy
            ? clientIsInstalled ? "ACTUALIZANDO" : "INSTALANDO"
            : clientIsInstalled ? "JUGAR" : "ACTUALIZAR";
        CheckButton.Content = "ACTUALIZAR PARCHES";
        CheckButton.Visibility = clientIsInstalled && _updateService.IsConfigured
            ? Visibility.Visible
            : Visibility.Collapsed;
        CheckButton.IsEnabled = !isBusy && clientIsInstalled && _updateService.IsConfigured;
        PlayButton.IsEnabled = !isBusy;
        ServerStatus.Text = isBusy
            ? clientIsInstalled ? "ACTUALIZANDO" : "DESCARGANDO CLIENTE"
            : clientIsInstalled ? "CLIENTE LISTO" : "CLIENTE PENDIENTE";
    }

    private async Task InstallBaseClientAsync()
    {
        if (_isBusy)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "¿Deseas descargar e instalar el cliente oficial Lineage II High Five ahora?\n\n" +
            "El archivo ocupa aproximadamente 6 GB. Se instalara dentro de la carpeta del launcher, " +
            "en la subcarpeta Client. Cuando termine, el boton cambiara a JUGAR. " +
            "El cliente que ya tienes en otra carpeta no se modificara.",
            "Confirmar descarga del cliente",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            StatusText.Text = "Descarga cancelada. Pulsa ACTUALIZAR cuando quieras instalar el cliente.";
            return;
        }

        SetBusy(true);
        StatusText.Text = "Descargando e instalando el cliente oficial High Five…";
        try
        {
            var progress = new Progress<ClientInstallProgress>(value =>
            {
                StatusText.Text = value.Message;
                UpdateProgressBar.Value = value.Percent;
                ProgressPercent.Text = $"{value.Percent:0}%";
            });

            await _clientInstallService.InstallAsync(
                progress,
                _windowCancellation.Token);
            if (_updateService.IsConfigured)
            {
                StatusText.Text = "Cliente base instalado. Buscando y aplicando el parche del servidor…";
                var patchProgress = new Progress<UpdateProgress>(value =>
                {
                    StatusText.Text = value.Message;
                    UpdateProgressBar.Value = value.Percent;
                    ProgressPercent.Text = $"{value.Percent:0}%";
                });

                await _updateService.ApplyLatestReleaseAsync(
                    patchProgress,
                    _windowCancellation.Token);
                UpdateProgressBar.Value = 100;
                ProgressPercent.Text = "100%";
                StatusText.Text = "Cliente instalado y parche del servidor aplicado. Pulsa JUGAR para iniciar.";
            }
            else
            {
                UpdateProgressBar.Value = 100;
                ProgressPercent.Text = "100%";
                StatusText.Text = "Cliente instalado. Pulsa JUGAR para iniciar.";
            }
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
            StatusText.Text = "Descarga cancelada. Vuelve a abrir el launcher para reintentar.";
        }
        catch (Exception exception)
        {
            StatusText.Text = _clientInstallService.IsInstalled
                ? $"Cliente instalado, pero no se pudo aplicar el parche: {exception.Message}"
                : $"No se pudo instalar el cliente: {exception.Message}";
            MessageBox.Show(
                this,
                exception.Message,
                _clientInstallService.IsInstalled
                    ? "No se pudo aplicar el parche"
                    : "Instalación del cliente",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CompositionTarget.Rendering -= ParticleCanvas_Rendering;
        _windowCancellation.Cancel();
        _signatureService.Dispose();
    }
    private void TitleBar_MouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LaunchGame()
    {
        var gamePath = Path.Combine(_clientRoot, "system", "l2.exe");
        if (!File.Exists(gamePath))
        {
            StatusText.Text = "No se encontró system\\l2.exe en la carpeta del cliente.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = gamePath,
                WorkingDirectory = Path.GetDirectoryName(gamePath)!,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(
                this,
                exception.Message,
                "Ascension Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
