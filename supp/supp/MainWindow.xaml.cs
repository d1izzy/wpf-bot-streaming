using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace supp
{
    public class FaqItem : INotifyPropertyChanged
    {
        private string _key;
        private string _question;
        private string _answer;
        private bool _isProtected;
        private FaqFlow _flow;
        private bool _isFlowEnabled;

        public string Key
        {
            get => _key;
            set
            {
                _key = value;
                OnPropertyChanged(nameof(Key));
            }
        }

        public string Question
        {
            get => _question;
            set
            {
                _question = value;
                OnPropertyChanged(nameof(Question));
            }
        }

        public string Answer
        {
            get => _answer;
            set
            {
                _answer = value;
                OnPropertyChanged(nameof(Answer));
            }
        }

        public bool IsProtected
        {
            get => _isProtected;
            set
            {
                _isProtected = value;
                OnPropertyChanged(nameof(IsProtected));
            }
        }

        public FaqFlow Flow
        {
            get => _flow;
            set
            {
                _flow = value;
                OnPropertyChanged(nameof(Flow));
                OnPropertyChanged(nameof(HasFlow));
            }
        }

        public bool HasFlow => Flow != null && Flow.Steps.Count > 0;

        public bool IsFlowEnabled
        {
            get => _isFlowEnabled;
            set
            {
                _isFlowEnabled = value;
                OnPropertyChanged(nameof(IsFlowEnabled));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class FaqFlow
    {
        public string StartStepId { get; set; } = string.Empty;
        public ObservableCollection<FaqStep> Steps { get; set; } = new ObservableCollection<FaqStep>();
    }

    public class FaqStep : INotifyPropertyChanged
    {
        private string _id;
        private string _text;

        public string Id
        {
            get => _id;
            set
            {
                _id = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Id)));
            }
        }

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public ObservableCollection<FaqTransition> Transitions { get; set; } = new ObservableCollection<FaqTransition>();

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class FaqTransition : INotifyPropertyChanged
    {
        private string _caption;
        private string _targetStepId;
        private string _action;

        public string Caption
        {
            get => _caption;
            set
            {
                _caption = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption)));
            }
        }

        public string TargetStepId
        {
            get => _targetStepId;
            set
            {
                _targetStepId = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetStepId)));
            }
        }

        public string Action
        {
            get => _action;
            set
            {
                _action = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Action)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class MainWindow : Window
    {
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly ObservableCollection<FaqItem> _faqItems = new ObservableCollection<FaqItem>();
        private Process _botProcess;
        private Process _proxyProcess;
        private bool _isTokenVisible;
        private bool _isStartingBot;
        private bool _isStoppingBot;
        private bool _isSyncingFlowEditor;
        private string _currentProxyUrl = string.Empty;
        private string _appBaseDir;
        private string _runtimeDir;
        private string _proxySubscriptionUrl = string.Empty;
        private const string DefaultSubscriptionUrl = "https://g.ultm.in/s/Rx-crvu0yz3fbhpX";
        private const string XrayDownloadUrl = "https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip";
        private const int LocalSocksProxyPort = 10808;
        private const int LocalHttpProxyPort = 10809;

        private string ProjectPath => ProjectPathTextBox.Text?.Trim();
        private string EnvPath => Path.Combine(ProjectPath ?? string.Empty, ".env");
        private string FaqPath => Path.Combine(ProjectPath ?? string.Empty, "faq.json");
        private string BotPyPath => Path.Combine(ProjectPath ?? string.Empty, "bot.py");
        private string ProxyRuntimeDir => Path.Combine(_appBaseDir, "proxy_runtime");
        private string ProxyConfigPath => Path.Combine(ProxyRuntimeDir, "config.json");

        public MainWindow()
        {
            InitializeComponent();
            FaqListBox.ItemsSource = _faqItems;
            _appBaseDir = AppDomain.CurrentDomain.BaseDirectory;
            _runtimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BotSuppRuntime"
            );
            EnsureRuntimeWorkspace();
            ProjectPathTextBox.Text = _runtimeDir;
            LoadEnv();
            LoadFaq();
            UpdateRuntimeStatusBadge();
        }

        private void EnsureRuntimeWorkspace()
        {
            Directory.CreateDirectory(_runtimeDir);

            var botDest = Path.Combine(_runtimeDir, "bot.py");
            var faqDest = Path.Combine(_runtimeDir, "faq.json");
            var envDest = Path.Combine(_runtimeDir, ".env");

            // Источники из output + fallback на корень проекта только для запуска из VS.
            var projectRootBot = GetDevelopmentRootBotPath();
            var candidateBotSources = new[]
            {
                // При запуске из VS приоритет у корневого bot.py (актуальный файл разработки).
                projectRootBot,
                Path.Combine(_appBaseDir, "RuntimeAssets", "bot.py"),
                Path.Combine(_appBaseDir, "bot.py")
            };
            var candidateFaqSources = new[]
            {
                Path.Combine(_appBaseDir, "RuntimeAssets", "faq.json"),
                Path.Combine(_appBaseDir, "faq.json"),
                Path.GetFullPath(Path.Combine(_appBaseDir, "..", "..", "..", "faq.json"))
            };

            var botSrc = PickLatestExistingFile(candidateBotSources);
            var faqSrc = PickLatestExistingFile(candidateFaqSources);

            // bot.py обновляем на каждом запуске, чтобы пользователь всегда получал
            // актуальные исправления логики (proxy, валидация и т.д.).
            if (File.Exists(botSrc))
                File.Copy(botSrc, botDest, true);

            if (!File.Exists(faqDest))
            {
                if (File.Exists(faqSrc))
                    File.Copy(faqSrc, faqDest, true);
            }

            if (!File.Exists(envDest))
            {
                File.WriteAllLines(envDest, new[]
                {
                    "BOT_TOKEN=",
                    "ADMIN_ID="
                });
            }
        }

        private string SyncRuntimeBotScript()
        {
            var botDest = Path.Combine(_runtimeDir, "bot.py");
            var projectRootBot = GetDevelopmentRootBotPath();
            var botSrc = string.Empty;

            // Жесткий приоритет для локальной разработки: корневой bot.py.
            if (File.Exists(projectRootBot))
            {
                botSrc = projectRootBot;
            }
            else
            {
                var candidateBotSources = new[]
                {
                    Path.Combine(_appBaseDir, "RuntimeAssets", "bot.py"),
                    Path.Combine(_appBaseDir, "bot.py")
                };
                botSrc = PickLatestExistingFile(candidateBotSources);
            }
            if (!string.IsNullOrWhiteSpace(botSrc) && File.Exists(botSrc))
            {
                File.Copy(botSrc, botDest, true);
                return botSrc;
            }

            return string.Empty;
        }

        private static string PickLatestExistingFile(IEnumerable<string> candidates)
        {
            return candidates
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .FirstOrDefault() ?? string.Empty;
        }

        private string GetDevelopmentRootBotPath()
        {
            try
            {
                var devRoot = Path.GetFullPath(Path.Combine(_appBaseDir, "..", "..", ".."));
                var solutionPath = Path.Combine(devRoot, "supp.sln");
                if (!File.Exists(solutionPath))
                    return string.Empty;

                return Path.Combine(devRoot, "bot.py");
            }
            catch
            {
                return string.Empty;
            }
        }

        private void SetStatus(string message)
        {
            StatusTextBlock.Text = message;
        }

        private void AppendLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                LogTextBox.ScrollToEnd();
            });
        }

        private void UpdateRuntimeStatusBadge()
        {
            var botRunning = _botProcess != null && !_botProcess.HasExited;
            var proxyRunning =
                (_proxyProcess != null && !_proxyProcess.HasExited) ||
                IsLocalPortOpen("127.0.0.1", LocalSocksProxyPort) ||
                IsLocalPortOpen("127.0.0.1", LocalHttpProxyPort) ||
                !string.IsNullOrWhiteSpace(_currentProxyUrl);

            var badgeBackground = botRunning ? "#ECFDF3" : "#FEE4E2";
            var badgeBorder = botRunning ? "#ABEFC6" : "#FDA29B";
            var textColor = botRunning ? "#067647" : "#B42318";
            var iconColor = botRunning ? "#12B76A" : "#F04438";

            RuntimeStatusBadge.Background = (Brush)new BrushConverter().ConvertFromString(badgeBackground);
            RuntimeStatusBadge.BorderBrush = (Brush)new BrushConverter().ConvertFromString(badgeBorder);
            RuntimeStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(textColor);
            RuntimeStatusIcon.Fill = (Brush)new BrushConverter().ConvertFromString(iconColor);
            RuntimeStatusText.Text = botRunning ? "online" : "offline";

            RuntimeStatusBadge.ToolTip = null;
        }

        private void LoadEnv()
        {
            try
            {
                if (!File.Exists(EnvPath))
                {
                    SetStatus("Файл .env не найден. Создайте и сохраните настройки.");
                    return;
                }

                var lines = File.ReadAllLines(EnvPath);
                var tokenLine = lines.FirstOrDefault(l => l.StartsWith("BOT_TOKEN="));
                var adminLine = lines.FirstOrDefault(l => l.StartsWith("ADMIN_ID="));
                var proxySubscriptionLine = lines.FirstOrDefault(l => l.StartsWith("BOTSUPP_SUBSCRIPTION_URL="));

                var token = tokenLine?.Substring("BOT_TOKEN=".Length) ?? string.Empty;
                SetTokenValue(token);
                AdminIdsTextBox.Text = adminLine?.Substring("ADMIN_ID=".Length) ?? string.Empty;
                _proxySubscriptionUrl = proxySubscriptionLine?.Substring("BOTSUPP_SUBSCRIPTION_URL=".Length)?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(_proxySubscriptionUrl))
                    _proxySubscriptionUrl = DefaultSubscriptionUrl;
                SetStatus("Настройки .env загружены.");
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка загрузки .env: " + ex.Message);
            }
        }

        private void SaveEnv()
        {
            if (string.IsNullOrWhiteSpace(ProjectPath) || !Directory.Exists(ProjectPath))
            {
                SetStatus("Укажите корректную папку проекта.");
                return;
            }

            var token = GetTokenValue();
            if (string.IsNullOrWhiteSpace(token))
            {
                SetStatus("Токен не может быть пустым.");
                return;
            }

            try
            {
                var lines = File.Exists(EnvPath)
                    ? File.ReadAllLines(EnvPath).ToList()
                    : new List<string>();

                UpsertEnvLine(lines, "BOT_TOKEN", token.Trim());
                UpsertEnvLine(lines, "ADMIN_ID", AdminIdsTextBox.Text.Trim());
                if (!string.IsNullOrWhiteSpace(_proxySubscriptionUrl))
                    UpsertEnvLine(lines, "BOTSUPP_SUBSCRIPTION_URL", _proxySubscriptionUrl);

                File.WriteAllLines(EnvPath, lines);
                SetStatus(".env успешно сохранен.");
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка сохранения .env: " + ex.Message);
            }
        }

        private static void UpsertEnvLine(List<string> lines, string key, string value)
        {
            var prefix = key + "=";
            var index = lines.FindIndex(l => l.StartsWith(prefix));
            var newLine = prefix + value;

            if (index >= 0)
                lines[index] = newLine;
            else
                lines.Add(newLine);
        }

        private void LoadFaq()
        {
            try
            {
                _faqItems.Clear();
                if (!File.Exists(FaqPath))
                {
                    SetStatus("faq.json не найден. Создайте записи и сохраните.");
                    return;
                }

                var jsonText = File.ReadAllText(FaqPath);
                var root = _json.Deserialize<Dictionary<string, Dictionary<string, object>>>(jsonText);
                if (root == null)
                {
                    SetStatus("Не удалось разобрать faq.json.");
                    return;
                }

                foreach (var entry in root)
                {
                    var question = entry.Value != null && entry.Value.ContainsKey("question")
                        ? Convert.ToString(entry.Value["question"])
                        : string.Empty;
                    var answer = entry.Value != null && entry.Value.ContainsKey("answer")
                        ? Convert.ToString(entry.Value["answer"])
                        : string.Empty;
                    var isProtected = entry.Value != null && entry.Value.ContainsKey("protected")
                        && Convert.ToBoolean(entry.Value["protected"]);

                    var parsedFlow = ParseFlow(entry.Value);
                    _faqItems.Add(new FaqItem
                    {
                        Key = entry.Key,
                        Question = question,
                        Answer = answer,
                        IsProtected = isProtected,
                        Flow = parsedFlow,
                        IsFlowEnabled = GetFlowEnabledFlag(entry.Value, parsedFlow != null)
                    });
                }

                if (_faqItems.Count > 0)
                {
                    FaqListBox.SelectedIndex = 0;
                    SyncEditorFromSelectedItem();
                }
                else
                {
                    ClearFaqEditor();
                }

                SetStatus("FAQ загружен.");
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка загрузки FAQ: " + ex.Message);
            }
        }

        private void SyncEditorFromSelectedItem()
        {
            if (FaqListBox.SelectedItem is FaqItem selected)
            {
                FaqKeyTextBox.Text = selected.Key ?? string.Empty;
                FaqQuestionTextBox.Text = selected.Question ?? string.Empty;
                FaqAnswerTextBox.Text = selected.Answer ?? string.Empty;
                FaqProtectedCheckBox.IsChecked = selected.IsProtected;
                SyncFlowEditorFromSelectedItem(selected);
                return;
            }

            ClearFaqEditor();
        }

        private void ApplyEditorToSelectedItem()
        {
            if (FaqListBox.SelectedItem is FaqItem selected)
            {
                selected.Key = FaqKeyTextBox.Text?.Trim() ?? string.Empty;
                selected.Question = FaqQuestionTextBox.Text?.Trim() ?? string.Empty;
                selected.Answer = FaqAnswerTextBox.Text?.Trim() ?? string.Empty;
                selected.IsProtected = FaqProtectedCheckBox.IsChecked == true;
                ApplyFlowEditorToSelectedItem(selected);
            }
        }

        private void ClearFaqEditor()
        {
            FaqKeyTextBox.Text = string.Empty;
            FaqQuestionTextBox.Text = string.Empty;
            FaqAnswerTextBox.Text = string.Empty;
            FaqProtectedCheckBox.IsChecked = false;
            FaqEnableFlowCheckBox.IsChecked = false;
            FlowStartStepComboBox.ItemsSource = null;
            FlowStepsListBox.ItemsSource = null;
            FlowTransitionsDataGrid.ItemsSource = null;
            FlowStepIdTextBox.Text = string.Empty;
            FlowStepTextTextBox.Text = string.Empty;
        }

        private bool ValidateFaq(out string error)
        {
            error = string.Empty;
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in _faqItems)
            {
                if (string.IsNullOrWhiteSpace(item.Key))
                {
                    error = "Ключ FAQ не может быть пустым.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(item.Question))
                {
                    error = $"Пустой вопрос у ключа '{item.Key}'.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(item.Answer))
                {
                    error = $"Пустой ответ у ключа '{item.Key}'.";
                    return false;
                }
                if (!keys.Add(item.Key.Trim()))
                {
                    error = $"Дублирующийся ключ '{item.Key}'.";
                    return false;
                }

                if (!ValidateFlow(item, out error))
                    return false;
            }
            return true;
        }

        private void SaveFaq()
        {
            ApplyEditorToSelectedItem();

            if (string.IsNullOrWhiteSpace(ProjectPath) || !Directory.Exists(ProjectPath))
            {
                SetStatus("Укажите корректную папку проекта.");
                return;
            }

            if (!ValidateFaq(out var error))
            {
                SetStatus("Ошибка валидации FAQ: " + error);
                return;
            }

            try
            {
                var root = new Dictionary<string, Dictionary<string, object>>();
                foreach (var item in _faqItems)
                {
                    var faq = new Dictionary<string, object>
                    {
                        ["question"] = item.Question.Trim(),
                        ["answer"] = item.Answer.Trim(),
                        ["protected"] = item.IsProtected ? "true" : "false",
                        ["flowEnabled"] = item.IsFlowEnabled
                    };

                    if (item.Flow != null && item.Flow.Steps.Count > 0)
                        faq["flow"] = SerializeFlow(item.Flow);

                    root[item.Key.Trim()] = faq;
                }

                var serialized = _json.Serialize(root);
                File.WriteAllText(FaqPath, PrettyPrintJson(serialized));
                SetStatus("FAQ успешно сохранен.");
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка сохранения FAQ: " + ex.Message);
            }
        }

        private FaqFlow ParseFlow(Dictionary<string, object> faqEntry)
        {
            if (faqEntry == null || !faqEntry.ContainsKey("flow"))
                return null;

            if (!(faqEntry["flow"] is Dictionary<string, object> flowObj))
                return null;

            var flow = new FaqFlow
            {
                StartStepId = flowObj.ContainsKey("startStepId") ? Convert.ToString(flowObj["startStepId"]) ?? string.Empty : string.Empty,
                Steps = new ObservableCollection<FaqStep>()
            };

            if (flowObj.TryGetValue("steps", out var stepsObj) && stepsObj is ArrayList stepsList)
            {
                foreach (var rawStep in stepsList.OfType<Dictionary<string, object>>())
                {
                    var step = new FaqStep
                    {
                        Id = rawStep.ContainsKey("id") ? Convert.ToString(rawStep["id"]) ?? string.Empty : string.Empty,
                        Text = rawStep.ContainsKey("text") ? Convert.ToString(rawStep["text"]) ?? string.Empty : string.Empty,
                        Transitions = new ObservableCollection<FaqTransition>()
                    };

                    if (rawStep.TryGetValue("transitions", out var transitionsObj) && transitionsObj is ArrayList transitionsList)
                    {
                        foreach (var rawTransition in transitionsList.OfType<Dictionary<string, object>>())
                        {
                            step.Transitions.Add(new FaqTransition
                            {
                                Caption = rawTransition.ContainsKey("caption") ? Convert.ToString(rawTransition["caption"]) ?? string.Empty : string.Empty,
                                TargetStepId = rawTransition.ContainsKey("targetStepId") ? Convert.ToString(rawTransition["targetStepId"]) ?? string.Empty : string.Empty,
                                Action = rawTransition.ContainsKey("action") ? Convert.ToString(rawTransition["action"]) ?? string.Empty : string.Empty
                            });
                        }
                    }

                    flow.Steps.Add(step);
                }
            }

            return flow.Steps.Count > 0 ? flow : null;
        }

        private bool GetFlowEnabledFlag(Dictionary<string, object> faqEntry, bool defaultValue)
        {
            if (faqEntry == null || !faqEntry.TryGetValue("flowEnabled", out var value) || value == null)
                return defaultValue;

            if (value is bool b)
                return b;

            var text = Convert.ToString(value)?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return defaultValue;
            if (bool.TryParse(text, out var parsedBool))
                return parsedBool;
            if (text == "1")
                return true;
            if (text == "0")
                return false;
            return defaultValue;
        }

        private Dictionary<string, object> SerializeFlow(FaqFlow flow)
        {
            var steps = flow.Steps.Select(step => new Dictionary<string, object>
            {
                ["id"] = step.Id?.Trim() ?? string.Empty,
                ["text"] = step.Text?.Trim() ?? string.Empty,
                ["transitions"] = step.Transitions.Select(t => new Dictionary<string, object>
                {
                    ["caption"] = t.Caption?.Trim() ?? string.Empty,
                    ["targetStepId"] = t.TargetStepId?.Trim() ?? string.Empty,
                    ["action"] = t.Action?.Trim() ?? string.Empty
                }).ToList()
            }).ToList();

            return new Dictionary<string, object>
            {
                ["startStepId"] = flow.StartStepId?.Trim() ?? string.Empty,
                ["steps"] = steps
            };
        }

        private bool ValidateFlow(FaqItem item, out string error)
        {
            error = string.Empty;
            if (!item.IsFlowEnabled || item.Flow == null || item.Flow.Steps.Count == 0)
                return true;

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in item.Flow.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Id))
                {
                    error = $"У записи '{item.Key}' есть шаг с пустым id.";
                    return false;
                }

                if (!ids.Add(step.Id.Trim()))
                {
                    error = $"У записи '{item.Key}' найден дубликат stepId '{step.Id}'.";
                    return false;
                }

                foreach (var transition in step.Transitions)
                {
                    var caption = transition.Caption?.Trim() ?? string.Empty;
                    var target = transition.TargetStepId?.Trim() ?? string.Empty;
                    var action = transition.Action?.Trim() ?? string.Empty;
                    // Пустой caption разрешен: такой переход игнорируется, шаг работает с дефолтными кнопками.
                    if (string.IsNullOrWhiteSpace(caption))
                        continue;

                    if (string.IsNullOrWhiteSpace(target) == string.IsNullOrWhiteSpace(action))
                    {
                        error = $"У записи '{item.Key}' в шаге '{step.Id}' у перехода '{caption}' заполните либо targetStepId, либо action.";
                        return false;
                    }
                }
            }

            if (!ids.Contains(item.Flow.StartStepId ?? string.Empty))
            {
                item.Flow.StartStepId = item.Flow.Steps.FirstOrDefault()?.Id ?? string.Empty;
            }
            if (!ids.Contains(item.Flow.StartStepId ?? string.Empty))
            {
                error = $"У записи '{item.Key}' startStepId '{item.Flow.StartStepId}' не найден среди шагов.";
                return false;
            }

            foreach (var step in item.Flow.Steps)
            {
                foreach (var transition in step.Transitions)
                {
                    var target = transition.TargetStepId?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(target) && !ids.Contains(target))
                    {
                        error = $"У записи '{item.Key}' в шаге '{step.Id}' переход ссылается на несуществующий stepId '{target}'.";
                        return false;
                    }
                }
            }

            var firstStep = item.Flow.Steps.FirstOrDefault(s => string.Equals(s.Id, item.Flow.StartStepId, StringComparison.OrdinalIgnoreCase));
            if (firstStep == null || firstStep.Transitions.Count < 2)
            {
                error = $"У записи '{item.Key}' стартовый шаг должен содержать минимум 2 исхода.";
                return false;
            }

            return true;
        }

        private void SyncFlowEditorFromSelectedItem(FaqItem selected)
        {
            _isSyncingFlowEditor = true;
            try
            {
                var hasFlowData = selected.Flow != null && selected.Flow.Steps.Count > 0;
                var flowEnabled = selected.IsFlowEnabled && hasFlowData;
                FaqEnableFlowCheckBox.IsChecked = selected.IsFlowEnabled;
                FlowStepsListBox.IsEnabled = flowEnabled;
                FlowStartStepComboBox.IsEnabled = flowEnabled;
                FlowTransitionsDataGrid.IsEnabled = flowEnabled;
                FlowStepIdTextBox.IsEnabled = flowEnabled;
                FlowStepTextTextBox.IsEnabled = flowEnabled;

                if (!flowEnabled)
                {
                    FlowStepsListBox.ItemsSource = null;
                    FlowStartStepComboBox.ItemsSource = null;
                    FlowTransitionsDataGrid.ItemsSource = null;
                    FlowStepIdTextBox.Text = string.Empty;
                    FlowStepTextTextBox.Text = string.Empty;
                    return;
                }

                FlowStepsListBox.ItemsSource = selected.Flow.Steps;
                FlowStartStepComboBox.ItemsSource = selected.Flow.Steps.Select(s => s.Id).ToList();
                FlowStartStepComboBox.SelectedItem = selected.Flow.StartStepId;
                FlowStepsListBox.SelectedIndex = selected.Flow.Steps.Count > 0 ? 0 : -1;
                SyncSelectedStepEditor();
            }
            finally
            {
                _isSyncingFlowEditor = false;
            }
        }

        private void ApplyFlowEditorToSelectedItem(FaqItem selected)
        {
            if (_isSyncingFlowEditor)
                return;

            selected.IsFlowEnabled = FaqEnableFlowCheckBox.IsChecked == true;
            if (!selected.IsFlowEnabled)
                return;

            if (selected.Flow == null)
                selected.Flow = new FaqFlow();

            selected.Flow.StartStepId = Convert.ToString(FlowStartStepComboBox.SelectedItem) ?? string.Empty;
        }

        private void SyncSelectedStepEditor()
        {
            _isSyncingFlowEditor = true;
            try
            {
                if (FlowStepsListBox.SelectedItem is FaqStep step)
                {
                    FlowStepIdTextBox.Text = step.Id ?? string.Empty;
                    FlowStepTextTextBox.Text = step.Text ?? string.Empty;
                    FlowTransitionsDataGrid.ItemsSource = step.Transitions;
                    return;
                }

                FlowStepIdTextBox.Text = string.Empty;
                FlowStepTextTextBox.Text = string.Empty;
                FlowTransitionsDataGrid.ItemsSource = null;
            }
            finally
            {
                _isSyncingFlowEditor = false;
            }
        }

        // Простой форматтер JSON без внешних пакетов
        private static string PrettyPrintJson(string json)
        {
            var indent = 0;
            var quoted = false;
            using (var sw = new StringWriter())
            {
                for (int i = 0; i < json.Length; i++)
                {
                    var ch = json[i];
                    switch (ch)
                    {
                        case '{':
                        case '[':
                            sw.Write(ch);
                            if (!quoted)
                            {
                                sw.WriteLine();
                                indent++;
                                sw.Write(new string(' ', indent * 2));
                            }
                            break;
                        case '}':
                        case ']':
                            if (!quoted)
                            {
                                sw.WriteLine();
                                indent--;
                                sw.Write(new string(' ', indent * 2));
                            }
                            sw.Write(ch);
                            break;
                        case '"':
                            sw.Write(ch);
                            var escaped = false;
                            var index = i;
                            while (index > 0 && json[--index] == '\\')
                            {
                                escaped = !escaped;
                            }
                            if (!escaped) quoted = !quoted;
                            break;
                        case ',':
                            sw.Write(ch);
                            if (!quoted)
                            {
                                sw.WriteLine();
                                sw.Write(new string(' ', indent * 2));
                            }
                            break;
                        case ':':
                            sw.Write(ch);
                            if (!quoted) sw.Write(" ");
                            break;
                        default:
                            if (!quoted && char.IsWhiteSpace(ch)) { }
                            else sw.Write(ch);
                            break;
                    }
                }
                return sw.ToString();
            }
        }

        private bool TryGetPythonCommand(out string fileName, out string argsPrefix)
        {
            var embeddedPython = Path.Combine(_appBaseDir, "python_runtime", "python.exe");
            if (File.Exists(embeddedPython))
            {
                fileName = embeddedPython;
                argsPrefix = string.Empty;
                return true;
            }

            fileName = "py";
            argsPrefix = "-3.10";
            if (CanRun(fileName, "-3.10 --version"))
                return true;

            // Fallback: если 3.10 нет, пробуем более новые версии.
            argsPrefix = "-3.11";
            if (CanRun(fileName, "-3.11 --version"))
                return true;

            argsPrefix = "-3.12";
            if (CanRun(fileName, "-3.12 --version"))
                return true;

            argsPrefix = "-3";
            if (CanRun(fileName, "-3 --version"))
                return true;

            fileName = "python";
            argsPrefix = string.Empty;
            return CanRun(fileName, "--version");
        }

        private static bool CanRun(string command, string args)
        {
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    p.Start();
                    if (!p.WaitForExit(3000))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }

                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task StartBotAsync()
        {
            if (_isStoppingBot)
            {
                SetStatus("Дождитесь завершения остановки...");
                return;
            }

            if (_isStartingBot)
            {
                SetStatus("Запуск уже выполняется, подождите...");
                return;
            }

            if (_botProcess != null && !_botProcess.HasExited)
            {
                SetStatus("Бот уже запущен.");
                return;
            }

            var syncedBotSource = SyncRuntimeBotScript();
            if (!string.IsNullOrWhiteSpace(syncedBotSource))
                AppendLog("Используется bot.py: " + syncedBotSource);

            if (!File.Exists(BotPyPath))
            {
                SetStatus("Не найден bot.py в указанной папке.");
                return;
            }

            if (!TryGetPythonCommand(out var pythonExe, out var argsPrefix))
            {
                SetStatus("Python не найден. Установите Python 3.10-3.12.");
                return;
            }

            if (!EnsurePythonModuleAvailable(pythonExe, argsPrefix, "socks", "PySocks==1.7.1"))
            {
                SetStatus("Не удалось подготовить модуль PySocks. Проверьте интернет и перезапустите.");
                return;
            }

            _isStartingBot = true;
            StartBotButton.IsEnabled = false;
            SetStatus("Подготовка прокси и запуск бота...");
            var proxyUrl = await Task.Run(() => TryStartLocalProxy());
            _currentProxyUrl = proxyUrl;

            var args = string.IsNullOrWhiteSpace(argsPrefix)
                ? "\"bot.py\""
                : $"{argsPrefix} \"bot.py\"";

            _botProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = args,
                    WorkingDirectory = ProjectPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                _botProcess.StartInfo.EnvironmentVariables["BOTSUPP_PROXY_URL"] = proxyUrl;
                _botProcess.StartInfo.EnvironmentVariables["ALL_PROXY"] = proxyUrl;
                _botProcess.StartInfo.EnvironmentVariables["HTTPS_PROXY"] = proxyUrl;
                _botProcess.StartInfo.EnvironmentVariables["HTTP_PROXY"] = proxyUrl;
            }

            _botProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) AppendLog(e.Data); };
            _botProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) AppendLog("ERR: " + e.Data); };
            _botProcess.Exited += (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    ProcessStatusTextBlock.Text = "Статус: остановлен";
                    SetStatus("Процесс бота завершен.");
                    _currentProxyUrl = string.Empty;
                    UpdateRuntimeStatusBadge();
                });
            };

            try
            {
                _botProcess.Start();
                _botProcess.BeginOutputReadLine();
                _botProcess.BeginErrorReadLine();
                ProcessStatusTextBlock.Text = "Статус: запущен";
                SetStatus(string.IsNullOrWhiteSpace(proxyUrl) ? "Бот запущен." : "Бот запущен через локальный прокси.");
                AppendLog(string.IsNullOrWhiteSpace(proxyUrl) ? "Бот запущен." : $"Бот запущен. Используется прокси: {proxyUrl}");
                UpdateRuntimeStatusBadge();
            }
            finally
            {
                _isStartingBot = false;
                StartBotButton.IsEnabled = true;
            }
        }

        private async Task StopBotAsync()
        {
            if (_isStoppingBot)
            {
                SetStatus("Остановка уже выполняется...");
                return;
            }

            _isStoppingBot = true;
            StopBotButton.IsEnabled = false;
            RestartBotButton.IsEnabled = false;

            try
            {
                if (_botProcess == null || _botProcess.HasExited)
                {
                    await StopProxyAsync();
                    SetStatus("Бот уже остановлен.");
                    return;
                }

                try
                {
                    var proc = _botProcess;
                    _botProcess = null;

                    await Task.Run(() =>
                    {
                        try
                        {
                            if (!proc.HasExited)
                                proc.Kill();
                            proc.WaitForExit(2000);
                        }
                        catch
                        {
                            // best effort
                        }
                        finally
                        {
                            proc.Dispose();
                        }
                    });
                }
                catch (Exception ex)
                {
                    AppendLog("Ошибка остановки: " + ex.Message);
                }

                ProcessStatusTextBlock.Text = "Статус: остановлен";
                SetStatus("Бот остановлен.");
                AppendLog("Бот остановлен.");
                await StopProxyAsync();
                _currentProxyUrl = string.Empty;
                UpdateRuntimeStatusBadge();
            }
            finally
            {
                StopBotButton.IsEnabled = true;
                RestartBotButton.IsEnabled = true;
                _isStoppingBot = false;
            }
        }

        private string TryStartLocalProxy()
        {
            if (_proxyProcess != null && !_proxyProcess.HasExited)
                return $"socks5://127.0.0.1:{LocalSocksProxyPort}";

            // Если внешний клиент (Happ/Nekoray и т.п.) уже запущен локально,
            // используем его без отдельного proxy-core внутри приложения.
            if (IsLocalPortOpen("127.0.0.1", LocalSocksProxyPort))
            {
                AppendLog($"Найден внешний локальный SOCKS-прокси на 127.0.0.1:{LocalSocksProxyPort}.");
                return $"socks5://127.0.0.1:{LocalSocksProxyPort}";
            }
            if (IsLocalPortOpen("127.0.0.1", LocalHttpProxyPort))
            {
                AppendLog($"Найден внешний локальный HTTP-прокси на 127.0.0.1:{LocalHttpProxyPort}.");
                return $"http://127.0.0.1:{LocalHttpProxyPort}";
            }

            var candidates = new[]
            {
                new { Exe = Path.Combine(ProxyRuntimeDir, "sing-box.exe"), Args = $"run -c \"{ProxyConfigPath}\"" },
                new { Exe = Path.Combine(ProxyRuntimeDir, "xray.exe"), Args = $"run -config \"{ProxyConfigPath}\"" }
            };

            EnsureProxyCoreFromWeb();
            EnsureProxyConfigFromSubscription();
            EnsureProxyConfigIsCompatible();
            ForceRemoveGeositeTokensFromConfigText();

            var selected = candidates.FirstOrDefault(c => File.Exists(c.Exe) && File.Exists(ProxyConfigPath));
            if (selected == null)
            {
                AppendLog("Локальный proxy-core не найден. Если у вас запущен VPN-клиент (Happ/Nekoray), проверьте что открыт 127.0.0.1:10808 или 10809.");
                return string.Empty;
            }

            try
            {
                _proxyProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = selected.Exe,
                        Arguments = selected.Args,
                        WorkingDirectory = ProxyRuntimeDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };

                _proxyProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) AppendLog("[proxy] " + e.Data); };
                _proxyProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) AppendLog("[proxy-err] " + e.Data); };
                _proxyProcess.Exited += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        AppendLog("Прокси-процесс завершен.");
                        _currentProxyUrl = string.Empty;
                        UpdateRuntimeStatusBadge();
                    });
                };

                _proxyProcess.Start();
                _proxyProcess.BeginOutputReadLine();
                _proxyProcess.BeginErrorReadLine();

                if (!WaitForLocalPort("127.0.0.1", LocalSocksProxyPort, 8000))
                {
                    AppendLog("Локальный прокси не открыл порт вовремя. Запуск бота продолжен без прокси.");
                    StopProxy();
                    return string.Empty;
                }

                AppendLog($"Локальный прокси запущен на 127.0.0.1:{LocalSocksProxyPort}.");
                Dispatcher.Invoke(UpdateRuntimeStatusBadge);
                return $"socks5://127.0.0.1:{LocalSocksProxyPort}";
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка запуска локального прокси: " + ex.Message);
                StopProxy();
                return string.Empty;
            }
        }

        private void EnsureProxyConfigFromSubscription()
        {
            if (File.Exists(ProxyConfigPath))
                return;

            if (string.IsNullOrWhiteSpace(_proxySubscriptionUrl))
                return;

            try
            {
                Directory.CreateDirectory(ProxyRuntimeDir);
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "BotSuppConfigurator/1.0";
                    var content = wc.DownloadString(_proxySubscriptionUrl);
                    if (string.IsNullOrWhiteSpace(content))
                        return;

                    var parsed = _json.DeserializeObject(content);
                    object selected = parsed;

                    // Поддержка формата подписки: массив профилей или один профиль-объект.
                    if (parsed is object[] arr && arr.Length > 0)
                        selected = arr[0];
                    else if (parsed is System.Collections.ArrayList list && list.Count > 0)
                        selected = list[0];

                    if (selected is Dictionary<string, object> selectedDict)
                    {
                        StripIncompatibleRoutingRules(selectedDict);
                        selected = selectedDict;
                    }

                    var serialized = _json.Serialize(selected);
                    File.WriteAllText(ProxyConfigPath, PrettyPrintJson(serialized));
                    AppendLog("proxy config.json создан автоматически из BOTSUPP_SUBSCRIPTION_URL.");
                }
            }
            catch (Exception ex)
            {
                AppendLog("Не удалось получить proxy config из подписки: " + ex.Message);
            }
        }

        private void EnsureProxyConfigIsCompatible()
        {
            if (!File.Exists(ProxyConfigPath))
                return;

            try
            {
                var text = File.ReadAllText(ProxyConfigPath);
                var parsed = _json.DeserializeObject(text);
                if (parsed is Dictionary<string, object> rootDict)
                {
                    var changed = StripIncompatibleRoutingRules(rootDict);
                    if (changed)
                    {
                        File.WriteAllText(ProxyConfigPath, PrettyPrintJson(_json.Serialize(rootDict)));
                        AppendLog("proxy config.json автоматически очищен от несовместимых geosite-правил.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("Не удалось проверить совместимость proxy config: " + ex.Message);
            }
        }

        private static bool StripIncompatibleRoutingRules(Dictionary<string, object> rootDict)
        {
            if (!rootDict.TryGetValue("routing", out var routingObj))
                return false;
            if (!(routingObj is Dictionary<string, object> routingDict))
                return false;
            if (!routingDict.TryGetValue("rules", out var rulesObj))
                return false;
            if (!(rulesObj is IList rules))
                return false;

            var changed = false;
            for (int i = rules.Count - 1; i >= 0; i--)
            {
                if (!(rules[i] is Dictionary<string, object> rule))
                    continue;

                if (!rule.TryGetValue("domain", out var domainObj))
                    continue;
                if (!(domainObj is IList domains))
                    continue;

                var domainStrings = domains
                    .OfType<object>()
                    .Select(d => Convert.ToString(d) ?? string.Empty)
                    .ToList();

                var geositeItems = domainStrings
                    .Where(d => d.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (geositeItems.Count > 0)
                {
                    // Удаляем только geosite-элементы из domain-правила.
                    for (int j = domains.Count - 1; j >= 0; j--)
                    {
                        var value = Convert.ToString(domains[j]) ?? string.Empty;
                        if (value.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
                            domains.RemoveAt(j);
                    }

                    // Если после чистки доменов правило пустое, удаляем правило целиком.
                    if (domains.Count == 0)
                        rules.RemoveAt(i);

                    changed = true;
                }
            }

            return changed;
        }

        private void ForceRemoveGeositeTokensFromConfigText()
        {
            if (!File.Exists(ProxyConfigPath))
                return;

            try
            {
                var text = File.ReadAllText(ProxyConfigPath);
                var cleaned = text
                    .Replace("\"geosite:category-ru\",", string.Empty)
                    .Replace(",\"geosite:category-ru\"", string.Empty)
                    .Replace("\"geosite:category-ru\"", string.Empty);

                if (!string.Equals(text, cleaned, StringComparison.Ordinal))
                {
                    File.WriteAllText(ProxyConfigPath, cleaned);
                    AppendLog("proxy config.json дополнительно очищен от geosite:category-ru (text fallback).");
                }
            }
            catch (Exception ex)
            {
                AppendLog("Не удалось выполнить fallback-очистку geosite в config: " + ex.Message);
            }
        }

        private void EnsureProxyCoreFromWeb()
        {
            var singBoxExe = Path.Combine(ProxyRuntimeDir, "sing-box.exe");
            var xrayExe = Path.Combine(ProxyRuntimeDir, "xray.exe");
            if (File.Exists(singBoxExe) || File.Exists(xrayExe))
                return;

            try
            {
                Directory.CreateDirectory(ProxyRuntimeDir);
                var zipPath = Path.Combine(ProxyRuntimeDir, "xray-core.zip");
                var extractDir = Path.Combine(ProxyRuntimeDir, "xray_tmp");

                AppendLog("proxy-core не найден. Скачиваем xray-core...");
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "BotSuppConfigurator/1.0";
                    wc.DownloadFile(XrayDownloadUrl, zipPath);
                }

                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, true);
                Directory.CreateDirectory(extractDir);

                ZipFile.ExtractToDirectory(zipPath, extractDir);
                var extractedXray = Directory.GetFiles(extractDir, "xray.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(extractedXray) || !File.Exists(extractedXray))
                    throw new InvalidOperationException("xray.exe не найден в загруженном архиве.");

                File.Copy(extractedXray, xrayExe, true);
                AppendLog("xray-core скачан и подготовлен автоматически.");

                try
                {
                    File.Delete(zipPath);
                    Directory.Delete(extractDir, true);
                }
                catch
                {
                    // не критично
                }
            }
            catch (Exception ex)
            {
                AppendLog("Не удалось автоматически скачать proxy-core: " + ex.Message);
            }
        }

        private static bool WaitForLocalPort(string host, int port, int timeoutMs)
        {
            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalMilliseconds < timeoutMs)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        var ar = client.BeginConnect(host, port, null, null);
                        var success = ar.AsyncWaitHandle.WaitOne(300);
                        if (success && client.Connected)
                        {
                            client.EndConnect(ar);
                            return true;
                        }
                    }
                }
                catch
                {
                    // продолжаем ждать
                }

                Thread.Sleep(250);
            }

            return false;
        }

        private static bool IsLocalPortOpen(string host, int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var ar = client.BeginConnect(host, port, null, null);
                    var success = ar.AsyncWaitHandle.WaitOne(250);
                    if (!success)
                        return false;
                    client.EndConnect(ar);
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool EnsurePythonModuleAvailable(string pythonExe, string argsPrefix, string moduleName, string pipPackage)
        {
            if (CanImportPythonModule(pythonExe, argsPrefix, moduleName))
                return true;

            AppendLog($"Модуль '{moduleName}' не найден. Устанавливаем {pipPackage}...");

            if (RunPythonCommand(pythonExe, argsPrefix, $"-m pip install --no-warn-script-location {pipPackage}", 90000))
                return CanImportPythonModule(pythonExe, argsPrefix, moduleName);

            // Fallback для embedded runtime: установка в локальный site-packages.
            try
            {
                var pyDir = Path.GetDirectoryName(pythonExe) ?? string.Empty;
                var embeddedSite = Path.Combine(pyDir, "Lib", "site-packages");
                if (Directory.Exists(embeddedSite))
                {
                    var targetCmd = $"-m pip install --no-warn-script-location {pipPackage} --target \"{embeddedSite}\"";
                    if (RunPythonCommand(pythonExe, argsPrefix, targetCmd, 90000))
                        return CanImportPythonModule(pythonExe, argsPrefix, moduleName);
                }
            }
            catch
            {
                // fallback best-effort
            }

            return false;
        }

        private bool CanImportPythonModule(string pythonExe, string argsPrefix, string moduleName)
        {
            return RunPythonCommand(pythonExe, argsPrefix, $"-c \"import {moduleName}\"", 10000);
        }

        private bool RunPythonCommand(string pythonExe, string argsPrefix, string commandArgs, int timeoutMs)
        {
            try
            {
                var fullArgs = string.IsNullOrWhiteSpace(argsPrefix)
                    ? commandArgs
                    : $"{argsPrefix} {commandArgs}";

                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo
                    {
                        FileName = pythonExe,
                        Arguments = fullArgs,
                        WorkingDirectory = ProjectPath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    p.Start();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private void StopProxy()
        {
            if (_proxyProcess == null)
                return;

            try
            {
                if (!_proxyProcess.HasExited)
                {
                    _proxyProcess.Kill();
                    _proxyProcess.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка остановки прокси: " + ex.Message);
            }
            finally
            {
                _proxyProcess.Dispose();
                _proxyProcess = null;
                _currentProxyUrl = string.Empty;
                Dispatcher.Invoke(UpdateRuntimeStatusBadge);
            }
        }

        private async Task StopProxyAsync()
        {
            if (_proxyProcess == null)
                return;

            try
            {
                var proc = _proxyProcess;
                _proxyProcess = null;

                await Task.Run(() =>
                {
                    try
                    {
                        if (!proc.HasExited)
                            proc.Kill();
                        proc.WaitForExit(1500);
                    }
                    catch
                    {
                        // best effort
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                });
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка остановки прокси: " + ex.Message);
            }
            finally
            {
                Dispatcher.Invoke(UpdateRuntimeStatusBadge);
            }
        }

        private void SaveEnvButton_Click(object sender, RoutedEventArgs e) => SaveEnv();
        private void ReloadEnvButton_Click(object sender, RoutedEventArgs e) => LoadEnv();
        private void ReloadFaqButton_Click(object sender, RoutedEventArgs e) => LoadFaq();
        private void OpenProjectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(ProjectPath))
            {
                Process.Start("explorer.exe", ProjectPath);
            }
        }

        private string GetTokenValue()
        {
            return _isTokenVisible ? (TokenTextBox.Text ?? string.Empty) : (TokenPasswordBox.Password ?? string.Empty);
        }

        private void SetTokenValue(string value)
        {
            TokenPasswordBox.Password = value ?? string.Empty;
            TokenTextBox.Text = value ?? string.Empty;
        }

        private void ToggleTokenVisibilityButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTokenVisible)
            {
                TokenPasswordBox.Password = TokenTextBox.Text ?? string.Empty;
                TokenTextBox.Visibility = Visibility.Collapsed;
                TokenPasswordBox.Visibility = Visibility.Visible;
                ToggleTokenVisibilityButton.Content = "👁";
                _isTokenVisible = false;
            }
            else
            {
                TokenTextBox.Text = TokenPasswordBox.Password ?? string.Empty;
                TokenPasswordBox.Visibility = Visibility.Collapsed;
                TokenTextBox.Visibility = Visibility.Visible;
                ToggleTokenVisibilityButton.Content = "🙈";
                _isTokenVisible = true;
            }
        }

        private void AddFaqButton_Click(object sender, RoutedEventArgs e)
        {
            var item = new FaqItem
            {
                Key = $"new_{_faqItems.Count + 1}",
                Question = "Новый вопрос",
                Answer = "Новый ответ",
                IsProtected = false
            };
            _faqItems.Add(item);
            FaqListBox.SelectedItem = item;
            SyncEditorFromSelectedItem();
            SetStatus("Добавлена новая FAQ-запись.");
        }

        private void DeleteFaqButton_Click(object sender, RoutedEventArgs e)
        {
            if (FaqListBox.SelectedItem is FaqItem selected)
            {
                if (selected.IsProtected)
                {
                    SetStatus("Удаление запрещено: у записи включена защита. Снимите галочку и повторите удаление.");
                    return;
                }

                var result = MessageBox.Show(
                    $"Вы точно хотите удалить запись FAQ?\n\nКлюч: {selected.Key}\nВопрос: {selected.Question}",
                    "Подтверждение удаления",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );

                if (result != MessageBoxResult.Yes)
                {
                    SetStatus("Удаление отменено пользователем.");
                    return;
                }

                _faqItems.Remove(selected);
                SaveFaq(); // Сразу сохраняем в faq.json, чтобы запись не возвращалась после перезапуска WPF
                if (_faqItems.Count > 0)
                    FaqListBox.SelectedIndex = 0;
                SyncEditorFromSelectedItem();
                SetStatus("FAQ-запись удалена и сохранена.");
            }
            else
            {
                SetStatus("Выберите запись для удаления.");
            }
        }

        private void SaveFaqButton_Click(object sender, RoutedEventArgs e) => SaveFaq();
        private async void StartBotButton_Click(object sender, RoutedEventArgs e) => await StartBotAsync();

        private async void RestartBotButton_Click(object sender, RoutedEventArgs e)
        {
            await StopBotAsync();
            await StartBotAsync();
        }

        private async void StopBotButton_Click(object sender, RoutedEventArgs e) => await StopBotAsync();

        private void FaqListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SyncEditorFromSelectedItem();
        }

        private void FaqEnableFlowCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isSyncingFlowEditor || !(FaqListBox.SelectedItem is FaqItem selected))
                return;

            selected.IsFlowEnabled = FaqEnableFlowCheckBox.IsChecked == true;
            if (FaqEnableFlowCheckBox.IsChecked == true)
            {
                if (selected.Flow == null || selected.Flow.Steps.Count == 0)
                {
                    selected.Flow = new FaqFlow();
                    selected.Flow.Steps.Add(new FaqStep
                    {
                        Id = "Шаг 1",
                        Text = "Уточняющий шаг",
                        Transitions = new ObservableCollection<FaqTransition>
                        {
                            new FaqTransition { Caption = "Да", TargetStepId = "Шаг 2", Action = string.Empty },
                            new FaqTransition { Caption = "Нет", TargetStepId = string.Empty, Action = "operator" }
                        }
                    });
                    selected.Flow.Steps.Add(new FaqStep
                    {
                        Id = "Шаг 2",
                        Text = "Отлично, рады помочь.",
                        Transitions = new ObservableCollection<FaqTransition>
                        {
                            new FaqTransition { Caption = "В меню", TargetStepId = string.Empty, Action = "to_menu" }
                        }
                    });
                    selected.Flow.StartStepId = "Шаг 1";
                }
            }

            SyncFlowEditorFromSelectedItem(selected);
        }

        private void FlowStepsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SyncSelectedStepEditor();
        }

        private void FlowStartStepComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingFlowEditor || !(FaqListBox.SelectedItem is FaqItem selected) || selected.Flow == null)
                return;
            selected.Flow.StartStepId = Convert.ToString(FlowStartStepComboBox.SelectedItem) ?? string.Empty;
        }

        private void FlowStepEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingFlowEditor || !(FlowStepsListBox.SelectedItem is FaqStep step))
                return;

            step.Id = FlowStepIdTextBox.Text?.Trim() ?? string.Empty;
            step.Text = FlowStepTextTextBox.Text?.Trim() ?? string.Empty;
            RefreshFlowStepBindings();
        }

        private void FlowTransitionsDataGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(RefreshFlowStepBindings));
        }

        private void AddFlowStepButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(FaqListBox.SelectedItem is FaqItem selected) || FaqEnableFlowCheckBox.IsChecked != true)
                return;

            if (selected.Flow == null)
                selected.Flow = new FaqFlow();

            var idx = selected.Flow.Steps.Count + 1;
            var newStep = new FaqStep
            {
                Id = $"Шаг {idx}",
                Text = "Новый шаг",
                Transitions = new ObservableCollection<FaqTransition>()
            };
            selected.Flow.Steps.Add(newStep);
            RefreshFlowStepBindings();
            FlowStepsListBox.SelectedItem = newStep;
        }

        private void DeleteFlowStepButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(FaqListBox.SelectedItem is FaqItem selected) || selected.Flow == null || !(FlowStepsListBox.SelectedItem is FaqStep step))
                return;

            selected.Flow.Steps.Remove(step);
            foreach (var s in selected.Flow.Steps)
            {
                foreach (var t in s.Transitions.Where(t => string.Equals(t.TargetStepId, step.Id, StringComparison.OrdinalIgnoreCase)).ToList())
                    t.TargetStepId = string.Empty;
            }

            if (!selected.Flow.Steps.Any())
            {
                selected.Flow = null;
                FaqEnableFlowCheckBox.IsChecked = false;
                SyncFlowEditorFromSelectedItem(selected);
                return;
            }

            if (string.Equals(selected.Flow.StartStepId, step.Id, StringComparison.OrdinalIgnoreCase))
                selected.Flow.StartStepId = selected.Flow.Steps[0].Id;

            RefreshFlowStepBindings();
            FlowStepsListBox.SelectedIndex = 0;
        }

        private void AddFlowTransitionButton_Click(object sender, RoutedEventArgs e)
        {
            if (FlowStepsListBox.SelectedItem is FaqStep step)
                step.Transitions.Add(new FaqTransition { Caption = "Новый вариант", TargetStepId = string.Empty, Action = "to_menu" });
        }

        private void DeleteFlowTransitionButton_Click(object sender, RoutedEventArgs e)
        {
            if (FlowStepsListBox.SelectedItem is FaqStep step && FlowTransitionsDataGrid.SelectedItem is FaqTransition transition)
                step.Transitions.Remove(transition);
        }

        private void RefreshFlowStepBindings()
        {
            if (!(FaqListBox.SelectedItem is FaqItem selected) || selected.Flow == null)
                return;

            var currentStep = FlowStepsListBox.SelectedItem as FaqStep;
            var items = selected.Flow.Steps.Select(s => s.Id).ToList();
            FlowStartStepComboBox.ItemsSource = items;

            if (!items.Contains(selected.Flow.StartStepId))
                selected.Flow.StartStepId = items.FirstOrDefault() ?? string.Empty;

            FlowStartStepComboBox.SelectedItem = selected.Flow.StartStepId;
            FlowStepsListBox.Items.Refresh();

            if (currentStep != null)
                FlowStepsListBox.SelectedItem = selected.Flow.Steps.FirstOrDefault(s => ReferenceEquals(s, currentStep)) ?? selected.Flow.Steps.FirstOrDefault();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            try
            {
                if (_botProcess != null && !_botProcess.HasExited)
                    _botProcess.Kill();
            }
            catch { }
            try
            {
                if (_proxyProcess != null && !_proxyProcess.HasExited)
                    _proxyProcess.Kill();
            }
            catch { }
            base.OnClosing(e);
        }
    }
}
