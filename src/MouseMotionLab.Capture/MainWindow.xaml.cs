using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseMotionLab.Capture.Interop;
using MouseMotionLab.Capture.Services;
using MouseMotionLab.Core.Capture;
using MouseMotionLab.Core.Contracts;
using MouseMotionLab.Core.Data;
using MouseMotionLab.Core.Domain;
using OpenTK.Wpf;

namespace MouseMotionLab.Capture;

using Path = System.IO.Path;

public partial class MainWindow : Window
{
    private readonly string _repositoryRoot;
    private readonly DatasetRepository _repository;
    private readonly RawInputCaptureService _rawInput;
    private readonly FlickCaptureStateMachine _captureState;
    private readonly TrajectoryGeneratorClient _generator;
    private readonly FirstPersonLookController _lookController = new();
    private readonly OpenGlTrainingSceneRenderer _sceneRenderer = new();
    private readonly ArmingShortcutStore _armingShortcutStore = new();
    private readonly CancellationTokenSource _windowLifetime = new();
    private ArmingShortcut _armingShortcut = ArmingShortcut.Default;
    private bool _choosingArmingShortcut;
    private bool _armingInProgress;
    private long _captureArmedQpc;
    private readonly DispatcherTimer _visualTimer;
    private readonly List<(Guid Id, int Dx, int Dy)> _coverageEndpoints = new();
    private DatasetWriteQueue? _writeQueue;
    private SpeedCurvesWindow? _speedCurvesWindow;
    private CursorConfinementLease? _cursorLease;
    private CaptureProfile? _profile;
    private CaptureSession? _session;
    private string? _profileSignature;
    private Guid? _lastTrialId;
    private int _trialOrdinal;
    private readonly Stopwatch _renderRateStopwatch = Stopwatch.StartNew();
    private readonly List<(int Dx, int Dy)> _displayTrajectoryDeltas = new();
    private TrajectoryResult? _activeReplay;
    private Stopwatch? _replayStopwatch;
    private long _replayScheduledMicroseconds;
    private int _replayIndex;
    private double _replayDegreesPerCount;
    private int _renderedFrames;
    private double _renderFps;
    private bool _initialized;
    private volatile bool _closing;
    private int _mouseLookActive;
    private int _pendingLookDx;
    private int _pendingLookDy;
    private string? _mouseLookDeviceId;
    private bool _isFullScreen;
    private WindowState _windowStateBeforeFullScreen;
    private WindowStyle _windowStyleBeforeFullScreen;
    private ResizeMode _resizeModeBeforeFullScreen;
    private bool _topmostBeforeFullScreen;

    public MainWindow()
    {
        InitializeComponent();
        GameViewport.Start(new GLWpfControlSettings
        {
            MajorVersion = 4,
            MinorVersion = 1,
            RenderContinuously = true
        });
        _repositoryRoot = FindRepositoryRoot();
        _armingShortcut = _armingShortcutStore.Load();
        UpdateArmingShortcutUi();
        _repository = new DatasetRepository(Path.Combine(_repositoryRoot, "data", "mousemotion.db"));
        _rawInput = new RawInputCaptureService();
        _captureState = new FlickCaptureStateMachine(_rawInput.QpcFrequency);
        _generator = new TrajectoryGeneratorClient(_repositoryRoot);
        _visualTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _visualTimer.Tick += VisualTimer_Tick;

        _rawInput.PacketHandlerAsync = RawInput_PacketReceivedAsync;
        _rawInput.PacketDropped += RawInput_PacketDropped;
        _rawInput.DevicesChanged += RawInput_DevicesChanged;
        _rawInput.ServiceError += RawInput_ServiceError;
        GenerationModeComboBox.SelectionChanged += GenerationModeComboBox_SelectionChanged;
        Loaded += MainWindow_Loaded;
        Deactivated += MainWindow_Deactivated;
        GameViewport.SizeChanged += GameViewport_SizeChanged;
        CoverageCanvas.SizeChanged += CoverageCanvas_SizeChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _repository.InitializeAsync();
            _writeQueue = new DatasetWriteQueue(_repository);
            _writeQueue.WriteFailed += WriteQueue_WriteFailed;
            await _rawInput.StartAsync();
            RefreshDeviceList();

            var previousEndpoints = await _repository.GetAcceptedEndpointsAsync();
            foreach (var endpoint in previousEndpoints.Reverse())
            {
                _coverageEndpoints.Add((Guid.Empty, endpoint.Dx, endpoint.Dy));
            }

            DrawCoverage();
            InitializeGenerationMode();
            _visualTimer.Start();
            _initialized = true;
            SpeedCurvesButton.IsEnabled = true;
            ArmingShortcutButton.IsEnabled = true;
            ResetArmingShortcutButton.IsEnabled = true;
            CaptureStateText.Text = "Prêt — clique dans la scène";
            FooterStatusText.Text = $"Dataset : {Path.Combine(_repositoryRoot, "data", "mousemotion.db")}";
        }
        catch (Exception exception)
        {
            ShowError("Initialisation impossible", exception);
            CaptureStateText.Text = "Erreur d’initialisation";
        }
    }

    private async void Arm_Click(object sender, RoutedEventArgs e) => await ArmAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelCapture("cancelled_by_user");

    private async void Exclude_Click(object sender, RoutedEventArgs e) => await ExcludeLastAsync();

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDeviceList();

    private async void Generate_Click(object sender, RoutedEventArgs e) => await GenerateAndReplayAsync();

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ArmingShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_choosingArmingShortcut)
        {
            CancelArmingShortcutSelection();
            return;
        }

        ReleaseMouseLook();
        _choosingArmingShortcut = true;
        ArmingShortcutButton.Content = "Touche ou clic…";
        ArmingShortcutHintText.Text = "Appuie sur une touche ou clique avec le bouton souhaité · Échap pour annuler";
        _ = ArmingShortcutButton.Focus();
    }

    private void ResetArmingShortcut_Click(object sender, RoutedEventArgs e) =>
        SaveArmingShortcut(ArmingShortcut.Default);

    private void ArmingShortcut_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_choosingArmingShortcut)
        {
            CancelArmingShortcutSelection();
        }
    }

    private void CancelArmingShortcutSelection()
    {
        _choosingArmingShortcut = false;
        UpdateArmingShortcutUi();
    }

    private void SaveArmingShortcut(ArmingShortcut shortcut)
    {
        try
        {
            _armingShortcutStore.Save(shortcut);
            Volatile.Write(ref _armingShortcut, shortcut);
            _choosingArmingShortcut = false;
            UpdateArmingShortcutUi();
            FooterStatusText.Text = $"Raccourci d’armement enregistré : {_armingShortcut.DisplayName}.";
        }
        catch (Exception exception)
        {
            CancelArmingShortcutSelection();
            ShowError("Enregistrement du raccourci impossible", exception);
        }
    }

    private void UpdateArmingShortcutUi()
    {
        ArmButton.Content = $"Armer  [{_armingShortcut.DisplayName}]";
        ArmingShortcutButton.Content = _armingShortcut.DisplayName;
        ArmingShortcutHintText.Text = "Clique puis appuie sur une touche ou un bouton de souris · Ctrl, Alt et Maj possibles";
        if (IsMouseLookActive)
        {
            MouseLockHintText.Text = $"Souris capturée · {_armingShortcut.DisplayName} pour armer · Échap pour libérer";
            CaptureStateText.Text = $"Vue 3D active — {_armingShortcut.DisplayName} pour armer";
        }
    }

    private void SpeedCurves_Click(object sender, RoutedEventArgs e)
    {
        if (_speedCurvesWindow is not null)
        {
            if (_speedCurvesWindow.WindowState == WindowState.Minimized)
            {
                _speedCurvesWindow.WindowState = WindowState.Normal;
            }

            _speedCurvesWindow.Activate();
            _ = _speedCurvesWindow.RefreshAsync();
            return;
        }

        ReleaseMouseLook();
        _speedCurvesWindow = new SpeedCurvesWindow(_repository, _writeQueue) { Owner = this };
        _speedCurvesWindow.Closed += (_, _) => _speedCurvesWindow = null;
        _speedCurvesWindow.Show();
    }

    private bool IsMouseLookActive => Volatile.Read(ref _mouseLookActive) != 0;

    private void GameViewport_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseLookActive)
        {
            _ = TryAcquireMouseLook();
            e.Handled = true;
        }
    }

    private bool TryAcquireMouseLook(RawInputDeviceDescriptor? descriptor = null)
    {
        if (!_initialized)
        {
            return false;
        }

        descriptor ??= DeviceComboBox.SelectedItem as RawInputDeviceDescriptor;
        if (descriptor is null)
        {
            FooterStatusText.Text = "Sélectionne une souris Raw Input avant d’entrer dans la scène.";
            return false;
        }

        if (IsMouseLookActive && string.Equals(
                _mouseLookDeviceId,
                descriptor.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            _ = GameViewport.Focus();
            return true;
        }

        ReleaseMouseLook();
        try
        {
            _cursorLease = CursorConfinementLease.Acquire(GameViewport);
            _ = GameViewport.Focus();
            if (!Mouse.Capture(GameViewport, CaptureMode.Element))
            {
                throw new InvalidOperationException("WPF n’a pas pu capturer la souris.");
            }

            GameViewport.Cursor = Cursors.None;
            _mouseLookDeviceId = descriptor.Id;
            Volatile.Write(ref _mouseLookActive, 1);
            _rawInput.SetCaptureGate(true, descriptor.Id);
            MouseLockHintText.Text = $"Souris capturée · {_armingShortcut.DisplayName} pour armer · Échap pour libérer";
            MouseLockHint.Opacity = 0.72;
            CaptureStateText.Text = $"Vue 3D active — {_armingShortcut.DisplayName} pour armer";
            FooterStatusText.Text = "La caméra suit les deltas Raw Input de la souris sélectionnée.";
            return true;
        }
        catch (Exception exception)
        {
            ReleaseMouseLook();
            FooterStatusText.Text = $"Capture de la souris impossible : {exception.Message}";
            return false;
        }
    }

    private void ReleaseMouseLook()
    {
        Volatile.Write(ref _mouseLookActive, 0);
        _mouseLookDeviceId = null;
        Interlocked.Exchange(ref _pendingLookDx, 0);
        Interlocked.Exchange(ref _pendingLookDy, 0);
        _rawInput.SetCaptureGate(false, null);
        if (ReferenceEquals(Mouse.Captured, GameViewport))
        {
            _ = Mouse.Capture(null);
        }

        GameViewport.Cursor = Cursors.Arrow;
        _cursorLease?.Dispose();
        _cursorLease = null;
        if (!_closing)
        {
            MouseLockHintText.Text =
                "Clique dans la scène pour capturer la souris · Échap pour la libérer";
            MouseLockHint.Opacity = 1d;
        }
    }

    private void RefreshMouseLookBounds()
    {
        if (!IsMouseLookActive)
        {
            return;
        }

        try
        {
            _cursorLease?.Dispose();
            _cursorLease = CursorConfinementLease.Acquire(GameViewport);
            _ = Mouse.Capture(GameViewport, CaptureMode.Element);
            GameViewport.Cursor = Cursors.None;
        }
        catch (Exception exception)
        {
            ReleaseMouseLook();
            FooterStatusText.Text = $"La capture souris a été libérée : {exception.Message}";
        }
    }

    private async Task ArmAsync(long? armedQpc = null)
    {
        if (_choosingArmingShortcut)
        {
            CancelArmingShortcutSelection();
        }

        if (!_initialized || _closing || _armingInProgress || _captureState.Lifecycle != CaptureLifecycle.Idle)
        {
            return;
        }

        _armingInProgress = true;
        SetCaptureControlsEnabled(false);
        try
        {
            var descriptor = DeviceComboBox.SelectedItem as RawInputDeviceDescriptor
                ?? throw new InvalidOperationException("Sélectionne une souris Raw Input.");
            var dpi = ParseInteger(DpiTextBox.Text, "DPI", 100, 100_000);
            var pollingHz = ParseComboInteger(PollingComboBox, "polling rate", 125, 8_000);
            var displayScale = ParseDouble(DisplayScaleTextBox.Text, "sensibilité 3D", 0.1, 100);
            await EnsureSessionAsync(descriptor, dpi, pollingHz, displayScale);
            if (_closing || (armedQpc.HasValue && !IsMouseLookActive))
            {
                return;
            }

            StopReplay();
            ClearTrajectoryOverlay();
            if (!IsMouseLookActive && !TryAcquireMouseLook(descriptor))
            {
                throw new InvalidOperationException("Impossible de capturer la souris dans la scène 3D.");
            }

            // Refresh the cursor-origin diagnostic without warping the pointer or
            // changing the camera orientation. Arming must never recenter the view.
            _rawInput.SetCaptureGate(true, descriptor.Id);
            var timestamp = armedQpc ?? _rawInput.TimestampNow();
            Volatile.Write(ref _captureArmedQpc, timestamp);
            _captureState.Arm(_session!.Id, ++_trialOrdinal, timestamp);
            SetCaptureControlsEnabled(false);
            CaptureStateText.Text = "Armé — commence le flick";
            FooterStatusText.Text =
                "Orientation conservée · premier delta non nul pour démarrer · clic gauche pour terminer.";
        }
        catch (Exception exception)
        {
            _rawInput.SetCaptureGate(false, null);
            _ = _captureState.Cancel(_rawInput.TimestampNow(), "arm_failed");
            ReleaseMouseLook();
            ShowError("Impossible d’armer la capture", exception);
        }
        finally
        {
            _armingInProgress = false;
            if (!_closing)
            {
                SetCaptureControlsEnabled(_captureState.Lifecycle == CaptureLifecycle.Idle);
            }
        }
    }

    private async Task EnsureSessionAsync(
        RawInputDeviceDescriptor descriptor,
        int dpi,
        int pollingHz,
        double displayScale)
    {
        var signature = string.Create(
            CultureInfo.InvariantCulture,
            $"{descriptor.Id}|{dpi}|{pollingHz}|{displayScale:R}");
        if (_session is not null && string.Equals(signature, _profileSignature, StringComparison.Ordinal))
        {
            return;
        }

        _profile = new CaptureProfile(
            Guid.NewGuid(),
            descriptor.Id,
            descriptor.DisplayName,
            dpi,
            pollingHz,
            displayScale,
            DateTimeOffset.UtcNow);
        _profile.Validate();
        await _repository.UpsertProfileAsync(_profile);

        _session = new CaptureSession(
            Guid.NewGuid(),
            _profile.Id,
            DateTimeOffset.UtcNow,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            Environment.OSVersion.VersionString,
            _rawInput.QpcFrequency);
        await _repository.CreateSessionAsync(_session);
        _profileSignature = signature;
        _trialOrdinal = 0;
    }

    private async Task RawInput_PacketReceivedAsync(RawMouseEvent mouseEvent, ModifierKeys modifiers)
    {
        if (_closing)
        {
            return;
        }

        var cancellationToken = _windowLifetime.Token;
        if (IsMouseLookActive && mouseEvent.HasMovement)
        {
            Interlocked.Add(ref _pendingLookDx, mouseEvent.Dx);
            Interlocked.Add(ref _pendingLookDy, mouseEvent.Dy);
        }

        var shortcut = Volatile.Read(ref _armingShortcut);
        if (IsMouseLookActive && _captureState.Lifecycle == CaptureLifecycle.Idle &&
            shortcut.MatchesMouseDown(mouseEvent.ButtonFlags, modifiers))
        {
            // Wait only on the packet consumer, never on the native input thread.
            // The arming click is consumed; buffered movement follows it in order.
            // In particular, binding left click must not instantly finish a trial.
            await Dispatcher.InvokeAsync(
                    () => ArmAsync(mouseEvent.QpcTicks), DispatcherPriority.Send, cancellationToken)
                .Task.Unwrap().WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_captureState.Lifecycle != CaptureLifecycle.Idle &&
            mouseEvent.QpcTicks < Volatile.Read(ref _captureArmedQpc))
        {
            return;
        }

        var trial = _captureState.Handle(mouseEvent);
        if (trial is null)
        {
            return;
        }

        if (!IsMouseLookActive)
        {
            _rawInput.SetCaptureGate(false, null);
        }

        _ = Dispatcher.InvokeAsync(async () => await CompleteTrialAsync(trial), DispatcherPriority.Send);
    }

    private void RawInput_PacketDropped(object? sender, EventArgs e)
    {
        InvalidateCapture("raw_input_queue_overflow");
        _ = Dispatcher.InvokeAsync(ReleaseMouseLook, DispatcherPriority.Send);
    }

    private async Task CompleteTrialAsync(TrialCapture trial)
    {
        SetCaptureControlsEnabled(true);
        _lastTrialId = trial.Id;

        if (_writeQueue is null || !_writeQueue.TryEnqueue(trial))
        {
            var invalidTrial = trial with
            {
                Status = TrialStatus.Invalid,
                RejectionReason = "dataset_write_queue_overflow"
            };
            await _repository.SaveTrialAsync(invalidTrial);
            trial = invalidTrial;
        }

        if (trial.Status == TrialStatus.Accepted)
        {
            _coverageEndpoints.Add((trial.Id, trial.Metrics.TargetDx, trial.Metrics.TargetDy));
            DrawCoverage();
        }

        DrawTrialTrajectory(trial);
        UpdateTrialStats(trial);
        if (_speedCurvesWindow is not null)
        {
            _ = _speedCurvesWindow.RefreshAsync();
        }
        CaptureStateText.Text = trial.Status == TrialStatus.Accepted
            ? $"Essai enregistré — {_armingShortcut.DisplayName} pour recommencer"
            : $"Essai {trial.Status.ToString().ToLowerInvariant()} — {trial.RejectionReason}";
        FooterStatusText.Text =
            $"Essai #{trial.Ordinal} · {trial.Metrics.EventCount} paquets Raw Input · caméra toujours capturée.";
    }

    private void CancelCapture(string reason)
    {
        if (_captureState.Lifecycle == CaptureLifecycle.Idle)
        {
            return;
        }

        _rawInput.SetCaptureGate(false, null);
        var trial = _captureState.Cancel(_rawInput.TimestampNow(), reason);
        if (trial is not null)
        {
            CompleteTrialOnUiThread(trial);
            return;
        }

        SetCaptureControlsEnabled(true);
        CaptureStateText.Text = $"Capture annulée — {_armingShortcut.DisplayName} pour armer";
        FooterStatusText.Text = "Capture annulée avant le premier mouvement.";
    }

    private void InvalidateCapture(string reason)
    {
        _rawInput.SetCaptureGate(false, null);
        var trial = _captureState.Invalidate(_rawInput.TimestampNow(), reason);
        if (trial is not null)
        {
            CompleteTrialOnUiThread(trial);
        }
    }

    private void CompleteTrialOnUiThread(TrialCapture trial)
    {
        if (Dispatcher.CheckAccess())
        {
            _ = CompleteTrialAsync(trial);
            return;
        }

        _ = Dispatcher.InvokeAsync(async () => await CompleteTrialAsync(trial), DispatcherPriority.Send);
    }

    private async Task ExcludeLastAsync()
    {
        if (_lastTrialId is not { } trialId)
        {
            FooterStatusText.Text = "Aucun essai à exclure.";
            return;
        }

        if (_writeQueue is null)
        {
            await _repository.SetTrialStatusAsync(trialId, TrialStatus.Excluded, "excluded_by_user");
        }
        else if (!_writeQueue.TryExclude(trialId))
        {
            // Wait for a slot so this update remains ordered after the queued save.
            await _writeQueue.EnqueueExcludeAsync(trialId);
        }

        _coverageEndpoints.RemoveAll(item => item.Id == trialId);
        DrawCoverage();
        TrialStatusText.Text = "excluded";
        FooterStatusText.Text = "Le dernier essai est conservé en base mais exclu de l’entraînement.";
        _lastTrialId = null;
        if (_speedCurvesWindow is not null)
        {
            _ = _speedCurvesWindow.RefreshAsync();
        }
    }

    private async Task GenerateAndReplayAsync()
    {
        if (_captureState.Lifecycle != CaptureLifecycle.Idle)
        {
            CancelCapture("generation_started");
        }

        GenerateButton.IsEnabled = false;
        try
        {
            var dx = ParseInteger(GenerateDxTextBox.Text, "dx", int.MinValue, int.MaxValue);
            var dy = ParseInteger(GenerateDyTextBox.Text, "dy", int.MinValue, int.MaxValue);
            var dpi = ParseInteger(DpiTextBox.Text, "DPI", 100, 100_000);
            var outputHz = ParseComboInteger(PollingComboBox, "polling rate", 125, 8_000);
            var seed = long.TryParse(GenerateSeedTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeed)
                ? parsedSeed
                : throw new FormatException("Seed invalide.");
            var mode = SelectedGenerationMode();
            var artifact = ArtifactPathTextBox.Text.Trim();
            var absoluteArtifact = ResolveArtifactDirectory(artifact);
            if (mode == "diffusion" && !IsModelArtifactAvailable(absoluteArtifact))
            {
                throw new InvalidOperationException(
                    "Aucun modèle diffusion entraîné n’est disponible. Exécute scripts\\train-example.ps1.");
            }

            if (mode == "baseline" && !Directory.Exists(Path.Combine(_repositoryRoot, artifact)))
            {
                artifact = string.Empty;
            }

            var request = new TrajectoryRequest(dx, dy, dpi, outputHz, seed, mode);
            GenerationStatusText.Text = "Génération en cours…";
            var result = await _generator.GenerateAsync(request, artifact);
            StartReplay(result, dpi, ParseDouble(DisplayScaleTextBox.Text, "sensibilité 3D", 0.1, 100));
            var warningText = result.Warnings.Count == 0 ? "aucun avertissement" : string.Join(" · ", result.Warnings);
            GenerationStatusText.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"{result.ModelVersion} · {result.Samples.Count} samples · " +
                $"couverture {result.CoverageScore:F3} · {warningText}");
        }
        catch (Exception exception)
        {
            GenerationStatusText.Text = exception.Message;
            ShowError("Génération impossible", exception);
        }
        finally
        {
            GenerateButton.IsEnabled = true;
        }
    }

    private void StartReplay(TrajectoryResult result, int dpi, double displayScale)
    {
        StopReplay();
        ClearTrajectoryOverlay();
        _activeReplay = result;
        _replayStopwatch = Stopwatch.StartNew();
        _replayScheduledMicroseconds = 0;
        _replayIndex = 0;
        _replayDegreesPerCount = 25.4d * displayScale / dpi;
        CaptureStateText.Text = "Replay sandbox dans la caméra 3D";
    }

    private void StopReplay()
    {
        _activeReplay = null;
        _replayStopwatch = null;
        _replayScheduledMicroseconds = 0;
        _replayIndex = 0;
    }

    private void InitializeGenerationMode()
    {
        var artifact = ResolveArtifactDirectory(ArtifactPathTextBox.Text.Trim());
        GenerationModeComboBox.SelectedIndex = IsModelArtifactAvailable(artifact) ? 0 : 1;
        UpdateGenerationModeHint();
    }

    private void GenerationModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateGenerationModeHint();

    private void UpdateGenerationModeHint()
    {
        if (GenerationStatusText is null || ArtifactPathTextBox is null)
        {
            return;
        }

        if (SelectedGenerationMode() == "baseline")
        {
            GenerationStatusText.Text =
                "Baseline mathématique : ligne minimum-jerk, sans trajectoires apprises.";
            return;
        }

        var artifact = ResolveArtifactDirectory(ArtifactPathTextBox.Text.Trim());
        GenerationStatusText.Text = IsModelArtifactAvailable(artifact)
            ? "Modèle diffusion entraîné détecté — les trajectoires du dataset seront utilisées."
            : "Aucun modèle entraîné détecté. Lance scripts\\train-example.ps1 avant la diffusion.";
    }

    private string SelectedGenerationMode() =>
        (GenerationModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "baseline";

    private string ResolveArtifactDirectory(string artifactPath) =>
        Path.GetFullPath(
            Path.IsPathRooted(artifactPath)
                ? artifactPath
                : Path.Combine(_repositoryRoot, artifactPath));

    private static bool IsModelArtifactAvailable(string artifactDirectory) =>
        File.Exists(Path.Combine(artifactDirectory, "metadata.json")) &&
        File.Exists(Path.Combine(artifactDirectory, "diffusion.safetensors")) &&
        File.Exists(Path.Combine(artifactDirectory, "duration.safetensors")) &&
        File.Exists(Path.Combine(artifactDirectory, "coverage.npz")) &&
        File.Exists(Path.Combine(artifactDirectory, "normalization.npz"));

    private void ToggleFullScreen()
    {
        if (_captureState.Lifecycle != CaptureLifecycle.Idle)
        {
            CaptureStateText.Text = "Termine ou annule l’essai avant de changer l’affichage";
            return;
        }

        StopReplay();
        if (_isFullScreen)
        {
            ExitFullScreen();
        }
        else
        {
            EnterFullScreen();
        }
    }

    private void EnterFullScreen()
    {
        _windowStateBeforeFullScreen = WindowState;
        _windowStyleBeforeFullScreen = WindowStyle;
        _resizeModeBeforeFullScreen = ResizeMode;
        _topmostBeforeFullScreen = Topmost;

        WindowState = System.Windows.WindowState.Normal;
        WindowStyle = System.Windows.WindowStyle.None;
        ResizeMode = System.Windows.ResizeMode.NoResize;
        Topmost = true;
        RootLayout.Margin = new Thickness(0);
        HeaderPanel.Visibility = Visibility.Collapsed;
        SidebarPanel.Visibility = Visibility.Collapsed;
        FooterPanel.Visibility = Visibility.Collapsed;
        CaptureColumn.Width = new GridLength(1, GridUnitType.Star);
        SidebarColumn.Width = new GridLength(0);
        CapturePanel.Margin = new Thickness(0);
        CapturePanel.CornerRadius = new CornerRadius(0);
        CapturePanel.BorderThickness = new Thickness(0);
        FullScreenButton.Content = "⛶  Quitter le plein écran  [F11]";
        WindowState = System.Windows.WindowState.Maximized;
        _isFullScreen = true;
        RefreshSceneAfterLayout();
    }

    private void ExitFullScreen()
    {
        WindowState = System.Windows.WindowState.Normal;
        Topmost = _topmostBeforeFullScreen;
        WindowStyle = _windowStyleBeforeFullScreen;
        ResizeMode = _resizeModeBeforeFullScreen;
        RootLayout.Margin = new Thickness(18);
        HeaderPanel.Visibility = Visibility.Visible;
        SidebarPanel.Visibility = Visibility.Visible;
        FooterPanel.Visibility = Visibility.Visible;
        CaptureColumn.Width = new GridLength(3, GridUnitType.Star);
        SidebarColumn.Width = new GridLength(1.15, GridUnitType.Star);
        CapturePanel.Margin = new Thickness(0, 0, 14, 0);
        CapturePanel.CornerRadius = new CornerRadius(10);
        CapturePanel.BorderThickness = new Thickness(1);
        FullScreenButton.Content = "⛶  Plein écran  [F11]";
        WindowState = _windowStateBeforeFullScreen;
        _isFullScreen = false;
        RefreshSceneAfterLayout();
    }

    private void RefreshSceneAfterLayout() =>
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                GameViewport.InvalidateVisual();
                DrawDisplayTrajectory();
                if (IsMouseLookActive)
                {
                    RefreshMouseLookBounds();
                }
            },
            DispatcherPriority.Loaded);

    private void GameViewport_OnRender(TimeSpan delta)
    {
        _ = ApplyPendingCameraInput();
        _ = AdvanceReplay();
        _sceneRenderer.Render(
            GameViewport.ActualWidth,
            GameViewport.ActualHeight,
            _lookController.YawDegrees,
            _lookController.PitchDegrees);

        _renderedFrames++;
        if (_renderRateStopwatch.Elapsed.TotalSeconds >= 0.5d)
        {
            _renderFps = _renderedFrames / _renderRateStopwatch.Elapsed.TotalSeconds;
            _renderedFrames = 0;
            _renderRateStopwatch.Restart();
        }
    }

    private void VisualTimer_Tick(object? sender, EventArgs e)
    {
        DroppedPacketText.Text =
            $"File Raw Input : {_rawInput.PendingPackets} en attente · {_rawInput.DroppedPackets} perte(s)";
        SceneOrientationText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"yaw {_lookController.YawDegrees:F1}° · pitch {_lookController.PitchDegrees:F1}° · " +
            $"{_renderFps:F0} FPS");

        CrosshairRing.Stroke = _captureState.Lifecycle switch
        {
            CaptureLifecycle.Armed => new SolidColorBrush(Color.FromRgb(255, 206, 115)),
            CaptureLifecycle.Recording => new SolidColorBrush(Color.FromRgb(78, 214, 178)),
            _ => new SolidColorBrush(Color.FromRgb(234, 244, 255))
        };
        if (_captureState.Lifecycle == CaptureLifecycle.Idle)
        {
            return;
        }

        var snapshot = _captureState.GetSnapshot();
        CaptureStateText.Text = snapshot.Lifecycle switch
        {
            CaptureLifecycle.Armed => "Armé — commence le flick",
            CaptureLifecycle.Recording => $"Enregistrement — {snapshot.Events.Count} paquets",
            _ => CaptureStateText.Text
        };
    }

    private bool ApplyPendingCameraInput()
    {
        var dx = Interlocked.Exchange(ref _pendingLookDx, 0);
        var dy = Interlocked.Exchange(ref _pendingLookDy, 0);
        if (dx == 0 && dy == 0)
        {
            return false;
        }

        _lookController.ApplyRawDelta(dx, dy, CurrentDegreesPerCount());
        return true;
    }

    private bool AdvanceReplay()
    {
        if (_activeReplay is null || _replayStopwatch is null)
        {
            return false;
        }

        var changed = false;
        var elapsedMicroseconds = _replayStopwatch.Elapsed.TotalMicroseconds;
        while (_replayIndex < _activeReplay.Samples.Count)
        {
            var sample = _activeReplay.Samples[_replayIndex];
            if (_replayScheduledMicroseconds + sample.DtUs > elapsedMicroseconds)
            {
                break;
            }

            _replayScheduledMicroseconds += sample.DtUs;
            _lookController.ApplyRawDelta(sample.Dx, sample.Dy, _replayDegreesPerCount);
            _displayTrajectoryDeltas.Add((sample.Dx, sample.Dy));
            _replayIndex++;
            changed = true;
        }

        if (changed)
        {
            DrawDisplayTrajectory();
        }

        if (_replayIndex >= _activeReplay.Samples.Count)
        {
            StopReplay();
            CaptureStateText.Text = "Replay 3D terminé — orientation conservée";
        }

        return changed;
    }

    private double CurrentDegreesPerCount()
    {
        var dpi = _profile?.Dpi ?? ParseIntegerOrDefault(DpiTextBox.Text, 800);
        var degreesPerMillimeter = _profile?.DisplayPixelsPerMillimeter ??
                                    ParseDoubleOrDefault(DisplayScaleTextBox.Text, 1.25);
        return 25.4d * degreesPerMillimeter / dpi;
    }

    private void DrawTrialTrajectory(TrialCapture trial)
    {
        _displayTrajectoryDeltas.Clear();
        _displayTrajectoryDeltas.AddRange(trial.Events.Select(item => (item.Dx, item.Dy)));
        DrawDisplayTrajectory();
    }

    private void DrawDisplayTrajectory()
    {
        if (_displayTrajectoryDeltas.Count == 0 ||
            TrajectoryOverlay.ActualWidth <= 1d ||
            TrajectoryOverlay.ActualHeight <= 1d)
        {
            TrajectoryLine.Points.Clear();
            TrajectoryStartMarker.Visibility = Visibility.Collapsed;
            EndpointMarker.Visibility = Visibility.Collapsed;
            return;
        }

        var rawPoints = new List<(double X, double Y)>(_displayTrajectoryDeltas.Count + 1)
        {
            (0d, 0d)
        };
        var x = 0d;
        var y = 0d;
        foreach (var delta in _displayTrajectoryDeltas)
        {
            x += delta.Dx;
            y += delta.Dy;
            rawPoints.Add((x, y));
        }

        var maxX = Math.Max(1d, rawPoints.Max(point => Math.Abs(point.X)));
        var maxY = Math.Max(1d, rawPoints.Max(point => Math.Abs(point.Y)));
        var fitScale = Math.Min(
            TrajectoryOverlay.ActualWidth * 0.42d / maxX,
            TrajectoryOverlay.ActualHeight * 0.42d / maxY);
        var scale = Math.Min(2.5d, fitScale);
        var centerX = TrajectoryOverlay.ActualWidth / 2d;
        var centerY = TrajectoryOverlay.ActualHeight / 2d;
        var points = new PointCollection(rawPoints.Count);
        foreach (var point in rawPoints)
        {
            points.Add(new Point(centerX + point.X * scale, centerY + point.Y * scale));
        }

        TrajectoryLine.Points = points;
        var start = points[0];
        var endpoint = points[^1];
        Canvas.SetLeft(TrajectoryStartMarker, start.X - TrajectoryStartMarker.Width / 2d);
        Canvas.SetTop(TrajectoryStartMarker, start.Y - TrajectoryStartMarker.Height / 2d);
        Canvas.SetLeft(EndpointMarker, endpoint.X - EndpointMarker.Width / 2d);
        Canvas.SetTop(EndpointMarker, endpoint.Y - EndpointMarker.Height / 2d);
        TrajectoryStartMarker.Visibility = Visibility.Visible;
        EndpointMarker.Visibility = Visibility.Visible;
    }

    private void ClearTrajectoryOverlay()
    {
        _displayTrajectoryDeltas.Clear();
        TrajectoryLine.Points.Clear();
        TrajectoryStartMarker.Visibility = Visibility.Collapsed;
        EndpointMarker.Visibility = Visibility.Collapsed;
    }

    private void UpdateTrialStats(TrialCapture trial)
    {
        var degreesPerCount = CurrentDegreesPerCount();
        var yawDegrees = trial.Metrics.TargetDx * degreesPerCount;
        var pitchDegrees = -trial.Metrics.TargetDy * degreesPerCount;
        EndpointText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"({trial.Metrics.TargetDx}, {trial.Metrics.TargetDy}) counts · " +
            $"({yawDegrees:F2}°, {pitchDegrees:F2}°)");
        DurationText.Text = string.Create(CultureInfo.InvariantCulture, $"{trial.Metrics.DurationMicroseconds / 1_000d:F2} ms");
        PacketCountText.Text = trial.Metrics.EventCount.ToString(CultureInfo.InvariantCulture);
        PathLengthText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{trial.Metrics.PathLengthCounts:F1} counts · " +
            $"{trial.Metrics.PathLengthCounts * degreesPerCount:F2}° angulaires");
        EffectiveHzText.Text = string.Create(CultureInfo.InvariantCulture, $"{trial.Metrics.EffectivePacketHz:F0} Hz");
        IntervalText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{trial.Metrics.MedianIntervalMicroseconds / 1_000d:F2} / {trial.Metrics.MaximumIntervalMicroseconds / 1_000d:F2} ms");
        PauseCountText.Text = trial.Metrics.PauseCount.ToString(CultureInfo.InvariantCulture);
        TrialStatusText.Text = trial.Status.ToString().ToLowerInvariant();
    }

    private void DrawCoverage()
    {
        if (CoverageCanvas.ActualWidth <= 0 || CoverageCanvas.ActualHeight <= 0)
        {
            return;
        }

        CoverageCanvas.Children.Clear();
        var centerX = CoverageCanvas.ActualWidth / 2d;
        var centerY = CoverageCanvas.ActualHeight / 2d;
        var radius = Math.Max(20, Math.Min(centerX, centerY) - 14);
        for (var ring = 1; ring <= 4; ring++)
        {
            var ringRadius = radius * ring / 4d;
            var ellipse = new Ellipse
            {
                Width = ringRadius * 2,
                Height = ringRadius * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(35, 53, 72)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(ellipse, centerX - ringRadius);
            Canvas.SetTop(ellipse, centerY - ringRadius);
            CoverageCanvas.Children.Add(ellipse);
        }

        AddCoverageLine(0, centerY, CoverageCanvas.ActualWidth, centerY);
        AddCoverageLine(centerX, 0, centerX, CoverageCanvas.ActualHeight);
        var maxDistance = Math.Max(1d, _coverageEndpoints.Count == 0
            ? 1d
            : _coverageEndpoints.Max(item => Math.Sqrt((double)item.Dx * item.Dx + (double)item.Dy * item.Dy)));
        foreach (var endpoint in _coverageEndpoints.TakeLast(2_000))
        {
            var x = centerX + endpoint.Dx / maxDistance * radius;
            var y = centerY + endpoint.Dy / maxDistance * radius;
            var dot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = new SolidColorBrush(Color.FromArgb(150, 78, 214, 178))
            };
            Canvas.SetLeft(dot, x - 2);
            Canvas.SetTop(dot, y - 2);
            CoverageCanvas.Children.Add(dot);
        }
    }

    private void AddCoverageLine(double x1, double y1, double x2, double y2)
    {
        CoverageCanvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = new SolidColorBrush(Color.FromRgb(35, 53, 72)),
            StrokeThickness = 1
        });
    }

    private void RefreshDeviceList()
    {
        var previous = (DeviceComboBox.SelectedItem as RawInputDeviceDescriptor)?.Id;
        var devices = _rawInput.Devices;
        DeviceComboBox.ItemsSource = devices;
        DeviceComboBox.SelectedItem = devices.FirstOrDefault(item =>
            string.Equals(item.Id, previous, StringComparison.OrdinalIgnoreCase));
        if (DeviceComboBox.SelectedItem is null && devices.Count > 0)
        {
            DeviceComboBox.SelectedIndex = 0;
        }

        FooterStatusText.Text = devices.Count switch
        {
            0 => "Aucune interface souris Raw Input détectée.",
            1 => "1 interface souris Raw Input détectée.",
            _ => $"{devices.Count} interfaces souris détectées — un clavier ou un récepteur peut aussi exposer une collection souris HID."
        };
    }

    private void SetCaptureControlsEnabled(bool enabled)
    {
        DeviceComboBox.IsEnabled = enabled;
        DpiTextBox.IsEnabled = enabled;
        PollingComboBox.IsEnabled = enabled;
        DisplayScaleTextBox.IsEnabled = enabled;
        ArmButton.IsEnabled = enabled;
        GenerateButton.IsEnabled = enabled;
        FullScreenButton.IsEnabled = enabled;
        SpeedCurvesButton.IsEnabled = enabled && _initialized;
        ArmingShortcutButton.IsEnabled = enabled && _initialized;
        ResetArmingShortcutButton.IsEnabled = enabled && _initialized;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_choosingArmingShortcut)
        {
            e.Handled = true;
            if (e.IsRepeat || ArmingShortcut.IsModifierKey(key))
            {
                return;
            }

            if (key == Key.Escape)
            {
                CancelArmingShortcutSelection();
                return;
            }

            var shortcut = new ArmingShortcut(key, Keyboard.Modifiers);
            if (!shortcut.IsValid)
            {
                ArmingShortcutHintText.Text = "Cette touche est réservée. Choisis une autre touche ou Échap pour annuler.";
                return;
            }

            SaveArmingShortcut(shortcut);
            return;
        }

        var editingText = Keyboard.FocusedElement is TextBox ||
            DeviceComboBox.IsKeyboardFocusWithin || PollingComboBox.IsKeyboardFocusWithin ||
            GenerationModeComboBox.IsKeyboardFocusWithin;
        if (key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (key == Key.Escape)
        {
            if (_captureState.Lifecycle != CaptureLifecycle.Idle)
            {
                CancelCapture("cancelled_by_user");
            }

            if (IsMouseLookActive)
            {
                ReleaseMouseLook();
                CaptureStateText.Text = "Souris libérée — clique dans la scène pour reprendre";
            }
            else if (_isFullScreen)
            {
                ExitFullScreen();
            }

            e.Handled = true;
        }
        else if (!editingText && _armingShortcut.Matches(key, Keyboard.Modifiers))
        {
            if (!e.IsRepeat)
            {
                _ = ArmAsync();
            }

            e.Handled = true;
        }
        else if (!editingText && key == Key.Back)
        {
            _ = ExcludeLastAsync();
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_choosingArmingShortcut)
        {
            return;
        }

        // Consume the selection click before it can focus another control,
        // enter the scene, open a context menu or activate a toolbar button.
        e.Handled = true;
        var shortcut = new ArmingShortcut(Key.None, Keyboard.Modifiers, e.ChangedButton);
        if (shortcut.IsValid)
        {
            SaveArmingShortcut(shortcut);
        }
        else
        {
            ArmingShortcutHintText.Text = "Cette combinaison est réservée. Choisis un autre raccourci ou Échap pour annuler.";
        }
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (!_closing)
        {
            CancelArmingShortcutSelection();
            InvalidateCapture("focus_lost");
            ReleaseMouseLook();
        }
    }

    private void GameViewport_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DrawDisplayTrajectory();

    private void CoverageCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawCoverage();

    private void RawInput_DevicesChanged(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            var activeDevice = _profile?.DeviceId;
            if (_captureState.Lifecycle != CaptureLifecycle.Idle &&
                activeDevice is not null &&
                !_rawInput.Devices.Any(item =>
                    string.Equals(item.Id, activeDevice, StringComparison.OrdinalIgnoreCase)))
            {
                InvalidateCapture("device_disconnected");
                ReleaseMouseLook();
            }
            else if (IsMouseLookActive &&
                     _mouseLookDeviceId is not null &&
                     !_rawInput.Devices.Any(item =>
                         string.Equals(item.Id, _mouseLookDeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                ReleaseMouseLook();
            }

            RefreshDeviceList();
        });

    private void RawInput_ServiceError(object? sender, Exception exception)
    {
        if (_closing)
        {
            return;
        }

        if (!_closing && _captureState.Lifecycle != CaptureLifecycle.Idle)
        {
            InvalidateCapture("raw_input_error");
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            ReleaseMouseLook();
            FooterStatusText.Text = $"Raw Input : {exception.Message}";
        });
    }

    private void WriteQueue_WriteFailed(object? sender, Exception exception) =>
        _ = Dispatcher.InvokeAsync(() => FooterStatusText.Text = $"Écriture dataset : {exception.Message}");

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _windowLifetime.Cancel();
        StopReplay();
        _visualTimer.Stop();
        ReleaseMouseLook();
        var unfinished = _captureState.Invalidate(_rawInput.TimestampNow(), "application_closed");
        if (unfinished is not null)
        {
            if (_writeQueue is not null && !_writeQueue.TryEnqueue(unfinished))
            {
                var invalidTrial = unfinished with
                {
                    Status = TrialStatus.Invalid,
                    RejectionReason = "dataset_write_queue_overflow"
                };
                _repository.SaveTrialAsync(invalidTrial).GetAwaiter().GetResult();
            }
        }

        CursorConfinementLease.ForceRelease();
        _rawInput.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _writeQueue?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _windowLifetime.Dispose();
        base.OnClosed(e);
    }

    private static int ParseInteger(string value, string name, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ||
            result < minimum ||
            result > maximum)
        {
            throw new FormatException($"{name} doit être un entier entre {minimum} et {maximum}.");
        }

        return result;
    }

    private static int ParseComboInteger(ComboBox comboBox, string name, int minimum, int maximum)
    {
        var text = (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        return ParseInteger(text, name, minimum, maximum);
    }

    private static double ParseDouble(string value, string name, double minimum, double maximum)
    {
        var valid = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
                    double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
        if (!valid || result < minimum || result > maximum)
        {
            throw new FormatException($"{name} doit être compris entre {minimum} et {maximum}.");
        }

        return result;
    }

    private static int ParseIntegerOrDefault(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;

    private static double ParseDoubleOrDefault(string value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : fallback;

    private static string FindRepositoryRoot()
    {
        var candidates = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (var candidate in candidates)
        {
            var directory = new DirectoryInfo(candidate);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "global.json")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Impossible de localiser la racine de MouseMotionLab.");
    }

    private void ShowError(string title, Exception exception) =>
        MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
