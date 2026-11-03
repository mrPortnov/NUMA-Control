using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace NumaControl
{
    internal class Interaction
    {
        internal virtual MessageBoxResult Ask(Window owner, string text, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            AppDialog dialog = new AppDialog(owner, text, title, buttons, image);
            dialog.ShowDialog(); return dialog.Answer;
        }
        internal virtual string Open(Window owner, string title, string filter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }
        internal virtual string Save(Window owner, string title, string filter, string name)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = name };
            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }
        internal virtual bool? Edit(ProfileEditor editor) { return editor.ShowDialog(); }
        internal virtual bool? Pick(ProcessPicker picker) { return picker.ShowDialog(); }
        internal virtual void Reveal(string directory)
        {
            Directory.CreateDirectory(directory);
            using (Process p = Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })) { }
        }
    }
    internal sealed class AppDialog : Window
    {
        internal MessageBoxResult Answer = MessageBoxResult.Cancel;
        internal AppDialog(Window owner, string text, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            Style = (Style)Application.Current.FindResource(typeof(Window));
            Owner = owner; Title = title + " — NUMA Control"; Width = 530; MinWidth = 410; MaxHeight = 650;
            SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            Ui.DarkTitle(this);
            StackPanel root = new StackPanel { Margin = new Thickness(26) }; Content = root;
            TextBlock heading = Ui.Text(title, 22, image == MessageBoxImage.Error ? "#F28D9B" : "#EEF0F8"); root.Children.Add(heading);
            TextBlock message = Ui.Text(text, 14, "#B5BED1"); message.LineHeight = 22; message.Margin = new Thickness(0, 15, 0, 24);
            root.Children.Add(new ScrollViewer { Content = message, MaxHeight = 380 });
            WrapPanel actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; root.Children.Add(actions);
            if (buttons == MessageBoxButton.YesNoCancel || buttons == MessageBoxButton.YesNo)
            {
                Add(actions, L10n.T("Да"), MessageBoxResult.Yes, true, false);
                Add(actions, L10n.T("Нет"), MessageBoxResult.No, false, false);
            }
            else Add(actions, L10n.T("ОК"), MessageBoxResult.OK, true, buttons == MessageBoxButton.OK);
            if (buttons == MessageBoxButton.OKCancel || buttons == MessageBoxButton.YesNoCancel) Add(actions, L10n.T("Отмена"), MessageBoxResult.Cancel, false, true);
        }
        private void Add(WrapPanel actions, string text, MessageBoxResult answer, bool primary, bool cancel)
        {
            Button button = Ui.Button(text, delegate { Answer = answer; DialogResult = answer == MessageBoxResult.OK || answer == MessageBoxResult.Yes; }, primary);
            button.Name = "Answer" + answer; button.IsDefault = primary; button.IsCancel = cancel; actions.Children.Add(button);
        }
    }
    internal sealed class AppEnvironment
    {
        internal string StartupPath, DesktopDirectory;
        internal List<LegacyKit> Legacy;
        internal Interaction Interaction;
        internal bool ExitOnFatal = true;
        internal bool ActivateWindows;
        internal TimeSpan ToastDuration = TimeSpan.FromSeconds(5);
        internal HealthLimits DiagnosticLimits { get; set; }
        internal Action<string> FatalObserved { get; set; }
        internal AppEnvironment(string dataDirectory)
        {
            Interaction = new Interaction();
            ActivateWindows = dataDirectory == null;
            if (dataDirectory == null)
            {
                StartupPath = ShellLinks.StartupPath;
                DesktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Legacy = LegacyKit.All();
            }
            else
            {
                StartupPath = Path.Combine(dataDirectory, "shell", "Startup", "NUMA Control.lnk");
                DesktopDirectory = Path.Combine(dataDirectory, "shell", "Desktop");
                Legacy = new List<LegacyKit>();
                Directory.CreateDirectory(Path.GetDirectoryName(StartupPath));
                Directory.CreateDirectory(DesktopDirectory);
            }
        }
    }
}
