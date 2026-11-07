using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace NumaControl
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--uninstall-cleanup")
                {
                    InstallMaintenance.Cleanup(ShellLinks.Executable, ShellLinks.StartupPath, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); return 0;
                }
                bool preview = args.Contains("--preview"), show = ShouldShow(args);
                string launchId = null;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--launch" && i + 1 < args.Length) { launchId = args[++i]; Guid id; if (!Guid.TryParseExact(launchId, "N", out id)) throw new ArgumentException(L10n.T("Некорректный идентификатор игры.")); }
                    else if (args[i] != "--preview" && args[i] != "--show" && args[i] != "--tray") throw new ArgumentException(L10n.T("Параметры: --show, --tray, --launch <ID>, --preview."));
                }
                Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                AppController controller = null;
                using (InstanceChannel channel = new InstanceChannel(preview ? "-preview" : "", command => application.Dispatcher.BeginInvoke(new Action(delegate { if (controller != null) controller.HandleCommand(command); }))))
                {
                    if (!channel.Primary) { channel.Send(launchId == null ? "show" : "launch " + launchId); return 0; }
                    controller = new AppController(application, preview);
                    channel.Start();
                    using (ExceptionHooks hooks = new ExceptionHooks(application, controller.Critical))
                    try
                    {
                        controller.Start();
                        if (show) controller.ShowWindow();
                        if (launchId != null) controller.Launch(launchId);
                        application.Run();
                    }
                    finally { controller.Dispose(); }
                }
                return Environment.ExitCode;
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, L10n.T("NUMA Control — ошибка запуска"), MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
        internal static bool ShouldShow(string[] args) { return args.Contains("--preview") || args.Contains("--show") || (!args.Contains("--tray") && !args.Contains("--launch")); }
    }

    internal sealed class GameRow : INotifyPropertyChanged
    {
        internal GameProfile Game;
        public ImageSource Icon { get; internal set; }
        public string Name { get { return Game.Name; } }
        public string Badge { get { return BadgeFor(Game); } }
        internal static string BadgeFor(GameProfile game) { return game.ProcessName == "cs2" ? "CS2" : game.ProcessName == "dota2" ? "D2" : game.Name.Substring(0, Math.Min(2, game.Name.Length)).ToUpperInvariant(); }
        public Brush Brush { get { return Themes.BadgeBrush(Game.Color); } }
        private string status = L10n.T("Проверка…");
        private Brush statusBrush = Ui.Brush("#949DB1");
        public string Status { get { return status; } }
        public Brush StatusBrush { get { return statusBrush; } }
        public event PropertyChangedEventHandler PropertyChanged;
        internal void Update(GameStatus value)
        {
            status = value.State;
            statusBrush = Ui.Brush(value.Error ? "#E5B578" : value.Running > 0 ? "#8ED9B7" : "#949DB1");
            if (PropertyChanged != null) { PropertyChanged(this, new PropertyChangedEventArgs("Status")); PropertyChanged(this, new PropertyChangedEventArgs("StatusBrush")); }
        }
    }

    internal static class Ui
    {
        internal static Brush Brush(string color) { return Themes.Brush(color); }
        internal static T Load<T>(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NumaControl." + name))
            {
                if (stream == null) throw new FileNotFoundException(name);
                if (name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) using (var reader = new StreamReader(stream)) return (T)XamlReader.Parse(Themes.Transform(L10n.Xaml(reader.ReadToEnd())).Replace("assembly=UI_ASSEMBLY", "assembly=" + Assembly.GetExecutingAssembly().GetName().Name));
                return (T)XamlReader.Load(stream);
            }
        }
        internal static void DarkTitle(Window window)
        {
            if (window.Owner != null) window.Icon = window.Owner.Icon;
            window.SourceInitialized += delegate
            {
                int dark = Themes.Current == "win11-light" || Themes.Current == "ami-bios" ? 0 : 1;
                Native.DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref dark, sizeof(int));
                if (Themes.Current == "ami-bios")
                {
                    int square = 1; Native.DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 33, ref square, sizeof(int));
                    var color = (Color)ColorConverter.ConvertFromString(Themes.Color("Sidebar")); int caption = color.R | (color.G << 8) | (color.B << 16);
                    Native.DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 35, ref caption, sizeof(int));
                }
            };
        }
        internal static TextBlock Text(string value, int size, string color)
        {
            return new TextBlock { Text = value, FontSize = Themes.TextSize(size), FontFamily = Themes.Font, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
        }
        internal static Button Button(string label, Action action, bool primary, string id = null)
        {
            Button button = new Button { Content = label, FontFamily = Themes.Font, Margin = new Thickness(0, 0, 8, 8) };
            if (primary) button.Style = (Style)Application.Current.FindResource("Primary");
            if (id != null) { button.Name = id; System.Windows.Automation.AutomationProperties.SetAutomationId(button, id); }
            button.Click += delegate { action(); };
            return button;
        }
        internal static void FitScreen(Window window)
        {
            double width = Math.Max(600, SystemParameters.WorkArea.Width - 20), height = Math.Max(420, SystemParameters.WorkArea.Height - 20);
            window.MinWidth = Math.Min(window.MinWidth, width); window.MinHeight = Math.Min(window.MinHeight, height);
            window.Width = Math.Min(window.Width, width); window.Height = Math.Min(window.Height, height);
        }
        internal static string CpuRanges(ulong mask)
        {
            List<string> ranges = new List<string>();
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) == 0) continue;
                int start = i; while (i < 63 && (mask & (1UL << (i + 1))) != 0) i++;
                ranges.Add(start == i ? start.ToString() : start + "–" + i);
            }
            return string.Join(", ", ranges);
        }
    }

    internal sealed class AppController : IDisposable
    {
        private readonly Application app;
        internal readonly bool Preview;
        private readonly bool isolated;
        internal readonly Topology Topology;
        internal readonly ProfileStore Store;
        internal readonly AppLog Log;
        internal readonly AppEnvironment Platform;
        internal readonly BugReports Reports;
        internal readonly GameIcons Icons;
        internal readonly bool HardwareChanged;
        internal SelfDiagnostics Health;
        internal Configuration Config;
        internal WatchEngine Engine;
        private Forms.NotifyIcon tray;
        private Forms.ToolStripMenuItem pauseMenu, startupMenu;
        private System.Drawing.Icon trayIcon;
        private Window window;
        private bool resources, rebuilding;
        private ResourceDictionary themeResources;
        private volatile bool closing;
        private volatile bool diagnosticsVisible;
        private Timer emergencyExit;
        private DispatcherTimer toastTimer;
        internal const string IdleFooter = "";
        private GameProfile selected;
        private readonly Dictionary<int, ToggleButton> cpuButtons = new Dictionary<int, ToggleButton>();
        private ulong draft;
        private string view = "library";
        private string lastSelectedId;
        private string startupWarning;
        private readonly Dictionary<string, TextBlock> diagnosticLabels = new Dictionary<string, TextBlock>();
        private readonly Dictionary<string, Border> diagnosticDots = new Dictionary<string, Border>();

        internal AppController(Application application, bool preview, string dataDirectory = null, AppEnvironment environment = null)
        {
            app = application; Preview = preview; isolated = dataDirectory != null;
            Platform = environment ?? new AppEnvironment(dataDirectory);
            Topology = NumaControl.Topology.Detect();
            Store = new ProfileStore(dataDirectory ?? (preview ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "preview") : LocalData.Resolve()));
            Log = new AppLog(Store.DirectoryPath);
            Icons = new GameIcons(Store.DirectoryPath);
            if (!preview)
                try { HardwareChanged = CpuInfoStore.Save(Store.DirectoryPath, Topology); }
                catch (IOException error) { Log.Write(L10n.T("Сведения о CPU не сохранены: ") + error.Message); }
                catch (UnauthorizedAccessException error) { Log.Write(L10n.T("Сведения о CPU не сохранены: ") + error.Message); }
            Reports = new BugReports(Store.DirectoryPath, Log);
            if (preview) Config = Configuration.Defaults(Topology);
            else
            {
                try
                {
                    bool first = !File.Exists(Store.FilePath);
                    Config = Store.Load(Topology);
                    L10n.Set(Config.Language);
                    if (first)
                    {
                        foreach (LegacyKit kit in Platform.Legacy)
                            if (kit.Running || File.Exists(kit.ShortcutPath))
                                foreach (GameProfile g in Config.Games.Where(g => string.Equals(g.ProcessName, kit.ProcessName, StringComparison.OrdinalIgnoreCase))) g.Enabled = false;
                        Store.Save(Config);
                    }
                }
                catch (Exception error)
                {
                    Config = Configuration.Defaults(Topology);
                    bool recovered = false;
                    try
                    {
                        Store.PreserveRecoveryFiles();
                        if (File.Exists(Store.FilePath + ".bak")) { Config = ProfileStore.Read(Store.FilePath + ".bak", Topology, false); recovered = true; }
                    }
                    catch (Exception recoveryError) { Log.Write(L10n.T("Восстановление: ") + recoveryError.Message); }
                    Config.Paused = true;
                    startupWarning = L10n.T("Не удалось прочитать profiles.json: ") + error.Message + "\n" + (recovered ? L10n.T("Загружена резервная копия. Проверьте профили и нажмите «Продолжить».") : L10n.T("Наблюдение приостановлено. Восстановите профили через импорт в настройках.")) + L10n.T("\nКопии доступных файлов сохраняются рядом с настройками с отметкой времени.");
                }
                if (HardwareChanged) { Config.Paused = true; Store.Save(Config); Log.Write(L10n.T("Изменился процессор или топология. Наблюдение на паузе до проверки привязок.")); }
                Engine = new WatchEngine(Config, Topology, Log, true);
                Engine.Changed += delegate { if (!closing) app.Dispatcher.BeginInvoke(new Action(UpdateStatus)); };
            }
            if (!preview && (Reports.PreviousFailure || new BugReports(Reports.FallbackDataDirectory, Log).PreviousFailure || new BugReports(Reports.EmergencyDataDirectory, Log).PreviousFailure))
            {
                Config.Paused = true;
                if (Engine != null) Engine.Update(Config);
                startupWarning = (startupWarning == null ? "" : startupWarning + "\n\n") + L10n.T("Предыдущее завершение вызвала самодиагностика. Наблюдение на паузе. Отчёт сохранён в папке bug-reports. Проверьте его перед продолжением.");
            }
            L10n.Set(Config.Language); Themes.Set(Config.Theme);
        }
        internal void Start()
        {
            using (var stream = new MemoryStream(Themes.IconBytes())) using (var icon = new System.Drawing.Icon(stream)) trayIcon = (System.Drawing.Icon)icon.Clone();
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Items.Add(L10n.T("Открыть NUMA Control"), null, delegate { Guard(ShowWindow); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            pauseMenu = new Forms.ToolStripMenuItem(L10n.T("Пауза наблюдения"), null, delegate { TogglePause(); }); menu.Items.Add(pauseMenu);
            startupMenu = new Forms.ToolStripMenuItem(L10n.T("Автозапуск Windows"), null, delegate { ChangeAutostart(!File.Exists(Platform.StartupPath)); }); menu.Items.Add(startupMenu);
            menu.Items.Add(L10n.T("Самодиагностика"), null, delegate { Guard(delegate { ShowWindow(); if (ConfirmDraft()) { BuildDiagnostics(); ShowView("diagnostics"); } }); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(L10n.T("Выход"), null, delegate { Guard(Exit); });
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = Preview ? L10n.T("NUMA Control — просмотр интерфейса") : L10n.T("NUMA Control — наблюдение за играми"), ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { Guard(ShowWindow); };
            menu.Opening += delegate { pauseMenu.Checked = Config.Paused; startupMenu.Checked = !Preview && File.Exists(Platform.StartupPath); startupMenu.Enabled = !Preview; };
            TrayStyle.Apply(menu);
            UpdateThemeIcons();
            if (Engine != null) { Engine.Start(); Log.Write(L10n.T("NUMA Control запущен · ") + Topology.Count + L10n.T(" ЦП · один наблюдатель")); }
            Health = new SelfDiagnostics(app.Dispatcher, Engine, Reports, Log, DiagnosticContext, EmergencyStop, Platform.DiagnosticLimits, ArmEmergencyExit);
            Health.Sampled += delegate { if (diagnosticsVisible && !closing && !app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { if (window != null && window.IsVisible && view == "diagnostics") UpdateDiagnostics(); })); };
            Health.Start();
            if (startupWarning != null) { ShowWindow(); Platform.Interaction.Ask(window, startupWarning, L10n.T("Восстановление и диагностика"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        private T Find<T>(string name) where T : class { return (T)window.FindName(name); }
        private void Click(string name, Action action) { Find<Button>(name).Click += delegate { Guard(action); }; }
        internal void Guard(Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                if (!(error is ArgumentException || error is InvalidDataException || error is InvalidOperationException || error is IOException || error is UnauthorizedAccessException || error is System.ComponentModel.Win32Exception || error is System.Runtime.InteropServices.COMException || error is NotSupportedException)) { Critical("unexpected-action-exception", error); return; }
                Log.Write(L10n.T("Ошибка действия: ") + error.Message); if (window == null || !window.IsVisible) ShowWindow();
                Platform.Interaction.Ask(window, error.Message, L10n.T("Не удалось выполнить действие"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        internal void ShowWindow()
        {
            if (window == null)
            {
                if (!resources) { themeResources = Ui.Load<ResourceDictionary>("Theme.xaml"); app.Resources.MergedDictionaries.Add(themeResources); resources = true; }
                window = Ui.Load<Window>("MainWindow.xaml");
                Find<TextBlock>("VersionLabel").Text = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                Ui.FitScreen(window);
                if (Preview) window.Title += L10n.T(" — предпросмотр");
                else if (isolated) window.Title += L10n.T(" — тест");
                using (var icon = new MemoryStream(Themes.IconBytes())) window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(icon, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                Ui.DarkTitle(window);
                window.Closing += delegate(object sender, CancelEventArgs e)
                {
                    if (closing) return;
                    if (!ConfirmDraft()) { e.Cancel = true; return; }
                };
                window.Closed += delegate
                {
                    if (toastTimer != null) toastTimer.Stop();
                    lastSelectedId = selected == null ? null : selected.Id;
                    window = null; selected = null; cpuButtons.Clear(); diagnosticLabels.Clear(); diagnosticDots.Clear();
                    diagnosticsVisible = false;
                    if (Health != null) Health.SetWindowVisible(false);
                    app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { GC.Collect(2, GCCollectionMode.Optimized, false); }));
                };
                window.StateChanged += delegate
                {
                    if (window != null && window.WindowState == WindowState.Minimized)
                    {
                        if (selected != null && draft != selected.CpuMask) window.Hide();
                        else window.Close();
                    }
                };
                window.IsVisibleChanged += delegate(object sender, DependencyPropertyChangedEventArgs e) { diagnosticsVisible = (bool)e.NewValue && view == "diagnostics"; if (Health != null) Health.SetWindowVisible((bool)e.NewValue); if ((bool)e.NewValue) UpdateStatus(); };
                Find<ListBox>("GameList").SelectionChanged += delegate
                {
                    if (rebuilding) return;
                    GameRow row = Find<ListBox>("GameList").SelectedItem as GameRow;
                    if (row != null && (selected == null || row.Game.Id != selected.Id))
                    {
                        if (!ConfirmDraft()) { rebuilding = true; Find<ListBox>("GameList").SelectedItem = ((List<GameRow>)Find<ListBox>("GameList").ItemsSource).Find(g => g.Game.Id == selected.Id); rebuilding = false; return; }
                        selected = row.Game; draft = selected.CpuMask; ShowView("library"); RenderProfile();
                    }
                };
                Click("AddGame", delegate { if (ConfirmDraft()) Edit(null); });
                Click("EditGame", delegate { if (ConfirmDraft()) Edit(selected); });
                Click("LaunchGame", delegate { if (ConfirmDraft() && selected != null) Launch(selected.Id); });
                Click("DesktopShortcut", delegate
                {
                    if (Preview) { Toast(L10n.T("В предпросмотре создание ярлыков отключено.")); return; }
                    Save(Config.Copy());
                    ShellLinks.Desktop(selected, Platform.DesktopDirectory); Toast(L10n.T("Ярлык для «") + selected.Name + L10n.T("» создан на рабочем столе."));
                });
                Click("SaveMask", SaveDraft);
                Click("PresetAll", delegate { SetDraft(Topology.AllMask); });
                Click("PresetEdges", delegate { SetDraft(Topology.WithoutEdgeCores()); });
                Click("PresetPhysical", delegate { SetDraft(Topology.OnePerCore()); });
                Click("PauseAll", TogglePause);
                Click("LibraryNav", delegate { if (ConfirmDraft()) ShowView("library"); });
                Click("SettingsNav", delegate { if (ConfirmDraft()) { BuildSettings(); ShowView("settings"); } });
                Click("LogNav", delegate { if (ConfirmDraft()) { RefreshLog(); ShowView("log"); } });
                Click("DiagnosticsNav", delegate { if (ConfirmDraft()) { BuildDiagnostics(); ShowView("diagnostics"); } });
                Click("RefreshLog", RefreshLog);
                Find<CheckBox>("EnabledToggle").Click += delegate { Guard(delegate
                {
                    if (selected == null) return;
                    Configuration c = Config.Copy(); c.Games.Find(g => g.Id == selected.Id).Enabled = Find<CheckBox>("EnabledToggle").IsChecked == true;
                    try { Save(c); selected = Config.Games.Find(g => g.Id == selected.Id); RebuildLibrary(selected.Id, false); }
                    catch { Find<CheckBox>("EnabledToggle").IsChecked = selected.Enabled; throw; }
                }); };
                Find<TextBlock>("CpuMetric").Text = Topology.Cores.Count + L10n.T(" ядер · ") + Topology.Count + L10n.T(" потоков");
                Find<TextBlock>("CpuHint").Text = Topology.Name.Replace("(R)", "").Replace("(TM)", "").Replace("CPU ", "").Trim();
                Find<TextBlock>("CpuHint").ToolTip = Topology.Name + L10n.T("\nОпределено при запуске · групп: ") + Topology.Groups;
                Find<TextBlock>("ProcessorName").Text = Topology.Name;
                Find<TextBlock>("ProcessorName").ToolTip = Topology.Name;
                RebuildLibrary(lastSelectedId, true);
                if (view == "settings") BuildSettings();
                if (view == "log") RefreshLog();
                if (view == "diagnostics") BuildDiagnostics();
                ShowView(view);
                if (!Platform.ActivateWindows) { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = SystemParameters.VirtualScreenLeft - 10000; window.Top = 0; window.ShowInTaskbar = false; window.ShowActivated = false; }
            }
            window.Show(); window.WindowState = WindowState.Normal;
            if (Platform.ActivateWindows) { Native.ShowWindow(new WindowInteropHelper(window).Handle, 9); window.Activate(); Native.SetForegroundWindow(new WindowInteropHelper(window).Handle); }
            UpdateStatus(); CheckLegacy();
        }
        private void RebuildLibrary(string id, bool render)
        {
            rebuilding = true;
            List<GameRow> rows = Config.Games.Select(g => new GameRow { Game = g, Icon = Icons.Get(g) }).ToList();
            Find<ListBox>("GameList").ItemsSource = rows;
            GameRow row = rows.Find(r => r.Game.Id == id) ?? rows.FirstOrDefault();
            Find<ListBox>("GameList").SelectedItem = row;
            selected = row == null ? null : row.Game;
            if (render) { draft = selected == null ? 0 : selected.CpuMask; RenderProfile(); }
            Find<TextBlock>("GameCount").Text = Config.Games.Count.ToString();
            rebuilding = false; UpdateStatus();
        }
        private bool ConfirmDraft()
        {
            if (selected == null || draft == selected.CpuMask) return true;
            MessageBoxResult answer = Platform.Interaction.Ask(window, L10n.T("Сохранить изменения привязки для «") + selected.Name + "»?", L10n.T("Несохранённые изменения"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes) { try { SaveDraft(); } catch (Exception e) { Platform.Interaction.Ask(window, e.Message, L10n.T("Не удалось сохранить"), MessageBoxButton.OK, MessageBoxImage.Warning); return false; } }
            else { draft = selected.CpuMask; SetDraft(draft); }
            return true;
        }
        private void RenderProfile()
        {
            Find<StackPanel>("ProfilePanel").Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
            Find<Border>("EmptyState").Visibility = selected == null ? Visibility.Visible : Visibility.Collapsed;
            if (selected == null) return;
            Find<TextBlock>("SelectedName").Text = selected.Name;
            Find<TextBlock>("SelectedName").ToolTip = selected.Name;
            var icon = Icons.Get(selected); Find<Image>("SelectedGameIcon").Source = icon;
            Find<TextBlock>("SelectedGameBadge").Text = GameRow.BadgeFor(selected);
            Find<TextBlock>("SelectedGameBadge").Foreground = Themes.BadgeBrush(selected.Color);
            Find<TextBlock>("SelectedGameBadge").Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("SelectedProcess").Text = selected.ProcessName + ".exe" + (string.IsNullOrWhiteSpace(selected.ExePath) ? L10n.T(" · по имени процесса") : L10n.T(" · точный путь EXE"));
            Find<TextBlock>("SelectedProcess").ToolTip = selected.ExePath;
            Border cpuModeInfo = Find<Border>("CpuModeInfo");
            var cpuModeTip = (ToolTip)cpuModeInfo.ToolTip;
            string cpuMode = L10n.T(CpuSets.IsUsedForProcess(selected.ProcessName) ? "Режим профиля · CPU Sets" : "Режим профиля · жёсткая маска");
            cpuModeTip.DataContext = cpuMode;
            System.Windows.Automation.AutomationProperties.SetName(cpuModeInfo, cpuMode);
            System.Windows.Automation.AutomationProperties.SetHelpText(cpuModeInfo, L10n.T("Жёсткая маска ограничивает, на каких ЦП процесс может выполняться.") + " " +
                L10n.T("CPU Sets задаёт набор ЦП по умолчанию для потоков без собственного назначения. В Диспетчере задач при этом могут отображаться все ЦП."));
            Find<CheckBox>("EnabledToggle").IsChecked = selected.Enabled;
            WrapPanel map = Find<WrapPanel>("CpuMap"); map.Children.Clear(); cpuButtons.Clear();
            bool hybrid = Topology.Cores.Select(c => c.Efficiency).Distinct().Count() > 1;
            foreach (CpuCore core in Topology.Cores.Where(c => Topology.Supported))
            {
                StackPanel group = new StackPanel { Margin = new Thickness(3, 0, 7, 10) };
                TextBlock label = Ui.Text(L10n.T("ЯДРО ") + core.Index + (hybrid ? L10n.T(" · кл. ") + core.Efficiency : ""), 9, "#7F899D"); label.Margin = new Thickness(4, 0, 0, 6); group.Children.Add(label);
                StackPanel cpus = new StackPanel { Orientation = Orientation.Horizontal };
                for (int cpu = 0; cpu < 64; cpu++)
                {
                    if ((core.Mask & (1UL << cpu)) == 0) continue;
                    int number = cpu;
                    ToggleButton toggle = new ToggleButton { Content = cpu.ToString(), IsChecked = (draft & (1UL << cpu)) != 0, Style = (Style)app.FindResource("CpuTile"), ToolTip = L10n.T("ЦП ") + cpu + L10n.T(" · физическое ядро ") + core.Index };
                    System.Windows.Automation.AutomationProperties.SetName(toggle, L10n.T("Логический процессор ") + cpu);
                    toggle.Click += delegate { if (toggle.IsChecked == true) draft |= 1UL << number; else draft &= ~(1UL << number); UpdateDraftLabel(); };
                    cpuButtons.Add(cpu, toggle); cpus.Children.Add(toggle);
                }
                group.Children.Add(cpus); map.Children.Add(group);
            }
            Find<Button>("PresetAll").IsEnabled = Find<Button>("PresetEdges").IsEnabled = Find<Button>("PresetPhysical").IsEnabled = Topology.Supported;
            UpdateDraftLabel(); UpdateStatus();
        }
        private void SetDraft(ulong mask)
        {
            draft = mask;
            foreach (var p in cpuButtons) p.Value.IsChecked = (mask & (1UL << p.Key)) != 0;
            UpdateDraftLabel();
        }
        private void UpdateDraftLabel()
        {
            Find<TextBlock>("MaskLabel").Text = "0x" + draft.ToString("X");
            Find<TextBlock>("SelectionHint").Text = L10n.T("Выбрано ") + NumaControl.Topology.Bits(draft) + L10n.T(" из ") + Topology.Count + L10n.T(" ЦП") + (selected != null && draft != selected.CpuMask ? L10n.T(" · не сохранено") : L10n.T(" · сохранено"));
            Find<Button>("SaveMask").IsEnabled = Topology.Supported && draft != 0 && selected != null && draft != selected.CpuMask;
        }
        private void SaveDraft()
        {
            if (selected == null || draft == 0) throw new InvalidOperationException(L10n.T("Выберите хотя бы один логический ЦП."));
            Configuration c = Config.Copy(); c.Games.Find(g => g.Id == selected.Id).Mask = draft.ToString("X");
            Save(c); selected = Config.Games.Find(g => g.Id == selected.Id); RebuildLibrary(selected.Id, false); UpdateDraftLabel(); Toast(L10n.T("Привязка сохранена. Наблюдатель применит её при ближайшей проверке."));
        }
        internal void Save(Configuration next)
        {
            // Other profiles may have been brought from a different CPU. The engine reports each incompatible mask separately.
            next.Validate(Topology, false);
            if (!Preview) Store.Save(next);
            if (!next.Paused && Config.Paused)
            {
                try { Reports.ClearFailure(); new BugReports(Reports.FallbackDataDirectory, Log).ClearFailure(); new BugReports(Reports.EmergencyDataDirectory, Log).ClearFailure(); }
                catch (IOException error) { Log.Write(L10n.T("Маркер аварии: ") + error.Message); }
                catch (UnauthorizedAccessException error) { Log.Write(L10n.T("Маркер аварии: ") + error.Message); }
            }
            Config = next;
            if (Engine != null) Engine.Update(Config);
            if (window != null && window.IsVisible) UpdateStatus();
        }
        private void UpdateStatus()
        {
            if (tray != null) tray.Text = Preview ? L10n.T("NUMA Control — предпросмотр") : Config.Paused ? L10n.T("NUMA Control — наблюдение приостановлено") : L10n.T("NUMA Control — наблюдение активно");
            if (window == null || !window.IsVisible) return;
            var statuses = Engine == null ? new Dictionary<string, GameStatus>() : Engine.Snapshot();
            int enabled = Config.Games.Count(g => g.Enabled), active = statuses.Values.Sum(s => s.Applied);
            Find<TextBlock>("ObserverMetric").Text = (Config.Paused ? 0 : enabled) + " / " + Config.Games.Count;
            Find<TextBlock>("ObserverHint").Text = Preview ? L10n.T("предпросмотр · без применения") : active > 0 ? L10n.T("процессов с правилом: ") + active : L10n.T("в игре: 0 · ожидают запуск");
            Find<TextBlock>("IntervalHint").Text = (Config.IdleIntervalMs / 1000.0).ToString("0.#") + L10n.T(" с в ожидании · ") + (Config.ActiveIntervalMs / 1000.0).ToString("0.#") + L10n.T(" с в игре");
            Find<TextBlock>("ObserverHint").ToolTip = Find<TextBlock>("IntervalHint").Text;
            AppAffinityState affinity = Engine == null ? null : Engine.AppAffinity;
            Find<TextBlock>("AppCoreMetric").Text = affinity == null || affinity.ActiveGames == 0 ? L10n.T("Автовыбор") : affinity.Fallback ? L10n.T("Общее ядро") : L10n.T("Отдельные ядра");
            Find<TextBlock>("AppCoreHint").Text = affinity == null ? L10n.T("Подбор при запуске игры") : affinity.ActiveGames == 0 ? L10n.T("Игра не запущена") : affinity.Fallback ? L10n.T("Свободных ядер нет") : NumaControl.Topology.Bits(affinity.Mask) + L10n.T(" потоков вне игры");
            Find<TextBlock>("AppCoreHint").ToolTip = affinity == null ? "" : affinity.Detail + L10n.T("\nЛогические ЦП: ") + SelfAffinity.CpuList(affinity.Mask);
            Find<TextBlock>("EngineLabel").Text = Preview ? (Themes.Current == "ami-bios" ? "□" : "○") + L10n.T("  Предпросмотр") : Themes.Indicator + (Config.Paused ? L10n.T("  Наблюдение на паузе") : !Topology.Supported ? L10n.T("  Проверьте систему") : L10n.T("  Наблюдение активно"));
            Find<TextBlock>("EngineLabel").Foreground = Ui.Brush(Themes.Color(Config.Paused || !Topology.Supported ? "Warning" : "Success"));
            Find<Border>("EngineBadge").Background = Ui.Brush(Themes.Color(Config.Paused || !Topology.Supported ? "WarningBg" : "SuccessBg"));
            Find<Button>("PauseAll").Content = Config.Paused ? L10n.T("▷  Продолжить") : L10n.T("Ⅱ  Пауза");
            foreach (GameRow row in (List<GameRow>)Find<ListBox>("GameList").ItemsSource)
            {
                GameStatus status;
                if (!statuses.TryGetValue(row.Game.Id, out status)) status = new GameStatus { State = Preview ? L10n.T("Предпросмотр") : !row.Game.Enabled ? L10n.T("Выключен") : Config.Paused ? L10n.T("Общая пауза") : L10n.T("Проверка…"), Detail = Preview ? L10n.T("Настройки не применяются к процессам в режиме предпросмотра.") : L10n.T("Ожидается проверка наблюдателя.") };
                row.Update(status);
                if (selected != null && row.Game.Id == selected.Id)
                {
                    Find<TextBlock>("LiveState").Text = status.State;
                    Find<TextBlock>("LiveState").Foreground = Ui.Brush(status.Error ? "#E5B578" : "#9DD8BF");
                    Find<TextBlock>("LiveDetail").Text = status.Detail;
                }
            }
        }
        private void ShowView(string target)
        {
            view = target;
            diagnosticsVisible = target == "diagnostics" && window.IsVisible;
            Find<ScrollViewer>("LibraryView").Visibility = target == "library" ? Visibility.Visible : Visibility.Collapsed;
            Find<ScrollViewer>("SettingsView").Visibility = target == "settings" ? Visibility.Visible : Visibility.Collapsed;
            Find<Grid>("LogView").Visibility = target == "log" ? Visibility.Visible : Visibility.Collapsed;
            Find<ScrollViewer>("DiagnosticsView").Visibility = target == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("PageTitle").Text = target == "library" ? L10n.T("Твоя игра. Твои ядра.") : target == "settings" ? L10n.T("Всё под контролем.") : target == "diagnostics" ? L10n.T("Самодиагностика") : L10n.T("Журнал событий");
            Find<TextBlock>("PageSubtitle").Text = target == "library" ? L10n.T("Управляй привязкой игр — остальное под контролем.") : target == "settings" ? L10n.T("Фоновая работа, автозапуск и твои данные.") : target == "diagnostics" ? L10n.T("Ресурсы приложения, отклик и защита от сбоев.") : L10n.T("Состояния наблюдателей, применение правил и диагностика.");
            foreach (string nav in new[] { "LibraryNav", "SettingsNav", "LogNav", "DiagnosticsNav" }) Find<Button>(nav).Background = Ui.Brush((nav == "LibraryNav" && target == "library") || (nav == "SettingsNav" && target == "settings") || (nav == "LogNav" && target == "log") || (nav == "DiagnosticsNav" && target == "diagnostics") ? "#292638" : "#00000000");
        }
        private void CheckLegacy()
        {
            var old = Platform.Legacy.Where(k => k.Running || File.Exists(k.ShortcutPath)).ToList();
            Find<Border>("LegacyBanner").Visibility = old.Count > 0 || !Topology.Supported || HardwareChanged ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("LegacyText").Text = !Topology.Supported ? Topology.Error : HardwareChanged ? L10n.T("Обнаружен другой процессор. Проверьте привязки игр и возобновите наблюдение.") : L10n.T("Обнаружен другой наблюдатель: ") + string.Join(", ", old.Select(k => k.Name)) + L10n.T(". В настройках доступно отключение предыдущего наблюдения.");
        }
        private void TogglePause() { Guard(delegate { Configuration next = Config.Copy(); next.Paused = !next.Paused; Save(next); Toast(next.Paused ? L10n.T("Наблюдение приостановлено. Назначенные маски сохраняются.") : L10n.T("Наблюдение продолжено.")); }); }
        private void ChangeAutostart(bool enable)
        {
            Guard(delegate { if (Preview) return; ShellLinks.Autostart(enable, Platform.StartupPath); Log.Write(L10n.T("Автозапуск ") + (enable ? L10n.T("включён") : L10n.T("выключен"))); if (view == "settings" && window != null) BuildSettings(); Toast(enable ? L10n.T("Автозапуск включён. Сохраните приложение в этой папке.") : L10n.T("Автозапуск выключен.")); });
        }
        internal void Launch(string id)
        {
            Guard(delegate
            {
                if (Preview) { Toast(L10n.T("В предпросмотре запуск игр отключён.")); return; }
                GameProfile game = Config.Games.Find(g => g.Id == id);
                if (game == null) throw new InvalidOperationException(L10n.T("Этот профиль удалён. Создайте новый ярлык для нужной игры."));
                if (!Topology.Supported || (game.CpuMask & ~Topology.AllMask) != 0) throw new InvalidOperationException(L10n.T("Сначала выберите доступные ЦП в профиле игры."));
                Configuration c = Config.Copy(); c.Paused = false; c.Games.Find(g => g.Id == id).Enabled = true;
                Save(c); GameLauncher.Launch(game, Topology.AllMask); if (Engine != null) Engine.Refresh();
                if (window != null) RebuildLibrary(selected == null ? id : selected.Id, false);
                Toast(L10n.T("Наблюдатель «") + game.Name + L10n.T("» включён. Запрос запуска отправлен."));
            });
        }
        internal void HandleCommand(string command) { if (command == "show") ShowWindow(); else if (command.StartsWith("launch ", StringComparison.Ordinal)) Launch(command.Substring(7)); }
        private void Toast(string text)
        {
            if (window == null) return;
            Find<TextBlock>("Toast").Text = text; Find<TextBlock>("Toast").ToolTip = text;
            if (toastTimer == null)
            {
                toastTimer = new DispatcherTimer(DispatcherPriority.Background, app.Dispatcher);
                toastTimer.Tick += delegate { toastTimer.Stop(); if (window != null) { Find<TextBlock>("Toast").Text = IdleFooter; Find<TextBlock>("Toast").ToolTip = null; } };
            }
            toastTimer.Stop(); toastTimer.Interval = Platform.ToastDuration; toastTimer.Start();
        }
        private void RefreshLog() { Find<TextBox>("LogText").Text = Preview ? L10n.T("Режим предпросмотра. Привязка процессов не изменяется.") : Log.Tail(); }
        private void Edit(GameProfile original)
        {
            ProfileEditor editor = new ProfileEditor(window, original, Topology, Config, Platform.Interaction, delegate(GameProfile edited, bool delete)
            {
                Configuration c = Config.Copy();
                if (delete) c.Games.RemoveAll(g => g.Id == original.Id);
                else if (original == null) c.Games.Add(edited);
                else c.Games[c.Games.FindIndex(g => g.Id == original.Id)] = edited;
                Save(c);
            });
            if (Platform.Interaction.Edit(editor) != true) return;
            RebuildLibrary(editor.DeleteRequested ? null : editor.Result.Id, true);
        }
        private void Exit()
        {
            if (window != null && !ConfirmDraft()) return;
            closing = true; if (window != null) window.Close(); app.Shutdown();
        }
        public void Dispose()
        {
            closing = true;
            if (toastTimer != null) toastTimer.Stop();
            if (Health != null) Health.Dispose();
            if (Engine != null) Engine.Dispose();
            if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
            if (trayIcon != null) trayIcon.Dispose();
            if (emergencyExit != null) emergencyExit.Dispose();
        }
        private object DiagnosticContext()
        {
            Configuration current = Config;
            AppAffinityState affinity = Engine == null ? null : Engine.AppAffinity;
            return new { Session = Health == null ? null : Health.SessionId, ProfileCount = current.Games.Count, EnabledProfiles = current.Games.Count(g => g.Enabled), Paused = current.Paused, Preview = Preview, IsolatedTest = isolated, LogicalProcessors = Topology.Count, PhysicalCores = Topology.Cores.Count, Groups = Topology.Groups, View = view, AppCpuMask = affinity == null ? null : affinity.Mask.ToString("X"), GamesCpuMask = affinity == null ? null : affinity.GamesMask.ToString("X"), LastCoreFallback = affinity != null && affinity.Fallback };
        }
        private void ArmEmergencyExit()
        {
            closing = true;
            if (Platform.ExitOnFatal) emergencyExit = new Timer(delegate { Environment.Exit(70); }, null, 8000, Timeout.Infinite);
        }
        private void EmergencyStop(string reason)
        {
            closing = true;
            if (Platform.FatalObserved != null) Platform.FatalObserved(reason);
            if (!Platform.ExitOnFatal) return;
            Environment.ExitCode = 70;
            if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate { app.Shutdown(70); }));
        }
        internal void Critical(string reason, Exception error)
        {
            if (Health != null) Health.Fatal(reason, error);
            else { Reports.Write(reason, error, new ResourceSample[0], DiagnosticContext(), true); ArmEmergencyExit(); EmergencyStop(reason); }
        }
        private void BuildDiagnostics()
        {
            StackPanel body = Find<StackPanel>("DiagnosticsBody"); body.Children.Clear(); diagnosticLabels.Clear();
            Grid cards = new Grid { Margin = new Thickness(0, 0, -12, 0) };
            cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition());
            cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (string key in new[] { "Status", "Memory", "Cpu", "Handles", "Affinity", "Heartbeat" })
            {
                string title = key == "Status" ? L10n.T("Самоконтроль") : key == "Memory" ? L10n.T("Память") : key == "Cpu" ? L10n.T("Загрузка CPU") : key == "Handles" ? L10n.T("Ресурсы процесса") : key == "Affinity" ? L10n.T("ЯДРА ДЛЯ РАБОТЫ КОНТРОЛЛЕРА NUMA CONTROL") : L10n.T("Отклик и время работы");
                StackPanel card = new StackPanel(); DockPanel header = new DockPanel();
                Border dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(Themes.Current == "ami-bios" ? 0 : 4), Background = Ui.Brush(Themes.Color("Success")), VerticalAlignment = VerticalAlignment.Center }; dot.Name = "Diagnostic" + key + "Status"; dot.Margin = new Thickness(12, 0, 0, 0); DockPanel.SetDock(dot, Dock.Right); header.Children.Add(dot); diagnosticDots[key] = dot;
                header.Children.Add(new OpticalText { Name = "Diagnostic" + key + "Caption", Text = title, FontSize = Themes.TextSize(key == "Affinity" ? 12 : 15), Foreground = Ui.Brush("#EEF0F8"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }); card.Children.Add(header);
                TextBlock value = key == "Status" ? new OpticalText { Text = L10n.T("Сбор первого замера…"), FontSize = Themes.TextSize(13), Foreground = Ui.Brush(Themes.Color("Success")), TextWrapping = TextWrapping.Wrap } : Ui.Text(L10n.T("Сбор первого замера…"), 13, Themes.Color("Success")); value.Name = "Diagnostic" + key; value.Margin = new Thickness(0, 10, 0, 0); value.LineHeight = 23;
                diagnosticLabels[key] = value; card.Children.Add(value);
                Border border = new Border { Style = (Style)app.FindResource("Card"), Child = card, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(14) };
                if (key == "Status")
                {
                    Grid strip = new Grid(); strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); strip.ColumnDefinitions.Add(new ColumnDefinition()); strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    TextBlock caption = new OpticalText { Name = "DiagnosticStatusCaption", Text = title, FontSize = 15, Foreground = Ui.Brush("#EEF0F8"), Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center }; strip.Children.Add(caption);
                    header.Children.Remove(dot); Grid.SetColumn(dot, 2); dot.VerticalAlignment = VerticalAlignment.Center; strip.Children.Add(dot);
                    card.Children.Remove(value); Grid.SetColumn(value, 1); value.Margin = new Thickness(0); value.VerticalAlignment = VerticalAlignment.Center; value.TextAlignment = TextAlignment.Left; value.LineHeight = double.NaN; strip.Children.Add(value); border.Child = strip;
                    border.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(border); body.Children.Add(cards);
                }
                else if (key == "Heartbeat") { border.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(border); }
                else { int index = key == "Memory" ? 0 : key == "Cpu" ? 1 : key == "Handles" ? 2 : 3; Grid.SetRow(border, index / 2); Grid.SetColumn(border, index % 2); cards.Children.Add(border); }
            }
            WrapPanel buttons = new WrapPanel();
            buttons.Children.Add(Ui.Button(L10n.T("Обновить показатели"), delegate { Guard(delegate { Health.Sample(); UpdateDiagnostics(); }); }, false, "RefreshDiagnostics"));
            buttons.Children.Add(Ui.Button(L10n.T("Сохранить диагностический отчёт"), delegate { Guard(delegate { string path = Health.ManualReport(); Toast(L10n.T("Отчёт сохранён: ") + path); }); }, true, "SaveDiagnosticReport"));
            buttons.Children.Add(Ui.Button(L10n.T("Открыть папку отчётов"), delegate { Guard(delegate { Platform.Interaction.Reveal(Reports.DirectoryPath); }); }, false, "OpenReports"));
            body.Children.Add(buttons);
            TextBlock explanation = Ui.Text(L10n.T("Проверка каждые 5 с. При длительном превышении лимитов или потере отклика программа сохранит отчёт и завершится. Отчёты остаются на этом компьютере."), 12, "#949DB1"); explanation.LineHeight = 21; explanation.Margin = new Thickness(0, 12, 0, 18); body.Children.Add(explanation);
            UpdateDiagnostics();
        }
        private void UpdateDiagnostics()
        {
            if (diagnosticLabels.Count == 0 || Health == null) return;
            ResourceSample sample = Health.Current;
            AppAffinityState affinity = Engine == null ? null : Engine.AppAffinity;
            diagnosticLabels["Affinity"].Text = affinity == null ? L10n.T("Автовыбор при запуске игры\nРежим предпросмотра") : affinity.ActiveGames == 0 ? L10n.T("Автовыбор включён\nИгра пока не запущена") : (affinity.Fallback ? L10n.T("Последнее физическое ядро") : L10n.T("Вне привязки игры")) + L10n.T("\nПотоки: ") + Ui.CpuRanges(affinity.Mask);
            diagnosticLabels["Affinity"].ToolTip = affinity == null ? L10n.T("Привязка не применяется в предпросмотре.") : affinity.Detail + L10n.T("\nМаска: 0x") + affinity.Mask.ToString("X");
            diagnosticLabels["Status"].Text = Health.IsFatal ? L10n.T("Обнаружена проблема · отчёт сохранён") : L10n.T("Норма · защита от сбоев и контроль ресурсов включены");
            if (sample == null) return;
            HealthLimits limits = Platform.DiagnosticLimits ?? new HealthLimits();
            int memoryLevel = HealthPresentation.Level(Math.Max(HealthPresentation.Ratio(sample.PrivateMiB, limits.PrivateMiB), HealthPresentation.Ratio(sample.WorkingMiB, limits.WorkingMiB)));
            int cpuLevel = HealthPresentation.Level(HealthPresentation.Ratio(sample.CpuOneCorePercent, window.IsVisible ? limits.VisibleCpuPercent : limits.HiddenCpuPercent));
            int handlesLevel = HealthPresentation.Level(HealthPresentation.Ratio(sample.Handles, limits.Handles));
            int affinityLevel = affinity == null ? 0 : affinity.Error ? 2 : affinity.Fallback ? 1 : 0;
            int heartbeatLevel = sample.EngineRequired && !sample.EngineAlive ? 2 : HealthPresentation.Level(Math.Max(Math.Max(HealthPresentation.Ratio(sample.UiHeartbeatAgeSeconds, limits.UiTimeoutSeconds), HealthPresentation.Ratio(sample.EngineHeartbeatAgeSeconds, limits.EngineTimeoutSeconds)), HealthPresentation.Ratio(sample.EngineErrors, limits.MaxCycleErrors)));
            SetDiagnosticLevel("Memory", memoryLevel); SetDiagnosticLevel("Cpu", cpuLevel); SetDiagnosticLevel("Handles", handlesLevel); SetDiagnosticLevel("Affinity", affinityLevel); SetDiagnosticLevel("Heartbeat", heartbeatLevel);
            int overall = Health.IsFatal ? 2 : new[] { memoryLevel, cpuLevel, handlesLevel, affinityLevel, heartbeatLevel }.Max(); SetDiagnosticLevel("Status", overall);
            if (!Health.IsFatal && overall > 0) diagnosticLabels["Status"].Text = overall == 2 ? L10n.T("Показатели близки к пределу. Самоконтроль включён.") : L10n.T("Повышенные показатели. Самоконтроль включён.");
            ResourceSample earlier = Health.History.LastOrDefault(s => s.ElapsedSeconds <= sample.ElapsedSeconds - 60);
            string trend = earlier == null ? L10n.T("прогрев тренда") : L10n.T("за 60 с: ") + (sample.PrivateMiB - earlier.PrivateMiB).ToString("+0.0;-0.0;0.0") + L10n.T(" МиБ");
            diagnosticLabels["Memory"].Text = L10n.T("Частная: ") + sample.PrivateMiB.ToString("0.0") + L10n.T(" / 512 МиБ\nРабочая: ") + sample.WorkingMiB.ToString("0.0") + L10n.T(" / 768 МиБ\n.NET: ") + sample.ManagedMiB.ToString("0.0") + L10n.T(" МиБ · ") + trend;
            diagnosticLabels["Memory"].ToolTip = L10n.T("Частная — память этого процесса. Рабочая — страницы в RAM, включая разделяемые библиотеки.\nУправляемая .NET: ") + sample.ManagedMiB.ToString("0.0") + L10n.T(" МиБ. Лимиты должны превышаться 30 с подряд.");
            diagnosticLabels["Cpu"].Text = sample.CpuIntervalSeconds == 0 ? L10n.T("Первый замер: до 5 с…") : sample.CpuMachinePercent.ToString("0.000") + L10n.T("% всего CPU\nСреднее за ") + sample.CpuAverageSeconds.ToString("0") + L10n.T(" с: ") + sample.CpuAverageMachinePercent.ToString("0.000") + L10n.T("%\nЭквивалент одного ЦП: ") + sample.CpuOneCorePercent.ToString("0.00") + L10n.T("% · шаг ") + sample.CpuIntervalSeconds.ToString("0.0") + L10n.T(" с");
            diagnosticLabels["Cpu"].ToolTip = L10n.T("Текущий интервал: ") + sample.CpuIntervalSeconds.ToString("0.0") + L10n.T(" с.\nЭквивалент одного логического ЦП: ") + sample.CpuOneCorePercent.ToString("0.00") + L10n.T("% · среднее: ") + sample.CpuAverageOneCorePercent.ToString("0.00") + L10n.T("%.\n100% одного ЦП не означает 100% всей системы. Значение совпадает по масштабу с Диспетчером задач.");
            diagnosticLabels["Handles"].Text = sample.Handles + L10n.T(" / 6000 дескрипторов\n") + sample.Threads + L10n.T(" потоков\nПрофили: ") + Config.Games.Count(g => g.Enabled) + L10n.T(" включено из ") + Config.Games.Count;
            diagnosticLabels["Handles"].ToolTip = L10n.T("Дескрипторы — открытые ресурсы Windows. Число потоков — из последнего цикла наблюдателя.");
            diagnosticLabels["Heartbeat"].Text = (sample.EngineRequired ? sample.EngineAlive ? L10n.T("Наблюдатель работает") : L10n.T("Наблюдатель остановлен") : L10n.T("Предпросмотр")) + L10n.T(" · ошибок: ") + sample.EngineErrors + L10n.T(" · последний обход: ") + (Engine == null ? "—" : Engine.LastScanMilliseconds.ToString("0.0") + L10n.T(" мс")) + L10n.T("\nВ работе: ") + TimeSpan.FromSeconds(sample.ElapsedSeconds).ToString(@"d\.hh\:mm\:ss") + L10n.T(" · отклик UI: ") + sample.UiHeartbeatAgeSeconds.ToString("0.0") + L10n.T(" с · циклов: ") + (Engine == null ? "—" : Engine.CycleCount.ToString());
            diagnosticLabels["Heartbeat"].ToolTip = L10n.T("Отклик UI: ") + sample.UiHeartbeatAgeSeconds.ToString("0.0") + L10n.T(" с назад.\nПоследний цикл наблюдателя: ") + sample.EngineHeartbeatAgeSeconds.ToString("0.0") + L10n.T(" с назад.");
        }
        private void SetDiagnosticLevel(string key, int level)
        {
            Brush brush = Ui.Brush(Themes.Color(HealthPresentation.Role(level))); diagnosticLabels[key].Foreground = brush; diagnosticDots[key].Background = brush;
            diagnosticDots[key].ToolTip = level == 2 ? L10n.T("90% предела и выше или ошибка") : level == 1 ? L10n.T("70% предела и выше или резервный режим") : L10n.T("Показатели в норме");
        }

        private StackPanel SettingsCard(string title, string detail)
        {
            StackPanel body = new StackPanel();
            body.Children.Add(Ui.Text(title, 18, "#EEF0F8"));
            if (!string.IsNullOrEmpty(detail)) { TextBlock text = Ui.Text(detail, 12, "#949DB1"); text.Margin = new Thickness(0, 9, 0, 17); body.Children.Add(text); }
            Find<StackPanel>("SettingsBody").Children.Add(new Border { Style = (Style)app.FindResource("Card"), Margin = new Thickness(0, 0, 0, 16), Child = body });
            return body;
        }
        private void BuildSettings()
        {
            Find<StackPanel>("SettingsBody").Children.Clear();
            var languageCard = SettingsCard(L10n.T("Язык интерфейса"), L10n.T("Выберите Русский или English. Изменение применяется сразу и сохраняется для следующего запуска."));
            ComboBox language = new ComboBox { Name = "LanguageSelector", ItemsSource = new[] { "Русский", "English" }, SelectedIndex = Config.Language == "en" ? 1 : 0, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            System.Windows.Automation.AutomationProperties.SetAutomationId(language, "LanguageSelector");
            language.SelectionChanged += delegate { if (language.SelectedIndex >= 0) { string id = language.SelectedIndex == 1 ? "en" : "ru"; if (id != Config.Language) Guard(delegate { ChangeLanguage(id); }); } };
            languageCard.Children.Add(language);
            var startup = SettingsCard(L10n.T("Всегда наготове"), L10n.T("При старте Windows приложение будет работать в системном трее. Закрытие окна приложения не останавливает его работу. Полностью выйти из приложения можно через меню значка в системном трее."));
            CheckBox auto = new CheckBox { Name = "AutostartToggle", Content = L10n.T("Запускать вместе с Windows"), IsChecked = !Preview && File.Exists(Platform.StartupPath), IsEnabled = !Preview, Style = (Style)app.FindResource("Switch") };
            auto.Click += delegate { ChangeAutostart(auto.IsChecked == true); }; startup.Children.Add(auto);
            var appearance = SettingsCard(L10n.T("Оформление"), L10n.T("Выберите тему приложения. Оформление окон, значков и меню системного трея меняется вместе."));
            ComboBox theme = new ComboBox { Name = "ThemeSelector", ItemsSource = Themes.Names, SelectedIndex = Array.IndexOf(Themes.Ids, Config.Theme), MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            theme.SelectionChanged += delegate { if (theme.SelectedIndex >= 0 && Themes.Ids[theme.SelectedIndex] != Config.Theme) Guard(delegate { ChangeTheme(Themes.Ids[theme.SelectedIndex]); }); }; appearance.Children.Add(theme);
            var polling = SettingsCard(L10n.T("Работа в фоне"), null);
            polling.Children.Add(Ui.Text(L10n.T("Интервал опроса процессов из профилей"), 14, "#DDE1EF"));
            WrapPanel choices = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (int value in new[] { 1000, 2000, 5000 })
            {
                int interval = value;
                Button b = Ui.Button((value / 1000) + L10n.T(" с"), delegate { Guard(delegate { Configuration c = Config.Copy(); c.IdleIntervalMs = interval; c.ActiveIntervalMs = interval; Save(c); BuildSettings(); }); }, Config.IdleIntervalMs == value, "Interval" + value); choices.Children.Add(b);
            }
            polling.Children.Add(choices);
            polling.Children.Add(Ui.Text(L10n.T("Меньший интервал опроса быстрее обнаруживает запуск процессов, но чаще будит наблюдатель."), 11, "#7F899D"));
            var data = SettingsCard(L10n.T("Профили всегда с тобой"), L10n.T("Экспортируйте библиотеку перед переносом на другой компьютер. Импорт заменяет библиотеку и ставит наблюдение на паузу, чтобы вы могли проверить настройки."));
            WrapPanel dataActions = new WrapPanel();
            dataActions.Children.Add(Ui.Button(L10n.T("Экспорт профилей"), delegate { Guard(Export); }, false, "ExportProfiles"));
            dataActions.Children.Add(Ui.Button(L10n.T("Импорт / восстановление"), delegate { Guard(Import); }, false, "ImportProfiles")); data.Children.Add(dataActions);
            TextBlock dataPath = Ui.Text(L10n.T("Файл, где хранятся игровые профили: ") + Path.GetFullPath(Store.FilePath), 11, "#7F899D"); dataPath.TextWrapping = TextWrapping.Wrap; data.Children.Add(dataPath);
            if (Platform.Legacy.Any(kit => kit.Running || File.Exists(kit.ShortcutPath)))
            {
            var legacy = SettingsCard(L10n.T("Предыдущее наблюдение"), L10n.T("Найден другой наблюдатель привязки. Его можно штатно остановить и сохранить прежние ярлыки автозагрузки в резервной папке, чтобы правила не конфликтовали."));
            bool found = false;
            foreach (LegacyKit kit in Platform.Legacy)
            {
                bool running = kit.Running, autostart = File.Exists(kit.ShortcutPath); found |= running || autostart;
                TextBlock text = Ui.Text(kit.Name + "   ·   " + (running ? L10n.T("работает") : L10n.T("остановлен")) + L10n.T("   ·   автозагрузка ") + (autostart ? L10n.T("есть") : L10n.T("нет")), 12, running ? "#E9CFA0" : "#949DB1"); text.Margin = new Thickness(0, 0, 0, 10); legacy.Children.Add(text);
            }
            Button migrate = Ui.Button(L10n.T("Отключить предыдущее наблюдение"), delegate { Guard(Migrate); }, false, "LegacyMigrate"); migrate.IsEnabled = found && !Preview; legacy.Children.Add(migrate);
            legacy.Children.Add(Ui.Button(L10n.T("Обновить статусы"), delegate { Guard(delegate { CheckLegacy(); BuildSettings(); if (Engine != null) Engine.Refresh(); }); }, false, "LegacyRefresh"));
            }
            var about = SettingsCard(L10n.T("О программе"), "NUMA Control " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
            ((TextBlock)about.Children[1]).Name = "AboutVersion";
            about.Children.Add(Ui.Text(L10n.T("Сведения о процессоре проверяются при каждом запуске. После замены CPU наблюдение ставится на паузе для проверки прежних привязок.\nФайл со сведениями о процессоре: ") + Path.GetFullPath(Path.Combine(Store.DirectoryPath, "processor.json")), 12, "#949DB1"));
            about.Children.Add(Ui.Text(L10n.T("Нативное приложение x64 на .NET Framework. Без сетевых запросов, телеметрии и оверлеев. Работает через штатные функции Windows. Поддерживает одну группу до 64 ЦП."), 12, "#949DB1"));
        }
        private void ChangeTheme(string id)
        {
            if (!ConfirmDraft()) { BuildSettings(); return; }
            Configuration next = Config.Copy(); next.Theme = id; Save(next);
            double width = window.Width, height = window.Height, left = window.Left, top = window.Top; WindowState state = window.WindowState;
            window.Close(); if (window != null) return;
            if (themeResources != null) app.Resources.MergedDictionaries.Remove(themeResources); resources = false;
            Themes.Set(id); UpdateThemeIcons(); ShowWindow();
            window.Width = width; window.Height = height; window.Left = left; window.Top = top; window.WindowState = state;
        }
        internal void ChangeLanguage(string id)
        {
            if (!L10n.Valid(id)) throw new ArgumentException(L10n.T("Неизвестный язык интерфейса."));
            if (id == Config.Language) return;
            if (window != null && !ConfirmDraft()) { BuildSettings(); return; }
            Configuration next = Config.Copy(); next.Language = id;
            try { Save(next); } catch { if (window != null) BuildSettings(); throw; }
            L10n.Set(id);
            if (tray != null)
            {
                var menu = tray.ContextMenuStrip;
                menu.Items[0].Text = L10n.T("Открыть NUMA Control");
                pauseMenu.Text = L10n.T("Пауза наблюдения"); startupMenu.Text = L10n.T("Автозапуск Windows");
                menu.Items[4].Text = L10n.T("Самодиагностика"); menu.Items[6].Text = L10n.T("Выход");
                TrayStyle.Refresh(menu);
            }
            if (window != null)
            {
                double width = window.Width, height = window.Height, left = window.Left, top = window.Top; WindowState state = window.WindowState;
                window.Close();
                ShowWindow(); window.Width = width; window.Height = height; window.Left = left; window.Top = top; window.WindowState = state;
            }
            if (Engine != null) Engine.Refresh();
            UpdateStatus();
        }
        private void UpdateThemeIcons()
        {
            if (tray != null)
            {
                System.Drawing.Icon next;
                using (var bytes = new MemoryStream(Themes.IconBytes())) using (var icon = new System.Drawing.Icon(bytes)) next = (System.Drawing.Icon)icon.Clone();
                tray.Icon = next; if (trayIcon != null) trayIcon.Dispose(); trayIcon = next; TrayStyle.Refresh(tray.ContextMenuStrip);
            }
            try
            {
                ShellLinks.IconOverride = Themes.SaveIcon(Store.DirectoryPath);
                ShellLinks.UpdateOwnedIcons(Platform.DesktopDirectory, ShellLinks.Executable, ShellLinks.IconOverride);
                ShellLinks.UpdateIcon(Platform.StartupPath, ShellLinks.Executable, ShellLinks.IconOverride);
                if (!isolated && !Preview) ShellLinks.UpdateOwnedIcons(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NUMA Control"), ShellLinks.Executable, ShellLinks.IconOverride);
            }
            catch (IOException error) { Log.Write(L10n.T("Значки ярлыков: ") + error.Message); }
            catch (UnauthorizedAccessException error) { Log.Write(L10n.T("Значки ярлыков: ") + error.Message); }
            catch (System.Runtime.InteropServices.COMException error) { Log.Write(L10n.T("Значки ярлыков: ") + error.Message); }
        }
        private void Export()
        {
            string path = Platform.Interaction.Save(window, L10n.T("Экспорт профилей NUMA Control"), L10n.T("Профили JSON|*.json"), "NUMA-profiles.json");
            if (path != null) { ProfileStore.WriteAtomic(path, ProfileStore.Serializer().Serialize(Config)); Toast(L10n.T("Профили экспортированы.")); }
        }
        private void Import()
        {
            string path = Platform.Interaction.Open(window, L10n.T("Импорт профилей NUMA Control"), L10n.T("Профили JSON|*.json;*.bak|Все файлы|*.*"));
            if (path == null) return;
            Configuration incoming = ProfileStore.Read(path, Topology, true);
            incoming.Theme = Config.Theme;
            incoming.Language = Config.Language;
            if (Platform.Interaction.Ask(window, L10n.T("Заменить текущую библиотеку (") + Config.Games.Count + L10n.T(" игр) на импортируемую (") + incoming.Games.Count + L10n.T(")? Текущая версия останется в profiles.json.bak."), L10n.T("Импорт профилей"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            incoming.Paused = true; Save(incoming); RebuildLibrary(null, true); BuildSettings(); Toast(L10n.T("Импорт завершён. Проверьте профили и нажмите «Продолжить»."));
        }
        private void Migrate()
        {
            if (Platform.Interaction.Ask(window, L10n.T("Остановить прежние наблюдатели и убрать их автозагрузку? Ярлыки сохранятся в папке legacy-backup рядом с настройками. Старые EXE останутся на месте."), L10n.T("Переход на NUMA Control"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            foreach (LegacyKit kit in Platform.Legacy) kit.Retire(Path.Combine(Store.DirectoryPath, "legacy-backup"));
            if (Engine != null) Engine.Refresh(); CheckLegacy(); BuildSettings(); Toast(L10n.T("Сигналы остановки отправлены. Включите нужные профили переключателем «Следить»."));
        }
    }
}
