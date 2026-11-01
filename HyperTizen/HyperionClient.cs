using Newtonsoft.Json;
using System.Threading.Tasks;
using Tizen.Applications;
using System.Net.WebSockets;
using System.Text;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using SkiaSharp;
using Tizen.NUI;
using System.Diagnostics;
using System.Linq;
using Tizen.Uix.Tts;
using System.Net.Sockets;
using Tizen.Applications.RPCPort;
using System.IO;
using Tizen.Messaging.Messages;
using System.Linq.Expressions;
using Tizen.Applications.Notifications;
using HyperTizen.Capture;
using HyperTizen.Bulbs;

namespace HyperTizen
{
    // Service state enum for lifecycle management
    public enum ServiceState
    {
        Idle,           // Not capturing
        Starting,       // Initializing
        Capturing,      // Active capture
        Paused,         // Temporarily suspended
        Stopping,       // Shutting down
        Error           // Failed state
    }

    // Detailed status information
    public class ServiceStatus
    {
        public ServiceState State { get; set; }
        public long FramesCaptured { get; set; }
        public double AverageFPS { get; set; }
        public int ErrorCount { get; set; }
        public bool IsConnected { get; set; }
        public string LastError { get; set; }
        public DateTime StartTime { get; set; }
    }

    internal class HyperionClient
    {
        // Lifecycle management fields
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isRunning = false;
        private bool _isPaused = false;
        private readonly object _pauseLock = new object();
        private ServiceState _serviceState = ServiceState.Idle;
        private readonly object _stateLock = new object();

        // Capture architecture fields
        private ICaptureMethod _selectedCaptureMethod;
        private CaptureMethodSelector _captureSelector;

        // Capture statistics
        private long _framesCaptured = 0;
        private int _errorCount = 0;
        private string _lastError = null;
        private DateTime _startTime;
        private List<double> _fpsHistory = new List<double>();

        public HyperionClient()
        {
            Task.Run(async () =>
            {
                try
                {
                    await Start();
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Error,
                        $"Unhandled exception in Start(): {ex.Message}");
                }
            });
        }

        // Get current service state
        public ServiceState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _serviceState;
                }
            }
            private set
            {
                lock (_stateLock)
                {
                    _serviceState = value;
                }
            }
        }

        // True while Start() runs (the capture loop is alive); Stop() waits for it to drop.
        public bool IsRunning => _isRunning;

        public async Task Start()
        {
            // Only SetConfig "enabled" changes what the user wants; every Start caller goes through this gate.
            if (!App.Configuration.Enabled)
            {
                Helper.Log.Write(Helper.eLogType.Info, "Sync disabled in settings, not starting");
                return;
            }

            // Prevent multiple simultaneous starts (outside the try, so the finally doesn't clear the running loop's flag)
            lock (_stateLock)
            {
                if (_isRunning)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, "HyperionClient already running");
                    return;
                }
                _isRunning = true;
            }

            CancellationTokenSource runCts = null;
            Task bulbRun = Task.CompletedTask;
            try
            {
                // STEP 1: Early initialization for logging
                Helper.Log.Write(Helper.eLogType.Info, "=== STEP 1: Service startup ===");
                State = ServiceState.Starting;
                _startTime = DateTime.Now;

                // STEP 2: WebSocket logging/control is started in App.OnCreate
                Helper.Log.Write(Helper.eLogType.Info, "=== STEP 2: WebSocket initialization ===");
                Helper.Log.Write(Helper.eLogType.Info, "WebSocket server should be running on port 45678");
                Helper.Log.Write(Helper.eLogType.Info, $"Connect to http://<TV_IP>:45678 to view logs");

                // FILESTEALER MODE CHECK - Execute after logging is initialized
                if (Globals.FILESTEALER_ENABLED)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, "");
                    Helper.Log.Write(Helper.eLogType.Warning, "╔════════════════════════════════════════════╗");
                    Helper.Log.Write(Helper.eLogType.Warning, "║   FILESTEALER MODE ENABLED                 ║");
                    Helper.Log.Write(Helper.eLogType.Warning, "║   Normal capture is DISABLED               ║");
                    Helper.Log.Write(Helper.eLogType.Warning, "╚════════════════════════════════════════════╝");
                    Helper.Log.Write(Helper.eLogType.Warning, "");
                    Helper.Log.Write(Helper.eLogType.Info, "This mode will:");
                    Helper.Log.Write(Helper.eLogType.Info, "  1. Copy /usr/bin contents to USB drive");
                    Helper.Log.Write(Helper.eLogType.Info, "  2. Show notifications at start and completion");
                    Helper.Log.Write(Helper.eLogType.Info, "  3. Exit the service when done");
                    Helper.Log.Write(Helper.eLogType.Info, "");
                    Helper.Log.Write(Helper.eLogType.Warning, "Make sure USB drive is connected at /opt/media/USBDriveA1!");
                    Helper.Log.Write(Helper.eLogType.Info, "");
                    Helper.Log.Write(Helper.eLogType.Info, "📡 WebSocket logs are available at http://<TV_IP>:45678");
                    Helper.Log.Write(Helper.eLogType.Info, "");

                    // Wait for WebSocket server to fully initialize
                    Helper.Log.Write(Helper.eLogType.Info, "Waiting 3 seconds for WebSocket server to initialize...");
                    Helper.Log.Write(Helper.eLogType.Info, "Connect your browser now to see all logs!");
                    await Task.Delay(3000);

                    // Show start notification
                    try
                    {
                        Notification startNotif = new Notification
                        {
                            Title = "📁 Filestealer Starting",
                            Content = "Copying OS files to USB drive...",
                            Count = 1
                        };
                        NotificationManager.Post(startNotif);
                        Helper.Log.Write(Helper.eLogType.Info, "Start notification sent");
                    }
                    catch (Exception notifEx)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, $"Could not show notification: {notifEx.Message}");
                    }

                    // Run filestealer and wait for completion
                    Helper.Log.Write(Helper.eLogType.Info, "Starting filestealer operation...");
                    await SDK.Filestealer.CopyToUsbAsync();
                    Helper.Log.Write(Helper.eLogType.Info, "Filestealer operation completed!");

                    // Show completion notification
                    try
                    {
                        Notification completeNotif = new Notification
                        {
                            Title = "✅ Filestealer Complete",
                            Content = "OS files copied to USB. Service will now exit.",
                            Count = 2
                        };
                        NotificationManager.Post(completeNotif);
                        Helper.Log.Write(Helper.eLogType.Info, "Completion notification sent");
                    }
                    catch (Exception notifEx)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, $"Could not show notification: {notifEx.Message}");
                    }

                    // Brief delay for notification visibility, then exit
                    Helper.Log.Write(Helper.eLogType.Info, "");
                    Helper.Log.Write(Helper.eLogType.Info, "Waiting 2 seconds before exit...");
                    await Task.Delay(2000);

                    // Exit the service
                    Helper.Log.Write(Helper.eLogType.Info, "");
                    Helper.Log.Write(Helper.eLogType.Warning, "╔════════════════════════════════════════════╗");
                    Helper.Log.Write(Helper.eLogType.Warning, "║   FILESTEALER MODE COMPLETE                ║");
                    Helper.Log.Write(Helper.eLogType.Warning, "║   Exiting service now                      ║");
                    Helper.Log.Write(Helper.eLogType.Warning, "╚════════════════════════════════════════════╝");
                    Helper.Log.Write(Helper.eLogType.Info, "");

                    // Exit the application
                    return;
                }

                // Continue with normal startup flow
                _framesCaptured = 0;
                _errorCount = 0;
                _fpsHistory.Clear();

                // Create new cancellation token source
                _cancellationTokenSource = new CancellationTokenSource();

                Helper.Log.Write(Helper.eLogType.Info, "HyperionClient starting normal capture mode...");

                // STEP 3: no HyperHDR discovery: frames go straight to the bulbs (App.Bulbs)
                if (Globals.Instance.DiagnosticMode)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "DIAGNOSTIC MODE ACTIVE - Will pause after initialization");
                }

                // STEP 5: Testing capture methods
                Helper.Log.Write(Helper.eLogType.Info, "=== STEP 5: Testing capture methods ===");

                if (!Globals.Instance.DiagnosticMode)
                {
                    Helper.Log.Write(Helper.eLogType.Info, "CaptureMethodSelector: Initializing all capture methods");

                    try
                    {
                        _captureSelector = new CaptureMethodSelector();

                        Helper.Log.Write(Helper.eLogType.Info, "CaptureMethodSelector: Starting capture method selection");
                        _selectedCaptureMethod = _captureSelector.SelectBestMethod();

                        if (_selectedCaptureMethod == null)
                        {
                            Helper.Log.Write(Helper.eLogType.Error,
                                "CAPTURE METHOD SELECTION FAILED: No working capture methods found!");
                            Helper.Log.Write(Helper.eLogType.Error,
                                "STARTUP ABORTED: Cannot proceed without a working capture method");
                            return;
                        }
                        else
                        {
                            Helper.Log.Write(Helper.eLogType.Info,
                                $"CAPTURE METHOD SELECTED: {_selectedCaptureMethod.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Helper.Log.Write(Helper.eLogType.Error,
                            $"CAPTURE METHOD SELECTION ERROR: {ex.GetType().Name}: {ex.Message}");
                        Helper.Log.Write(Helper.eLogType.Error,
                            "STARTUP ABORTED: Exception during capture method selection");
                        return;
                    }
                }
                else
                {
                    Helper.Log.Write(Helper.eLogType.Warning, "DIAGNOSTIC MODE: Skipping capture method testing");
                    Helper.Log.Write(Helper.eLogType.Info, "Capture testing will be skipped - diagnostic mode active");
                    _selectedCaptureMethod = null;
                }

                Helper.Log.Write(Helper.eLogType.Info, "=== STEP 6: Cleanup tests (automatic) ===");
                Helper.Log.Write(Helper.eLogType.Info, "=== STEP 7: Initialize best method (complete) ===");

                // STEP 4: DIAGNOSTIC MODE - Gather diagnostic info
                if (Globals.Instance.DiagnosticMode)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "=== DIAGNOSTIC MODE ===");
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "Initialization complete. Pausing for 10 minutes...");
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "Connect to WebSocket at http://<TV_IP>:45678 to view ALL logs");
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "App will stay alive for 10 minutes, then exit gracefully");
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "======================");

                    // Log capture method selection results
                    Helper.Log.Write(Helper.eLogType.Info, "");
                    Helper.Log.Write(Helper.eLogType.Info, "DIAGNOSTIC: Capture Method Selection Results:");
                    if (_selectedCaptureMethod != null)
                    {
                        Helper.Log.Write(Helper.eLogType.Info,
                            $"Selected Method: {_selectedCaptureMethod.Name} (Type: {_selectedCaptureMethod.Type})");
                    }
                    else
                    {
                        Helper.Log.Write(Helper.eLogType.Warning,
                            "No capture method was selected - all methods failed");
                    }
                    Helper.Log.Write(Helper.eLogType.Info, "");

                    // SCAN FOR ALTERNATIVES - Disabled for clean starter project
                    // Uncomment to scan for alternative capture libraries during diagnostics
                    // if (SDK.SystemInfo.TizenVersionMajor >= 8)
                    // {
                    //     Helper.Log.Write(Helper.eLogType.Info, "Scanning for alternative capture methods...");
                    //     try
                    //     {
                    //         SDK.LibraryScanner.ScanForAlternatives();
                    //     }
                    //     catch (Exception scanEx)
                    //     {
                    //         Helper.Log.Write(Helper.eLogType.Error, $"Library scan exception: {scanEx.Message}");
                    //     }
                    // }

                    // Show notification
                    if (Globals.Instance.ShowNotifications)
                    {
                        Notification diagNotif = new Notification
                        {
                            Title = "🔬 DIAGNOSTIC MODE",
                            Content = "Paused for 10 min - Connect WebSocket to see logs!",
                            Count = 999
                        };
                        NotificationManager.Post(diagNotif);
                    }

                    // Wait for 10 minutes (with cancellation support)
                    for (int i = 10; i > 0; i--)
                    {
                        // Check for cancellation
                        if (_cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            Helper.Log.Write(Helper.eLogType.Warning, "DIAGNOSTIC MODE: Cancelled by user");
                            break;
                        }

                        // Get WebSocket diagnostics
                        string wsDiag = Helper.Log.GetWebSocketDiagnostics();

                        Helper.Log.Write(Helper.eLogType.Info,
                            $"Diagnostic mode: {i} minute(s) remaining...");
                        Helper.Log.Write(Helper.eLogType.Info, wsDiag);

                        // Show notification with WebSocket status
                        if (Globals.Instance.ShowNotifications)
                        {
                            Notification countdownNotif = new Notification
                            {
                                Title = $"⏱ {i} min remaining",
                                Content = wsDiag,
                                Count = 100 + i
                            };
                            NotificationManager.Post(countdownNotif);
                        }

                        try
                        {
                            await Task.Delay(60000, _cancellationTokenSource.Token); // 1 minute
                        }
                        catch (OperationCanceledException)
                        {
                            Helper.Log.Write(Helper.eLogType.Info, "DIAGNOSTIC MODE: Cancelled during delay");
                            break;
                        }
                    }

                    Helper.Log.Write(Helper.eLogType.Warning,
                        "DIAGNOSTIC MODE: 10 minutes elapsed. Exiting gracefully.");
                    return;
                }

                // STEP 8: Start capture loop
                Helper.Log.Write(Helper.eLogType.Info, "=== STEP 8: Starting main capture loop ===");
                State = ServiceState.Capturing;

                int consecutiveErrors = 0;
                const int maxConsecutiveErrors = 10;

                // Bulbs run with the capture loop, on this run's token (never a long-lived one: BulbSync links to it).
                // Stop() fades them to warm white first; the finally below only cancels (any other loop exit).
                runCts = _cancellationTokenSource;
                if (App.Bulbs != null) bulbRun = App.Bulbs.RunAsync(runCts.Token);

                // STEP 10: Continue until stopped
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        // Handle pause state
                        if (_isPaused)
                        {
                            if (State != ServiceState.Paused)
                            {
                                State = ServiceState.Paused;
                                Helper.Log.Write(Helper.eLogType.Info, "Capture loop paused");
                            }

                            await Task.Delay(100, _cancellationTokenSource.Token);
                            continue;
                        }
                        else
                        {
                            // Resume from paused state
                            if (State == ServiceState.Paused)
                            {
                                State = ServiceState.Capturing;
                                Helper.Log.Write(Helper.eLogType.Info, "Capture loop resumed");
                            }
                        }

                        // Validate capture method is available
                        if (_selectedCaptureMethod == null)
                        {
                            Helper.Log.Write(Helper.eLogType.Error,
                                "Cannot capture: No capture method selected!");
                            await Task.Delay(2000, _cancellationTokenSource.Token);
                            continue;
                        }

                        var watchFPS = System.Diagnostics.Stopwatch.StartNew();

                        // Capture using selected method
                        CaptureResult captureResult = null;
                        await Task.Run(() =>
                        {
                            captureResult = _selectedCaptureMethod.Capture(
                                Globals.Instance.Width,
                                Globals.Instance.Height);
                        });

                        watchFPS.Stop();

                        // Process capture result
                        if (captureResult != null && captureResult.Success)
                        {
                            var elapsedFPS = 1 / watchFPS.Elapsed.TotalSeconds;

                            // STEP 9: feed the frame to the bulbs. Not once their run has ended (Stop's warm-white
                            // window): a frame set then would be replayed at the start of the next run.
                            if (App.Bulbs?.IsRunning == true)
                            {
                                var (lower, upper) = ColourChain.LeftZones(captureResult.YData, captureResult.UVData, captureResult.Width, captureResult.Height);
                                App.Bulbs.SetFrame(lower, upper);
                            }

                            // Update statistics
                            _framesCaptured++;
                            _fpsHistory.Add(elapsedFPS);
                            if (_fpsHistory.Count > 100) // Keep last 100 samples
                            {
                                _fpsHistory.RemoveAt(0);
                            }

                            // Reset error counter on success
                            consecutiveErrors = 0;
                        }
                        else
                        {
                            // Capture failed
                            string errorMsg = captureResult?.ErrorMessage ?? "Unknown capture error";
                            Helper.Log.Write(Helper.eLogType.Warning,
                                $"Capture failed: {errorMsg}");
                            consecutiveErrors++;

                            // Brief delay before retry
                            await Task.Delay(500, _cancellationTokenSource.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Helper.Log.Write(Helper.eLogType.Info, "Capture loop cancelled gracefully");
                        break;
                    }
                    catch (Exception loopEx)
                    {
                        consecutiveErrors++;
                        _errorCount++;
                        _lastError = $"{loopEx.GetType().Name}: {loopEx.Message}";
                        Helper.Log.Write(Helper.eLogType.Error,
                            $"LOOP ERROR #{consecutiveErrors}: {_lastError}");

                        if (consecutiveErrors >= maxConsecutiveErrors)
                        {
                            Helper.Log.Write(Helper.eLogType.Error,
                                $"TOO MANY ERRORS ({consecutiveErrors}). Stopping capture to prevent crash.");
                            State = ServiceState.Error;
                            break;
                        }

                        // Wait before retrying to prevent tight error loop
                        try
                        {
                            await Task.Delay(2000, _cancellationTokenSource.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            Helper.Log.Write(Helper.eLogType.Info, "Cancelled during error recovery delay");
                            break;
                        }
                    }
                }

                Helper.Log.Write(Helper.eLogType.Info, "Capture loop exited");
                State = ServiceState.Idle;
            }
            catch (Exception ex)
            {
                _errorCount++;
                _lastError = $"FATAL: {ex.GetType().Name}: {ex.Message}";
                Helper.Log.Write(Helper.eLogType.Error,
                    $"FATAL ERROR in Start(): {_lastError}\nStack: {ex.StackTrace}");
                State = ServiceState.Error;
            }
            finally
            {
                // Any loop exit ends the bulb run. No warm white here (only Stop() sends it), so after
                // too many capture errors the bulbs keep their last colour.
                try
                {
                    runCts?.Cancel();
                    await bulbRun;
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"Error ending bulb sync: {ex.Message}");
                }
                _isRunning = false;
                Helper.Log.Write(Helper.eLogType.Info, "HyperionClient Start() method completed");
            }
        }

        public async Task Stop()
        {
            Helper.Log.Write(Helper.eLogType.Info, "Stopping HyperionClient...");

            // Warm white first, while the network is still up (it stays up >= 5 s after the remote's power-off).
            // No-op when sync isn't running.
            if (App.Bulbs != null)
            {
                try
                {
                    await App.Bulbs.StopToWarmWhiteAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"Error stopping bulb sync: {ex.Message}");
                }
            }

            State = ServiceState.Stopping;

            // Cancel the cancellation token source
            if (_cancellationTokenSource != null)
            {
                try
                {
                    _cancellationTokenSource.Cancel();
                    Helper.Log.Write(Helper.eLogType.Info, "Cancellation token signaled");
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"Error cancelling token: {ex.Message}");
                }

                // Wait for graceful shutdown with timeout
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                while (_isRunning && stopwatch.ElapsedMilliseconds < 5000)
                {
                    await Task.Delay(100);
                }

                if (_isRunning)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "Force stopped after timeout - capture loop may still be running");
                }
                else
                {
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"Graceful shutdown completed in {stopwatch.ElapsedMilliseconds}ms");
                }

                // Dispose of cancellation token source
                try
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"Error disposing cancellation token: {ex.Message}");
                }
            }

            // Close network connections
            if (Networking.client != null && Networking.client.Connected)
            {
                try
                {
                    Helper.Log.Write(Helper.eLogType.Info, "Closing network connection...");
                    Networking.DisconnectClient();
                    Helper.Log.Write(Helper.eLogType.Info, "Network connection closed");
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"Error closing network connection: {ex.Message}");
                }
            }

            // Cleanup capture method resources
            if (_selectedCaptureMethod != null)
            {
                try
                {
                    Helper.Log.Write(Helper.eLogType.Info, "Cleaning up capture method...");
                    _selectedCaptureMethod.Cleanup();
                    _selectedCaptureMethod = null;
                    Helper.Log.Write(Helper.eLogType.Info, "Capture method cleaned up");
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"Error cleaning up capture method: {ex.Message}");
                }
            }

            // Cleanup capture selector
            if (_captureSelector != null)
            {
                try
                {
                    Helper.Log.Write(Helper.eLogType.Info, "Resetting capture selector...");
                    _captureSelector.Reset();
                    _captureSelector = null;
                    Helper.Log.Write(Helper.eLogType.Info, "Capture selector reset");
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"Error resetting capture selector: {ex.Message}");
                }
            }

            State = ServiceState.Idle;
            Helper.Log.Write(Helper.eLogType.Info, "HyperionClient stopped");
        }

        public void Pause()
        {
            lock (_pauseLock)
            {
                if (_isPaused)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, "Capture already paused");
                    return;
                }

                _isPaused = true;
                Helper.Log.Write(Helper.eLogType.Info, "Capture paused");
            }
        }

        public void Resume()
        {
            lock (_pauseLock)
            {
                if (!_isPaused)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, "Capture not paused");
                    return;
                }

                _isPaused = false;
                Helper.Log.Write(Helper.eLogType.Info, "Capture resumed");
            }
        }

        public ServiceStatus GetStatus()
        {
            double avgFPS = 0;
            if (_fpsHistory.Count > 0)
            {
                avgFPS = _fpsHistory.Average();
            }

            bool isConnected = Networking.client != null && Networking.client.Connected;

            var status = new ServiceStatus
            {
                State = State,
                FramesCaptured = _framesCaptured,
                AverageFPS = avgFPS,
                ErrorCount = _errorCount,
                IsConnected = isConnected,
                LastError = _lastError,
                StartTime = _startTime
            };

            return status;
        }
    }
}