using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace NumaControl
{
    internal sealed class ProfileEditor : Window
    {
        private readonly TextBox name, process, path, launch, arguments;
        private readonly CheckBox enabled, highPriority;
        private readonly GameProfile original;
        private readonly Topology topology;
        private readonly Configuration library;
        private readonly Interaction interaction;
        private readonly Action<GameProfile, bool> commit;
        private string launchDirectory;
        private bool launchViaShell;
        internal GameProfile Result;
        internal bool DeleteRequested;
        internal ProfileEditor(Window owner, GameProfile game, Topology cpu, Configuration current, Interaction dialogs = null, Action<GameProfile, bool> save = null)
        {
            Style = (Style)Application.Current.FindResource(typeof(Window));
            Owner = owner; original = game; topology = cpu; library = current; interaction = dialogs ?? new Interaction(); commit = save;
            Title = game == null ? L10n.T("Добавить игру — NUMA Control") : L10n.T("Параметры игры — NUMA Control");
            Width = 760; Height = 800; MinWidth = 650; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Ui.FitScreen(this);
            Ui.DarkTitle(this);
            Grid root = new Grid { Margin = new Thickness(27) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;
            StackPanel body = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            root.Children.Add(new ScrollViewer { Content = body });
            body.Children.Add(Ui.Text(game == null ? L10n.T("Добавим новую игру") : L10n.T("Параметры профиля"), 24, "#EEF0F8"));
            TextBlock hint = Ui.Text(L10n.T("Выбери запущенный процесс или укажи EXE игры. Точный путь позволяет различать игры с одинаковым именем процесса."), 12, "#949DB1"); hint.Margin = new Thickness(0, 9, 0, 16); body.Children.Add(hint);
            WrapPanel methods = new WrapPanel();
            methods.Children.Add(Ui.Button(L10n.T("Из запущенных процессов"), PickProcess, true, "ChooseProcess"));
            methods.Children.Add(Ui.Button(L10n.T("Выбрать файл .exe"), BrowseGame, false, "BrowseGameExe"));
            methods.Children.Add(Ui.Button(L10n.T("Настроить Fortnite"), SetFortnite, false, "SetFortnite"));
            body.Children.Add(methods);
            name = Field(body, L10n.T("Название в библиотеке"), game == null ? "" : game.Name);
            process = Field(body, L10n.T("Имя процесса без .exe"), game == null ? "" : game.ProcessName);
            path = Field(body, L10n.T("Точный путь к EXE игры · можно оставить пустым"), game == null ? "" : game.ExePath);
            launch = Field(body, L10n.T("Цель запуска · EXE или ссылка лаунчера"), game == null ? "" : game.LaunchTarget);
            launchDirectory = game == null ? "" : game.LaunchDirectory;
            launchViaShell = game != null && game.LaunchViaShell;
            launch.TextChanged += delegate { launchDirectory = ""; launchViaShell = false; };
            var launchOptions = new WrapPanel();
            launchOptions.Children.Add(Ui.Button(L10n.T("Выбрать файл запуска / лаунчер"), BrowseLaunch, false, "BrowseLaunchExe"));
            launchOptions.Children.Add(Ui.Button(L10n.T("Взять цель из ярлыка"), BrowseLauncherShortcut, false, "BrowseLauncherShortcut"));
            body.Children.Add(launchOptions);
            body.Children.Add(Ui.Text(L10n.T("Для Epic Games, Battle.net и Ubisoft Connect выберите ярлык игры. Выше укажите процесс самой игры."), 11, "#949DB1"));
            arguments = Field(body, L10n.T("Аргументы запуска · только для EXE"), game == null ? "" : game.Arguments);
            name.Name = "GameNameInput"; process.Name = "ProcessNameInput"; path.Name = "GamePathInput"; launch.Name = "LaunchTargetInput"; arguments.Name = "ArgumentsInput";
            enabled = new CheckBox { Content = L10n.T("Автоматически следить за этой игрой"), IsChecked = game == null || game.Enabled, Margin = new Thickness(0, 17, 0, 9), Style = (Style)Application.Current.FindResource("Switch") }; body.Children.Add(enabled);
            enabled.Name = "ProfileEnabled";
            highPriority = new CheckBox { Name = "HighPriorityToggle", Content = L10n.T("Высокий приоритет процесса игры"), IsChecked = game == null || game.HighPriority, Margin = new Thickness(0, 5, 0, 9), Style = (Style)Application.Current.FindResource("Switch") }; body.Children.Add(highPriority);
            body.Children.Add(Ui.Text(L10n.T("Поддерживать высокий приоритет во время работы игры. Если выключить, программа перестанет управлять приоритетом. Уже назначенный приоритет сохранится до закрытия процесса."), 11, "#7F899D"));
            body.Children.Add(Ui.Text(L10n.T("Выбор ЦП доступен в карточке игры после сохранения. Выключение наблюдателя или удаление профиля не меняет уже назначенную маску процесса."), 11, "#7F899D"));
            Grid actions = new Grid { Margin = new Thickness(0, 23, 0, 0) }; Grid.SetRow(actions, 1); root.Children.Add(actions);
            if (game != null)
            {
                Button delete = Ui.Button(L10n.T("Удалить профиль"), delegate
                {
                    if (interaction.Ask(this, L10n.T("Удалить профиль «") + game.Name + L10n.T("»? Ярлык на рабочем столе можно удалить отдельно."), L10n.T("Удаление профиля"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                    try { if (commit != null) commit(game, true); DeleteRequested = true; DialogResult = true; }
                    catch (Exception error) { interaction.Ask(this, error.Message, L10n.T("Не удалось удалить профиль"), MessageBoxButton.OK, MessageBoxImage.Warning); }
                }, false, "DeleteProfile"); delete.HorizontalAlignment = HorizontalAlignment.Left; delete.Foreground = Ui.Brush("#F28D9B"); actions.Children.Add(delete);
            }
            StackPanel right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; actions.Children.Add(right);
            Button cancel = Ui.Button(L10n.T("Отмена"), delegate { DialogResult = false; }, false, "CancelProfile"); cancel.IsCancel = true; right.Children.Add(cancel);
            Button saveButton = Ui.Button(L10n.T("Сохранить игру"), SaveProfile, true, "SaveProfile"); right.Children.Add(saveButton);
            saveButton.IsDefault = true;
        }
        private TextBox Field(StackPanel body, string label, string value)
        {
            TextBlock text = Ui.Text(label, 12, "#BBC2D3"); text.Margin = new Thickness(0, 14, 0, 7); body.Children.Add(text);
            TextBox input = new TextBox { Text = value ?? "", Height = 40, MaxLength = label.StartsWith(L10n.T("Аргументы")) ? 4096 : 1024, Margin = new Thickness(0, 0, 0, 3) };
            System.Windows.Automation.AutomationProperties.SetName(input, label); body.Children.Add(input); return input;
        }
        private void BrowseGame()
        {
            string selectedPath = interaction.Open(this, L10n.T("Выберите EXE самой игры"), L10n.T("Приложения Windows|*.exe"));
            if (selectedPath != null) SetExecutable(selectedPath);
        }
        private void BrowseLaunch()
        {
            string selectedPath = interaction.Open(this, L10n.T("Выберите EXE игры или лаунчера"), L10n.T("Приложения Windows|*.exe"));
            if (selectedPath != null) launch.Text = selectedPath;
        }
        private void BrowseLauncherShortcut()
        {
            string selectedPath = interaction.Open(this, L10n.T("Выберите ярлык игры"), L10n.T("Ярлыки игр|*.lnk;*.url"));
            if (selectedPath == null) return;
            try
            {
                LauncherTarget target = LauncherTargets.ReadShortcut(selectedPath);
                launch.Text = target.Target;
                arguments.Text = target.Arguments;
                launchDirectory = target.Directory;
                launchViaShell = true;
            }
            catch (Exception error) { interaction.Ask(this, error.Message, L10n.T("Проверьте параметры"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        private void SetFortnite()
        {
            try
            {
                string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
                GameProfile fortnite = LauncherTargets.FindFortnite(manifests);
                if (fortnite == null) throw new InvalidOperationException(L10n.T("Не удалось однозначно найти Fortnite. Выберите игровой EXE и укажите ярлык Fortnite из Epic Games Launcher."));
                name.Text = fortnite.Name;
                process.Text = fortnite.ProcessName;
                path.Text = fortnite.ExePath;
                launch.Text = fortnite.LaunchTarget;
                arguments.Text = "";
                launchDirectory = "";
                launchViaShell = true;
            }
            catch (Exception error) { interaction.Ask(this, error.Message, L10n.T("Проверьте параметры"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        private void SetExecutable(string executable)
        {
            bool followLaunch = string.IsNullOrWhiteSpace(launch.Text) || string.Equals(launch.Text, path.Text, StringComparison.OrdinalIgnoreCase);
            bool followName = string.IsNullOrWhiteSpace(name.Text) || name.Text == process.Text;
            path.Text = executable; process.Text = Path.GetFileNameWithoutExtension(executable);
            if (followName) name.Text = process.Text;
            if (followLaunch) launch.Text = executable;
        }
        private void PickProcess()
        {
            ProcessPicker picker = new ProcessPicker(this);
            if (interaction.Pick(picker) != true) return;
            process.Text = picker.Selected.ProcessName;
            if (!string.IsNullOrEmpty(picker.Selected.PathName)) SetExecutable(picker.Selected.PathName);
            else { path.Text = ""; if (string.IsNullOrWhiteSpace(name.Text)) name.Text = picker.Selected.ProcessName; }
        }
        private void SaveProfile()
        {
            try
            {
                GameProfile g = original == null ? new GameProfile { Mask = topology.Supported ? topology.RecommendedMask().ToString("X") : "1" } : original.Copy();
                g.Name = name.Text.Trim(); g.ProcessName = process.Text.Trim();
                if (g.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) g.ProcessName = g.ProcessName.Substring(0, g.ProcessName.Length - 4);
                g.ExePath = path.Text.Trim(); g.LaunchTarget = launch.Text.Trim(); g.Arguments = arguments.Text.Trim(); g.Enabled = enabled.IsChecked == true; g.HighPriority = highPriority.IsChecked == true;
                g.LaunchDirectory = launchDirectory;
                g.LaunchViaShell = launchViaShell;
                Configuration check = library.Copy();
                if (original != null) check.Games.RemoveAll(p => p.Id == original.Id);
                check.Games.Add(g); check.Validate(topology, false);
                if (commit != null) commit(g, false);
                Result = g; DialogResult = true;
            }
            catch (Exception error) { interaction.Ask(this, error.Message, L10n.T("Проверьте параметры"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }
    internal sealed class ProcessEntry
    {
        internal string ProcessName, PathName;
        internal int Pid;
        public string Display { get { return ProcessName + ".exe   ·   PID " + Pid + "\n" + (string.IsNullOrEmpty(PathName) ? L10n.T("Путь недоступен · будет добавлен профиль по имени") : PathName); } }
    }
    internal sealed class ProcessPicker : Window
    {
        private List<ProcessEntry> entries = new List<ProcessEntry>();
        private readonly ListBox list;
        private readonly TextBox search;
        internal ProcessEntry Selected;
        internal ProcessPicker(Window owner)
        {
            Style = (Style)Application.Current.FindResource(typeof(Window));
            Owner = owner; Title = L10n.T("Запущенные процессы — NUMA Control"); Width = 760; Height = 630; MinWidth = 600; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner; Ui.DarkTitle(this);
            Ui.FitScreen(this);
            Grid root = new Grid { Margin = new Thickness(24) }; Content = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(Ui.Text(L10n.T("Выбери процесс самой игры"), 22, "#EEF0F8"));
            search = new TextBox { Height = 40, Margin = new Thickness(0, 16, 0, 12), ToolTip = L10n.T("Поиск по имени или пути") }; System.Windows.Automation.AutomationProperties.SetName(search, L10n.T("Поиск процесса")); Grid.SetRow(search, 1); root.Children.Add(search);
            search.Name = "ProcessSearch";
            list = new ListBox { Name = "ProcessResults", DisplayMemberPath = "Display", Margin = new Thickness(0, 0, 0, 14) }; Grid.SetRow(list, 2); root.Children.Add(list);
            search.TextChanged += delegate { Filter(); };
            list.MouseDoubleClick += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) != null) Select(); };
            WrapPanel buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetRow(buttons, 3); root.Children.Add(buttons);
            buttons.Children.Add(Ui.Button(L10n.T("Обновить"), Refresh, false, "RefreshProcesses")); buttons.Children.Add(Ui.Button(L10n.T("Отмена"), delegate { DialogResult = false; }, false, "CancelProcess"));
            Button select = Ui.Button(L10n.T("Добавить выбранный"), Select, true, "SelectProcess"); select.IsEnabled = false; select.IsDefault = true; buttons.Children.Add(select);
            list.SelectionChanged += delegate { select.IsEnabled = list.SelectedItem != null; };
            Loaded += delegate { Refresh(); search.Focus(); };
        }
        private void Refresh()
        {
            entries = new List<ProcessEntry>();
            int session, own;
            using (Process self = Process.GetCurrentProcess()) { session = self.SessionId; own = self.Id; }
            Process[] processes = Process.GetProcesses();
            try
            {
                foreach (Process p in processes)
                {
                    try
                    {
                        if (p.Id == own || p.SessionId != session || p.HasExited) continue;
                        ProcessEntry entry = new ProcessEntry { ProcessName = p.ProcessName, Pid = p.Id, PathName = "" };
                        try { using (var handle = Native.Open(p.Id, false)) entry.PathName = Native.Image(handle); }
                        catch (System.ComponentModel.Win32Exception) { }
                        entries.Add(entry);
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            finally { foreach (Process p in processes) p.Dispose(); }
            entries.Sort(delegate(ProcessEntry a, ProcessEntry b) { return string.Compare(a.ProcessName, b.ProcessName, StringComparison.CurrentCultureIgnoreCase); }); Filter();
        }
        private void Filter()
        {
            string query = search.Text.Trim();
            list.ItemsSource = entries.Where(e => e.Display.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        }
        private void Select() { Selected = list.SelectedItem as ProcessEntry; if (Selected != null) DialogResult = true; }
    }
}
