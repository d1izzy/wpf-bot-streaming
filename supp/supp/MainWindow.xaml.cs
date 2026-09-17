using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace supp
{
    public partial class MainWindow : Window
    {
        private ObservableCollection<BotProfile> _bots = new ObservableCollection<BotProfile>();
        private BotProfile _selectedBot;
        private BotRegistryService _registry;
        private readonly BotProcessManager _processManager = new BotProcessManager();
        private string _runtimeDir;
        private string _appBaseDir;
        private Process _proxyProcess;
        private const int LocalSocksProxyPort = 10808;

        public MainWindow()
        {
            InitializeComponent();
            _appBaseDir = AppDomain.CurrentDomain.BaseDirectory;
            _runtimeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotSuppRuntime");
            _registry = new BotRegistryService(_runtimeDir, _appBaseDir);
            _bots = _registry.LoadOrCreate();
            BotsListBox.ItemsSource = _bots;
            BotsListBox.SelectedIndex = _bots.Count > 0 ? 0 : -1;
            _processManager.LogReceived += (s, e) => Dispatcher.Invoke(() => AppendLog(e.BotId, e.Line));
            _processManager.BotExited += (s, botId) => Dispatcher.Invoke(() => { SetStatus("Бот остановлен: " + botId); RefreshHeaderStatus(); });
            RefreshHeaderStatus();
        }

        private string SelectedWorkspace => _selectedBot == null ? _runtimeDir : _registry.GetBotWorkspace(_selectedBot);
        private string EnvPath => Path.Combine(SelectedWorkspace, ".env");
        private string ScenarioPath => Path.Combine(SelectedWorkspace, "scenario.json");

        private void SetStatus(string text)
        {
            StatusTextBlock.Text = text;
            HeaderStatusText.Text = text;
        }

        private void AppendLog(string botId, string line)
        {
            if (ShowAllLogsCheckBox.IsChecked != true && (_selectedBot == null || _selectedBot.Id != botId)) return;
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] [{botId}] {line}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();
        }

        private void RefreshHeaderStatus()
        {
            HeaderStatusText.Text = $"Online: {_processManager.RunningCount} | Всего ботов: {_bots.Count}";
        }

        private void BotsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedBot = BotsListBox.SelectedItem as BotProfile;
            LoadSelectedBot();
        }

        private void LoadSelectedBot()
        {
            if (_selectedBot == null) return;
            _registry.CreateBotWorkspace(_selectedBot);
            BotNameTextBox.Text = _selectedBot.Name ?? string.Empty;
            UseProxyCheckBox.IsChecked = _selectedBot.UseProxy;
            LoadEnv();
            LoadScenario();
            SetStatus("Выбран бот: " + _selectedBot.Name);
        }

        private void LoadEnv()
        {
            TokenPasswordBox.Password = string.Empty;
            AdminIdsTextBox.Text = string.Empty;
            if (!File.Exists(EnvPath)) return;
            foreach (var line in File.ReadAllLines(EnvPath))
            {
                if (line.StartsWith("BOT_TOKEN=")) TokenPasswordBox.Password = line.Substring("BOT_TOKEN=".Length);
                if (line.StartsWith("ADMIN_ID=")) AdminIdsTextBox.Text = line.Substring("ADMIN_ID=".Length);
            }
        }

        private void SaveEnv()
        {
            Directory.CreateDirectory(SelectedWorkspace);
            File.WriteAllLines(EnvPath, new[]
            {
                "BOT_TOKEN=" + (TokenPasswordBox.Password ?? string.Empty).Trim(),
                "ADMIN_ID=" + (AdminIdsTextBox.Text ?? string.Empty).Trim()
            });
        }

        private void LoadScenario()
        {
            ScenarioTextBox.Text = File.Exists(ScenarioPath) ? File.ReadAllText(ScenarioPath) : "{}";
        }

        private void SaveScenario()
        {
            Directory.CreateDirectory(SelectedWorkspace);
            File.WriteAllText(ScenarioPath, ScenarioTextBox.Text ?? "{}");
        }

        private void SaveBotSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBot == null) return;
            _selectedBot.Name = string.IsNullOrWhiteSpace(BotNameTextBox.Text) ? _selectedBot.Id : BotNameTextBox.Text.Trim();
            _selectedBot.UseProxy = UseProxyCheckBox.IsChecked == true;
            SaveEnv();
            _registry.Save(_bots);
            BotsListBox.Items.Refresh();
            SetStatus("Настройки сохранены: " + _selectedBot.Name);
        }

        private void ReloadBotButton_Click(object sender, RoutedEventArgs e) => LoadSelectedBot();
        private void SaveScenarioButton_Click(object sender, RoutedEventArgs e) { SaveScenario(); SetStatus("Сценарий сохранён."); }
        private void ReloadScenarioButton_Click(object sender, RoutedEventArgs e) { LoadScenario(); SetStatus("Сценарий загружен."); }

        private void AddBotButton_Click(object sender, RoutedEventArgs e)
        {
            var n = _bots.Count + 1;
            var bot = new BotProfile { Id = "bot_" + n, Name = "Новый бот " + n, Folder = "bots/bot_" + n, Enabled = true, Autostart = false, UseProxy = true, EntryPoint = "bot.py", Template = "scenario" };
            while (_bots.Any(b => string.Equals(b.Id, bot.Id, StringComparison.OrdinalIgnoreCase)))
            {
                n++;
                bot.Id = "bot_" + n;
                bot.Name = "Новый бот " + n;
                bot.Folder = "bots/bot_" + n;
            }
            _bots.Add(bot);
            _registry.CreateBotWorkspace(bot);
            _registry.Save(_bots);
            BotsListBox.SelectedItem = bot;
            SetStatus("Добавлен бот: " + bot.Name);
            RefreshHeaderStatus();
        }

        private void DeleteBotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBot == null) return;
            if (_processManager.IsRunning(_selectedBot.Id)) { SetStatus("Сначала остановите выбранного бота."); return; }
            if (MessageBox.Show("Удалить бота из списка? Папка с файлами останется на диске.", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var old = _selectedBot;
            _bots.Remove(old);
            _registry.Save(_bots);
            BotsListBox.SelectedIndex = _bots.Count > 0 ? 0 : -1;
            SetStatus("Бот удалён из списка: " + old.Name);
            RefreshHeaderStatus();
        }

        private void OpenBotFolderButton_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(SelectedWorkspace);
            Process.Start("explorer.exe", SelectedWorkspace);
        }

        private bool TryGetPythonCommand(out string fileName, out string argsPrefix)
        {
            var embedded = Path.Combine(_appBaseDir, "python_runtime", "python.exe");
            if (File.Exists(embedded)) { fileName = embedded; argsPrefix = string.Empty; return true; }
            fileName = "py"; argsPrefix = "-3.10"; if (CanRun(fileName, "-3.10 --version")) return true;
            argsPrefix = "-3.11"; if (CanRun(fileName, "-3.11 --version")) return true;
            argsPrefix = "-3.12"; if (CanRun(fileName, "-3.12 --version")) return true;
            argsPrefix = "-3"; if (CanRun(fileName, "-3 --version")) return true;
            fileName = "python"; argsPrefix = string.Empty; return CanRun(fileName, "--version");
        }

        private static bool CanRun(string command, string args)
        {
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo { FileName = command, Arguments = args, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    p.Start();
                    if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private string EnsureProxy()
        {
            if (_selectedBot == null || !_selectedBot.UseProxy) return string.Empty;
            if (IsPortOpen("127.0.0.1", LocalSocksProxyPort)) return $"socks5://127.0.0.1:{LocalSocksProxyPort}";
            var proxyDir = Path.Combine(_appBaseDir, "proxy_runtime");
            var config = Path.Combine(proxyDir, "config.json");
            var xray = Path.Combine(proxyDir, "xray.exe");
            if (!File.Exists(xray) || !File.Exists(config)) { AppendLog(_selectedBot.Id, "proxy_runtime не найден, запуск без встроенного прокси."); return string.Empty; }
            _proxyProcess = new Process { StartInfo = new ProcessStartInfo { FileName = xray, Arguments = $"run -config \"{config}\"", WorkingDirectory = proxyDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }, EnableRaisingEvents = true };
            _proxyProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Dispatcher.Invoke(() => AppendLog("proxy", e.Data)); };
            _proxyProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Dispatcher.Invoke(() => AppendLog("proxy", "ERR: " + e.Data)); };
            _proxyProcess.Start(); _proxyProcess.BeginOutputReadLine(); _proxyProcess.BeginErrorReadLine();
            return WaitForPort("127.0.0.1", LocalSocksProxyPort, 8000) ? $"socks5://127.0.0.1:{LocalSocksProxyPort}" : string.Empty;
        }

        private static bool IsPortOpen(string host, int port)
        {
            try { using (var c = new TcpClient()) { var ar = c.BeginConnect(host, port, null, null); if (!ar.AsyncWaitHandle.WaitOne(250)) return false; c.EndConnect(ar); return c.Connected; } } catch { return false; }
        }

        private static bool WaitForPort(string host, int port, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { if (IsPortOpen(host, port)) return true; System.Threading.Thread.Sleep(250); }
            return false;
        }

        private async void StartBotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBot == null) return;
            SaveBotSettingsButton_Click(sender, e);
            if (_processManager.IsRunning(_selectedBot.Id)) { SetStatus("Бот уже запущен."); return; }
            if (!TryGetPythonCommand(out var py, out var prefix)) { SetStatus("Python 3.10-3.12 не найден."); return; }
            SetStatus("Запуск: " + _selectedBot.Name);
            var proxy = await Task.Run(() => EnsureProxy());
            _registry.CreateBotWorkspace(_selectedBot);
            _processManager.Start(_selectedBot, SelectedWorkspace, py, prefix, proxy);
            SetStatus(string.IsNullOrWhiteSpace(proxy) ? "Бот запущен." : "Бот запущен через прокси.");
            BotsListBox.Items.Refresh(); RefreshHeaderStatus();
        }

        private void StopBotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBot == null) return;
            _processManager.Stop(_selectedBot.Id);
            BotsListBox.Items.Refresh(); RefreshHeaderStatus();
            SetStatus("Остановлен: " + _selectedBot.Name);
        }

        private void RestartBotButton_Click(object sender, RoutedEventArgs e)
        {
            StopBotButton_Click(sender, e);
            StartBotButton_Click(sender, e);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            try { _processManager.StopAll(); } catch { }
            try { if (_proxyProcess != null && !_proxyProcess.HasExited) _proxyProcess.Kill(); } catch { }
            base.OnClosing(e);
        }
    }
}
