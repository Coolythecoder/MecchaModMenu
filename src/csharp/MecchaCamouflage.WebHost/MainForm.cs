using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MecchaCamouflage.Controller;
using MecchaCamouflage.Core;

namespace MecchaCamouflage.WebHost;

public sealed class MainForm : Form
{
    private const string EvergreenBootstrapperFileName = "MicrosoftEdgeWebview2Setup.exe";
    private const string ManualWebView2RuntimeUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private const string WebUiHostName = "meccha.localhost";
    private const string WebUiStartUri = "https://meccha.localhost/index.html";
    private const string ModuleDocumentHeaders =
        "Content-Type: text/html; charset=utf-8\r\n" +
        "Cache-Control: no-store\r\n" +
        "X-Content-Type-Options: nosniff\r\n" +
        "Referrer-Policy: no-referrer\r\n" +
        "Permissions-Policy: camera=(), microphone=(), geolocation=(), clipboard-read=(), clipboard-write=()\r\n" +
        "Content-Security-Policy: default-src 'self'; base-uri 'none'; connect-src 'none'; object-src 'none'; " +
        "form-action 'none'; frame-ancestors https://meccha.localhost; frame-src 'self'; worker-src 'none'; " +
        "script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self' data:; media-src 'self'";
    private static readonly TimeSpan UiReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseCleanupTimeout = TimeSpan.FromSeconds(12);
    private const int HotkeyStart = 1;
    private const int HotkeyPreview = 2;
    private const int HotkeyUnPreview = 3;
    private const int HotkeyStop = 4;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly HostSession session;
    private readonly WebViewStartupLifecycle webViewStartup = new();
    private readonly HashSet<string> moduleVirtualHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleDescriptor> moduleVirtualHostModules =
        new(StringComparer.OrdinalIgnoreCase);
    private WebView2? webView;
    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 2000 };
    private CancellationTokenSource? uiReadyTimeoutCancellation;
    private bool webReady;
    private bool guiInitializedLogged;
    private bool settingsEditing;
    private bool hotkeyRecording;
    private bool hotkeysRegistered;
    private string lastHotkeyRegistrationFailure = "";
    private bool webViewRetryUsed;
    private bool webViewRecoveryInProgress;
    private bool closeCleanupStarted;
    private bool closeCleanupComplete;

    public MainForm(HostSession session)
    {
        this.session = session;
        Text = "Meccha Mod Menu";
        Icon = LoadWindowIcon();
        MinimumSize = new Size(960, 640);
        Width = (int)Math.Round(session.Settings.PanelWidth);
        Height = (int)Math.Round(session.Settings.PanelHeight);
        if (session.Settings.PanelX >= 0 && session.Settings.PanelY >= 0)
        {
            StartPosition = FormStartPosition.Manual;
            Left = (int)Math.Round(session.Settings.PanelX);
            Top = (int)Math.Round(session.Settings.PanelY);
        }
        TopMost = session.Settings.AlwaysOnTop;
        Opacity = session.Settings.Opacity;
        BackColor = Color.FromArgb(32, 32, 32);
        webView = CreateWebViewControl();
        Controls.Add(webView);

        Shown += async (_, _) =>
        {
            try
            {
                ApplyWindowSettings("shown");
                await InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                await HandleWebViewInitializationFailureAsync(ex);
            }
        };
        FormClosing += HandleFormClosingAsync;
        ResizeEnd += (_, _) => PersistWindowSnapshot();
        Move += (_, _) => PersistWindowSnapshot();
        statusTimer.Tick += async (_, _) =>
        {
            statusTimer.Interval = session.PaintRunning ? 500 : 2000;
            if (webReady)
                StartBridgeWarmup();
            await PushSnapshotAsync();
        };
        session.Log.Changed += (_, _) => PushSnapshotFromAnyThread();
        statusTimer.Start();
    }

    private async void HandleFormClosingAsync(object? sender, FormClosingEventArgs args)
    {
        webReady = false;
        statusTimer.Stop();
        CancelUiReadyTimeout();
        PersistWindowSnapshot();
        UnregisterHotkeys();

        if (closeCleanupComplete)
            return;

        args.Cancel = true;
        if (closeCleanupStarted)
            return;
        closeCleanupStarted = true;

        using var cleanupTimeout = new CancellationTokenSource(CloseCleanupTimeout);
        try
        {
            await session.ShutdownBridgeAsync(cleanupTimeout.Token);
        }
        catch (Exception ex)
        {
            session.Log.Warn("App close cleanup did not complete: " + ex.Message);
        }
        finally
        {
            closeCleanupComplete = true;
            if (!IsDisposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke((MethodInvoker)Close);
                }
                catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
                {
                }
            }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Hotkeys are bound to a concrete HWND. A recreated form handle must register again
        // after the controller bridge is attached instead of trusting the old handle's state.
        hotkeysRegistered = false;
        ApplyDarkTitleBar();
        ApplyWindowSettings("handle-created");
        if (session.Runtime.IsConnected)
            RequestHotkeyRegistrationAfterAttach();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey)
        {
            if (settingsEditing)
            {
                if (!hotkeyRecording)
                    SendToast(session.Localization.Text(session.Settings.Language, "toast.editing.hotkey.blocked"), "warn");
                return;
            }
            _ = HandleHotkeyAsync(message.WParam.ToInt32());
            return;
        }
        base.WndProc(ref message);
    }

    private async Task InitializeWebViewAsync()
    {
        var generation = webViewStartup.Begin();
        webReady = false;
        guiInitializedLogged = false;
        CancelUiReadyTimeout();
        DiagnosticsState.SetStartupPhase("webview2_prepare");
        var runtime = await PrepareEvergreenWebRuntimeAsync();
        var view = webView ?? throw new InvalidOperationException("MC-WV-201 WebView2 control is unavailable.");
        session.Log.Info("WebView2 runtime: creating Evergreen environment.");
        DiagnosticsState.SetStartupPhase("webview2_environment_create");
        var environment = await CoreWebView2Environment.CreateAsync(
            null,
            runtime.UserDataFolder,
            CreateEvergreenEnvironmentOptions());
        await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2 ?? throw new InvalidOperationException("MC-WV-202 WebView2 did not create a CoreWebView2 instance.");
        DiagnosticsState.SetStartupPhase("webview2_environment_ready");
        session.Log.Info($"WebView2 runtime: initialized ({runtime.Version}).");
        VerifyPackagedWebAssets(runtime.WebRoot);

        core.WebMessageReceived += async (sender, args) => await HandleWebMessageAsync(generation, sender, args);
        core.NavigationCompleted += (sender, args) => HandleNavigationCompleted(generation, sender, args);
        core.NavigationStarting += (sender, args) => HandleNavigationStarting(generation, sender, args);
        core.ContentLoading += (sender, args) => HandleContentLoading(generation, sender, args);
        core.DOMContentLoaded += (sender, args) => HandleDomContentLoaded(generation, sender, args);
        core.FrameNavigationStarting += HandleFrameNavigationStarting;
        core.NewWindowRequested += (_, args) => HandleNewWindowRequested(args);
        core.ProcessFailed += HandleWebViewProcessFailed;
        core.WebResourceRequested += HandleWebResourceRequested;
        core.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
            args.Handled = true;
        };
        core.SetVirtualHostNameToFolderMapping(
            WebUiHostName,
            runtime.WebRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
        Directory.CreateDirectory(session.Paths.ModulesDirectory);
        RefreshModuleVirtualHostMappings(core);
        core.Settings.AreDefaultScriptDialogsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreDevToolsEnabled = BuildFeatures.ResearchArtifactsEnabled;
        DiagnosticsState.SetStartupPhase("webview2_navigate");
        session.Log.Info("GUI: navigating packaged index.html.");
        DiagnosticsState.WriteLine("webview2", $"navigation_requested generation={generation} uri={WebUiStartUri}");
        core.Navigate(WebUiStartUri);
        StartUiReadyTimeout();
    }

    private void HandleFrameNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
        {
            args.Cancel = true;
            return;
        }

        var allowedModuleFrame = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            moduleVirtualHosts.Contains(uri.Host);
        var harmlessEmptyFrame = string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase);
        args.Cancel = !allowedModuleFrame && !harmlessEmptyFrame;
    }

    private void HandleWebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (sender is not CoreWebView2 core)
            return;

        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
        {
            BlockWebResource(core, args);
            return;
        }

        var allowedHttpsOrigin = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort &&
            string.IsNullOrEmpty(uri.UserInfo);
        if (allowedHttpsOrigin && string.Equals(uri.Host, WebUiHostName, StringComparison.OrdinalIgnoreCase))
            return;

        if (allowedHttpsOrigin && moduleVirtualHostModules.TryGetValue(uri.Host, out var module))
        {
            if (args.ResourceContext != CoreWebView2WebResourceContext.Document)
                return;

            var expectedPath = "/" + module.Entry;
            if (!string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal) ||
                !TryReadValidatedModuleEntry(module, out var entryBytes))
            {
                BlockWebResource(core, args);
                return;
            }

            args.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(entryBytes, writable: false),
                200,
                "OK",
                ModuleDocumentHeaders);
            return;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        BlockWebResource(core, args);
    }

    private static void BlockWebResource(
        CoreWebView2 core,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        args.Response = core.Environment.CreateWebResourceResponse(
            Stream.Null,
            403,
            "Blocked",
            "Content-Type: text/plain\r\nCache-Control: no-store");
    }

    private static bool TryReadValidatedModuleEntry(
        ModuleDescriptor module,
        out byte[] bytes)
    {
        bytes = [];
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(module.ModuleDirectory));
            var entryPath = Path.GetFullPath(module.EntryPath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!entryPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                return false;

            FileSystemInfo current = new DirectoryInfo(root);
            current.Refresh();
            if (!current.Exists || IsLinkedFileSystemEntry(current))
                return false;

            var segments = module.Entry.Split('/');
            var currentPath = root;
            for (var index = 0; index < segments.Length; ++index)
            {
                currentPath = Path.Combine(currentPath, segments[index]);
                current = index == segments.Length - 1
                    ? new FileInfo(currentPath)
                    : new DirectoryInfo(currentPath);
                current.Refresh();
                if (!current.Exists || IsLinkedFileSystemEntry(current))
                    return false;
            }

            var info = (FileInfo)current;
            if (info.Length > ModuleSdkV1.MaxEntryBytes)
                return false;
            bytes = File.ReadAllBytes(entryPath);
            if (bytes.LongLength > ModuleSdkV1.MaxEntryBytes)
            {
                bytes = [];
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsLinkedFileSystemEntry(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private void RefreshModuleVirtualHostMappings(CoreWebView2 core)
    {
        foreach (var hostName in moduleVirtualHosts)
        {
            try
            {
                core.ClearVirtualHostNameToFolderMapping(hostName);
            }
            catch (ArgumentException)
            {
                // A recovering WebView may already have discarded the old mapping.
            }
        }
        moduleVirtualHosts.Clear();
        moduleVirtualHostModules.Clear();

        foreach (var module in session.Modules.Modules)
        {
            var hostName = ModuleSdkV1.VirtualHostName(module.Id);
            core.SetVirtualHostNameToFolderMapping(
                hostName,
                module.ModuleDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);
            moduleVirtualHosts.Add(hostName);
            moduleVirtualHostModules[hostName] = module;
        }
    }

    private void VerifyPackagedWebAssets(string webRoot)
    {
        if (!Directory.Exists(webRoot))
            throw new DirectoryNotFoundException("MC-WV-105 Packaged web assets are missing: " + webRoot);

        foreach (var fileName in new[] { "index.html", "app.js", "styles.css" })
        {
            var path = Path.Combine(webRoot, fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"MC-WV-105 Packaged web asset is missing: {fileName}", path);

            var length = new FileInfo(path).Length;
            if (length == 0)
                throw new IOException($"MC-WV-105 Packaged web asset is empty: {fileName}");
            DiagnosticsState.WriteLine("webview2", $"asset_verified name={fileName} bytes={length} path={path}");
        }
    }

    private async void HandleNavigationCompleted(long generation, object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!IsActiveWebView(generation, sender) || webViewRecoveryInProgress)
            return;

        try
        {
            var source = webView?.CoreWebView2?.Source ?? WebUiStartUri;
            var startupNavigation = webViewStartup.IsInitialNavigation(generation, args.NavigationId);
            DiagnosticsState.WriteLine(
                "webview2",
                $"navigation_completed generation={generation} id={args.NavigationId} startup={startupNavigation} success={args.IsSuccess} status={args.HttpStatusCode} error={args.WebErrorStatus} uri={source}");
            if (!startupNavigation)
                return;
            DiagnosticsState.SetStartupPhase("webview2_navigation_completed");
            if (!args.IsSuccess)
            {
                await RecoverWebViewAsync(
                    "The Meccha Mod Menu interface could not load.",
                    new InvalidOperationException($"MC-WV-203 WebView2 navigation failed: {args.WebErrorStatus}"));
                return;
            }
            if (guiInitializedLogged)
                return;
            guiInitializedLogged = true;
            session.Log.Info("GUI: web assets loaded; waiting for uiReady.");
            if (webViewStartup.MarkNavigationSucceeded(generation, args.NavigationId))
                QueueWindowSettingsStabilization(generation);
        }
        catch (Exception ex)
        {
            DiagnosticsState.RecordException("gui_navigation_completed_failed", ex);
            session.Log.Error(ex.Message);
        }
    }

    private void HandleContentLoading(long generation, object? sender, CoreWebView2ContentLoadingEventArgs args)
    {
        if (!IsActiveWebView(generation, sender) || webViewRecoveryInProgress)
            return;

        var source = webView?.CoreWebView2?.Source ?? WebUiStartUri;
        DiagnosticsState.SetStartupPhase("webview2_content_loading");
        DiagnosticsState.WriteLine(
            "webview2",
            $"content_loading generation={generation} id={args.NavigationId} error_page={args.IsErrorPage} uri={source}");
    }

    private void HandleDomContentLoaded(long generation, object? sender, CoreWebView2DOMContentLoadedEventArgs args)
    {
        if (!IsActiveWebView(generation, sender) || webViewRecoveryInProgress)
            return;

        var source = webView?.CoreWebView2?.Source ?? WebUiStartUri;
        DiagnosticsState.SetStartupPhase("webview2_dom_content_loaded");
        session.Log.Info("GUI: DOM content loaded.");
        DiagnosticsState.WriteLine("webview2", $"dom_content_loaded generation={generation} id={args.NavigationId} uri={source}");
    }

    private async Task<WebRuntimeInfo> PrepareEvergreenWebRuntimeAsync()
    {
        DiagnosticsState.SetStartupPhase("webview2_runtime_detect");
        var version = await Task.Run(TryGetEvergreenRuntimeVersion);
        if (string.IsNullOrWhiteSpace(version))
        {
            DiagnosticsState.SetLastCode("MC-WV-101", "Evergreen WebView2 Runtime is not installed");
            var install = MessageBox.Show(
                this,
                "Microsoft Edge WebView2 Runtime is required to start Meccha Mod Menu.\n\n" +
                "Install it now? This small Microsoft bootstrapper needs an internet connection.",
                "Meccha Mod Menu",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (install != DialogResult.Yes)
                throw new InvalidOperationException("MC-WV-101 Microsoft Edge WebView2 Runtime is required.");

            DiagnosticsState.SetStartupPhase("webview2_runtime_install");
            await InstallEvergreenRuntimeAsync();
            version = await WaitForEvergreenRuntimeAsync();
            if (string.IsNullOrWhiteSpace(version))
            {
                DiagnosticsState.SetLastCode("MC-WV-102", "Evergreen bootstrapper completed but no compatible runtime was detected");
                throw new InvalidOperationException("MC-WV-102 The WebView2 Runtime was not available after installation.");
            }
        }

        var userDataFolder = Path.Combine(session.Paths.RootDirectory, "webview2-user-data");
        Directory.CreateDirectory(userDataFolder);
        var webRoot = await Task.Run(() => Path.Combine(PackagedAssets.ResolveRequiredAssetRoot(session.Paths, "web", session.Log), "web"));
        DiagnosticsState.SetWebView2Runtime($"evergreen version={version}");
        return new WebRuntimeInfo(userDataFolder, webRoot, version);
    }

    private static CoreWebView2EnvironmentOptions CreateEvergreenEnvironmentOptions() => new()
    {
        ReleaseChannels = CoreWebView2ReleaseChannels.Stable,
        ExclusiveUserDataFolderAccess = false
    };

    private static string? TryGetEvergreenRuntimeVersion()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString(
                null,
                CreateEvergreenEnvironmentOptions());
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return null;
        }
    }

    private static async Task<string?> WaitForEvergreenRuntimeAsync()
    {
        for (var attempt = 0; attempt != 10; ++attempt)
        {
            var version = await Task.Run(TryGetEvergreenRuntimeVersion);
            if (!string.IsNullOrWhiteSpace(version))
                return version;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        return null;
    }

    private async Task InstallEvergreenRuntimeAsync()
    {
        var assetRoot = await Task.Run(() => PackagedAssets.ResolveRequiredAssetRoot(session.Paths, "webview2-bootstrapper", session.Log));
        var source = Path.Combine(assetRoot, "webview2-bootstrapper", EvergreenBootstrapperFileName);
        if (!File.Exists(source))
        {
            DiagnosticsState.SetLastCode("MC-WV-103", "embedded Evergreen bootstrapper is missing");
            throw new FileNotFoundException("MC-WV-103 The embedded WebView2 Evergreen bootstrapper is missing.", source);
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "MecchaCamouflage",
            "webview2-bootstrapper",
            Guid.NewGuid().ToString("N"));
        var bootstrapperPath = Path.Combine(temporaryDirectory, EvergreenBootstrapperFileName);
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            File.Copy(source, bootstrapperPath, overwrite: true);
            var start = new ProcessStartInfo(bootstrapperPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("/silent");
            start.ArgumentList.Add("/install");
            using var process = Process.Start(start)
                ?? throw new IOException("MC-WV-104 The WebView2 Evergreen bootstrapper could not be started.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                DiagnosticsState.SetLastCode("MC-WV-104", $"Evergreen bootstrapper exit code {process.ExitCode}");
                throw new IOException($"MC-WV-104 The WebView2 Evergreen bootstrapper failed with exit code {process.ExitCode}.");
            }
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A security product can briefly retain the installer; the per-run directory is safe to clean later.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostics should describe the startup error, not turn temporary-file cleanup into a new failure.
        }
    }

    private sealed record WebRuntimeInfo(string UserDataFolder, string WebRoot, string Version);

    private WebView2 CreateWebViewControl() => new() { Dock = DockStyle.Fill };

    private void StartBridgeWarmup()
    {
        if (!webReady)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await session.WarmupBridgeAsync();
                if (session.Runtime.IsConnected)
                    RequestHotkeyRegistrationAfterAttach();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void StartUiReadyTimeout()
    {
        CancelUiReadyTimeout();
        var cancellation = new CancellationTokenSource();
        uiReadyTimeoutCancellation = cancellation;
        _ = WaitForUiReadyAsync(cancellation);
    }

    private void CancelUiReadyTimeout()
    {
        var cancellation = uiReadyTimeoutCancellation;
        uiReadyTimeoutCancellation = null;
        cancellation?.Cancel();
    }

    private async Task WaitForUiReadyAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(UiReadyTimeout, cancellation.Token);
            if (IsDisposed || Disposing || webReady || !ReferenceEquals(uiReadyTimeoutCancellation, cancellation))
                return;
            uiReadyTimeoutCancellation = null;
            await RecoverWebViewAsync(
                "The Meccha Mod Menu interface did not finish initializing.",
                new TimeoutException("MC-WV-204 Timed out waiting for the web interface uiReady signal."));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task MarkUiReadyAsync(long generation)
    {
        if (!webViewStartup.IsCurrent(generation) || webViewRecoveryInProgress || webReady || IsDisposed || Disposing)
            return;
        webReady = true;
        CancelUiReadyTimeout();
        DiagnosticsState.SetStartupPhase("webview2_ui_ready");
        session.Log.Info("GUI: initialized.");
        DiagnosticsState.WriteLine("webview2", $"ui_ready generation={generation}");
        if (webViewStartup.MarkUiReady(generation))
            QueueWindowSettingsStabilization(generation);
        StartBridgeWarmup();
        await PushSnapshotAsync();
    }

    private void QueueWindowSettingsStabilization(long generation)
    {
        if (!IsHandleCreated || IsDisposed || Disposing)
            return;
        try
        {
            BeginInvoke((MethodInvoker)(() =>
            {
                if (IsDisposed || Disposing || !webReady || !webViewStartup.IsCurrent(generation))
                    return;
                ApplyWindowSettings("webview-ready-posted");
            }));
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
        {
            // The form closed between the guard and BeginInvoke.
        }
    }

    private bool IsActiveWebView(long generation, object? sender) =>
        webViewStartup.IsCurrent(generation) && ReferenceEquals(sender, webView?.CoreWebView2);

    private async Task HandleWebViewInitializationFailureAsync(Exception exception)
    {
        await RecoverWebViewAsync("Meccha Mod Menu could not start its WebView2 interface.", exception);
    }

    private async Task RecoverWebViewAsync(string userMessage, Exception exception)
    {
        if (webViewRecoveryInProgress || IsDisposed || Disposing)
            return;

        webViewRecoveryInProgress = true;
        webReady = false;
        CancelUiReadyTimeout();
        DiagnosticsState.RecordException("webview2_failed", exception);
        DiagnosticsState.SetLastCode(WebViewFailureCode(exception), exception.Message);
        session.Log.Error("WebView2: " + exception.Message);
        try
        {
            var retryAvailable = !webViewRetryUsed;
            var action = WebViewFailureDialog.Show(
                this,
                userMessage,
                DiagnosticsState.Summary(session.Paths),
                ManualWebView2RuntimeUrl,
                retryAvailable);
            if (action != WebViewRecoveryAction.Retry)
            {
                Close();
                return;
            }

            webViewRetryUsed = true;
            try
            {
                await RecreateWebViewAsync();
            }
            catch (Exception retryException)
            {
                DiagnosticsState.RecordException("webview2_retry_failed", retryException);
                session.Log.Error("WebView2 retry: " + retryException.Message);
                WebViewFailureDialog.Show(
                    this,
                    "The WebView2 retry did not succeed.",
                    DiagnosticsState.Summary(session.Paths),
                    ManualWebView2RuntimeUrl,
                    retryAvailable: false);
                Close();
            }
        }
        finally
        {
            webViewRecoveryInProgress = false;
        }
    }

    private static string WebViewFailureCode(Exception exception)
    {
        var firstToken = exception.Message
            .Split([' ', ':', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(token => token.StartsWith("MC-WV-", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(firstToken) ? "MC-WV-999" : firstToken;
    }

    private async Task RecreateWebViewAsync()
    {
        webReady = false;
        guiInitializedLogged = false;
        CancelUiReadyTimeout();
        var previous = webView;
        webView = null;
        if (previous is not null)
        {
            Controls.Remove(previous);
            previous.Dispose();
        }

        var replacement = CreateWebViewControl();
        webView = replacement;
        Controls.Add(replacement);
        replacement.BringToFront();
        await InitializeWebViewAsync();
    }

    private void HandleWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (!ReferenceEquals(sender, webView?.CoreWebView2))
            return;

        var detail = BuildProcessFailureDetail(args);
        DiagnosticsState.SetStartupPhase("webview2_process_failed");
        DiagnosticsState.SetLastCode("MC-WV-301", detail);
        session.Log.Error("WebView2 process failed: " + detail);
        // RenderProcessUnresponsive can recover on its own, so it is diagnostic-only here.
        var requiresRecreate = args.ProcessFailedKind is
            CoreWebView2ProcessFailedKind.BrowserProcessExited or
            CoreWebView2ProcessFailedKind.RenderProcessExited or
            CoreWebView2ProcessFailedKind.UnknownProcessExited;
        if (!requiresRecreate || webViewRecoveryInProgress)
            return;

        if (InvokeRequired)
        {
            if (IsDisposed || Disposing || !IsHandleCreated)
                return;
            try
            {
                BeginInvoke((MethodInvoker)(() => HandleWebViewProcessFailed(sender, args)));
            }
            catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
            {
                // The form closed between the guard and BeginInvoke.
            }
            return;
        }
        _ = RecoverWebViewAsync(
            "The WebView2 browser process stopped unexpectedly.",
            new InvalidOperationException("MC-WV-301 " + detail));
    }

    private static string BuildProcessFailureDetail(CoreWebView2ProcessFailedEventArgs args)
    {
        var detail = $"{args.ProcessFailedKind} exit={args.ExitCode}";
        try
        {
            detail += $" reason={args.Reason}";
            if (!string.IsNullOrWhiteSpace(args.ProcessDescription))
                detail += $" description={args.ProcessDescription}";
            if (!string.IsNullOrWhiteSpace(args.FailureSourceModulePath))
                detail += $" module={args.FailureSourceModulePath}";
        }
        catch
        {
            // These extended diagnostics are optional on older Evergreen runtimes.
        }
        return detail;
    }

    private void HandleNavigationStarting(long generation, object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsActiveWebView(generation, sender))
            return;
        if (webViewRecoveryInProgress)
        {
            args.Cancel = true;
            return;
        }
        if (IsStartupUri(args.Uri))
        {
            var registered = webViewStartup.RegisterInitialNavigation(generation, args.NavigationId);
            DiagnosticsState.SetStartupPhase("webview2_navigation_starting");
            DiagnosticsState.WriteLine("webview2", $"navigation_starting generation={generation} id={args.NavigationId} startup={registered} uri={args.Uri}");
            return;
        }
        if (IsInternalUri(args.Uri))
        {
            DiagnosticsState.WriteLine("webview2", $"navigation_starting generation={generation} id={args.NavigationId} startup=false uri={args.Uri}");
            return;
        }
        args.Cancel = true;
        DiagnosticsState.WriteLine("webview2", $"navigation_blocked generation={generation} id={args.NavigationId} uri={args.Uri}");
        OpenExternal(args.Uri);
    }

    private static void HandleNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (!args.IsUserInitiated ||
            args.OriginalSourceFrameInfo is null ||
            !IsInternalUri(args.OriginalSourceFrameInfo.Source))
        {
            return;
        }
        OpenExternal(args.Uri);
    }

    private static bool IsInternalUri(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        parsed.Scheme == Uri.UriSchemeHttps &&
        parsed.IsDefaultPort &&
        string.IsNullOrEmpty(parsed.UserInfo) &&
        string.Equals(parsed.Host, WebUiHostName, StringComparison.OrdinalIgnoreCase);

    private static bool IsStartupUri(string uri) =>
        string.Equals(uri, WebUiStartUri, StringComparison.OrdinalIgnoreCase);

    private static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }
        Process.Start(new ProcessStartInfo(parsed.ToString()) { UseShellExecute = true });
    }

    private async Task HandleWebMessageAsync(long generation, object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsActiveWebView(generation, sender) || webViewRecoveryInProgress)
            return;
        if (!IsInternalUri(args.Source))
        {
            session.Log.Warn("GUI: ignored a web message from an untrusted origin.");
            DiagnosticsState.WriteLine("webview2", $"web_message_rejected generation={generation} source={args.Source}");
            return;
        }
        HostWebCommand? command = null;
        try
        {
            if (TryGetUiStartupFailure(args.WebMessageAsJson, out var startupFailure))
            {
                await HandleUiStartupFailureAsync(generation, startupFailure);
                return;
            }
            if (IsUiReadyMessage(args.WebMessageAsJson))
            {
                await MarkUiReadyAsync(generation);
                return;
            }
            command = JsonSerializer.Deserialize<HostWebCommand>(args.WebMessageAsJson, JsonOptions);
            if (command is null)
                return;
            var result = await ExecuteCommandAsync(command);
            PostResponse(command.Id, true, result);
        }
        catch (Exception ex)
        {
            PostResponse(command?.Id ?? "", false, new { message = ex.Message });
        }
    }

    private static bool IsUiReadyMessage(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("type", out var type) &&
            string.Equals(type.GetString(), "uiReady", StringComparison.Ordinal);
    }

    private static bool TryGetUiStartupFailure(string message, out string detail)
    {
        detail = "";
        using var document = JsonDocument.Parse(message);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("type", out var type) ||
            !string.Equals(type.GetString(), "uiStartupFailure", StringComparison.Ordinal))
        {
            return false;
        }

        var kind = document.RootElement.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : "javascript";
        var messageValue = document.RootElement.TryGetProperty("message", out var detailValue) ? detailValue.GetString() : "unknown JavaScript startup error";
        detail = $"{kind}: {messageValue}";
        return true;
    }

    private async Task HandleUiStartupFailureAsync(long generation, string detail)
    {
        DiagnosticsState.SetLastCode("MC-WV-205", detail);
        session.Log.Error("GUI startup JavaScript failure: " + detail);
        DiagnosticsState.WriteLine("webview2", $"ui_startup_failure generation={generation} detail={detail}");
        if (webReady)
            return;
        await RecoverWebViewAsync(
            "The Meccha Mod Menu interface failed while starting.",
            new InvalidOperationException("MC-WV-205 " + detail));
    }

    private async Task<object?> ExecuteCommandAsync(HostWebCommand command)
    {
        switch (command.Command)
        {
            case "getSnapshot":
                return await session.GetSnapshotAsync();
            case "updateSetting":
                return HandleUpdateSetting(command.Payload);
            case "updateSettings":
                return HandleUpdateSettings(command.Payload);
            case "resetSetting":
                return HandleResetSetting(command.Payload);
            case "resetSection":
                return HandleResetSection(command.Payload);
            case "resetAllSettings":
                return HandleResetAllSettings();
            case "openLogs":
                session.OpenLogs();
                return new { success = true };
            case "copyLogs":
                Clipboard.SetText(session.ClipboardLogText());
                return new { success = true };
            case "openModules":
                session.OpenModulesDirectory();
                return new { success = true };
            case "reloadModules":
                var reloadResult = session.ReloadModules();
                if (webView?.CoreWebView2 is { } core)
                    RefreshModuleVirtualHostMappings(core);
                return ApplyResult(reloadResult);
            case "setEditing":
                settingsEditing = command.Payload.GetProperty("editing").GetBoolean();
                if (!settingsEditing)
                {
                    hotkeyRecording = false;
                    ApplyWindowSettings();
                }
                return new { success = true };
            case "setHotkeyRecording":
                hotkeyRecording = command.Payload.GetProperty("recording").GetBoolean();
                return new { success = true };
            case "previewWindow":
                HandlePreviewWindow(command.Payload);
                return new { success = true };
            case "setWindowState":
                PersistWindowSnapshot();
                return new { success = true };
            case "paint":
                return ApplyResult(await RunPaintCommandAsync(previewOnly: false, unpreviewOnly: false));
            case "preview":
                return ApplyResult(await RunPaintCommandAsync(previewOnly: true, unpreviewOnly: false));
            case "unpreview":
                return ApplyResult(await RunPaintCommandAsync(previewOnly: false, unpreviewOnly: true));
            case "stop":
                return ApplyResult(await session.StopPaintAsync());
            case "savePaintPreset":
                return ApplyResult(session.SavePaintPreset(RequiredString(command.Payload, "name")));
            case "applyPaintPreset":
                return ApplyResult(session.ApplyPaintPreset(RequiredString(command.Payload, "name")));
            case "deletePaintPreset":
                return ApplyResult(session.DeletePaintPreset(RequiredString(command.Payload, "name")));
            case "undoPaintSettings":
                return ApplyResult(session.UndoPaintSettings());
            default:
                return new { success = false, message = "Unknown command: " + command.Command };
        }
    }

    private async Task<HostCommandResult> RunPaintCommandAsync(bool previewOnly, bool unpreviewOnly)
    {
        var previousInterval = statusTimer.Interval;
        statusTimer.Interval = 250;
        using var refresh = new CancellationTokenSource();
        var refreshTask = RefreshSnapshotsUntilCancelledAsync(refresh.Token);
        try
        {
            return await session.RunPaintAsync(previewOnly, unpreviewOnly);
        }
        finally
        {
            refresh.Cancel();
            try
            {
                await refreshTask;
            }
            catch (OperationCanceledException)
            {
            }
            statusTimer.Interval = session.PaintRunning ? 500 : previousInterval;
            await PushSnapshotAsync();
        }
    }

    private static string RequiredString(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"{name} must be a string.");
        }
        return value.GetString() ?? "";
    }

    private async Task RefreshSnapshotsUntilCancelledAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(250, cancellationToken);
            await PushSnapshotAsync();
        }
    }

    private object HandleUpdateSetting(JsonElement payload)
    {
        var key = payload.GetProperty("key").GetString() ?? "";
        var oldHotkeys = HotkeySet.From(session.Settings);
        var result = session.UpdateSetting(key, payload.GetProperty("value"));
        var hotkeyChanged = key.Contains("Hotkey", StringComparison.OrdinalIgnoreCase);
        if (result.Success && hotkeyChanged && session.Runtime.IsConnected && !TryRegisterHotkeys(out var message))
        {
            oldHotkeys.ApplyTo(session.Settings);
            session.UpdateSetting("app.startHotkey", JsonSerializer.SerializeToElement(oldHotkeys.Start));
            session.UpdateSetting("app.previewHotkey", JsonSerializer.SerializeToElement(oldHotkeys.Preview));
            session.UpdateSetting("app.unpreviewHotkey", JsonSerializer.SerializeToElement(oldHotkeys.UnPreview));
            session.UpdateSetting("app.stopHotkey", JsonSerializer.SerializeToElement(oldHotkeys.Stop));
            TryRegisterHotkeys(out _);
            result = new HostCommandResult(false, message);
        }
        else if (result.Success && hotkeyChanged && !session.Runtime.IsConnected)
        {
            // The old registrations belong to the prior settings. Do not leave them active
            // while the bridge is disconnected and the new values wait for attach.
            UnregisterHotkeys();
            lastHotkeyRegistrationFailure = "";
        }
        else if (result.Success && hotkeyChanged)
        {
            lastHotkeyRegistrationFailure = "";
        }
        ApplyWindowSettings();
        return ApplyResult(result);
    }

    private object HandleUpdateSettings(JsonElement payload)
    {
        var oldHotkeys = HotkeySet.From(session.Settings);
        var changes = new List<SettingChange>();
        var hasHotkeyChange = false;
        foreach (var item in payload.GetProperty("changes").EnumerateArray())
        {
            var key = item.GetProperty("key").GetString() ?? "";
            changes.Add(new SettingChange(key, item.GetProperty("value")));
            hasHotkeyChange |= key.Contains("Hotkey", StringComparison.OrdinalIgnoreCase);
        }

        var result = session.UpdateSettings(changes);
        if (result.Success && hasHotkeyChange && session.Runtime.IsConnected && !TryRegisterHotkeys(out var message))
        {
            session.UpdateSettings(HotkeyRevertChanges(oldHotkeys));
            TryRegisterHotkeys(out _);
            result = new HostCommandResult(false, message);
        }
        else if (result.Success && hasHotkeyChange && !session.Runtime.IsConnected)
        {
            UnregisterHotkeys();
            lastHotkeyRegistrationFailure = "";
        }
        else if (result.Success && hasHotkeyChange)
        {
            lastHotkeyRegistrationFailure = "";
        }
        ApplyWindowSettings();
        return ApplyResult(result);
    }

    private object HandleResetSetting(JsonElement payload)
    {
        var key = payload.GetProperty("key").GetString() ?? "";
        var oldHotkeys = HotkeySet.From(session.Settings);
        var result = session.ResetSetting(key);
        if (result.Success && key.Contains("Hotkey", StringComparison.OrdinalIgnoreCase))
            result = ReconcileChangedHotkeys(oldHotkeys);
        ApplyWindowSettings();
        return ApplyResult(result);
    }

    private object HandleResetSection(JsonElement payload)
    {
        var section = payload.GetProperty("section").GetString() ?? "";
        var oldHotkeys = HotkeySet.From(session.Settings);
        var result = session.ResetSection(section);
        if (result.Success && section.Equals("app", StringComparison.OrdinalIgnoreCase))
            result = ReconcileChangedHotkeys(oldHotkeys);
        ApplyWindowSettings();
        return ApplyResult(result);
    }

    private object HandleResetAllSettings()
    {
        var oldHotkeys = HotkeySet.From(session.Settings);
        var result = session.ResetAllSettings();
        if (result.Success)
            result = ReconcileChangedHotkeys(oldHotkeys);
        ApplyWindowSettings();
        return ApplyResult(result);
    }

    private HostCommandResult ReconcileChangedHotkeys(HotkeySet oldHotkeys)
    {
        if (!session.Runtime.IsConnected)
        {
            // The next successful bridge attach owns the deferred registration. Never leave the
            // old OS registrations live after persisting a new hotkey set.
            UnregisterHotkeys();
            lastHotkeyRegistrationFailure = "";
            return new HostCommandResult(true);
        }
        if (TryRegisterHotkeys(out var message))
        {
            lastHotkeyRegistrationFailure = "";
            return new HostCommandResult(true);
        }
        session.UpdateSettings(HotkeyRevertChanges(oldHotkeys));
        TryRegisterHotkeys(out _);
        return new HostCommandResult(false, message);
    }

    private object ApplyResult(HostCommandResult result)
    {
        ApplyWindowSettings();
        _ = PushSnapshotAsync();
        return new { success = result.Success, message = result.Message };
    }

    private void HandlePreviewWindow(JsonElement payload)
    {
        if (payload.TryGetProperty("opacity", out var opacityValue) && opacityValue.TryGetDouble(out var opacity))
            Opacity = Math.Clamp(opacity, 0.35, 1.0);
    }

    private async Task HandleHotkeyAsync(int id)
    {
        switch (id)
        {
            case HotkeyStart:
                _ = await RunPaintCommandAsync(previewOnly: false, unpreviewOnly: false);
                break;
            case HotkeyPreview:
                _ = await RunPaintCommandAsync(previewOnly: true, unpreviewOnly: false);
                break;
            case HotkeyUnPreview:
                _ = await RunPaintCommandAsync(previewOnly: false, unpreviewOnly: true);
                break;
            case HotkeyStop:
                _ = await session.StopPaintAsync();
                break;
        }
        await PushSnapshotAsync();
    }

    private void ApplyWindowSettings(string? stage = null)
    {
        Opacity = session.Settings.Opacity;
        var topMost = session.Settings.AlwaysOnTop;
        bool? nativeTopMostBefore = IsHandleCreated ? IsNativeTopMost() : null;
        TopMost = topMost;
        var setWindowPosSucceeded = true;
        var setWindowPosError = 0;
        if (IsHandleCreated)
        {
            setWindowPosSucceeded = SetWindowPos(
                Handle,
                topMost ? HwndTopMost : HwndNoTopMost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
            if (!setWindowPosSucceeded)
                setWindowPosError = Marshal.GetLastWin32Error();
        }
        ApplyDarkTitleBar();
        if (!string.IsNullOrWhiteSpace(stage))
            RecordWindowSettingsState(stage, topMost, nativeTopMostBefore, setWindowPosSucceeded, setWindowPosError);
    }

    private void RecordWindowSettingsState(string stage, bool requestedTopMost, bool? nativeTopMostBefore, bool setWindowPosSucceeded, int setWindowPosError)
    {
        if (!IsHandleCreated || IsDisposed || Disposing)
            return;

        var nativeTopMost = IsNativeTopMost();
        var state =
            $"stage={stage} requested_topmost={requestedTopMost} managed_topmost={TopMost} native_topmost_before={nativeTopMostBefore?.ToString() ?? "n/a"} native_topmost={nativeTopMost} " +
            $"visible={Visible} setwindowpos={setWindowPosSucceeded}";
        if (!setWindowPosSucceeded)
        {
            state += $" win32={setWindowPosError}";
            DiagnosticsState.SetLastCode("MC-WIN-101", state);
            session.Log.Warn("Window: " + state);
            return;
        }

        DiagnosticsState.WriteLine("window", state);
        if (nativeTopMost != requestedTopMost)
            session.Log.Warn("Window: " + state);
        else
            session.Log.Info("Window: " + state);
    }

    private bool IsNativeTopMost() => (GetWindowLong(Handle, GwlExStyle) & WsExTopMost) != 0;

    private void ApplyDarkTitleBar()
    {
        if (!IsHandleCreated || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return;
        try
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var caption = ColorRef(Color.FromArgb(32, 32, 32));
                var text = ColorRef(Color.White);
                _ = DwmSetWindowAttribute(Handle, DwmwaCaptionColor, ref caption, sizeof(int));
                _ = DwmSetWindowAttribute(Handle, DwmwaTextColor, ref text, sizeof(int));
            }
        }
        catch
        {
            // Non-client dark mode is best-effort and may be unavailable on older Windows builds.
        }
    }

    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static Icon? LoadWindowIcon()
    {
        var packagedIcon = Path.Combine(AppContext.BaseDirectory, "web", "icon.ico");
        if (File.Exists(packagedIcon))
            return new Icon(packagedIcon);
        return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    }

    private async Task PushSnapshotAsync()
    {
        var core = webView?.CoreWebView2;
        if (!webReady || core is null)
            return;
        var snapshot = await session.GetSnapshotAsync();
        PostEvent("snapshotChanged", snapshot);
    }

    private void PushSnapshotFromAnyThread()
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)(async () =>
                {
                    await PushSnapshotAsync();
                }));
                return;
            }
            _ = PushSnapshotAsync();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void PostResponse(string id, bool ok, object? data)
    {
        var core = webView?.CoreWebView2;
        if (!webReady || core is null)
            return;
        var json = JsonSerializer.Serialize(new { type = "response", id, ok, data }, JsonOptions);
        core.PostWebMessageAsJson(json);
    }

    private void PostEvent(string name, object data)
    {
        var core = webView?.CoreWebView2;
        if (!webReady || core is null)
            return;
        var json = JsonSerializer.Serialize(new { type = "event", name, data }, JsonOptions);
        core.PostWebMessageAsJson(json);
    }

    private void SendToast(string message, string level)
    {
        if (webReady && webView?.CoreWebView2 is not null)
            PostEvent("toast", new { message, level });
    }

    private void PersistWindowSnapshot()
    {
        if (!IsHandleCreated)
            return;
        session.SetWindowSnapshot(Width, Height, Left, Top);
    }

    private void RequestHotkeyRegistrationAfterAttach(bool force = false)
    {
        if (IsDisposed || Disposing || !IsHandleCreated || !session.Runtime.IsConnected)
            return;
        void RegisterAfterAttach()
        {
            if (IsDisposed || Disposing || !IsHandleCreated || !session.Runtime.IsConnected ||
                (!force && hotkeysRegistered))
            {
                return;
            }
            if (TryRegisterHotkeys(out var message))
            {
                lastHotkeyRegistrationFailure = "";
                return;
            }
            if (!string.Equals(lastHotkeyRegistrationFailure, message, StringComparison.Ordinal))
            {
                lastHotkeyRegistrationFailure = message;
                session.Log.Warn(message);
            }
        }
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)RegisterAfterAttach);
                return;
            }
            RegisterAfterAttach();
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
        {
            // The form closed while a background bridge warmup was completing.
        }
    }

    private bool TryRegisterHotkeys(out string message)
    {
        message = "";
        if (!IsHandleCreated)
        {
            message = "Hotkey registration failed: form handle is unavailable.";
            return false;
        }
        UnregisterHotkeys();
        var keys = new[]
        {
            (HotkeyStart, session.Settings.StartHotkey),
            (HotkeyPreview, session.Settings.PreviewHotkey),
            (HotkeyUnPreview, session.Settings.UnPreviewHotkey),
            (HotkeyStop, session.Settings.StopHotkey)
        };
        foreach (var (id, key) in keys)
        {
            var virtualKey = ParseVirtualKey(key);
            if (virtualKey == 0)
            {
                message = $"Hotkey registration failed: {key} (invalid function key).";
                UnregisterHotkeys();
                return false;
            }
            if (!RegisterHotKey(Handle, id, ModNoRepeat, virtualKey))
            {
                var error = Marshal.GetLastWin32Error();
                message = $"Hotkey registration failed: {key} (Win32 error {error})";
                UnregisterHotkeys();
                return false;
            }
        }
        hotkeysRegistered = true;
        return true;
    }

    private void UnregisterHotkeys()
    {
        hotkeysRegistered = false;
        if (!IsHandleCreated)
            return;
        for (var id = 1; id <= 4; ++id)
            UnregisterHotKey(Handle, id);
    }

    private static uint ParseVirtualKey(string text)
    {
        var value = HotkeySet.Normalize(text);
        if (value.Length >= 2 && value[0] == 'F' && int.TryParse(value[1..], out var f) && f is >= 1 and <= 24)
            return (uint)(0x70 + f - 1);
        return 0;
    }

    private static IEnumerable<SettingChange> HotkeyRevertChanges(HotkeySet oldHotkeys)
    {
        yield return new SettingChange("app.startHotkey", JsonSerializer.SerializeToElement(oldHotkeys.Start, JsonOptions));
        yield return new SettingChange("app.previewHotkey", JsonSerializer.SerializeToElement(oldHotkeys.Preview, JsonOptions));
        yield return new SettingChange("app.unpreviewHotkey", JsonSerializer.SerializeToElement(oldHotkeys.UnPreview, JsonOptions));
        yield return new SettingChange("app.stopHotkey", JsonSerializer.SerializeToElement(oldHotkeys.Stop, JsonOptions));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private const int GwlExStyle = -20;
    private const int WsExTopMost = 0x00000008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    private sealed record HostWebCommand(string Id, string Command, JsonElement Payload);
}
