using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NumaControl
{
    internal sealed class ExceptionHooks : IDisposable
    {
        private readonly Application app;
        private readonly Action<string, Exception> report;
        internal ExceptionHooks(Application application, Action<string, Exception> fatal)
        {
            app = application; report = fatal;
            app.DispatcherUnhandledException += Ui;
            AppDomain.CurrentDomain.UnhandledException += Domain;
            TaskScheduler.UnobservedTaskException += Task;
            System.Windows.Forms.Application.ThreadException += Forms;
        }
        private void Ui(object sender, DispatcherUnhandledExceptionEventArgs e) { report("unhandled-ui-exception", e.Exception); e.Handled = true; }
        private void Domain(object sender, UnhandledExceptionEventArgs e) { try { report("unhandled-thread-exception", e.ExceptionObject as Exception); } finally { if (e.IsTerminating) Environment.Exit(70); } }
        private void Task(object sender, UnobservedTaskExceptionEventArgs e) { report("unobserved-task-exception", e.Exception); e.SetObserved(); }
        private void Forms(object sender, ThreadExceptionEventArgs e) { report("unhandled-tray-exception", e.Exception); }
        public void Dispose()
        {
            app.DispatcherUnhandledException -= Ui;
            AppDomain.CurrentDomain.UnhandledException -= Domain;
            TaskScheduler.UnobservedTaskException -= Task;
            System.Windows.Forms.Application.ThreadException -= Forms;
        }
    }
}
