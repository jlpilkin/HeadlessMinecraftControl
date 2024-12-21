using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HeadlessMinecraftControl
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumThreadWindowsCallback callback, IntPtr extraData);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_CLOSE = 0x0010;
        private const int SW_SHOWNORMAL = 1;
        private const int SW_MAXIMIZE = 3;
        private const int SW_MINIMIZE = 6;
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam,
            StringBuilder lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, int wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, IntPtr lpdwResult);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private delegate bool EnumThreadWindowsCallback(IntPtr hWnd, IntPtr lParam);

        private Process _process;
        private List<String> _processLines = new List<string>();
        private bool _isRunning = false;
        private MinecraftState _state = MinecraftState.Stopped;
        private Process _headlessMcProcess;
        private Process _minecraftProcess;
        private string _minecraftInstanceId;
        private string _minecraftUser;
        private IntPtr _minecraftWindow = IntPtr.Zero;
        private int _connectAttempts = 0;
        private List<IntPtr> _minecraftWindows = new List<IntPtr>();
        private DateTime _lastUIUpdate = DateTime.Now;
        private Task _taskDownloadResourcePack = null;
        private CancellationTokenSource _taskDownloadResourcePackToken = null;
        private Task _taskInGameTimeout = null;
        private CancellationTokenSource _taskInGameTimeoutToken = null;
        private Task _taskStartToInGameTimeoutSeconds = null;
        private CancellationTokenSource _taskStartToInGameTimeoutToken = null;
        private string _processTextUIUpdate = string.Empty;
        private int _uiUpdateFrequencyMs = 10;
        private object _lock = new object();
        private object _uiUpdateLock = new object();
        private object _downloadResourcePackLock = new object();
        private object _inGameTimeoutLock = new object();
        private object _startToInGameTimeoutLock = new object();
        private object _isRunningLock = new object();

        public MainWindow()
        {
            this.InitializeComponent();
            if (!int.TryParse(ConfigurationManager.AppSettings["uiUpdateFrequencyMs"], out _uiUpdateFrequencyMs))
            {
                _uiUpdateFrequencyMs = 10;
            }
            launchRelaunch();
        }

        private void ScrollToBottom(TextBox textBox)
        {
            textBox.SelectionStart = textBox.Text.Length;
            textBox.SelectionLength = 0;
            var grid = (Grid)VisualTreeHelper.GetChild(textBox, 0);
            for (var i = 0; i <= VisualTreeHelper.GetChildrenCount(grid) - 1; i++)
            {
                object obj = VisualTreeHelper.GetChild(grid, i);
                if (!(obj is ScrollViewer)) continue;
                ((ScrollViewer)obj).ChangeView(0.0f, ((ScrollViewer)obj).ExtentHeight, 1.0f, true);
                break;
            }
        }

        private void sendCommand_Click(object sender, RoutedEventArgs e)
        {
            bool isRunning = false;
            lock (_isRunningLock)
            {
                isRunning = _isRunning;
            }
            if (isRunning && !string.IsNullOrWhiteSpace(commandText.Text))
            {
                if (!_process.HasExited)
                {
                    string trimmedCommand = commandText.Text.Trim();
                    bool isExiting = trimmedCommand == "exit" || trimmedCommand == "quit" || trimmedCommand == "stop";

                    if (isExiting)
                    {
                        closeProcess();
                    }
                    else
                    {
                        sendProcessCommand(trimmedCommand);
                    }
                    commandText.Text = "";

                    if (isExiting)
                    {
                        Application.Current.Exit();
                    }
                }
                else
                {
                    lock (_isRunningLock)
                    {
                        _isRunning = false;
                    }
                    launchRelaunch();
                }
            }
            else if (!isRunning && !string.IsNullOrWhiteSpace(commandText.Text))
            {
                launchRelaunch();
            }
        }

        private void startRestart_Click(object sender, RoutedEventArgs e)
        {
            launchRelaunch();
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            closeProcess();
        }

        private void commandText_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                sendCommand_Click(sender, e);
            }
        }

        private void launchRelaunch()
        {
            lock (_downloadResourcePackLock)
            {
                if (_taskDownloadResourcePackToken != null)
                {
                    _taskDownloadResourcePackToken.Cancel();
                }
            }

            lock (_inGameTimeoutLock)
            {
                if (_taskInGameTimeoutToken != null)
                {
                    _taskInGameTimeoutToken.Cancel();
                }
            }

            lock (_startToInGameTimeoutLock)
            {
                if (_taskStartToInGameTimeoutToken != null)
                {
                    _taskStartToInGameTimeoutToken.Cancel();
                    Thread.Sleep(5000);
                }
            }

            lock (_startToInGameTimeoutLock)
            {
                if (_taskStartToInGameTimeoutSeconds == null)
                {
                    _taskStartToInGameTimeoutToken = new CancellationTokenSource();
                    _taskStartToInGameTimeoutSeconds = StartToInGameTimeoutAsync(_taskStartToInGameTimeoutToken.Token);
                }
            }

            closeProcess();

            _process = new Process();
            _process.StartInfo.FileName = ConfigurationManager.AppSettings["cmdPath"];
            _process.StartInfo.ArgumentList.Add("/C");
            _process.StartInfo.ArgumentList.Add(ConfigurationManager.AppSettings["javaPath"]);
            _process.StartInfo.ArgumentList.Add("-jar");
            _process.StartInfo.ArgumentList.Add(Environment.ExpandEnvironmentVariables(ConfigurationManager.AppSettings["headlessMcJarPath"]));
            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.RedirectStandardInput = true;
            _process.StartInfo.CreateNoWindow = true;
            _process.StartInfo.WorkingDirectory = Environment.ExpandEnvironmentVariables(ConfigurationManager.AppSettings["workingDirectory"]);
            _process.OutputDataReceived += ProcessOutputReceived;
            _process.Start();
            _process.BeginOutputReadLine();
            lock (_isRunningLock)
            {
                _isRunning = true;
            }
            this.DispatcherQueue.TryEnqueue(() =>
            {
                startRestart.Content = "Restart";
            });
            _state = MinecraftState.Starting;
        }

        private void ProcessOutputReceived(object s, DataReceivedEventArgs e)
        {
            bool isRunning = false;
            lock (_isRunningLock)
            {
                isRunning = _isRunning;
            }
            if (isRunning)
            {
                lock (_lock)
                {
                    _processLines.Add(e.Data);
                    if (_processLines.Count > 1000)
                    {
                        _processLines.RemoveRange(0, _processLines.Count - 1000);
                    }
                }
                lock (_uiUpdateLock)
                {
                    _processTextUIUpdate += e.Data + Environment.NewLine;
                }
                if ((DateTime.Now - _lastUIUpdate).TotalMilliseconds > _uiUpdateFrequencyMs)
                {
                    string processTextUIUpdate = string.Empty;
                    lock (_uiUpdateLock)
                    {
                        processTextUIUpdate = _processTextUIUpdate;
                        _processTextUIUpdate = string.Empty;
                    }
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        processConsole.Text += processTextUIUpdate;
                        ScrollToBottom(processConsole);
                    });
                    _lastUIUpdate = DateTime.Now;
                }
                processConsoleText();
            }
        }

        private void closeProcess()
        {
            bool isRunning = false;
            lock (_isRunningLock)
            {
                isRunning = _isRunning;
            }
            if (isRunning)
            {
                if (_minecraftWindow != IntPtr.Zero)
                {
                    SendMessage(_minecraftWindow, WM_CLOSE, 0, null);
                    _minecraftProcess.WaitForExit(10000);
                    _minecraftProcess.Kill();
                }

                sendProcessCommand("exit");

                if (_minecraftProcess != null)
                {
                    _minecraftProcess.WaitForExit(10000);
                    _minecraftProcess.Kill();
                }

                if (_headlessMcProcess != null)
                {
                    _headlessMcProcess.WaitForExit(10000);
                    _headlessMcProcess.Kill();
                }

                if (_process != null)
                {
                    _process.WaitForExit(10000);
                    _process.Kill();
                }

                lock (_isRunningLock)
                {
                    _isRunning = false;
                }
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    startRestart.Content = "Start";
                });
                _processLines = new List<string>();
                _state = MinecraftState.Stopped;
                _headlessMcProcess = null;
                _minecraftProcess = null;
                _process = null;
                _minecraftInstanceId = null;
                _minecraftUser = null;
                _minecraftWindow = new IntPtr(0);
                _connectAttempts = 0;
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    processConsole.Text = "";
                });
            }
        }

        private void sendProcessCommand(string command)
        {
            bool isRunning = false;
            lock (_isRunningLock)
            {
                isRunning = _isRunning;
            }
            if (isRunning && !string.IsNullOrWhiteSpace(command))
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.WriteLine(command);
                }
            }
        }

        private void EnumerateWindowsForProcess(int processId)
        {
            EnumThreadWindowsCallback callback = (hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out int pid);
                if (pid == processId)
                {
                    _minecraftWindows.Add(hWnd);
                }
                return true;
            };

            EnumWindows(callback, IntPtr.Zero);

            if (_minecraftWindows.Count == 0)
            {
                _minecraftWindow = IntPtr.Zero;
                return;
            }

            foreach (IntPtr hWnd in _minecraftWindows)
            {
                string windowTitle = GetWindowTitle(hWnd);
                string windowClass = GetWindowClass(hWnd);
				if (
					windowTitle.Contains(ConfigurationManager.AppSettings["minecraftWindowTitle"], StringComparison.InvariantCultureIgnoreCase)
					&& windowClass.Contains(ConfigurationManager.AppSettings["minecraftWindowClass"], StringComparison.InvariantCultureIgnoreCase)
				)
				{
					_minecraftWindow = hWnd;
					break;
				}
            }

            if (_minecraftWindow == IntPtr.Zero && _minecraftWindows.Count == 1 )
            {
                _minecraftWindow = _minecraftWindows.First();
            }
        }

        static string GetWindowTitle(IntPtr hWnd)
        {
            const int maxTitleLength = 256;
            var title = new StringBuilder(maxTitleLength);
            SendMessage(hWnd, WM_GETTEXT, title.Capacity, title);
            //SendMessageTimeout(hWnd, WM_GETTEXT, title.Capacity, title, 0x0000, 1000, IntPtr.Zero);
            return title.ToString();
        }

        static string GetWindowClass(IntPtr hWnd)
        {
            var className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);
            return className.ToString();
        }

        private void processConsoleText()
        {
            List<String> processLines = new List<string>();
            lock (_lock)
            {
                processLines = new List<string>(_processLines);
                _processLines.Clear();
            }

            while (processLines.Any())
            {
                // Get the next line
                string processLine = processLines.First();

                if (string.IsNullOrWhiteSpace(processLine))
                {
                    processLines.RemoveAt(0);
                    continue;
                }

                switch (_state)
                {
                    case MinecraftState.Starting:
                        // Check if the line matches a regular expression containing a number followed by the version of the game to launch
                        string launchVersion = ConfigurationManager.AppSettings["minecraftVersion"];
                        string launchModVersion = ConfigurationManager.AppSettings["minecraftModVersion"];
                        string launchVersionRegex = "^\\d+\\s*" + launchModVersion.Replace(".", "\\.").Replace("-", "\\-") + "\\s+" + launchVersion.Replace(".", "\\.").Replace("-", "\\-") + "\\s*$";
                        if (System.Text.RegularExpressions.Regex.IsMatch(processLine, launchVersionRegex))
                        {
                            _headlessMcProcess = _process.GetChildProcesses().Where(process => process.ProcessName.Contains("java")).FirstOrDefault(_process);
                            if (_headlessMcProcess.Id == _process.Id)
                            {
                                _headlessMcProcess = null;
                                closeProcess();
                                return;
                            }
                            _state = MinecraftState.Running;

                            // Start the Minecraft process
                            sendProcessCommand($"launch {launchModVersion}");
                        }
                        break;

                    case MinecraftState.Running:
                        // Check if the line matches a regular expression containing the game version and the game state
                        // Launching version fabric-loader-0.15.3-1.20.4, 3f1d07aa-ad52-4dbe-9d84-63ca5effa606
                        string runningModVersion = ConfigurationManager.AppSettings["minecraftModVersion"];

                        if (_minecraftInstanceId == null)
                        {
                            string runningVersionRegex = "^Launching version " + runningModVersion.Replace(".", "\\.").Replace("-", "\\-") + ", " + "([0-9a-fA-F]{8}\\-[0-9a-fA-F]{4}\\-[0-9a-fA-F]{4}\\-[0-9a-fA-F]{4}\\-[0-9a-fA-F]{12})\\s*$";
                            if (System.Text.RegularExpressions.Regex.IsMatch(processLine, runningVersionRegex))
                            {
                                _minecraftInstanceId = System.Text.RegularExpressions.Regex.Match(processLine, runningVersionRegex).Groups[1].Value;
                            }
                        }

                        if (_minecraftUser == null)
                        {
                            string runningUserRegex = "^\\[\\d+:\\d+:\\d+\\] \\[Render thread/INFO\\]: Setting user: (.*)\\s*$";
                            if (System.Text.RegularExpressions.Regex.IsMatch(processLine, runningUserRegex))
                            {
                                _minecraftUser = System.Text.RegularExpressions.Regex.Match(processLine, runningUserRegex).Groups[1].Value;
                                _state = MinecraftState.LoggedIn;
                            }
                        }
                        break;

                    case MinecraftState.LoggedIn:
                        if (_minecraftProcess == null)
                        {
                            _minecraftProcess = _headlessMcProcess.GetChildProcesses().Where(process => process.ProcessName.Contains("java")).FirstOrDefault(_headlessMcProcess);
                            if (_minecraftProcess.Id == _headlessMcProcess.Id)
                            {
                                _minecraftProcess = null;
                                closeProcess();
                                return;
                            }
                            _state = MinecraftState.Launched;
                        }
                        break;

                    case MinecraftState.Launched:
                        if (_minecraftWindow == IntPtr.Zero)
                        {
                            string launchedWindowRegex = "^\\[\\d+:\\d+:\\d+\\] \\[Render thread/INFO\\]: Created: \\d+x\\d+x\\d+ minecraft:textures/atlas/gui.png-atlas\\s*$";
                            if (System.Text.RegularExpressions.Regex.IsMatch(processLine, launchedWindowRegex))
                            {
                                EnumerateWindowsForProcess(_minecraftProcess.Id);
                                if (_minecraftWindow != IntPtr.Zero)
                                {
                                    ShowWindow(_minecraftWindow, SW_MINIMIZE);
                                    _state = MinecraftState.MainMenu;
                                    processNextState();
                                }
                            }
                        }
                        break;

                    case MinecraftState.Connecting:
                        string connectingRegex = "^\\[\\d+:\\d+:\\d+\\] \\[Render thread/INFO\\]: Connecting to " + ConfigurationManager.AppSettings["serverAddress"] + ", " + ConfigurationManager.AppSettings["serverPort"] + "\\s*$";
                        if (System.Text.RegularExpressions.Regex.IsMatch(processLine, connectingRegex))
                        {
                            _connectAttempts++;
                            _state = MinecraftState.Connected;

                            bool needsResourcePack = false;
                            if (!bool.TryParse(ConfigurationManager.AppSettings["needsResourcePack"], out needsResourcePack))
                            {
                                needsResourcePack = false;
                            }
                            if (needsResourcePack)
                            {
                                lock (_downloadResourcePackLock)
                                {
                                    if (_taskDownloadResourcePackToken == null)
                                    {
                                        _taskDownloadResourcePackToken = new CancellationTokenSource();
                                        _taskDownloadResourcePack = DownloadResourcePackAsync(_taskDownloadResourcePackToken.Token);
                                    }
                                }
                            }

                            lock (_inGameTimeoutLock)
                            {
                                if (_taskInGameTimeoutToken == null)
                                {
                                    _taskInGameTimeoutToken = new CancellationTokenSource();
                                    _taskInGameTimeout = InGameTimeoutAsync(_taskInGameTimeoutToken.Token);
                                }
                            }
                        }
                        break;

                    case MinecraftState.Connected:
                        string errorConnectingRegex = "^\\[\\d+:\\d+:\\d+\\] \\[Server Connector #\\d+/ERROR\\]: Couldn't connect to server\\s*$";
                        if (System.Text.RegularExpressions.Regex.IsMatch(processLine, errorConnectingRegex))
                        {
                            lock (_downloadResourcePackLock)
                            {
                                if (_taskDownloadResourcePackToken != null)
                                {
                                    _taskDownloadResourcePackToken.Cancel();
                                }
                            }

                            lock (_inGameTimeoutLock)
                            {
                                if (_taskInGameTimeoutToken != null)
                                {
                                    _taskInGameTimeoutToken.Cancel();
                                }
                            }

                            int retryConnectAttempts = 10;
                            if (!int.TryParse(ConfigurationManager.AppSettings["retryConnectAttempts"], out retryConnectAttempts))
                            {
                                retryConnectAttempts = 10;
                            }
                            if (_connectAttempts < retryConnectAttempts)
                            {
                                _state = MinecraftState.Reconnecting;
                                Task.Run(async () =>
                                {
                                    int reconnectDelay = 10;
                                    if (!int.TryParse(ConfigurationManager.AppSettings["retryConnectSeconds"], out reconnectDelay))
                                    {
                                        reconnectDelay = 10;
                                    }
                                    reconnectDelay = reconnectDelay * 1000;
                                    await Task.Delay(reconnectDelay);
                                    await ReconnectAsync();
                                });
                            }
                            else
                            {
                                _state = MinecraftState.MainMenu;
                                sendProcessCommand("click 2");
                                Task.Run(async () =>
                                {
                                    int reconnectSeconds = 10;
                                    if (!int.TryParse(ConfigurationManager.AppSettings["reconnectSeconds"], out reconnectSeconds))
                                    {
                                        reconnectSeconds = 10;
                                    }
                                    reconnectSeconds = reconnectSeconds * 1000;
                                    await Task.Delay(reconnectSeconds);
                                    launchRelaunch();
                                });
                            }
                        }

                        string inGameRegex = "^\\[\\d+:\\d+:\\d+\\] \\[Render thread/INFO\\]: \\[CHAT\\] " + _minecraftUser + " joined the game\\s*$";
                        string inGameRegex2 = "^\\[\\d+:\\d+:\\d+\\] \\[Render thread/INFO\\]: \\[CHAT\\] Welcome, " + _minecraftUser + "!\\s*$";
                        if (System.Text.RegularExpressions.Regex.IsMatch(processLine, inGameRegex) || System.Text.RegularExpressions.Regex.IsMatch(processLine, inGameRegex2))
                        {
                            lock (_inGameTimeoutLock)
                            {
                                if (_taskInGameTimeoutToken != null)
                                {
                                    _taskInGameTimeoutToken.Cancel();
                                }
                            }

                            lock (_startToInGameTimeoutLock)
                            {
                                if (_taskStartToInGameTimeoutToken != null)
                                {
                                    _taskStartToInGameTimeoutToken.Cancel();
                                }
                            }

                            _state = MinecraftState.InGame;
                        }
                        break;

                    case MinecraftState.InGame:
                        string inGameStopRegex = "^\\[\\d+:\\d+:\\d+\\] \\[Render thread/INFO\\]: Stopping worker threads\\s*$";
                        string inGameServerClosedRegex = "^\\[\\d+:\\d+:\\d+\\] \\[Render thread/WARN\\]: Client disconnected with reason: Server closed\\s*$";
                        string inGameDisconnectRegex = "^\\[\\d+:\\d+:\\d+\\] \\[Render thread/WARN\\]: Client disconnected with reason: Disconnected\\s*$";
                        string inGameShuttingDownRegex = "^\\[\\d+:\\d+:\\d+\\] \\[Render thread/INFO\\]: \\[CHAT\\] \\[Rcon\\] Shutting down now!\\s*$";

                        if (System.Text.RegularExpressions.Regex.IsMatch(processLine, inGameStopRegex)
                            || System.Text.RegularExpressions.Regex.IsMatch(processLine, inGameServerClosedRegex)
                            || System.Text.RegularExpressions.Regex.IsMatch(processLine, inGameDisconnectRegex)
                            || System.Text.RegularExpressions.Regex.IsMatch(processLine, inGameShuttingDownRegex))
                        {
                            _state = MinecraftState.Disconnected;
                            sendProcessCommand("click 2");
                            Task.Run(async () =>
                            {
                                int reconnectSeconds = 10;
                                if (!int.TryParse(ConfigurationManager.AppSettings["reconnectSeconds"], out reconnectSeconds))
                                {
                                    reconnectSeconds = 10;
                                }
                                reconnectSeconds = reconnectSeconds * 1000;
                                await Task.Delay(reconnectSeconds);
                                launchRelaunch();
                            });
                        }
                        break;
                }

                processLines.RemoveAt(0);
            }
        }

        private void processNextState()
        {
            switch (_state)
            {
                case MinecraftState.MainMenu:
                    if (_minecraftWindow != IntPtr.Zero)
                    {
                        // Connect to the server
                        sendProcessCommand($"connect {ConfigurationManager.AppSettings["serverAddress"]} {ConfigurationManager.AppSettings["serverPort"]}");
                        _state = MinecraftState.Connecting;
                    }
                    break;

                default:
                    break;
            }
        }

        private async Task ReconnectAsync()
        {
            _state = MinecraftState.MainMenu;
            sendProcessCommand("click 2");
            processNextState();
        }

        private async Task DownloadResourcePackAsync(CancellationToken token)
        {
            try
            {
                int resourcePackWaitSeconds = 10;
                if (!int.TryParse(ConfigurationManager.AppSettings["resourcePackWaitSeconds"], out resourcePackWaitSeconds))
                {
                    resourcePackWaitSeconds = 10;
                }
                resourcePackWaitSeconds = resourcePackWaitSeconds * 1000;
                await Task.Delay(resourcePackWaitSeconds, token);
                if (!token.IsCancellationRequested)
                {
                    sendProcessCommand("click 0");
                }
            }
            catch (TaskCanceledException)
            {
                // Task was cancelled
            }
            finally
            {
                lock (_downloadResourcePackLock)
                {
                    _taskDownloadResourcePack = null;
                    _taskDownloadResourcePackToken = null;
                }
            }
        }

        private async Task InGameTimeoutAsync(CancellationToken token)
        {
            try
            {
                int inGameTimeoutSeconds = 10;
                if (!int.TryParse(ConfigurationManager.AppSettings["inGameTimeoutSeconds"], out inGameTimeoutSeconds))
                {
                    inGameTimeoutSeconds = 10;
                }
                inGameTimeoutSeconds = inGameTimeoutSeconds * 1000;
                await Task.Delay(inGameTimeoutSeconds, token);
                if (!token.IsCancellationRequested)
                {
                    _state = MinecraftState.Failed;
                    launchRelaunch();
                }
            }
            catch (TaskCanceledException)
            {
                // Task was cancelled
            }
            finally
            {
                lock (_inGameTimeoutLock)
                {
                    _taskInGameTimeout = null;
                    _taskInGameTimeoutToken = null;
                }
            }
        }

        private async Task StartToInGameTimeoutAsync(CancellationToken token)
        {
            try
            {
                int startToInGameTimeoutSeconds = 10;
                if (!int.TryParse(ConfigurationManager.AppSettings["startToInGameTimeoutSeconds"], out startToInGameTimeoutSeconds))
                {
                    startToInGameTimeoutSeconds = 10;
                }
                startToInGameTimeoutSeconds = startToInGameTimeoutSeconds * 1000;
                await Task.Delay(startToInGameTimeoutSeconds, token);
                if (!token.IsCancellationRequested)
                {
                    _state = MinecraftState.Failed;
                    launchRelaunch();
                }
            }
            catch (TaskCanceledException)
            {
                // Task was cancelled
            }
            finally
            {
                lock (_startToInGameTimeoutLock)
                {
                    _taskStartToInGameTimeoutSeconds = null;
                    _taskStartToInGameTimeoutToken = null;
                }
            }
        }
    }
}
