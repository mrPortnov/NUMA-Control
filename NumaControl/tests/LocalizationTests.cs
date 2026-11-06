using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace NumaControl
{
    internal static partial class Tests
    {
        private static void LocalizationChecks(Application app)
        {
            Topology cpu = Topology.Detect();
            string folder = Path.Combine(root, "languages");
            var store = new ProfileStore(folder);
            Configuration initial = new Configuration();
            var game = new GameProfile { Name = "Example game", ProcessName = "NumaDemoGame", Mask = cpu.WithoutEdgeCores().ToString("X"), Enabled = true };
            initial.Games.Add(game); store.Save(initial);
            Check(store.Load(cpu).Language == "ru", "language defaults to Russian for existing users");
            string json = File.ReadAllText(store.FilePath);
            File.WriteAllText(store.FilePath, Regex.Replace(json, ",?\"Language\":\"ru\"", ""));
            Check(store.Load(cpu).Language == "ru", "profiles without a language field migrate to Russian");
            Configuration invalid = initial.Copy(); invalid.Language = "de";
            Reject(() => invalid.Validate(cpu, false), "unknown imported language is rejected");
            var interaction = new ScriptedInteraction();
            var environment = new AppEnvironment(folder) { Interaction = interaction, ExitOnFatal = false };
            using (var controller = new AppController(app, false, folder, environment))
            {
                controller.Start(); controller.ShowWindow(); Window window = MainWindow(app);
                string id = controller.Config.Games.Single().Id, mask = controller.Config.Games.Single().Mask;
                int interval = controller.Config.IdleIntervalMs; bool paused = controller.Config.Paused;
                Press(window, "SettingsNav");
                ComboBox selector = Control<ComboBox>(window, "LanguageSelector");
                Check(selector.Items.Count == 2 && (string)selector.Items[0] == "Русский" && (string)selector.Items[1] == "English", "Settings exposes Russian and English explicitly");
                selector.SelectedIndex = 1; Pump(); window = MainWindow(app);
                Check(controller.Config.Language == "en" && store.Load(cpu).Language == "en", "Settings language selection is applied and persisted immediately");
                Check(Control<TextBlock>(window, "PageTitle").Text == "Everything under control.", "current Settings page is rebuilt in English");
                Check(controller.Config.Games.Single().Id == id && controller.Config.Games.Single().Mask == mask && controller.Config.IdleIntervalMs == interval && controller.Config.Paused == paused, "language change preserves profiles, affinity, polling and pause");
                var tray = (Forms.NotifyIcon)typeof(AppController).GetField("tray", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
                Check(tray.ContextMenuStrip.Items[0].Text == "Open NUMA Control" && tray.ContextMenuStrip.Items[6].Text == "Exit" && tray.ContextMenuStrip.Items[0].Image != null, "tray labels and icons update with the language");
                Check(L10n.Keys.All(key => !Regex.IsMatch(L10n.T(key), "[А-Яа-яЁё]")), "every catalog entry has an English translation");
                using (var dialog = new DialogProbe(window)) Check(dialog.Window.Title.StartsWith("Options", StringComparison.Ordinal), "custom dialogs use English captions");
                var personal = new GameProfile { Name = "Моя игра", ProcessName = "Example" };
                Check(personal.Name == "Моя игра", "user-provided profile names are never translated");
                foreach (string language in new[] { "ru", "en" })
                {
                    controller.ChangeLanguage(language); Pump(); window = MainWindow(app);
                    foreach (string theme in Themes.Ids)
                    {
                        Press(window, "SettingsNav"); Control<ComboBox>(window, "ThemeSelector").SelectedIndex = Array.IndexOf(Themes.Ids, theme); Pump(); window = MainWindow(app);
                        window.Width = 1220; window.Height = 900; Pump();
                        Press(window, "LibraryNav");
                        Check(Control<TextBlock>(window, "PageTitle").Text == (language == "en" ? "Your game. Your cores." : "Твоя игра. Твои ядра."), "library language follows selection / " + language + " / " + theme);
                        if (language == "en") Check(!Descendants(window).OfType<TextBlock>().Where(t => t.IsVisible).Any(t => Regex.IsMatch(t.Text ?? "", "[А-Яа-яЁё]")), "English library contains no untranslated labels / " + theme);
                        string libraryName = theme == "standard" ? "library-standard" : theme == "win11-light" ? "library-light" : theme == "win11-dark" ? "library-dark" : "library-bios";
                        Capture(window, "readme-" + language + "-" + libraryName);
                        if (theme == "standard")
                        {
                            ToggleButton tile = Descendants(Control<WrapPanel>(window, "CpuMap")).OfType<ToggleButton>().First();
                            MethodInfo click = typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
                            click.Invoke(tile, null);
                            Control<Button>(window, "SaveMask").BringIntoView();
                            Pump();
                            Capture(window, "readme-" + language + "-cpu-selection-standard");
                            click.Invoke(tile, null);
                            Control<ScrollViewer>(window, "LibraryView").ScrollToTop();
                            Pump();
                            interaction.Editors.Enqueue(editor =>
                            {
                                editor.Height = 960;
                                Pump();
                                Capture(editor, "readme-" + language + "-profile-editor-standard");
                                Press(editor, "CancelProfile");
                            });
                            Press(window, "EditGame");
                        }
                        Press(window, "SettingsNav");
                        Check(Control<TextBlock>(window, "AboutVersion").Text == "NUMA Control " + typeof(Program).Assembly.GetName().Version.ToString(3), "About shows only the application version without core/thread/group counts / " + language + " / " + theme);
                        DropdownContrastChecks(window, language, theme);
                        ListAndTrayContrastChecks(window, tray.ContextMenuStrip, language, theme);
                        VisualMatrix(app, window, controller, interaction, "matrix-" + language + "-" + theme + "-");
                        window.Width = 1220; window.Height = 900; Pump();
                        if (theme == "standard" || theme == "ami-bios")
                        {
                            Press(window, "SettingsNav"); Control<ScrollViewer>(window, "SettingsView").ScrollToTop(); Pump();
                            if (theme == "standard") Capture(window, "readme-" + language + "-settings-standard");
                            Check(Control<ComboBox>(window, "LanguageSelector").SelectedIndex == (language == "en" ? 1 : 0), "language selector survives theme changes / " + language + " / " + theme);
                        }
                        if (theme == "standard")
                        {
                            Press(window, "DiagnosticsNav");
                            Check(Until(delegate
                            {
                                Pump();
                                ResourceSample sample = controller.Health.Current;
                                return sample != null && sample.CpuIntervalSeconds > 0 && sample.ElapsedSeconds >= 65 && controller.Health.History.Any(earlier => earlier.ElapsedSeconds <= sample.ElapsedSeconds - 60);
                            }, 90000), "README diagnostics waits for actual CPU samples and the 60-second memory trend / " + language);
                            Press(window, "RefreshDiagnostics");
                            foreach (string metric in new[] { "Memory", "Cpu", "Handles", "Heartbeat" })
                            {
                                string value = Control<TextBlock>(window, "Diagnostic" + metric).Text;
                                Check(Regex.IsMatch(value, "[0-9]") && !value.Contains(L10n.T("Сбор первого замера…")) && !value.Contains(L10n.T("Первый замер: до 5 с…")) && !value.Contains(L10n.T("прогрев тренда")), "README diagnostics contains real measured values / " + language + " / " + metric);
                            }
                            if (language == "en") Check(!Descendants(window).OfType<TextBlock>().Where(t => t.IsVisible).Any(t => Regex.IsMatch(t.Text ?? "", "[А-Яа-яЁё]")), "English diagnostics contains no untranslated labels");
                            Capture(window, "readme-" + language + "-diagnostics-standard");
                        }
                    }
                }
                controller.ChangeLanguage("ru"); Pump(); window = MainWindow(app);
                Check(store.Load(cpu).Language == "ru" && tray.ContextMenuStrip.Items[6].Text == "Выход", "switching back restores Russian and persists it");
                window.Close();
            }
            Configuration restart = store.Load(cpu); restart.Language = "en"; store.Save(restart);
            using (var controller = new AppController(app, false, folder, environment))
            {
                controller.Start(); controller.ShowWindow(); Window window = MainWindow(app);
                Check(L10n.Current == "en" && Control<TextBlock>(window, "PageTitle").Text == "Your game. Your cores.", "saved English is restored after application restart");
                window.Close();
            }
            L10n.Set("ru"); Themes.Set("standard");
        }
        private static void DropdownContrastChecks(Window window, string language, string theme)
        {
            foreach (string name in new[] { "LanguageSelector", "ThemeSelector" })
            {
                ComboBox selector = Control<ComboBox>(window, name);
                selector.BringIntoView(); selector.IsDropDownOpen = true; Pump();
                int selected = selector.SelectedIndex;
                foreach (int index in new[] { selected, selected == selector.Items.Count - 1 ? selected - 1 : selected + 1 })
                {
                    var item = (ComboBoxItem)selector.ItemContainerGenerator.ContainerFromIndex(index);
                    if (index != selected)
                    {
                        selector.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(selector), Environment.TickCount, index > selected ? Key.Down : Key.Up) { RoutedEvent = Keyboard.KeyDownEvent });
                        Pump();
                    }
                    string context = language + " / " + theme + " / " + name + " / " + (index == selected ? "selected" : "other");
                    Check(item.IsHighlighted && item.IsSelected == (index == selected), "dropdown keyboard navigation highlights the intended item / " + context);
                    var surface = (Border)item.Template.FindName("Choice", item);
                    var text = Descendants(item).OfType<TextBlock>().First();
                    string foreground = ((SolidColorBrush)text.Foreground).Color.ToString();
                    string background = ((SolidColorBrush)surface.Background).Color.ToString();
                    Check(Contrast(foreground, background) >= 4.5, "rendered dropdown text has readable contrast on its highlight / " + context);
                    if (theme == "ami-bios") Check(((SolidColorBrush)text.Foreground).Color == Colors.White, "BIOS highlighted dropdown text is white / " + context);
                    if (theme == "ami-bios" && index != selected)
                    {
                        var popup = (Popup)selector.Template.FindName("PART_Popup", selector);
                        var content = (FrameworkElement)popup.Child; content.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(content); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                        string directory = Path.Combine(root, "visuals"); Directory.CreateDirectory(directory);
                        using (var file = File.Create(Path.Combine(directory, "bios-dropdown-" + language + "-" + name + ".png"))) png.Save(file);
                    }
                }
                selector.IsDropDownOpen = false; Pump();
                Check(selector.SelectedIndex == selected, "dropdown navigation preserves the saved selection without confirmation / " + language + " / " + theme + " / " + name);
                Check(((SolidColorBrush)selector.Foreground).Color == (Color)ColorConverter.ConvertFromString(Themes.Map("#EEF0F8")), "closed dropdown restores normal text color / " + language + " / " + theme + " / " + name);
            }
        }
        private static void ListAndTrayContrastChecks(Window window, Forms.ContextMenuStrip menu, string language, string theme)
        {
            string context = language + " / " + theme;
            ListBox games = Control<ListBox>(window, "GameList");
            CheckListSelectionContrast(games, "Example game", "game library / " + context);
            var picker = new ProcessPicker(window);
            try
            {
                picker.Show(); Pump();
                ListBox processes = Control<ListBox>(picker, "ProcessResults");
                var entry = new ProcessEntry { ProcessName = "ExampleGame", PathName = @"C:\Games\ExampleGame.exe", Pid = 1234 };
                processes.ItemsSource = new[] { entry }; processes.SelectedIndex = 0; Pump();
                CheckListSelectionContrast(processes, entry.Display, "process picker / " + context);
            }
            finally { picker.Close(); Pump(); }
            menu.PerformLayout(); menu.Size = menu.GetPreferredSize(System.Drawing.Size.Empty);
            foreach (Forms.ToolStripMenuItem item in menu.Items.OfType<Forms.ToolStripMenuItem>())
            {
                item.Select(); Check(item.Selected, "tray menu item enters its selected state / " + context + " / " + item.Text);
                using (var bitmap = new System.Drawing.Bitmap(item.Width, item.Height))
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.Clear(menu.BackColor);
                    menu.Renderer.DrawMenuItemBackground(new Forms.ToolStripItemRenderEventArgs(graphics, item));
                    System.Drawing.Color background = bitmap.GetPixel(4, item.Height / 2);
                    var args = new Forms.ToolStripItemTextRenderEventArgs(graphics, item, item.Text, new System.Drawing.Rectangle(30, 0, item.Width - 30, item.Height), item.ForeColor, item.Font, Forms.TextFormatFlags.Default);
                    menu.Renderer.DrawItemText(args);
                    Check(Contrast(System.Drawing.ColorTranslator.ToHtml(args.TextColor), System.Drawing.ColorTranslator.ToHtml(background)) >= 4.5, "rendered tray selection text has readable contrast / " + context + " / " + item.Text);
                }
            }
        }
        private static void CheckListSelectionContrast(ListBox list, string expectedText, string context)
        {
            list.UpdateLayout();
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex);
            Check(item != null && item.IsSelected, "list renders a selected item / " + context);
            var surface = (Border)item.Template.FindName("Row", item);
            var text = Descendants(item).OfType<TextBlock>().Single(value => value.Text == expectedText);
            Check(Contrast(((SolidColorBrush)text.Foreground).Color.ToString(), ((SolidColorBrush)surface.Background).Color.ToString()) >= 4.5, "rendered list selection text has readable contrast / " + context);
        }
        private sealed class DialogProbe : IDisposable
        {
            internal readonly AppDialog Window;
            internal DialogProbe(Window owner) { Window = new AppDialog(owner, L10n.T("Проверьте параметры"), L10n.T("Параметры"), MessageBoxButton.OKCancel, MessageBoxImage.Information); }
            public void Dispose() { Window.Close(); }
        }
    }
}
