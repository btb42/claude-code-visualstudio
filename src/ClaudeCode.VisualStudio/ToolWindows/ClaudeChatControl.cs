using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCode.VisualStudio.Services;
using ClaudeCode.VisualStudio.WebView;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Web.WebView2.Wpf;

namespace ClaudeCode.VisualStudio
{
    /// <summary>
    /// Chat tool window content: a WebView2 hosting the Claude Code chat UI, wired to a
    /// <see cref="ClaudeSession"/> that drives the real <c>claude</c> CLI.
    /// </summary>
    public class ClaudeChatControl : UserControl, IDisposable
    {
        private readonly WebView2 _webView;
        private readonly WebViewHost _host;

        // ── Per-tab state ────────────────────────────────────────────────────────────
        private sealed class TabState
        {
            public readonly string TabId;
            public string Title;           // user-visible tab label; set from first message
            public string Model = "default";
            public string PermissionMode = "default";
            public string Effort = "none";
            public bool ShowThinking = true;
            public int ForkWindowSize = 6;
            public bool ForkAllMessages = false;
            public ClaudeSession Session;
            public SessionRecord Record;
            public string LastSentText;
            public IReadOnlyList<ImageInput> LastSentImages;
            public bool ResumeRetried;
            public string PendingResumeId;
            public bool Compacting;
            public bool OptionsDirty;
            public readonly Dictionary<string, EditSnapshot> EditedFiles = new Dictionary<string, EditSnapshot>();
            public TabState(string id) { TabId = id; }
        }
        private readonly Dictionary<string, TabState> _tabs = new Dictionary<string, TabState>();
        private string _activeTabId;
        private int _nextTabIndex;

        private TabState ActiveTab => _activeTabId != null && _tabs.TryGetValue(_activeTabId, out var _aTab) ? _aTab : null;
        // Shim properties — forwards to the active tab so all existing code compiles unchanged.
        private ClaudeSession _session { get => ActiveTab?.Session; set { if (ActiveTab != null) ActiveTab.Session = value; } }
        private bool _optionsDirty { get => ActiveTab?.OptionsDirty == true; set { if (ActiveTab != null) ActiveTab.OptionsDirty = value; } }
        private bool _compacting { get => ActiveTab?.Compacting == true; set { if (ActiveTab != null) ActiveTab.Compacting = value; } }
        private SessionRecord _record { get => ActiveTab?.Record; set { if (ActiveTab != null) ActiveTab.Record = value; } }
        private string _lastSentText { get => ActiveTab?.LastSentText; set { if (ActiveTab != null) ActiveTab.LastSentText = value; } }
        private IReadOnlyList<ImageInput> _lastSentImages { get => ActiveTab?.LastSentImages; set { if (ActiveTab != null) ActiveTab.LastSentImages = value; } }
        private bool _resumeRetried { get => ActiveTab?.ResumeRetried == true; set { if (ActiveTab != null) ActiveTab.ResumeRetried = value; } }
        private string _pendingResumeId { get => ActiveTab?.PendingResumeId; set { if (ActiveTab != null) ActiveTab.PendingResumeId = value; } }
        // ── end per-tab state ─────────────────────────────────────────────────────────

        private string _model { get => ActiveTab?.Model ?? "default"; set { if (ActiveTab != null) ActiveTab.Model = value; } }
        private string _permissionMode { get => ActiveTab?.PermissionMode ?? "default"; set { if (ActiveTab != null) ActiveTab.PermissionMode = value; } }
        private string _effort { get => ActiveTab?.Effort ?? "none"; set { if (ActiveTab != null) ActiveTab.Effort = value; } }
        private bool _showThinking { get => ActiveTab?.ShowThinking ?? true; set { if (ActiveTab != null) ActiveTab.ShowThinking = value; } }
        private int _forkWindowSize { get => ActiveTab?.ForkWindowSize ?? 6; set { if (ActiveTab != null) ActiveTab.ForkWindowSize = value; } }
        private bool _forkAllMessages { get => ActiveTab?.ForkAllMessages ?? false; set { if (ActiveTab != null) ActiveTab.ForkAllMessages = value; } }

        private readonly IdeContextService _ide = new IdeContextService();
        private readonly DebugContextService _debug = new DebugContextService();
        private readonly ThemeService _theme = new ThemeService();

        private sealed class EditSnapshot { public string Path; public string OldText; }
        private readonly System.Collections.Generic.List<SessionRecord> _closedTabHistory = new System.Collections.Generic.List<SessionRecord>();
        private const int MaxClosedTabHistory = 10;
        private List<string> _tools = new List<string>();
        private List<string> _mcpServers = new List<string>();
        private bool _solutionHooked;         // subscribed to solution-load events (restore retry)
        private bool _updateWatchRunning;     // polling for a `claude update` to land
        private bool _updateRunning;          // a background `claude update` process is in flight
        private System.Threading.Timer _cliCheckTimer;   // hourly re-check for a newer CLI
        private DateTime _lastCliCheckUtc = DateTime.MinValue;   // when that check last completed
        private bool _disposed;
        private Action<Dictionary<string, string>> _themeChangedHandler;
        private Window _hostWindow;

        // Working directory for claude. Defaults to the user profile and is upgraded to the
        // solution directory once known. Cached so the send path never blocks on VS services.
        private string _cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public ClaudeChatControl()
        {
            Perf.Mark("control: ctor");
            _webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Content = _webView;

            _host = new WebViewHost(_webView);
            _host.MessageReceived += OnMessageReceived;
            _themeChangedHandler = vars => _host.PostMessage("theme", vars);
            _theme.ThemeChanged += _themeChangedHandler;

            // Create the initial tab before any message can arrive.
            _nextTabIndex = 1;
            _activeTabId = "t1";
            _tabs["t1"] = new TabState("t1");

            // KEY DIAGNOSTIC: log every keystroke that reaches WPF level (fires before WebView
            // consumes it). Used to identify which key combination is stealing focus/switching tabs.
            // Remove once the offending shortcut is identified.
            PreviewKeyDown += (s, e) =>
            {
                var mod = Keyboard.Modifiers;
                var sb = new System.Text.StringBuilder();
                if (mod.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
                if (mod.HasFlag(ModifierKeys.Alt))     sb.Append("Alt+");
                if (mod.HasFlag(ModifierKeys.Shift))   sb.Append("Shift+");
                if (mod.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
                Log.Write("KEY: " + sb + e.Key + " (sys=" + e.SystemKey + ")");

                // Space reaching WPF means the WebView dropped focus (e.g. after an async
                // clipboard write). Re-focus the WebView so VS does not interpret it as a
                // tool-window navigation command, then let the keystroke through — the
                // document-level keydown handler in app.js will route it to #input.
                if (e.Key == Key.Space && mod == ModifierKeys.None)
                {
                    _webView.Focus();
                }

                // Text-navigation keys: VS binds End/Home (and sometimes arrow keys) to IDE
                // document/tab navigation commands.  When WebView2 has focus the key was
                // already delivered to its Win32 HWND, so the cursor moves correctly.
                // Marking Handled here only prevents VS from also executing the IDE command
                // (e.g. End jumping to a different editor tab, mouse cursor disappearing).
                // Alt-modified keys are let through — those are VS menu accelerators the
                // user may intentionally want (e.g. Alt+Left = Navigate Back).
                if ((mod & ModifierKeys.Alt) == ModifierKeys.None)
                {
                    switch (e.Key)
                    {
                        case Key.Home:
                        case Key.End:
                        case Key.Left:
                        case Key.Right:
                        case Key.Up:
                        case Key.Down:
                        case Key.PageUp:
                        case Key.PageDown:
                            e.Handled = true;
                            break;
                    }
                }
            };

            Loaded += OnLoaded;
            // Hiding the panel or switching away from its tab unloads the control, and showing it
            // again loads it back. OnLoaded unsubscribes itself (it is the one-time boot), so this
            // second handler is what survives the cycle and puts the CLI check back on the clock.
            Loaded += (s, e) => StartPeriodicCliCheck();
            Unloaded += (s, e) =>
            {
                UnhookSolutionLoad();
                var timer = System.Threading.Interlocked.Exchange(ref _cliCheckTimer, null);
                timer?.Dispose();
                // Sessions are intentionally NOT disposed here. Unloaded fires whenever the panel
                // loses focus (user clicks Solution Explorer, switches tab, etc.) — that is a view
                // change, not a session termination. Killing the CLI process here was the root cause
                // of all "stops responding" issues: the CLI was killed every time the user looked away.
                // Sessions are cleaned up when the user explicitly closes/resets them, or when VS exits.
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _host.MessageReceived -= OnMessageReceived;
            _theme.ThemeChanged -= _themeChangedHandler;
            _debug.Break -= OnDebugBreak;

            if (_hostWindow != null)
            {
                _hostWindow.Activated -= OnHostWindowActivated;
                _hostWindow = null;
            }

            var timer = System.Threading.Interlocked.Exchange(ref _cliCheckTimer, null);
            timer?.Dispose();

            foreach (var tab in _tabs.Values)
                tab.Session?.Dispose();
            _tabs.Clear();

            _host.Dispose();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Event handler")]
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            Perf.Mark("control: Loaded");
            try
            {
                // Pass the current VS theme so the WebView paints in-theme from the first frame
                // (no white flash before app.js applies colors).
                long tTheme = Perf.Now;
                var themeVars = _theme.GetThemeVariables();
                Perf.Step("control: GetThemeVariables", tTheme);

                long tHost = Perf.Now;
                await _host.InitializeAsync(themeVars);
                Perf.Step("control: host.InitializeAsync total", tHost);
            }
            catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
            {
                // VS 2022+ installs the WebView2 Runtime; older Windows / VS 2019 machines may
                // lack it, so point at the Evergreen installer instead of dumping the exception.
                var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
                panel.Children.Add(new TextBlock
                {
                    Text = "Claude Code needs the Microsoft Edge WebView2 Runtime, which is not installed.",
                    TextWrapping = TextWrapping.Wrap,
                });
                var link = new System.Windows.Documents.Hyperlink(
                    new System.Windows.Documents.Run("Download the WebView2 Runtime"))
                {
                    NavigateUri = new Uri("https://go.microsoft.com/fwlink/p/?LinkId=2124703"),
                };
                link.RequestNavigate += (s2, e2) =>
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e2.Uri.AbsoluteUri) { UseShellExecute = true });
                panel.Children.Add(new TextBlock(link) { Margin = new Thickness(0, 8, 0, 0) });
                panel.Children.Add(new TextBlock
                {
                    Text = "Then reopen this window.",
                    Margin = new Thickness(0, 8, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
                Content = panel;
                return;
            }
            catch (Exception ex)
            {
                Content = new TextBlock
                {
                    Text = "Failed to start Claude Code WebView:\n" + ex.Message,
                    Margin = new Thickness(12),
                    TextWrapping = TextWrapping.Wrap,
                };
                return;
            }

            // Best-effort: upgrade cwd to the solution directory. Never blocks the chat.
            try
            {
                long tFolders = Perf.Now;
                var folders = await _ide.GetWorkspaceFoldersAsync();
                Perf.Step("control: GetWorkspaceFoldersAsync", tFolders);
                if (folders != null && folders.Count > 0) _cwd = folders[0];
            }
            catch { }

            // Listen for debugger break events so the chat can surface a live exception/pause.
            try
            {
                long tDebug = Perf.Now;
                _debug.Break += OnDebugBreak;
                await _debug.StartAsync();
                Perf.Step("control: DebugContextService.StartAsync", tDebug);
            }
            catch { }

            // One consolidated report, written long after everything above has settled — see Perf.
            // 90s: long enough to still catch the deliberately deferred command refresh below.
            Perf.FlushSoon(90000);

            // WebView2 DPI fix: on multi-monitor setups the control may render at the wrong scale
            // after a window restore or focus switch.  Subscribing to Activated forces a layout
            // cycle each time the VS window comes back, which re-queries the correct monitor DPI.
            try
            {
                var window = Window.GetWindow(this);
                if (window != null)
                {
                    _hostWindow = window;
                    window.Activated += OnHostWindowActivated;
                }
            }
            catch { }
        }

        private void OnHostWindowActivated(object sender, EventArgs e)
        {
            // WebView2 DPI bug on multi-monitor: after a window restore or focus switch,
            // WebView2 may render at the wrong scale (blurry/zoomed).  Nudging the margin
            // by 1px forces WPF to call ArrangeOverride, which makes WebView2 re-query the
            // current monitor DPI.  The nudge is one render frame — visually invisible.
            _webView.Margin = new Thickness(0, 0, 0, 1);
            _webView.Dispatcher.InvokeAsync(
                () => _webView.Margin = new Thickness(0),
                System.Windows.Threading.DispatcherPriority.Render);
        }

        // The debugger paused (breakpoint / step / thrown exception). Surface it in the
        // transcript so the user can ask Claude about the live runtime state.
        private void OnDebugBreak(DebugBreakInfo info)
        {
            if (info == null) return;
            _host.PostMessage("debugBreak", new
            {
                reason = info.Reason,
                exception = info.Exception,
                file = info.File,
                line = info.Line,
                function = info.Function,
            });
        }

        private void OnMessageReceived(WebMessage message)
        {
            Log.WriteVerbose("WebMessage: " + message.Type);
            switch (message.Type)
            {
                case "ready":
                    // The page is up: this is the number that matches "time until the chat
                    // window is usable", measured from package load.
                    Perf.Mark("page: ready (webview UI up)");
                    SendInit();
                    break;
                case "diag":
                    // Layout/scroll breadcrumbs from the page. Release builds ship without
                    // DevTools, so this is the only window into WebView-side behaviour. The
                    // page sends numbers only; cap the length so it can't be used to bulk-write.
                    {
                        var t = GetStr(message.Payload, "text") ?? string.Empty;
                        Log.Write("web: " + (t.Length > 200 ? t.Substring(0, 200) : t));
                    }
                    break;
                case "send":
                    HandleSend(message.Payload);
                    break;
                case "interrupt":
                    _session?.SendInterrupt();
                    break;
                case "newSession":
                    ResetSession();
                    break;
                case "newTab":
                    HandleNewTab(GetStr(message.Payload, "defaultModel"), GetStr(message.Payload, "defaultMode"), GetStr(message.Payload, "defaultEffort"));
                    break;
                case "forkTab":
                    HandleForkTab(GetStr(message.Payload, "msgId"));
                    break;
                case "switchTab":
                    HandleSwitchTab(GetStr(message.Payload, "tabId"));
                    break;
                case "closeTab":
                    HandleCloseTab(GetStr(message.Payload, "tabId"));
                    break;
                case "reopenLastTab":
                    HandleReopenLastTab(GetStr(message.Payload, "tabId"));
                    break;
                case "renameTab":
                    HandleRenameTab(GetStr(message.Payload, "tabId"), GetStr(message.Payload, "newTitle"));
                    break;
                case "setModel":
                    _model = InputValidation.SanitizeModel(GetStr(message.Payload, "model"), "default");
                    _optionsDirty = true;
                    SaveOptions();
                    break;
                case "setPermissionMode":
                    _permissionMode = InputValidation.SanitizeChoice(GetStr(message.Payload, "mode"), InputValidation.AllowedModes, "default");
                    // Apply to the live process when it can take it — a mode is usually switched
                    // *because* a prompt is on screen, and waiting for the next turn's relaunch
                    // would keep asking through the rest of the current one. Otherwise fall back to
                    // relaunching with the new mode on the next send.
                    if (_session == null || !_session.SetPermissionMode(_permissionMode)) _optionsDirty = true;
                    SaveOptions();
                    break;
                case "setEffort":
                    _effort = InputValidation.SanitizeChoice(GetStr(message.Payload, "effort"), InputValidation.AllowedEfforts, "none");
                    _optionsDirty = true;
                    SaveOptions();
                    break;
                case "setShowThinking":
                    _showThinking = GetBool(message.Payload, "on", true);
                    SaveOptions();
                    break;
                case "setForkWindowSize":
                    _forkWindowSize = Math.Max(1, Math.Min(200, GetInt(message.Payload, "size", 6)));
                    SaveOptions();
                    break;
                case "setForkAllMessages":
                    _forkAllMessages = GetBool(message.Payload, "on", false);
                    SaveOptions();
                    break;
                case "getContext":
                    SendContext();
                    break;
                case "getFiles":
                    SendFiles();
                    break;
                case "getUsage":
                    FetchAndSendAccountData();
                    break;
                case "getMcp":
                    SendMcp();
                    break;
                case "getCommands":
                    SendCommands();
                    break;
                case "mcpAuth":
                    LaunchClaudeTerminal();
                    break;
                case "openClaudeTerminal":
                    LaunchClaudeTerminal();
                    break;
                case "startLogin":
                    StartInPanelLogin();
                    break;
                case "submitAuthCode":
                    SubmitAuthCode(GetStr(message.Payload, "code"));
                    break;
                case "cancelLogin":
                    CancelInPanelLogin();
                    break;
                case "installCli":
                    LaunchCliInstall();
                    break;
                case "updateCli":
                    LaunchCliUpdate();
                    break;
                case "updateCliInTerminal":
                    LaunchCliUpdateInTerminal();
                    break;
                case "recheckSetup":
                    // An explicit user re-check must mean "ask everything again" — including the
                    // npm "latest", which is otherwise cached for the lifetime of the process.
                    SendSetupStatus(forceRefresh: true);
                    break;
                case "pickImage":
                    PickImage();
                    break;
                case "getSessionDiag":
                    SendSessionDiag(ActiveTab);
                    break;
                case "restartTab":
                    RestartTabSession(ActiveTab);
                    SendSessionDiag(ActiveTab);
                    break;
                case "restartAllTabs":
                    foreach (var rt in _tabs.Values) RestartTabSession(rt);
                    SendSessionDiag(ActiveTab);
                    break;
                case "pickFile":
                    PickFile();
                    break;
                case "compact":
                    HandleCompact();
                    break;
                case "permissionResponse":
                    HandlePermissionResponse(message.Payload);
                    break;
                case "openExternal":
                    TryOpenExternal(GetStr(message.Payload, "url"));
                    break;
                case "openFile":
                    OpenFileFromWebview(message.Payload);
                    break;
                case "openToolFile":
                    OpenToolFileFromWebview(message.Payload);
                    break;
                case "openDiff":
                    OpenDiffFromWebview(message.Payload);
                    break;
            }
        }

        // The inline diff card's "Open diff" button — open the full native VS Before/After diff for
        // a specific edit, on demand (looked up by tool-use id from the kept pre-edit snapshot).
        private void OpenDiffFromWebview(JsonElement payload)
        {
            string id = GetStr(payload, "id");
            if (string.IsNullOrEmpty(id)) return;
            var ef = ActiveTab?.EditedFiles;
            if (ef != null && ef.TryGetValue(id, out var snap))
                ThreadHelper.JoinableTaskFactory.RunAsync(async () => await ShowEditAsync(snap)).FireAndForget();
        }

        // Open a local file (optionally at a line) in the VS editor — used by the selection
        // chip on a sent message. Only opens an existing file in the editor; never executes.
        //
        // Security: the path arrives from the (local but untrusted) WebView, so it is confined to
        // the workspace before being opened — otherwise a crafted openFile message could pull any
        // readable file on disk (a credential store, another project's source) into the editor,
        // where its contents then become attachable chat context. The legitimate sender is the
        // selection chip, which can only ever name a file that is open in the IDE, so
        // currently-open documents are allowed too — that keeps the chip working for files edited
        // from outside the solution folder.
        private void OpenFileFromWebview(JsonElement payload)
        {
            string path = GetStr(payload, "path");
            if (string.IsNullOrEmpty(path)) return;
            int line = GetInt(payload, "line", 0);
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var roots = new List<string>();
                    if (!string.IsNullOrEmpty(_cwd)) roots.Add(_cwd);
                    try
                    {
                        var folders = await _ide.GetWorkspaceFoldersAsync();
                        if (folders != null) roots.AddRange(folders);
                    }
                    catch { }

                    if (!InputValidation.IsUnderAnyRoot(path, roots))
                    {
                        List<string> open = null;
                        try { open = await _ide.GetOpenFilesAsync(); } catch { }
                        if (!InputValidation.IsSamePath(path, open))
                        {
                            Log.Write("openFile blocked (outside workspace and not an open document)");
                            return;
                        }
                    }

                    await _ide.OpenFileAsync(path, line > 0 ? (int?)line : null);
                }
                catch { }
            }).FireAndForget();
        }

        private void OpenToolFileFromWebview(JsonElement payload)
        {
            string path = GetStr(payload, "path");
            if (string.IsNullOrEmpty(path)) return;
            int line = GetInt(payload, "line", 0);
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    if (!System.IO.File.Exists(path)) return;
                    await _ide.OpenFileAsync(path, line > 0 ? (int?)line : null);
                }
                catch { }
            }).FireAndForget();
        }

        private void FetchAndSendAccountData()
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var data = await AccountService.FetchAsync();
                var limits = new List<object>();
                foreach (var l in data.Limits)
                    limits.Add(new { name = l.Name, percent = l.Percent, resetsIn = l.ResetsIn, severity = l.Severity });

                UsagePeriodInsights day = null, week = null;
                try { UsageInsightsService.Compute(out day, out week); }
                catch (Exception ex) { Log.Write("FetchAndSendAccountData insights: " + ex.Message); }

                _host.PostMessage("accountData", new
                {
                    authMethod = data.AuthMethod,
                    email = data.Email,
                    organization = data.Organization,
                    plan = data.Plan,
                    limits = limits.ToArray(),
                    extraUsage = data.ExtraUsage == null ? (object)null : new
                    {
                        enabled = data.ExtraUsage.Enabled,
                        usedCredits = data.ExtraUsage.UsedCredits,
                        monthlyLimit = data.ExtraUsage.MonthlyLimit,
                        utilization = data.ExtraUsage.Utilization,
                        currency = data.ExtraUsage.Currency,
                        decimalPlaces = data.ExtraUsage.DecimalPlaces,
                    },
                    insights = new
                    {
                        day = day == null ? (object)null : new { totalTokens = day.TotalTokens, sessions = day.Sessions, pctOver150k = day.PctOver150k, pctSidechain = day.PctSidechain },
                        week = week == null ? (object)null : new { totalTokens = week.TotalTokens, sessions = week.Sessions, pctOver150k = week.PctOver150k, pctSidechain = week.PctSidechain },
                    },
                    manageUrl = data.ManageUrl,
                    error = data.Error,
                    extensionBuild = BuildInfo.Build,
                });
            });
        }

        private void SendContext()
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var sel = await _ide.GetActiveSelectionAsync();
                var openFiles = await _ide.GetOpenFilesAsync();
                var folders = await _ide.GetWorkspaceFoldersAsync();
                var dbg = await _debug.GetDebugStateAsync();

                // Refresh cwd from the open solution if no session has pinned it yet and the user
                // hasn't chosen one — the tool window often loads before the solution finished
                // opening, leaving the early (user-home) fallback showing here.
                if (_session == null)
                {
                    try { var d = await GetWorkingDirectoryAsync(); if (!string.IsNullOrEmpty(d)) _cwd = d; }
                    catch { }
                }

                // CLAUDE.md project/user memory the CLI will load for this working dir.
                string projectMd = null, userMd = null;
                try
                {
                    if (!string.IsNullOrEmpty(_cwd))
                    {
                        var p = Path.Combine(_cwd, "CLAUDE.md");
                        if (File.Exists(p)) projectMd = p;
                    }
                    var u = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "CLAUDE.md");
                    if (File.Exists(u)) userMd = u;
                }
                catch { }

                _host.PostMessage("context", new
                {
                    cwd = _cwd,
                    workspaceFolders = folders,
                    activeFile = sel?.FilePath,
                    languageId = sel?.LanguageId,
                    hasSelection = sel?.HasSelection ?? false,
                    selStart = sel?.StartLine ?? 0,
                    selEnd = sel?.EndLine ?? 0,
                    openFiles = openFiles,
                    claudeMdProject = projectMd,
                    claudeMdUser = userMd,
                    tools = _tools,
                    mcpServers = _mcpServers,
                    model = _model,
                    effort = _effort,
                    permissionMode = _permissionMode,
                    sessionId = _session?.SessionId,
                    dbgActive = dbg?.IsActive ?? false,
                    dbgMode = dbg?.Mode,
                    dbgProcess = dbg?.ProcessName,
                    dbgFunction = dbg?.Function,
                    dbgFile = dbg?.File,
                    dbgLine = dbg?.Line ?? 0,
                    dbgException = dbg?.Exception,
                    dbgLocals = dbg?.Locals?.Count ?? 0,
                });
            }).FireAndForget();
        }

        // Runs `claude mcp list` out-of-band so the /mcp screen can show configured servers
        // (with live health) even before the first message starts a chat session.
        private void SendMcp()
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (_session == null)
                    {
                        try { var d = await GetWorkingDirectoryAsync(); if (!string.IsNullOrEmpty(d)) _cwd = d; }
                        catch { }
                    }
                    var servers = await McpService.ListAsync(_cwd);
                    var list = new List<object>();
                    foreach (var s in servers)
                        list.Add(new { name = s.Name, detail = s.Detail, status = s.Status, ok = s.Ok, scope = s.Scope, missingEnv = s.MissingEnv, envMaybeInvalid = s.EnvMaybeInvalid });
                    _host.PostMessage("mcpList", new { servers = list });
                }
                catch (Exception ex)
                {
                    Log.Write("SendMcp: " + ex.Message);
                    _host.PostMessage("mcpList", new { servers = new List<object>(), error = ex.Message });
                }
            });
        }

        private void RestartTabSession(TabState tab)
        {
            if (tab == null) return;
            tab.Session?.Dispose();
            tab.Session = null;
            tab.PendingResumeId = null;
            if (tab.Record != null) { tab.Record.SessionId = null; SaveAllTabs(); }
            Log.Write("RestartTabSession: tab=" + tab.TabId);
            _host.PostMessage("status", new { tabId = tab.TabId, state = "idle" });
        }

        private void SendSessionDiag(TabState tab)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var proxyUrl = ReadProxyUrl();
                bool? proxyOk = null;
                if (!string.IsNullOrEmpty(proxyUrl))
                {
                    try
                    {
                        var uri = new Uri(proxyUrl);
                        var port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
                        using var tcp = new System.Net.Sockets.TcpClient();
                        var ct = tcp.ConnectAsync(uri.Host, port);
                        proxyOk = await System.Threading.Tasks.Task.WhenAny(ct, System.Threading.Tasks.Task.Delay(1500)) == ct && !ct.IsFaulted;
                    }
                    catch { proxyOk = false; }
                }

                // Read the last ~40 useful log lines, stripping scroll/resize/AccountService spam.
                var logLines = new List<string>();
                try
                {
                    var lines = System.IO.File.ReadAllLines(Log.Path);
                    for (int li = lines.Length - 1; li >= 0 && logLines.Count < 40; li--)
                    {
                        var ln = lines[li];
                        if (ln.Contains("restore(resize)") || ln.Contains("restore(tab-switch)") ||
                            ln.Contains("restore(scroll") || ln.Contains("AccountService: no credentials") ||
                            ln.Contains("web: "))
                            continue;
                        logLines.Insert(0, ln);
                    }
                }
                catch { }

                string cliVer = null;
                try { cliVer = GetInstalledCliVersion(); } catch { }

                _host.PostMessage("sessionDiag", new
                {
                    tabId = tab?.TabId ?? "—",
                    sessionId = tab?.Session?.SessionId ?? tab?.Record?.SessionId,
                    isRunning = tab?.Session?.IsRunning ?? false,
                    permissionMode = _permissionMode,
                    cliVersion = cliVer ?? "—",
                    proxyUrl = proxyUrl ?? "—",
                    proxyOk = proxyOk,
                    tabCount = _tabs.Count,
                    log = string.Join("\n", logLines),
                    extensionBuild = BuildInfo.Build,
                });
            }).FireAndForget();
        }

        // How long to sit on the slash-command refresh when the cache already answered. Spawning
        // a claude CLI is ~13s of process work; doing it while the solution is still loading buys
        // nothing (the palette is already populated) and competes with the IDE coming up.
        private const int WarmCommandRefreshDelayMs = 30000;

        // Fetches the CLI's full slash-command set out-of-band so the / palette shows the
        // complete list (built-ins + project .claude/commands) before the first message
        // starts a session. The live session's system/init refreshes the same "commands"
        // message later. Stale-while-revalidate: a per-cwd cache is shown instantly, then the
        // live fetch refreshes both the UI and the cache. On a cold cache the UI shows a
        // "loading" note for the few seconds the fetch (CLI startup + SessionStart hooks) takes.
        //
        // cwdResolved: the caller has just settled the working directory, so skip re-asking for
        // it. That question has to be answered on the UI thread, and during startup the UI thread
        // is busy — the hop measured 3.1s, which the / palette spent empty for no reason, since
        // the cache underneath it answers in 6ms.
        private void SendCommands(int warmRefreshDelayMs = 0, bool cwdResolved = false)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (_session == null && !cwdResolved)
                    {
                        try { var d = await GetWorkingDirectoryAsync(); if (!string.IsNullOrEmpty(d)) _cwd = d; }
                        catch { }
                    }

                    // 1) Instant fill from cache, or signal "loading" when the cache is cold.
                    long tc = Perf.Now;
                    var cached = SlashCommandCache.Load(_cwd);
                    var cachedModels = ModelListCache.Load();
                    Perf.Step("commands: cache load", tc);
                    bool warm = cached != null && cached.Count > 0;
                    if (warm)
                        _host.PostMessage("commands", new { commands = cached });
                    else
                        _host.PostMessage("commandsLoading", new { on = true });
                    // The picker opened on the fallback rows; the list the CLI reported last time
                    // names the current models until the live probe below lands.
                    if (cachedModels != null) PostModels(cachedModels, "cache");

                    // A warm cache means nothing is waiting on the refresh, so let the IDE finish
                    // starting before spending a CLI process on it. A cold cache is the opposite:
                    // the palette is showing "loading" until this lands, so it goes out at once.
                    if (warm && warmRefreshDelayMs > 0)
                    {
                        Perf.Mark("commands: refresh deferred " + warmRefreshDelayMs + "ms (warm cache)");
                        await System.Threading.Tasks.Task.Delay(warmRefreshDelayMs).ConfigureAwait(false);
                    }

                    // 2) Live fetch refreshes the UI + cache (then clears the loading note).
                    try
                    {
                        long tl = Perf.Now;
                        var probe = await SlashCommandService.ProbeAsync(_cwd);
                        Perf.Step("commands: live CLI fetch", tl);
                        if (probe.Commands.Count > 0)
                        {
                            SlashCommandCache.Save(_cwd, probe.Commands);
                            _host.PostMessage("commands", new { commands = probe.Commands });
                        }
                        // The same probe answers with the models this CLI and account can use.
                        if (probe.Models != null && probe.Models.Count > 0)
                        {
                            ModelListCache.Save(probe.Models);
                            PostModels(probe.Models, "cli");
                        }
                    }
                    finally
                    {
                        _host.PostMessage("commandsLoading", new { on = false });
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("SendCommands: " + ex.Message);
                }
            });
        }

        // First-run readiness: is the claude CLI installed, and is there a stored login? Drives
        // the onboarding banner so a new user is guided to install / log in instead of hitting a
        // raw "could not launch" / exit-code error. No network call (token validity is not checked
        // here — an expired token still reports loggedIn=true; a failed turn then guides re-login).
        // Cached for the process lifetime: the npm "latest" tag changes rarely, and we re-read the
        // *installed* version each time, so the outdated warning clears as soon as the user updates.
        private static string _cachedLatestCli;

        // How long to hold back the version probe on the load path. `claude --version` is a ~3s
        // process; the install/login banners it gates do not depend on it, so it can wait until
        // the IDE is past its startup rush.
        private const int SetupVersionDelayMs = 5000;

        private void SendSetupStatus(bool forceRefresh = false, bool periodic = false, int versionDelayMs = 0)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (forceRefresh) _cachedLatestCli = null;
                    long t = Perf.Now;
                    bool cliFound = ClaudeCliLocator.IsInstalled();
                    Perf.Step("setup: ClaudeCliLocator.IsInstalled", t);

                    t = Perf.Now;
                    bool loggedIn = cliFound && AccountService.HasStoredToken();
                    Perf.Step("setup: HasStoredToken", t);

                    // HasStoredToken only reads credential files on disk. Some platforms (Windows
                    // native install) store the OAuth token outside those files — fall back to
                    // `claude auth status --json` in that case. It is a ~1.5s process but runs on
                    // a thread-pool thread here, so it never touches the UI.
                    if (cliFound && !loggedIn)
                    {
                        t = Perf.Now;
                        var authStatus = AccountService.GetAuthStatus();
                        Perf.Step("setup: GetAuthStatus fallback", t);
                        if (authStatus != null && authStatus.LoggedIn) loggedIn = true;
                    }

                    // npm presence decides whether the banner offers a one-click "Install CLI"
                    // (runs npm in a visible terminal) or just a "get Node.js" link.
                    t = Perf.Now;
                    bool npmFound = !cliFound && IsNpmAvailable();
                    Perf.Step("setup: IsNpmAvailable", t);

                    // Version check is general / future-proof: read the installed version and the
                    // current npm "latest" at runtime, compare numerically — no hardcoded versions.
                    // Only run it once the CLI is usable so the not-installed / not-logged-in
                    // banners still post instantly (this does a process + network call).
                    string cliVersion = null, latestCliVersion = null;
                    bool cliOutdated = false;

                    // On the load path, answer the question the user might actually be blocked on
                    // — is the CLI there, am I logged in — before spending a process on the version.
                    // Everything below is unchanged; it just happens a few seconds later.
                    if (cliFound && loggedIn && versionDelayMs > 0)
                    {
                        _host.PostMessage("setup", new
                        {
                            cliFound = cliFound,
                            loggedIn = loggedIn,
                            npmFound = npmFound,
                            cliVersion = (string)null,
                            latestCliVersion = (string)null,
                            cliOutdated = false,
                            periodic = false,
                        });
                        Perf.Mark("setup: version probe deferred " + versionDelayMs + "ms");
                        await System.Threading.Tasks.Task.Delay(versionDelayMs).ConfigureAwait(false);
                    }

                    if (cliFound && loggedIn)
                    {
                        t = Perf.Now;
                        cliVersion = GetInstalledCliVersion();
                        Perf.Step("setup: claude --version (process)", t);

                        t = Perf.Now;
                        latestCliVersion = GetLatestCliVersion();
                        Perf.Step("setup: npm registry latest (network)", t);
                        cliOutdated = IsCliOutdated(cliVersion, latestCliVersion);
                        Log.Write("setup: cli=" + (cliVersion ?? "?") + " latest=" +
                                  (latestCliVersion ?? "?") + " outdated=" + cliOutdated +
                                  (forceRefresh ? " (forced)" : ""));
                    }

                    _host.PostMessage("setup", new
                    {
                        cliFound = cliFound,
                        loggedIn = loggedIn,
                        npmFound = npmFound,
                        cliVersion = cliVersion,
                        latestCliVersion = latestCliVersion,
                        cliOutdated = cliOutdated,
                        // Tells the page this is the hourly re-check rather than a load-time or
                        // user-requested one, which is what lets a dismissed reminder come back.
                        periodic = periodic,
                    });

                    // Keep checking for the rest of the VS session, not just at startup.
                    _lastCliCheckUtc = DateTime.UtcNow;
                    StartPeriodicCliCheck();
                }
                catch (Exception ex) { Log.Write("SendSetupStatus: " + ex.Message); }
            });
        }

        // How often to re-ask, while VS stays open, whether a newer CLI has shipped.
        internal const int CliCheckIntervalMs = 60 * 60 * 1000;   // 1 hour

        /// <summary>
        /// Test override for that interval, in milliseconds: <c>CLAUDE_CODE_VS_CHECK_MS</c>.
        /// <para>
        /// Without it the update banner cannot be exercised on a running VS at all — the hourly tick
        /// is the only trigger while the panel is healthy (Re-check exists only once a banner is
        /// already showing), so verifying a change meant waiting an hour or restarting VS, and
        /// restarting hides the very regression worth testing. Set it to 60000, downgrade the CLI,
        /// and the banner should appear within the minute — including after hiding and re-showing
        /// the panel, which is what used to kill the timer for good.
        /// </para>
        /// <para>Floored at 10s so a typo cannot turn the check into a process-spawning hot loop.</para>
        /// </summary>
        internal static int ResolveCliCheckIntervalMs(string raw)
        {
            const int MinMs = 10 * 1000;
            const int MaxMs = 24 * 60 * 60 * 1000;
            if (string.IsNullOrWhiteSpace(raw)) return CliCheckIntervalMs;
            if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out var ms))
                return CliCheckIntervalMs;
            if (ms < MinMs) return MinMs;
            return ms > MaxMs ? MaxMs : ms;
        }

        /// <summary>
        /// How long the re-armed timer should wait before its first tick, given when the last check
        /// actually ran. Hiding the panel unloads the control and stops the timer, so re-arming with
        /// a flat hour would let a panel that is hidden and shown every few minutes go for ever
        /// without a check — the clock has to carry across the gap. A panel hidden for longer than
        /// the interval is due immediately, subject to a small floor so a re-dock does not fire a
        /// process + network call in the middle of the layout churn.
        /// </summary>
        internal static int NextCliCheckDelayMs(DateTime lastCheckUtc, DateTime nowUtc, int intervalMs)
        {
            const int FloorMs = 5000;
            if (lastCheckUtc == DateTime.MinValue) return intervalMs;

            var elapsed = nowUtc - lastCheckUtc;
            // A clock that jumped backwards (or a last-check stamp from the future) must not park
            // the timer beyond its interval.
            if (elapsed < TimeSpan.Zero) return intervalMs;

            var remaining = intervalMs - elapsed.TotalMilliseconds;
            if (remaining <= FloorMs) return FloorMs;
            return remaining >= intervalMs ? intervalMs : (int)remaining;
        }

        /// <summary>
        /// Re-run the setup/version check every hour for as long as the window lives.
        /// <para>
        /// The check used to run once, when the chat window first loaded. VS commonly stays open
        /// for days, so the banner reported whatever was current the morning the solution was
        /// opened and never noticed a release that landed afterwards.
        /// </para>
        /// <para>
        /// Deliberately started from the *end* of the first status run - which is already on a
        /// thread-pool thread - so it adds nothing to the load path: no work happens here beyond
        /// allocating a timer, and the first tick is an hour away, long after the window is up.
        /// Each tick then runs on a thread-pool thread as well (<see cref="SendSetupStatus"/> does
        /// its own <c>Task.Run</c>), so the process + network call never touches the UI thread.
        /// </para>
        /// </summary>
        private void StartPeriodicCliCheck()
        {
            if (_cliCheckTimer != null) return;
            // Nothing has checked yet: the load path is mid-check and will arm this itself.
            if (_lastCliCheckUtc == DateTime.MinValue) return;

            // Read the interval each time the timer is armed, so changing the override and then
            // hiding/showing the panel picks it up without restarting VS.
            var interval = ResolveCliCheckIntervalMs(Environment.GetEnvironmentVariable("CLAUDE_CODE_VS_CHECK_MS"));
            var due = NextCliCheckDelayMs(_lastCliCheckUtc, DateTime.UtcNow, interval);
            if (interval != CliCheckIntervalMs)
                Log.Write("cli check: interval override " + interval + "ms, first tick in " + due + "ms");

            var timer = new System.Threading.Timer(_ =>
            {
                // An update in flight already has a watcher pushing a fresh status when it lands;
                // a second check racing it could flip the banner back mid-update.
                if (_updateRunning || _updateWatchRunning) return;

                Log.Write("cli check: hourly");
                // Forced: the npm "latest" is cached for the process lifetime, and re-reading the
                // same cached answer every hour would defeat the point of checking at all.
                SendSetupStatus(forceRefresh: true, periodic: true);
            }, null, due, interval);

            // Two status runs can reach this at once (init racing a re-check); keep the first.
            if (System.Threading.Interlocked.CompareExchange(ref _cliCheckTimer, timer, null) != null)
                timer.Dispose();
        }

        // Runs `claude --version` and pulls the X.Y.Z it prints (e.g. "2.1.170 (Claude Code)").
        private static string GetInstalledCliVersion()
        {
            try
            {
                var cli = ClaudeCliLocator.Locate();
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cli.FileName,
                    Arguments = cli.ArgumentPrefix + "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return null;
                    string outp = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
                    var m = System.Text.RegularExpressions.Regex.Match(outp ?? string.Empty, @"\d+\.\d+\.\d+");
                    return m.Success ? m.Value : null;
                }
            }
            catch (Exception ex) { Log.Write("GetInstalledCliVersion: " + ex.Message); return null; }
        }

        // The release channel the CLI updates along: "latest" (the default) or "stable", read from
        // autoUpdatesChannel in the user settings file. The banner's whole job is to predict what
        // clicking "Update CLI" would install, and a stable-channel user measured against the
        // latest head gets a prompt that updating can never satisfy - stable trails latest by
        // about a week, so the banner would reappear for ever, every hour, on an up-to-date CLI.
        internal static string GetUpdateChannel()
        {
            try
            {
                // CLAUDE_CONFIG_DIR relocates the whole ~/.claude tree; honour it or we would read
                // a settings file the CLI itself is ignoring.
                var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

                var settings = Path.Combine(dir, "settings.json");
                if (!File.Exists(settings)) return "latest";

                using (var doc = JsonDocument.Parse(File.ReadAllText(settings)))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("autoUpdatesChannel", out var ch) &&
                        ch.ValueKind == JsonValueKind.String)
                    {
                        var name = (ch.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                        if (name == "stable" || name == "latest") return name;
                    }
                }
            }
            catch (Exception ex) { Log.Write("GetUpdateChannel: " + ex.Message); }
            return "latest";
        }

        // Head of the release channel an update would actually install from. The npm dist-tags
        // mirror the native installer's channels (stable/latest carry the same versions), so one
        // small request answers for both install kinds. Cached after the first success; returns
        // null on any failure (offline etc) so we simply don't warn.
        private static string GetLatestCliVersion()
        {
            if (!string.IsNullOrEmpty(_cachedLatestCli)) return _cachedLatestCli;
            try
            {
                string channel = GetUpdateChannel();
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers.Add("User-Agent", "ClaudeCode-VS-Extension");
                    string json = wc.DownloadString("https://registry.npmjs.org/-/package/@anthropic-ai/claude-code/dist-tags");
                    using (var doc = JsonDocument.Parse(json))
                    {
                        // An unknown/retired channel tag falls back to latest rather than warning
                        // about nothing at all.
                        if (!doc.RootElement.TryGetProperty(channel, out var v) ||
                            v.ValueKind != JsonValueKind.String)
                            doc.RootElement.TryGetProperty("latest", out v);

                        if (v.ValueKind == JsonValueKind.String)
                        {
                            _cachedLatestCli = v.GetString();
                            Log.Write("cli channel=" + channel + " head=" + (_cachedLatestCli ?? "?"));
                            return _cachedLatestCli;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("GetLatestCliVersion: " + ex.Message); }
            return null;
        }

        // True only when both versions parse and latest > installed (numeric major.minor.patch).
        internal static bool IsCliOutdated(string installed, string latest)
        {
            var a = ParseVersion(installed);
            var b = ParseVersion(latest);
            if (a == null || b == null) return false;
            for (int i = 0; i < 3; i++)
            {
                if (b[i] > a[i]) return true;
                if (b[i] < a[i]) return false;
            }
            return false;
        }

        private static int[] ParseVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(v, @"(\d+)\.(\d+)\.(\d+)");
            if (!m.Success) return null;
            return new[]
            {
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value),
            };
        }

        // True when an npm launcher (npm.cmd / npm.exe / npm) is on PATH — used to gate the
        // optional one-click CLI install. We don't try to bootstrap Node itself.
        private static bool IsNpmAvailable()
        {
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string d;
                    try { d = dir.Trim(); } catch { continue; }
                    if (File.Exists(Path.Combine(d, "npm.cmd")) ||
                        File.Exists(Path.Combine(d, "npm.exe")) ||
                        File.Exists(Path.Combine(d, "npm")))
                        return true;
                }
            }
            catch { }
            return false;
        }

        // Runs the global CLI install in a VISIBLE terminal so the user sees progress + any errors
        // and explicitly consents — never a silent background install. The command is a fixed
        // literal (no webview input), so there is no injection surface. After it finishes the user
        // clicks "Re-check" (a VS restart may be needed for the new claude to be on VS's PATH).
        private void LaunchCliInstall()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/k npm install -g @anthropic-ai/claude-code",
                    WorkingDirectory = string.IsNullOrEmpty(_cwd) ? Environment.CurrentDirectory : _cwd,
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex) { Log.Write("LaunchCliInstall: " + ex.Message); }
        }

        // Updates the SAME claude the extension actually runs, via its built-in self-updater
        // (`claude update`). Using the LOCATED binary is what makes this correct: a hardcoded
        // `npm i -g` updates the npm copy, but the extension may resolve a different install
        // (e.g. the native ~/.local/bin build) — updating that one is the only thing that moves
        // the version the extension uses. Native self-update needs neither npm nor node.
        // Runs in a VISIBLE terminal (same consent model as install). The target is the located
        // CLI path (filesystem, never webview input), so there is no injection surface.
        private const int UpdateTimeoutMs = 10 * 60 * 1000;

        /// <summary>
        /// Runs <c>claude update</c> without a console window, reporting progress in the banner.
        /// <para>
        /// The visible terminal used to be the only sign the update had done anything, so it earned
        /// its place; now that completion is detected and confirmed in the UI, the window is just a
        /// leftover to close (<c>cmd /k</c> deliberately stays open). Output is captured instead —
        /// but a hidden process that fails or stalls must never be silent, so a non-zero exit or a
        /// timeout surfaces the output in the banner with a "Run in terminal" escape hatch, which
        /// is also the way out if the updater ever needs interactive input.
        /// </para>
        /// </summary>
        private void LaunchCliUpdate()
        {
            if (_updateRunning) { Log.Write("LaunchCliUpdate: already running"); return; }
            _updateRunning = true;

            _host.PostMessage("cliUpdate", new { state = "running" });
            WatchForCliUpdate();   // safety net: catches a swap that lands after the process exits

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                string before = GetInstalledCliVersion();
                string output = string.Empty;
                int exit = -1;
                bool timedOut = false;

                try
                {
                    // Same launcher resolution the extension uses everywhere else, so the binary
                    // that gets updated is the one the chat actually runs (npm shim included).
                    var cli = ClaudeCliLocator.Locate();
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = cli.FileName,
                        Arguments = cli.ArgumentPrefix + "update",
                        WorkingDirectory = string.IsNullOrEmpty(_cwd) ? Environment.CurrentDirectory : _cwd,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    Log.Write("cli update (background): " + cli.ResolvedPath);
                    using (var p = System.Diagnostics.Process.Start(psi))
                    {
                        if (p == null) throw new InvalidOperationException("process did not start");

                        // Read both pipes before waiting: a full stderr buffer would deadlock us.
                        var stdout = p.StandardOutput.ReadToEndAsync();
                        var stderr = p.StandardError.ReadToEndAsync();

                        if (!p.WaitForExit(UpdateTimeoutMs))
                        {
                            timedOut = true;
                            try { p.Kill(); } catch { }
                        }
                        else
                        {
                            exit = p.ExitCode;
                        }

                        output = ((await stdout.ConfigureAwait(false) ?? string.Empty) + "\n" +
                                  (await stderr.ConfigureAwait(false) ?? string.Empty)).Trim();
                    }
                }
                catch (Exception ex)
                {
                    output = ex.Message;
                    Log.Write("LaunchCliUpdate: " + ex.Message);
                }
                finally { _updateRunning = false; }

                // Keep the updater's own words whatever the exit code says. They were previously
                // thrown away on the success path, which is exactly the path that went wrong: the
                // CLI downloaded a release, failed to put it in place, exited 0, and the only
                // record left behind was "ok: 2.1.241 -> 2.1.241".
                if (!string.IsNullOrEmpty(output)) Log.Write("cli update output: " + Tail(output, 600));

                if (timedOut || exit != 0)
                {
                    Log.Write("cli update FAILED exit=" + (timedOut ? "timeout" : exit.ToString()));
                    _host.PostMessage("cliUpdate", new
                    {
                        state = "failed",
                        detail = timedOut
                            ? "Timed out after 10 minutes."
                            : Tail(output, 400),
                    });
                    return;
                }

                string after = GetInstalledCliVersion();
                bool changed = !string.IsNullOrEmpty(after) && after != before;
                Log.Write("cli update " + (changed ? "ok: " : "NO-OP: ") + (before ?? "?") + " -> " + (after ?? "?"));

                // Exit 0 is not proof that anything moved. When it doesn't, saying so is the whole
                // point: the banner otherwise re-renders identically and the click reads as having
                // done nothing at all, so the user just presses it again. The watcher started
                // earlier still runs, so a swap that lands late is still reported as a success.
                if (!changed)
                {
                    _host.PostMessage("cliUpdate", new
                    {
                        state = "stalled",
                        version = after,
                        // Roomier than the failure detail: this is the only account of what went
                        // wrong, and the banner scrolls it.
                        detail = Tail(output, 600),
                    });
                    SendSetupStatus(forceRefresh: true);
                    return;
                }

                // Report the outcome directly rather than relying on the status message racing it.
                _host.PostMessage("cliUpdate", new
                {
                    state = "done",
                    version = after,
                    changed = true,
                });
                SendSetupStatus(forceRefresh: true);
                // A CLI release is when new models (and commands) arrive: re-read both from it.
                SendCommands();
            });
        }

        // The original visible-terminal path, kept as the escape hatch offered when the background
        // update fails — some failures (auth, a prompt) are only resolvable interactively.
        private void LaunchCliUpdateInTerminal()
        {
            try
            {
                var cli = ClaudeCliLocator.Locate();
                string target = (!string.IsNullOrEmpty(cli.ResolvedPath) && File.Exists(cli.ResolvedPath))
                    ? cli.ResolvedPath
                    : "claude";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/k \"\"" + target + "\" update\"",
                    WorkingDirectory = string.IsNullOrEmpty(_cwd) ? Environment.CurrentDirectory : _cwd,
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
                WatchForCliUpdate();
            }
            catch (Exception ex) { Log.Write("LaunchCliUpdateInTerminal: " + ex.Message); }
        }

        private static string Tail(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Trim();
            return s.Length <= max ? s : "…" + s.Substring(s.Length - max);
        }

        private static string ReadProxyUrl()
        {
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var path = System.IO.Path.Combine(home, ".claude", "settings.json");
                if (!System.IO.File.Exists(path)) return null;
                using (var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path)))
                {
                    if (doc.RootElement.TryGetProperty("env", out var env) &&
                        env.TryGetProperty("ANTHROPIC_BASE_URL", out var url) &&
                        url.ValueKind == JsonValueKind.String)
                        return url.GetString();
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Notice when the update actually lands and refresh the banner by itself.
        /// <para>
        /// The updater runs in a detached <c>cmd /k</c> window, which stays open after the command
        /// finishes, so process exit says nothing about when the update completed — and nothing
        /// else was watching, which left the "update available" banner up even after a successful
        /// update. Poll the version the banner itself reports (<c>claude --version</c>) and push a
        /// fresh status the moment it moves.
        /// </para>
        /// </summary>
        private void WatchForCliUpdate()
        {
            if (_updateWatchRunning) return;
            _updateWatchRunning = true;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    string before = GetInstalledCliVersion();
                    Log.Write("update watch: started at " + (before ?? "?"));

                    // ~5 minutes. A native self-update is usually seconds, but it can queue behind
                    // a download, and the binary cannot be replaced while a claude session holds
                    // it open — so give a slow swap room to happen rather than giving up early.
                    for (int i = 0; i < 60; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(5000).ConfigureAwait(false);

                        string now = GetInstalledCliVersion();
                        if (string.IsNullOrEmpty(now) || now == before) continue;

                        Log.Write("update watch: " + (before ?? "?") + " -> " + now);
                        SendSetupStatus(forceRefresh: true);
                        return;
                    }
                    Log.Write("update watch: gave up, still at " + (before ?? "?"));
                }
                catch (Exception ex) { Log.Write("WatchForCliUpdate: " + ex.Message); }
                finally { _updateWatchRunning = false; }
            });
        }

        // Opens an interactive `claude` session in a console at the working dir. Used for the
        // first-run login (`/login`) and for completing MCP OAuth via `/mcp` — the headless CLI
        // has no non-interactive auth path. Once authenticated, the credentials are shared, so
        // this extension's sessions pick them up. No webview-controlled input reaches the command
        // line (the only argument is the located CLI path), so there is no injection surface.
        private void LaunchClaudeTerminal()
        {
            try
            {
                var cli = ClaudeCliLocator.Locate();
                string launch = (!string.IsNullOrEmpty(cli.ResolvedPath) && File.Exists(cli.ResolvedPath))
                    ? "\"" + cli.ResolvedPath + "\""
                    : "claude";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/k " + launch,
                    WorkingDirectory = string.IsNullOrEmpty(_cwd) ? Environment.CurrentDirectory : _cwd,
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex) { Log.Write("LaunchMcpAuthTerminal: " + ex.Message); }
        }

        // ---- Sign-in, without leaving the tool window -----------------------------------------
        //
        // `claude auth login` is scriptable: with stdout on a pipe it prints the authorize URL,
        // opens the browser itself, and then waits on STDIN for the code the callback page shows.
        // All three parts have somewhere to go in the panel - the URL becomes a link, the wait
        // becomes a text box, and the code goes to the child's stdin - so the terminal is no
        // longer the only way in. The terminal route stays as the fallback for the cases that
        // really are interactive (an SSO prompt, a broken browser handoff).

        private System.Diagnostics.Process _loginProcess;
        private readonly object _loginLock = new object();
        private const int LoginTimeoutMs = 10 * 60 * 1000;

        private void StartInPanelLogin()
        {
            lock (_loginLock)
            {
                if (_loginProcess != null && !_loginProcess.HasExited) { Log.Write("login: already running"); return; }
            }

            _host.PostMessage("authFlow", new { state = "starting" });

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var transcript = new System.Text.StringBuilder();
                try
                {
                    var cli = ClaudeCliLocator.Locate();
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = cli.FileName,
                        Arguments = cli.ArgumentPrefix + "auth login",
                        WorkingDirectory = string.IsNullOrEmpty(_cwd) ? Environment.CurrentDirectory : _cwd,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    Log.Write("login: " + cli.ResolvedPath + " auth login");
                    var p = System.Diagnostics.Process.Start(psi);
                    if (p == null) throw new InvalidOperationException("process did not start");
                    lock (_loginLock) { _loginProcess = p; }

                    // stderr is drained separately so a full pipe can never wedge the child.
                    var errTask = p.StandardError.ReadToEndAsync();

                    // Read in chunks, not lines: the "Paste code here if prompted >" prompt has no
                    // trailing newline, so ReadLine would block on the very thing we are waiting
                    // to react to.
                    var buf = new char[512];
                    bool urlSent = false;
                    while (true)
                    {
                        int read = await p.StandardOutput.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                        if (read <= 0) break;
                        transcript.Append(buf, 0, read);

                        if (!urlSent)
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(transcript.ToString(), @"https://\S+");
                            if (m.Success)
                            {
                                urlSent = true;
                                Log.Write("login: authorize url received");
                                // The CLI opens the browser itself; the link is for when it can't.
                                _host.PostMessage("authFlow", new { state = "awaitingCode", url = m.Value });
                            }
                        }
                    }

                    if (!p.WaitForExit(LoginTimeoutMs)) { try { p.Kill(); } catch { } }
                    string err = (await errTask.ConfigureAwait(false) ?? string.Empty).Trim();
                    if (err.Length > 0) transcript.AppendLine().Append(err);
                }
                catch (Exception ex)
                {
                    Log.Write("StartInPanelLogin: " + ex.Message);
                    transcript.AppendLine().Append(ex.Message);
                }
                finally
                {
                    lock (_loginLock)
                    {
                        try { if (_loginProcess != null) _loginProcess.Dispose(); } catch { }
                        _loginProcess = null;
                    }
                }

                // Ask the CLI what it thinks rather than inferring from an exit code - the same
                // answer the rest of the extension now gates on.
                var status = AccountService.GetAuthStatus();
                bool ok = status != null && status.LoggedIn;
                Log.Write("login finished: loggedIn=" + ok);
                _host.PostMessage("authFlow", new
                {
                    state = ok ? "done" : "failed",
                    email = status == null ? null : status.Email,
                    plan = status == null ? null : status.Plan,
                    detail = Tail(transcript.ToString().Trim(), 400),
                });
                SendSetupStatus(forceRefresh: true);
            });
        }

        // The code from the callback page, handed to the waiting child on stdin. It never reaches
        // a command line, and newlines are stripped so one paste can only answer one prompt.
        private void SubmitAuthCode(string code)
        {
            try
            {
                var clean = (code ?? string.Empty).Replace("\r", "").Replace("\n", "").Trim();
                if (clean.Length == 0 || clean.Length > 512) { Log.Write("login: ignoring empty/oversized code"); return; }

                System.Diagnostics.Process p;
                lock (_loginLock) { p = _loginProcess; }
                if (p == null || p.HasExited) { Log.Write("login: no process waiting for a code"); return; }

                p.StandardInput.WriteLine(clean);
                p.StandardInput.Flush();
                Log.Write("login: code submitted");
                _host.PostMessage("authFlow", new { state = "verifying" });
            }
            catch (Exception ex) { Log.Write("SubmitAuthCode: " + ex.Message); }
        }

        private void CancelInPanelLogin()
        {
            try
            {
                System.Diagnostics.Process p;
                lock (_loginLock) { p = _loginProcess; _loginProcess = null; }
                if (p != null && !p.HasExited) { try { p.Kill(); } catch { } }
                Log.Write("login: cancelled");
            }
            catch (Exception ex) { Log.Write("CancelInPanelLogin: " + ex.Message); }
        }


        // The picker rows and their effort ranges. Replaces the fallback rows the init message
        // carried with the list the CLI reported (`source` is "cache" or "cli", for the log).
        private void PostModels(System.Collections.Generic.List<CliModelInfo> models, string source)
        {
            Log.Write("models: " + models.Count + " row(s) from " + source);
            _host.PostMessage("models", new
            {
                models = models,
                effortsByModel = CliModelList.EffortsByModel(models),
                source = source,
            });
        }

        private void SendInit()
        {
            // Fallback rows only: SendCommands swaps in the CLI's own list (cached, then live).
            var fallback = CliModelList.Fallback();
            _host.PostMessage("init", new
            {
                version = "1.0.17",
                theme = _theme.GetThemeVariables(),
                model = _model,
                effort = _effort,
                permissionMode = _permissionMode,
                showThinking = _showThinking,
                forkWindowSize = _forkWindowSize,
                forkAllMessages = _forkAllMessages,
                // Laid out like the VS Code panel: each row reads "<model> · <what it is for>",
                // with an explicit Opus row alongside Default. These are the hardcoded fallback
                // rows (CLI aliases, no version numbers); the list the CLI itself reports — with
                // the current names, ids and effort ranges — follows on a "models" message from
                // SendCommands, first from cache and then live, so a new model release needs no
                // extension update. See CliModelList.
                models = fallback,
                modes = new object[]
                {
                    new { id = "default", name = "Ask before edits", desc = "Claude asks for approval before each edit", icon = "✋" },
                    new { id = "acceptEdits", name = "Edit automatically", desc = "Claude edits files without asking", icon = "✎" },
                    new { id = "plan", name = "Plan mode", desc = "Explore and present a plan before editing", icon = "▤" },
                    new { id = "bypassPermissions", name = "Auto mode", desc = "Claude runs any tool automatically", icon = "⚡" },
                },
                effortsByModel = CliModelList.EffortsByModel(fallback),
            });
            // Tell the page about the initial tab before anything else is rendered.
            // Pass the saved model so the tab doesn't fall back to localStorage's last-used value
            // from a different workspace.
            _host.PostMessage("tabCreated", new { tabId = _activeTabId, title = "Chat 1", active = true, model = _model });

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    long tDir = Perf.Now;
                    var dir = await GetWorkingDirectoryAsync();
                    Perf.Step("init: GetWorkingDirectoryAsync", tDir);
                    if (!string.IsNullOrEmpty(dir)) { _cwd = dir; _host.PostMessage("init", new { cwd = _cwd }); }

                    // Populate the / palette with the full CLI command set up front, now that
                    // cwd is resolved (so project .claude/commands are included) — before the
                    // user sends a first message and the live session would otherwise be needed.
                    SendCommands(warmRefreshDelayMs: WarmCommandRefreshDelayMs, cwdResolved: true);

                    // First-run readiness (CLI installed? logged in?) drives the onboarding banner.
                    SendSetupStatus(versionDelayMs: SetupVersionDelayMs);

                    // Restore the prior options (and conversation, if any) for this working dir.
                    long tRestore = Perf.Now;
                    TryRestoreForCwd();
                    Perf.Step("init: TryRestoreForCwd (transcript load)", tRestore);

                    // The tool window is typically restored — docked next to Solution Explorer —
                    // before the solution finishes opening, so the cwd above is often still the
                    // user-home fallback and the lookup above missed. Watch for the solution
                    // landing and try again against the real project directory.
                    HookSolutionLoad();
                }
                catch { }
            }).FireAndForget();
        }

        /// <summary>
        /// Load all persisted tabs for this working directory and push them to the UI.
        /// No-op once any tab already has a record or live session.
        /// </summary>
        private void TryRestoreForCwd()
        {
            // If any tab already has data, we've already restored (or a session is live).
            if (_tabs.Values.Any(t => t.Record != null || t.Session != null)) return;

            var bundle = SessionStore.LoadBundle(_cwd);
            if (bundle == null)
            {
                Log.Write("restore: no stored bundle for cwd=" + _cwd);
                return;
            }

            // Restore closed-tab history first — even when there are no open tabs to restore,
            // the reopen button must appear if the user closed a tab while a fresh empty tab
            // remained (empty tabs have no Record so SaveAllTabs produces Tabs=[]).
            if (bundle.ClosedTabs != null && bundle.ClosedTabs.Count > 0)
            {
                _closedTabHistory.AddRange(bundle.ClosedTabs);
                if (_closedTabHistory.Count > MaxClosedTabHistory)
                    _closedTabHistory.RemoveRange(MaxClosedTabHistory, _closedTabHistory.Count - MaxClosedTabHistory);
                _host.PostMessage("closedTabHistoryChanged", BuildClosedTabsPayload());
            }

            if (bundle.Tabs == null || bundle.Tabs.Count == 0)
            {
                Log.Write("restore: no open tabs in bundle for cwd=" + _cwd);
                return;
            }

            Log.Write("restore: " + bundle.Tabs.Count + " tab(s) for cwd=" + _cwd);

            bool first = true;
            foreach (var rec in bundle.Tabs)
            {
                var tabId = rec.TabId ?? "t1";
                // Reuse the initial t1, create new TabState for extras.
                TabState tab;
                if (first && _tabs.ContainsKey("t1") && _tabs["t1"].Record == null)
                {
                    tab = _tabs["t1"];
                    // If stored tab id differs from "t1" remap it and tell JS to drop the placeholder.
                    if (tabId != "t1")
                    {
                        _tabs.Remove("t1");
                        tab = new TabState(tabId);
                        _tabs[tabId] = tab;
                        if (_activeTabId == "t1") _activeTabId = tabId;
                        _host.PostMessage("tabClosed", new { tabId = "t1" });
                    }
                    first = false;
                }
                else
                {
                    if (!_tabs.ContainsKey(tabId))
                        _tabs[tabId] = new TabState(tabId);
                    tab = _tabs[tabId];
                    first = false;
                }

                var title = rec.TabTitle ?? "Chat";
                tab.Title = rec.TabTitle;
                tab.Record = rec;
                tab.Model = InputValidation.SanitizeModel(rec.Model, "default");
                tab.PermissionMode = InputValidation.SanitizeChoice(rec.Mode, InputValidation.AllowedModes, "default");
                tab.Effort = InputValidation.SanitizeChoice(rec.Effort, InputValidation.AllowedEfforts, "none");
                tab.ShowThinking = rec.ShowThinking;
                tab.ForkWindowSize = rec.ForkWindowSize > 0 ? rec.ForkWindowSize : 6;
                tab.ForkAllMessages = rec.ForkAllMessages;
                bool hasMsgs = rec.Messages != null && rec.Messages.Count > 0;
                if (hasMsgs && !string.IsNullOrEmpty(rec.SessionId)) tab.PendingResumeId = rec.SessionId;

                bool isActive = bundle.ActiveTabId == tabId || (bundle.ActiveTabId == null && tab.TabId == _activeTabId);

                // Always announce via tabCreated so JS gets the correct tab id — the initial "t1"
                // placeholder sent before restore may have been remapped to a different id here.
                _host.PostMessage("tabCreated", new { tabId = tab.TabId, title = title, active = isActive });

                _host.PostMessage("restore", new
                {
                    tabId = tab.TabId,
                    messages = hasMsgs ? rec.Messages : new System.Collections.Generic.List<StoredMessage>(),
                    model = tab.Model,
                    mode = tab.PermissionMode,
                    effort = tab.Effort,
                    showThinking = tab.ShowThinking,
                    forkWindowSize = tab.ForkWindowSize,
                    forkAllMessages = tab.ForkAllMessages,
                });
            }

            // Activate the right tab.
            if (!string.IsNullOrEmpty(bundle.ActiveTabId) && _tabs.ContainsKey(bundle.ActiveTabId) && bundle.ActiveTabId != _activeTabId)
            {
                _activeTabId = bundle.ActiveTabId;
                _host.PostMessage("tabSwitched", new { tabId = _activeTabId });
            }

            // Ensure new tabs get indices above the restored ones.
            foreach (var key in _tabs.Keys)
            {
                if (key.StartsWith("t") && int.TryParse(key.Substring(1), out var n))
                    _nextTabIndex = Math.Max(_nextTabIndex, n);
            }

        }

        /// <summary>
        /// Subscribe (once) to solution-load events so a transcript that could not be found during
        /// startup — because no solution was open yet — is restored when one arrives.
        /// </summary>
        private void HookSolutionLoad()
        {
            if (_solutionHooked) return;
            _solutionHooked = true;
            try
            {
                VS.Events.SolutionEvents.OnAfterOpenSolution += OnSolutionLoaded;
                VS.Events.SolutionEvents.OnAfterOpenFolder += OnFolderLoaded;
            }
            catch (Exception ex) { Log.Write("HookSolutionLoad failed: " + ex.Message); }
        }

        private void UnhookSolutionLoad()
        {
            if (!_solutionHooked) return;
            _solutionHooked = false;
            try
            {
                VS.Events.SolutionEvents.OnAfterOpenSolution -= OnSolutionLoaded;
                VS.Events.SolutionEvents.OnAfterOpenFolder -= OnFolderLoaded;
            }
            catch { }
        }

        private void OnSolutionLoaded(Solution solution) => ReresolveWorkingDirectory();

        private void OnFolderLoaded(string folder) => ReresolveWorkingDirectory();

        /// <summary>
        /// Re-resolve the working directory after a solution/folder opens and, if it moved,
        /// re-point the UI at it and retry the transcript restore.
        /// </summary>
        private void ReresolveWorkingDirectory()
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // claude inherits its cwd at launch and can't be moved afterwards, and the
                    // transcript is persisted per cwd — so once a session is live, leave it be.
                    if (_session != null) return;

                    var dir = await GetWorkingDirectoryAsync();
                    if (string.IsNullOrEmpty(dir) ||
                        string.Equals(dir, _cwd, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    Log.Write("cwd re-resolved after solution load: " + _cwd + " -> " + dir);
                    _cwd = dir;
                    _host.PostMessage("init", new { cwd = _cwd });

                    // The / palette is cwd-scoped (project .claude/commands), so refresh it too.
                    // Still the startup window — the solution has only just finished loading.
                    SendCommands(warmRefreshDelayMs: WarmCommandRefreshDelayMs, cwdResolved: true);
                    TryRestoreForCwd();
                }
                catch (Exception ex) { Log.Write("ReresolveWorkingDirectory failed: " + ex.Message); }
            }).FireAndForget();
        }

        private void HandleSend(JsonElement payload)
        {
            string text = GetStr(payload, "text") ?? string.Empty;
            var images = ParseImages(payload);
            var tab = ActiveTab;  // capture active tab — send runs on background thread
            if (tab == null)
            {
                // _activeTabId points at a tab that isn't in _tabs (can happen after a restore
                // whose stored ActiveTabId no longer matches any restored tab). Rather than
                // silently drop the message — which looks like "nothing happens" to the user —
                // recover by adopting the first tab we have, or minting a fresh one.
                Log.Write("HandleSend: ActiveTab is null (activeTabId=" + (_activeTabId ?? "null")
                    + ", tabs=" + _tabs.Count + ") — recovering");
                var firstId = _tabs.Keys.FirstOrDefault();
                if (firstId != null)
                {
                    _activeTabId = firstId;
                }
                else
                {
                    _activeTabId = "t1";
                    _tabs["t1"] = new TabState("t1");
                    _host.PostMessage("tabCreated", new { tabId = "t1", title = "Chat", active = true });
                }
                _host.PostMessage("tabSwitched", new { tabId = _activeTabId });
                tab = ActiveTab;
                if (tab == null) { Log.Write("HandleSend: recovery failed, dropping message"); return; }
            }

            Log.Write("HandleSend: enter tabId=" + tab.TabId + " len=" + text.Length);
            _host.PostMessage("status", new { state = "thinking", tabId = tab.TabId });

            // Spawn/send on a background thread so nothing on the UI thread can block it.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    // Best-effort: attach the active file/selection as context (with a timeout
                    // so a slow IDE call can never hold up the message).
                    string prefix = string.Empty;
                    SelectionContext attachedSel = null;
                    try
                    {
                        var ctx = BuildContextPrefixAsync();
                        if (await System.Threading.Tasks.Task.WhenAny(ctx, System.Threading.Tasks.Task.Delay(2000)) == ctx)
                        {
                            var cp = await ctx;
                            if (cp != null) { prefix = cp.Text ?? string.Empty; attachedSel = cp.Selection; }
                        }
                    }
                    catch { }

                    // Echo the captured selection so the sent user bubble shows a chip of what
                    // was attached (mirrors the official VS Code ext's ide_selection marker).
                    if (attachedSel != null && attachedSel.HasSelection)
                        _host.PostMessage("sentSelection", new
                        {
                            tabId = tab.TabId,
                            filePath = attachedSel.FilePath,
                            startLine = attachedSel.StartLine,
                            endLine = attachedSel.EndLine,
                        });

                    await EnsureWorkingDirectoryAsync();
                    EnsureSessionForTab(tab);
                    tab.LastSentText = prefix + text;
                    tab.LastSentImages = images;
                    tab.ResumeRetried = false;
                    Log.Write("HandleSend: writing to stdin len=" + (prefix + text).Length);
                    tab.Session.SendUserMessage(prefix + text, images);
                    Log.Write("HandleSend: stdin write done, appending history");
                    AppendHistoryForTab(tab, "user", text);

                    // Set tab title from the first user message (if not yet named).
                    if (tab.Title == null && !string.IsNullOrWhiteSpace(text))
                    {
                        tab.Title = MakeTabTitle(text);
                        if (tab.Record != null) tab.Record.TabTitle = tab.Title;
                        _host.PostMessage("updateTabTitle", new { tabId = tab.TabId, title = tab.Title });
                    }

                    Log.Write("HandleSend: message sent");
                }
                catch (Exception ex)
                {
                    Log.Write("HandleSend EXCEPTION: " + ex);
                    _host.PostMessage("error", new { tabId = tab.TabId, message = ex.ToString() });
                    _host.PostMessage("status", new { state = "idle", tabId = tab.TabId });
                }
            });
        }

        private sealed class ContextPrefix { public string Text = string.Empty; public SelectionContext Selection; }

        private async System.Threading.Tasks.Task<ContextPrefix> BuildContextPrefixAsync()
        {
            try
            {
                var sel = await _ide.GetActiveSelectionAsync();
                var diags = await _ide.GetDiagnosticsAsync(30);
                var dbg = await _debug.GetDebugStateAsync();

                bool any = false;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("<ide-context>");

                // Live debugger state — only when actually debugging, so it stays silent at
                // design time. When paused, this carries the current location, exception,
                // call stack and locals so Claude can reason about the running app.
                if (dbg != null && dbg.IsActive)
                {
                    any = true;
                    sb.AppendLine("Debugger: " + dbg.Mode + (string.IsNullOrEmpty(dbg.ProcessName) ? "" : " — process " + dbg.ProcessName));
                    if (dbg.IsPaused)
                    {
                        if (!string.IsNullOrEmpty(dbg.Function))
                            sb.AppendLine("Stopped in: " + dbg.Function + (dbg.Line > 0 ? " (" + (dbg.File ?? "") + ":" + dbg.Line + ")" : ""));
                        if (!string.IsNullOrEmpty(dbg.Exception))
                            sb.AppendLine("Exception: " + dbg.Exception);
                        if (dbg.CallStack != null && dbg.CallStack.Count > 0)
                        {
                            sb.AppendLine("Call stack:");
                            foreach (var f in dbg.CallStack) sb.Append("- ").AppendLine(f);
                        }
                        if (dbg.Locals != null && dbg.Locals.Count > 0)
                        {
                            sb.AppendLine("Locals:");
                            foreach (var l in dbg.Locals)
                                sb.AppendLine("- " + l.Name + " (" + l.Type + ") = " + l.Value);
                        }
                    }
                }

                if (sel != null && !string.IsNullOrEmpty(sel.FilePath))
                {
                    any = true;
                    sb.Append("Active file: ").AppendLine(sel.FilePath);
                    if (sel.HasSelection)
                    {
                        sb.AppendLine("Selected lines " + sel.StartLine + "-" + sel.EndLine + ":");
                        sb.AppendLine("```" + sel.LanguageId);
                        var text = sel.Text.Length > 4000 ? sel.Text.Substring(0, 4000) + "\n…(truncated)" : sel.Text;
                        sb.AppendLine(text);
                        sb.AppendLine("```");
                    }
                }

                if (dbg != null && dbg.IsActive && diags != null && diags.Count > 0)
                {
                    any = true;
                    sb.AppendLine("Problems (VS Error List):");
                    for (int i = 0; i < diags.Count && i < 30; i++)
                    {
                        var d = diags[i];
                        sb.AppendLine("- [" + d.Level + "] " + d.File + ":" + d.Line + " " + d.Description);
                    }
                }

                sb.AppendLine("</ide-context>");
                sb.AppendLine();
                return new ContextPrefix { Text = any ? sb.ToString() : string.Empty, Selection = sel };
            }
            catch { return new ContextPrefix(); }
        }

        private void EnsureSession() => EnsureSessionForTab(ActiveTab);
        private void EnsureSessionForTab(TabState tab)
        {
            if (tab == null) return;
            if (tab.Session != null && tab.Session.IsRunning && !tab.OptionsDirty)
                return;

            string resume = null;
            if (tab.Session != null)
            {
                // Restart to apply new model/mode but keep the conversation via --resume.
                resume = tab.Session.SessionId;
                tab.Session.Dispose();
                tab.Session = null;
            }

            // First start after a restore: resume the persisted CLI session.
            if (resume == null && !string.IsNullOrEmpty(tab.PendingResumeId))
            {
                resume = tab.PendingResumeId;
                tab.PendingResumeId = null;
            }

            // Last resort, from the persisted record itself. ResetSession clears the record, so a
            // deliberately-new session can never pick a conversation back up here.
            if (resume == null && !string.IsNullOrEmpty(tab.Record?.SessionId))
                resume = tab.Record.SessionId;

            tab.OptionsDirty = false;

            var options = new ClaudeSessionOptions
            {
                WorkingDirectory = _cwd,
                Model = _model,
                PermissionMode = _permissionMode,
                Effort = _effort,
                ResumeSessionId = resume,
            };

            Log.Write("starting claude session, cwd=" + _cwd + " tabId=" + tab.TabId + " proxy=" + (ReadProxyUrl() ?? "none"));
            tab.Session = new ClaudeSession(options);
            HookSession(tab.Session, tab);
            tab.Session.Start();
        }

        private void HookSession(ClaudeSession s, TabState tab)
        {
            var watchdogCts = new System.Threading.CancellationTokenSource();
            s.SystemInit += i =>
            {
                watchdogCts.Cancel();
                Log.Write("system/init: model=" + (i.Model ?? "?") + " session=" + (i.SessionId ?? "none") + " tabId=" + tab.TabId);
                _tools = i.Tools ?? new List<string>();
                _mcpServers = i.McpServers ?? new List<string>();
                _host.PostMessage("system", new { tabId = tab.TabId, subtype = "init", model = i.Model, cwd = i.Cwd });
                _host.PostMessage("commands", new { commands = i.SlashCommands });
            };
            s.AssistantStart += () => _host.PostMessage("assistantStart", new { tabId = tab.TabId });
            s.TextDelta += t => _host.PostMessage("assistantDelta", new { tabId = tab.TabId, text = t });
            s.ThinkingDelta += t => _host.PostMessage("thinkingDelta", new { tabId = tab.TabId, text = t });
            s.ToolUse += t =>
            {
                _host.PostMessage("toolUse", new { tabId = tab.TabId, id = t.Id, name = t.Name, input = RawJson(t.InputJson) });
                TrackEditedFileForTab(tab, t);
            };
            s.ToolResult += r =>
            {
                _host.PostMessage("toolResult", new { tabId = tab.TabId, id = r.ToolUseId, content = r.Content, isError = r.IsError });
                if (r.IsError && r.ToolUseId != null) tab.EditedFiles.Remove(r.ToolUseId);
            };
            s.AssistantEnd += () => _host.PostMessage("assistantEnd", new { tabId = tab.TabId });
            s.ContextUsage += u => _host.PostMessage("contextUsage", new
            {
                tabId = tab.TabId,
                promptTokens = u.PromptTokens,
                totalTokens = u.TotalTokens,
                cacheCreationTokens = u.CacheCreationTokens,
                cacheReadTokens = u.CacheReadTokens,
                inputTokens = u.InputTokens,
            });
            s.Result += r =>
            {
                var newMsgId = (!tab.Compacting && !r.IsError)
                    ? AppendHistoryForTab(tab, "assistant", r.Text)
                    : null;
                tab.Compacting = false;
                _host.PostMessage("result", new
                {
                    tabId = tab.TabId,
                    costUsd = r.CostUsd,
                    inputTokens = r.InputTokens,
                    outputTokens = r.OutputTokens,
                    cacheReadTokens = r.CacheReadTokens,
                    cacheCreationTokens = r.CacheCreationTokens,
                    contextWindow = r.ContextWindow,
                    model = r.Model,
                    durationMs = r.DurationMs,
                    msgId = newMsgId,
                });
                _host.PostMessage("status", new { tabId = tab.TabId, state = "idle" });
            };
            s.Compacted += c => _host.PostMessage("compacted", new
            {
                tabId = tab.TabId,
                trigger = c.Trigger,
                preTokens = c.PreTokens,
                postTokens = c.PostTokens,
                durationMs = c.DurationMs,
            });
            s.PermissionRequest += p => _host.PostMessage("permission", new { tabId = tab.TabId, id = p.RequestId, tool = p.ToolName, input = RawJson(p.InputJson) });
            s.PermissionAutoAllowed += id => _host.PostMessage("permissionResolved", new { tabId = tab.TabId, id, behavior = "allow" });
            s.PermissionModeChangeFailed += m => tab.OptionsDirty = true;
            s.ErrorEvent += m => _host.PostMessage("error", new { tabId = tab.TabId, message = m });
            s.Exited += code =>
            {
                watchdogCts.Cancel();
                _host.PostMessage("status", new { tabId = tab.TabId, state = "idle" });
                Log.Write("claude process exited (code " + code + ")");
                if (code == 0) return;

                // The session was replaced (Retry, New Session, tab close, or watchdog cleared
                // it). Suppress the error — the UI already moved on or was cleared.
                if (tab.Session != s) { Log.Write("replaced session exited (code " + code + "), suppressed"); return; }

                if (s.ResumeRejected && !tab.ResumeRetried)
                {
                    tab.ResumeRetried = true;
                    Log.Write("resume rejected - dropping stale session id and retrying on a fresh session");
                    RetryOnFreshSession(tab);
                    return;
                }

                var why = (s.LastError ?? string.Empty).Trim();
                _host.PostMessage("error", new
                {
                    tabId = tab.TabId,
                    message = why.Length > 0
                        ? "claude exited (code " + code + "): " + Tail(why, 300)
                        : "claude exited (code " + code + ") without reporting a reason. If this repeats, check that you are signed in.",
                    login = why.Length == 0 || LooksLikeAuthFailure(why),
                });
            };
            s.Diagnostic += d => Log.Write("diag: " + d);

            // Watchdog: if system/init doesn't arrive within 60 s, the proxy is likely down.
            // Cancelled by SystemInit (success) or Exited (process died naturally).
            // Phase 1 (3 s): quick proxy reachability check — fail fast if port is not open.
            // Phase 2 (60 s): if proxy is reachable but CLI still hasn't connected, show a longer error.
            var proxyUrl = ReadProxyUrl();
            Log.Write("HookSession: watchdog armed, proxy=" + (proxyUrl ?? "none"));
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // Phase 1: 3-second fast-fail when the proxy port is not even open.
                    if (!string.IsNullOrEmpty(proxyUrl))
                    {
                        await System.Threading.Tasks.Task.Delay(3000, watchdogCts.Token);
                        if (tab.Session == s)
                        {
                            bool proxyUp = false;
                            try
                            {
                                var uri = new Uri(proxyUrl);
                                var port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
                                using var tcp = new System.Net.Sockets.TcpClient();
                                var ct = tcp.ConnectAsync(uri.Host, port);
                                proxyUp = await System.Threading.Tasks.Task.WhenAny(ct, System.Threading.Tasks.Task.Delay(1500)) == ct && !ct.IsFaulted;
                            }
                            catch { }
                            Log.Write("HookSession: Phase 1 proxy " + (proxyUp ? "up" : "DOWN") + " for " + tab.TabId);
                            if (!proxyUp)
                            {
                                // Kill the hung process immediately — it will never connect to a
                                // proxy that isn't listening, and leaving it alive means the next
                                // Retry call finds tab.Session.IsRunning=true and reuses the broken
                                // session instead of starting a fresh one. Detach first so its Exited
                                // callback stays suppressed (tab.Session != s).
                                if (tab.Session == s)
                                {
                                    var dead = s;
                                    tab.Session = null;
                                    try { dead.Dispose(); } catch { }
                                }
                                _host.PostMessage("error", new { tabId = tab.TabId, message = "The proxy at " + proxyUrl + " is not reachable. Make sure it is running, then click Retry.", login = false, retry = true });
                                _host.PostMessage("status", new { tabId = tab.TabId, state = "idle" });
                                return;
                            }
                        }
                    }

                    // Phase 2: 60-second timeout — proxy is up but CLI hasn't responded.
                    await System.Threading.Tasks.Task.Delay(57_000, watchdogCts.Token); // 3+57 = 60 s total
                    if (tab.Session == s)
                    {
                        // If this was a --resume attempt, the stored session ID is stale (the
                        // conversation cannot be resumed). Clear it now so the next start — after
                        // the user clicks Retry — launches a fresh CLI without --resume. Without
                        // this the user is stuck in a loop: every Retry re-uses the same dead ID,
                        // hangs 60 s again, and the cycle repeats indefinitely.
                        bool wasResume = !string.IsNullOrEmpty(tab.Record?.SessionId);

                        // Log BEFORE SaveAllTabs — DPAPI can block on corporate machines, so if it
                        // stalls this entry confirms Phase 2 fired and shows the stale-resume state.
                        Log.Write("HookSession: watchdog fired — no system/init after 60 s for " + tab.TabId
                            + (wasResume ? " (stale resume id cleared)" : ""));

                        tab.PendingResumeId = null;
                        if (tab.Record != null) { tab.Record.SessionId = null; SaveAllTabs(); }

                        // Re-check: RestartTabSession or another watchdog may have already replaced
                        // this session while SaveAllTabs was running. Only kill + show error if we
                        // still own the tab.
                        if (tab.Session != s) return;

                        // Kill the hung process. It is almost certainly blocked on the file lock of
                        // the --resume session id (held by an earlier CLI that never exited), so it
                        // will never connect to the proxy and never close on its own — leaving it
                        // running would keep that lock held and orphan yet another process. Detach
                        // the reference first so its Exited callback stays suppressed (!= s above).
                        var dead = s;
                        tab.Session = null;
                        try { dead.Dispose(); } catch { }

                        string msg;
                        if (wasResume)
                            msg = "Could not reconnect to the previous conversation (the session may have expired). "
                                + "Click Retry to start a fresh session — your chat history is preserved.";
                        else if (string.IsNullOrEmpty(proxyUrl))
                            msg = "Claude CLI did not respond after 60 s. Check your internet connection, then click Retry.";
                        else
                            msg = "Claude CLI did not respond after 60 s. The proxy at " + proxyUrl
                                + " is reachable but Claude did not connect. Check authentication or network routing, then click Retry.";

                        _host.PostMessage("error", new { tabId = tab.TabId, message = msg, login = false, retry = true });
                        _host.PostMessage("status", new { tabId = tab.TabId, state = "idle" });
                    }
                }
                catch (OperationCanceledException) { }
            }).FireAndForget();
        }

        private void TrackEditedFileForTab(TabState tab, ToolUseInfo t)
        {
            if (t?.Name == null) return;
            switch (t.Name)
            {
                case "Edit":
                case "Write":
                case "MultiEdit":
                case "NotebookEdit":
                    try
                    {
                        var input = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrEmpty(t.InputJson) ? "{}" : t.InputJson);
                        if (input.TryGetProperty("file_path", out var fp) && fp.ValueKind == JsonValueKind.String)
                        {
                            var path = fp.GetString();
                            string old = null;
                            try { if (File.Exists(path)) old = File.ReadAllText(path); } catch { }
                            if (tab.EditedFiles.Count > 200) tab.EditedFiles.Clear();
                            tab.EditedFiles[t.Id] = new EditSnapshot { Path = path, OldText = old };
                        }
                    }
                    catch { }
                    break;
            }
        }

        private void PickImage()
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Attach image",
                    Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*",
                    Multiselect = true,
                };
                if (dlg.ShowDialog() != true) return;
                foreach (var f in dlg.FileNames)
                {
                    try
                    {
                        var data = Convert.ToBase64String(File.ReadAllBytes(f));
                        _host.PostMessage("attachImage", new
                        {
                            mediaType = MediaTypeForExt(Path.GetExtension(f)),
                            data,
                            name = Path.GetFileName(f),
                        });
                    }
                    catch { }
                }
            }).FireAndForget();
        }

        private void PickFile()
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Add file to context",
                    Filter = "All files|*.*",
                    Multiselect = true,
                };
                if (dlg.ShowDialog() != true) return;
                var refs = string.Join(" ", System.Array.ConvertAll(dlg.FileNames, p => "@" + p));
                _host.PostMessage("insertText", new { text = refs + " " });
            }).FireAndForget();
        }

        // Open a VS diff window comparing the file before Claude's edit (temp) to the new content.
        private async System.Threading.Tasks.Task ShowEditAsync(EditSnapshot snap)
        {
            if (snap == null || string.IsNullOrEmpty(snap.Path)) return;
            try
            {
                if (snap.OldText == null) { await _ide.OpenFileAsync(snap.Path); return; }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dir = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS", "diff");
                Directory.CreateDirectory(dir);
                PruneOldDiffTemps(dir);
                var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + Path.GetExtension(snap.Path));
                File.WriteAllText(tmp, snap.OldText);

                var diff = await VS.GetServiceAsync<SVsDifferenceService, IVsDifferenceService>();
                var name = Path.GetFileName(snap.Path);
                diff?.OpenComparisonWindow2(tmp, snap.Path, "Claude edit: " + name, name,
                    "Before", "After (Claude)", null, null,
                    (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary);
            }
            catch (Exception ex)
            {
                Log.Write("ShowEdit failed: " + ex.Message);
                try { await _ide.OpenFileAsync(snap.Path); } catch { }
            }
        }

        // The diff window holds its "before" temp open while shown, so the file we just wrote can't
        // be deleted now. Instead best-effort sweep older snapshots left from prior diff windows /
        // sessions so pre-edit file contents don't accumulate as plaintext in %TEMP%.
        private static void PruneOldDiffTemps(string dir)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-6);
                foreach (var f in Directory.GetFiles(dir))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                    catch { }
                }
            }
            catch { }
        }

        // Enumerate workspace files for the @-mention picker.
        private void SendFiles()
        {
            var cwd = _cwd;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var files = IdeContextService.EnumerateWorkspaceFiles(cwd, 800);
                    _host.PostMessage("files", new { files });
                }
                catch { }
            });
        }

        // Compaction is delegated to the CLI's own /compact, which the headless stream-json input
        // accepts like any other slash command. The CLI rewrites its context in place, keeps the
        // same session, and reports the result as a system/compact_boundary event — no assistant
        // text is produced, so nothing lands in the transcript.
        //
        // This previously asked the model to write a summary brief and then restarted the session
        // seeded with it. That cost an extra full turn every time, and the brief streamed into the
        // chat as an ordinary reply — which is why pressing the button printed a wall of markdown.
        //
        // Reached from both the ring button and /compact in the slash palette (the palette entry is
        // a built-in that posts the same "compact" message, and shadows the CLI's own /compact).
        private void HandleCompact()
        {
            var tab = ActiveTab;
            if (tab == null) return;
            bool live = tab.Session != null && tab.Session.IsRunning;
            if (!live && string.IsNullOrEmpty(ResumableSessionId(tab)))
            {
                _host.PostMessage("error", new { tabId = tab.TabId, message = "Nothing to compact yet — send a message first." });
                return;
            }

            tab.Compacting = true;
            _host.PostMessage("status", new { tabId = tab.TabId, state = "thinking" });
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!live) Log.Write("compact: no live session, resuming " + ResumableSessionId(tab));
                    await EnsureWorkingDirectoryAsync();
                    EnsureSessionForTab(tab);
                    tab.Session.SendUserMessage("/compact", null);
                }
                catch (Exception ex)
                {
                    tab.Compacting = false;
                    Log.Write("HandleCompact: " + ex.Message);
                    _host.PostMessage("error", new { tabId = tab.TabId, message = ex.Message });
                    _host.PostMessage("status", new { tabId = tab.TabId, state = "idle" });
                }
            }).FireAndForget();
        }

        /// <summary>
        /// The CLI session id a restart would resume, or null when there is nothing to continue.
        /// Mirrors the order <see cref="EnsureSession"/> resolves it in, so a caller can tell
        /// whether starting the CLI would actually bring a conversation back with it.
        /// </summary>
        private string ResumableSessionId(TabState tab = null)
        {
            var t = tab ?? ActiveTab;
            if (!string.IsNullOrEmpty(t?.Session?.SessionId)) return t.Session.SessionId;
            if (!string.IsNullOrEmpty(t?.PendingResumeId)) return t.PendingResumeId;
            return t?.Record?.SessionId;
        }

        private static string MediaTypeForExt(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                default: return "image/png";
            }
        }

        private void HandlePermissionResponse(JsonElement payload)
        {
            string id = GetStr(payload, "id");
            string behavior = GetStr(payload, "behavior") ?? "deny";
            if (behavior == "allow_always") behavior = "allow";
            ActiveTab?.Session?.RespondToPermission(id, behavior == "deny" ? "deny" : "allow", null);
        }

        /// <summary>
        /// Start over without the stale <c>--resume</c> id and re-send the turn that died with it.
        /// The transcript is kept: only the CLI-side conversation is gone, and re-sending is what
        /// the user would otherwise do by hand after being told to clear something invisible.
        /// </summary>
        private void RetryOnFreshSession(TabState tab)
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    tab.Session?.Dispose();
                    tab.Session = null;
                    tab.PendingResumeId = null;
                    if (tab.Record != null)
                    {
                        tab.Record.SessionId = null;
                        SaveAllTabs();
                    }

                    if (string.IsNullOrEmpty(tab.LastSentText)) return;

                    _host.PostMessage("status", new { tabId = tab.TabId, state = "thinking" });
                    EnsureSessionForTab(tab);
                    tab.Session.SendUserMessage(tab.LastSentText, tab.LastSentImages);
                    Log.Write("resume retry: message re-sent on a fresh session");
                }
                catch (Exception ex)
                {
                    Log.Write("RetryOnFreshSession: " + ex.Message);
                    _host.PostMessage("status", new { tabId = tab.TabId, state = "idle" });
                    _host.PostMessage("error", new { tabId = tab.TabId, message = "Could not restart the conversation: " + ex.Message });
                }
            });
        }

        // Words the CLI uses when the failure really is about the account, as opposed to the many
        // other things that exit non-zero.
        private static bool LooksLikeAuthFailure(string s)
        {
            var t = s.ToLowerInvariant();
            return t.Contains("log in") || t.Contains("login") || t.Contains("sign in")
                || t.Contains("unauthor") || t.Contains("authentication") || t.Contains("credential")
                || t.Contains("api key") || t.Contains("oauth") || t.Contains("401") || t.Contains("403");
        }

        private void ResetSession() => ResetSessionForTab(ActiveTab);

        private void ResetSessionForTab(TabState tab)
        {
            if (tab == null) return;
            tab.Session?.Dispose();
            tab.Session = null;
            tab.PendingResumeId = null;
            tab.Record = null;
            tab.Title = null;
            SessionStore.ClearTab(_cwd, tab.TabId);
            _host.PostMessage("clear", new { tabId = tab.TabId });
            _host.PostMessage("updateTabTitle", new { tabId = tab.TabId, title = DefaultTabTitle(tab.TabId) });
        }

        private string DefaultTabTitle(string tabId)
        {
            int idx = 1;
            foreach (var k in _tabs.Keys) { if (k == tabId) break; idx++; }
            return "Chat " + idx;
        }

        private void AppendHistory(string role, string text) => AppendHistoryForTab(ActiveTab, role, text);
        private string AppendHistoryForTab(TabState tab, string role, string text)
        {
            if (tab == null) return null;
            try
            {
                if (tab.Record == null) tab.Record = new SessionRecord { TabId = tab.TabId };
                if (role == "user" && tab.Title == null)
                {
                    tab.Title = DeriveTabTitle(text);
                    if (tab.Title != null)
                        _host.PostMessage("updateTabTitle", new { tabId = tab.TabId, title = tab.Title });
                }
                tab.Record.TabId = tab.TabId;
                tab.Record.TabTitle = tab.Title;
                var msgId = role == "assistant" ? Guid.NewGuid().ToString("N").Substring(0, 8) : null;
                tab.Record.Messages.Add(new StoredMessage { Role = role, Text = text ?? string.Empty, Id = msgId });
                tab.Record.SessionId = tab.Session?.SessionId ?? tab.Record.SessionId;
                tab.Record.Model = tab.Model;
                tab.Record.Mode = tab.PermissionMode;
                tab.Record.Effort = tab.Effort;
                tab.Record.ShowThinking = tab.ShowThinking;
                tab.Record.ForkWindowSize = tab.ForkWindowSize;
                tab.Record.ForkAllMessages = tab.ForkAllMessages;
                SaveAllTabs();
                return msgId;
            }
            catch { return null; }
        }

        // Persist the current composer options (model / permission mode / effort / show-thinking)
        // immediately when the user changes one, even before any message is sent — otherwise an
        // option change followed by closing VS would be lost.

        private static string DeriveTabTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var line = text.TrimStart().Split('\n')[0].Trim();
            if (line.Length == 0) return null;
            const int max = 28;
            return line.Length <= max ? line : line.Substring(0, max) + "…";
        }

        private void SaveOptions()
        {
            try
            {
                var tab = ActiveTab;
                if (tab == null) return;
                if (tab.Record == null) tab.Record = new SessionRecord { TabId = tab.TabId };
                tab.Record.TabId = tab.TabId;
                tab.Record.TabTitle = tab.Title;
                tab.Record.Model = tab.Model;
                tab.Record.Mode = tab.PermissionMode;
                tab.Record.Effort = tab.Effort;
                tab.Record.ShowThinking = tab.ShowThinking;
                tab.Record.ForkWindowSize = tab.ForkWindowSize;
                tab.Record.ForkAllMessages = tab.ForkAllMessages;
                SaveAllTabs();
            }
            catch { }
        }

        private void SaveAllTabs()
        {
            try
            {
                var bundle = new SessionBundle { ActiveTabId = _activeTabId };
                foreach (var tab in _tabs.Values)
                {
                    if (tab.Record == null) continue;
                    tab.Record.TabId = tab.TabId;
                    if (tab.Title != null) tab.Record.TabTitle = tab.Title;
                    bundle.Tabs.Add(tab.Record);
                }
                bundle.ClosedTabs = new System.Collections.Generic.List<SessionRecord>(_closedTabHistory);
                SessionStore.SaveBundle(_cwd, bundle);
            }
            catch { }
        }

        private static string MakeTabTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            // Strip newlines, collapse whitespace, trim to 32 chars
            var t = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
            return t.Length <= 32 ? t : t.Substring(0, 30) + "…";
        }

        private static List<ImageInput> ParseImages(JsonElement payload)
        {
            var list = new List<ImageInput>();
            if (payload.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in imgs.EnumerateArray())
                {
                    list.Add(new ImageInput
                    {
                        MediaType = img.TryGetProperty("mediaType", out var m) ? m.GetString() : "image/png",
                        Data = img.TryGetProperty("data", out var d) ? d.GetString() : null,
                    });
                }
            }
            return list.Count > 0 ? list : null;
        }

        private async System.Threading.Tasks.Task EnsureWorkingDirectoryAsync()
        {
            // The claude process inherits its cwd at launch and can't change it afterwards,
            // so resolve only before the first start. The tool window is usually restored
            // (docked next to Solution Explorer) before the solution finishes opening, which
            // leaves the OnLoaded value stale (the user-home fallback) — re-resolve here once
            // the solution is actually loaded so claude runs in the project folder.
            if (_session != null) return;
            try
            {
                var dir = await GetWorkingDirectoryAsync();
                if (!string.IsNullOrEmpty(dir)) _cwd = dir;
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<string> GetWorkingDirectoryAsync()
        {
            try
            {
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                if (solution?.FullPath != null)
                {
                    var dir = Path.GetDirectoryName(solution.FullPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
            catch { }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private static object RawJson(string json)
        {
            try { return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrEmpty(json) ? "{}" : json); }
            catch { return new { }; }
        }

        private static string GetStr(JsonElement el, string name)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static bool GetBool(JsonElement el, string name, bool fallback)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
            }
            return fallback;
        }

        private static int GetInt(JsonElement el, string name, int fallback)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

        private void TryOpenExternal(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            // Security: ShellExecute will launch ANY string (apps, files, scripts). Restrict to
            // real web URLs so a crafted "openExternal" message can't run a local program.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Log.Write("openExternal blocked (non-http url): " + url);
                return;
            }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { }
        }

        // ── Tab management ───────────────────────────────────────────────────────────
        private static string NewTabId() => "t" + Guid.NewGuid().ToString("N").Substring(0, 10);

        private void HandleNewTab(string defaultModel = null, string defaultMode = null, string defaultEffort = null)
        {
            var id = NewTabId();
            var label = "Chat " + (++_nextTabIndex);
            Log.Write("HandleNewTab: creating " + id + " (prev active=" + _activeTabId + ")");
            var tab = new TabState(id);
            if (!string.IsNullOrEmpty(defaultModel))
                tab.Model = InputValidation.SanitizeModel(defaultModel, "default");
            if (!string.IsNullOrEmpty(defaultMode))
                tab.PermissionMode = InputValidation.SanitizeChoice(defaultMode, InputValidation.AllowedModes, "default");
            if (!string.IsNullOrEmpty(defaultEffort))
                tab.Effort = InputValidation.SanitizeChoice(defaultEffort, InputValidation.AllowedEfforts, "none");
            _tabs[id] = tab;
            _host.PostMessage("tabCreated", new { tabId = id, title = label, active = false, model = tab.Model, mode = tab.PermissionMode });
            _activeTabId = id;
            _host.PostMessage("tabSwitched", new { tabId = id });
            Log.Write("HandleNewTab: done, activeTabId=" + _activeTabId);
        }

        private void HandleForkTab(string msgId)
        {
            _tabs.TryGetValue(_activeTabId, out var sourceTab);
            var sourceMsgs = sourceTab?.Record?.Messages ?? new System.Collections.Generic.List<StoredMessage>();

            // Find the clicked assistant message by its ID, fall back to last message.
            var idx = string.IsNullOrEmpty(msgId)
                ? sourceMsgs.Count - 1
                : sourceMsgs.FindLastIndex(m => m.Id == msgId);
            if (idx < 0) idx = sourceMsgs.Count - 1;

            // Take up to N messages ending at idx (inclusive); N comes from the tab's fork setting.
            var forkWindow = _forkAllMessages ? int.MaxValue : Math.Max(1, _forkWindowSize);
            var start = Math.Max(0, idx - forkWindow + 1);
            var count = idx - start + 1;
            var lastMsgs = count > 0 ? sourceMsgs.GetRange(start, count) : new System.Collections.Generic.List<StoredMessage>();

            var id = NewTabId();
            var label = "Fork " + (++_nextTabIndex);
            Log.Write("HandleForkTab: creating " + id + " from " + _activeTabId + " msgs=" + lastMsgs.Count + " (idx=" + idx + " start=" + start + ")");
            var tab = new TabState(id);
            if (sourceTab != null) { tab.Model = sourceTab.Model; tab.PermissionMode = sourceTab.PermissionMode; tab.Effort = sourceTab.Effort; tab.ShowThinking = sourceTab.ShowThinking; tab.ForkWindowSize = sourceTab.ForkWindowSize; tab.ForkAllMessages = sourceTab.ForkAllMessages; }
            tab.Record = new SessionRecord
            {
                TabId = id,
                TabTitle = label,
                Model = tab.Model,
                Mode = tab.PermissionMode,
                Effort = tab.Effort,
                ShowThinking = tab.ShowThinking,
                ForkWindowSize = tab.ForkWindowSize,
                ForkAllMessages = tab.ForkAllMessages,
                Messages = new System.Collections.Generic.List<StoredMessage>(lastMsgs)
            };
            _tabs[id] = tab;
            _host.PostMessage("tabCreated", new { tabId = id, title = label, active = false, model = tab.Model, mode = tab.PermissionMode });
            _activeTabId = id;
            _host.PostMessage("tabSwitched", new { tabId = id });
            if (lastMsgs.Count > 0)
                _host.PostMessage("restore", new { tabId = id, fork = true, model = tab.Model, mode = tab.PermissionMode, effort = tab.Effort, showThinking = tab.ShowThinking, forkWindowSize = tab.ForkWindowSize, forkAllMessages = tab.ForkAllMessages, messages = System.Linq.Enumerable.Select(lastMsgs, m => new { role = m.Role, text = m.Text, id = m.Id }).ToArray() });
            SaveAllTabs();
        }

        private void HandleSwitchTab(string tabId)
        {
            Log.Write("HandleSwitchTab: " + tabId + " (prev=" + _activeTabId + ")");
            if (tabId == null || !_tabs.ContainsKey(tabId) || tabId == _activeTabId) return;
            _activeTabId = tabId;
            _host.PostMessage("tabSwitched", new { tabId = tabId });
            SaveAllTabs();
        }

        private void HandleRenameTab(string tabId, string newTitle)
        {
            if (tabId == null || !_tabs.TryGetValue(tabId, out var tab)) return;
            newTitle = (newTitle ?? "").Trim();
            if (newTitle.Length == 0) return;
            if (newTitle.Length > 60) newTitle = newTitle.Substring(0, 60);
            tab.Title = newTitle;
            if (tab.Record == null) tab.Record = new SessionRecord { TabId = tab.TabId };
            tab.Record.TabTitle = newTitle;
            _host.PostMessage("updateTabTitle", new { tabId = tab.TabId, title = newTitle });
            SaveAllTabs();
        }

        private void HandleCloseTab(string tabId)
        {
            if (tabId == null || !_tabs.TryGetValue(tabId, out var tab)) return;
            if (_tabs.Count <= 1) return; // always keep at least one tab
            // Save snapshot to history before disposing so the user can reopen it.
            var recToSave = tab.Record;
            if (recToSave != null && recToSave.Messages?.Count > 0)
            {
                recToSave.ClosedAt = DateTime.UtcNow.ToString("O");
                _closedTabHistory.Insert(0, recToSave);
                if (_closedTabHistory.Count > MaxClosedTabHistory)
                    _closedTabHistory.RemoveRange(MaxClosedTabHistory, _closedTabHistory.Count - MaxClosedTabHistory);
            }
            tab.Session?.Dispose();
            _tabs.Remove(tabId);
            SessionStore.ClearTab(_cwd, tabId);
            if (_activeTabId == tabId)
            {
                _activeTabId = System.Linq.Enumerable.First(_tabs.Keys);
                _host.PostMessage("tabSwitched", new { tabId = _activeTabId });
            }
            _host.PostMessage("tabClosed", new { tabId = tabId });
            _host.PostMessage("closedTabHistoryChanged", BuildClosedTabsPayload());
            SaveAllTabs();
        }

        private void HandleReopenLastTab(string tabId = null)
        {
            int idx = tabId != null
                ? _closedTabHistory.FindIndex(r => r.TabId == tabId)
                : 0;
            if (idx < 0 || _closedTabHistory.Count == 0) return;
            var rec = _closedTabHistory[idx];
            _closedTabHistory.RemoveAt(idx);
            _host.PostMessage("closedTabHistoryChanged", BuildClosedTabsPayload());

            var id = NewTabId();
            var label = rec.TabTitle ?? ("Chat " + (++_nextTabIndex));
            var tab = new TabState(id)
            {
                Model = rec.Model ?? "default",
                PermissionMode = rec.Mode ?? "default",
                Effort = rec.Effort ?? "none",
                ShowThinking = rec.ShowThinking,
                ForkWindowSize = rec.ForkWindowSize,
                ForkAllMessages = rec.ForkAllMessages,
            };
            tab.Record = new SessionRecord
            {
                TabId = id,
                TabTitle = label,
                Model = tab.Model,
                Mode = tab.PermissionMode,
                Effort = tab.Effort,
                ShowThinking = tab.ShowThinking,
                ForkWindowSize = tab.ForkWindowSize,
                ForkAllMessages = tab.ForkAllMessages,
                Messages = new System.Collections.Generic.List<StoredMessage>(rec.Messages ?? new System.Collections.Generic.List<StoredMessage>()),
            };
            _tabs[id] = tab;
            _host.PostMessage("tabCreated", new { tabId = id, title = label, active = false, model = tab.Model, mode = tab.PermissionMode });
            _activeTabId = id;
            _host.PostMessage("tabSwitched", new { tabId = id });
            var msgs = tab.Record.Messages;
            if (msgs.Count > 0)
                _host.PostMessage("restore", new { tabId = id, fork = false, model = tab.Model, mode = tab.PermissionMode, effort = tab.Effort, showThinking = tab.ShowThinking, forkWindowSize = tab.ForkWindowSize, forkAllMessages = tab.ForkAllMessages, messages = System.Linq.Enumerable.Select(msgs, m => new { role = m.Role, text = m.Text, id = m.Id }).ToArray() });
            SaveAllTabs();
        }
        private object BuildClosedTabsPayload()
        {
            var items = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(_closedTabHistory, r =>
            {
                var firstUser = r.Messages?.Find(m => m.Role == "user")?.Text ?? "";
                if (firstUser.Length > 100) firstUser = firstUser.Substring(0, 100);
                var lastAssist = "";
                if (r.Messages != null)
                    for (int i = r.Messages.Count - 1; i >= 0; i--)
                        if (r.Messages[i].Role == "assistant") { lastAssist = r.Messages[i].Text ?? ""; break; }
                if (lastAssist.Length > 100) lastAssist = lastAssist.Substring(0, 100);
                return new { tabId = r.TabId ?? "", title = r.TabTitle ?? "Chat", closedAt = r.ClosedAt ?? "", firstUser, lastAssist };
            }));
            return new { count = _closedTabHistory.Count, tabs = items };
        }
        // ── end tab management ────────────────────────────────────────────────────────
    }
}
