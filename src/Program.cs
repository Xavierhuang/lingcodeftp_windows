using System;
using System.IO;
using System.Windows.Forms;

namespace LingCodeFTP
{
    static class Program
    {
        public static AppContext Context;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Log any unhandled exception (UI thread or background) so a failure
            // is diagnosable instead of the process vanishing silently.
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                LogCrash("UI-thread", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                LogCrash("background", e.ExceptionObject as Exception);
            };

            Context = new AppContext();
            Application.Run(Context);
        }

        public static void LogCrash(string where, Exception ex)
        {
            try
            {
                string path = Path.Combine(AppPaths.DataDir(), "crash.log");
                File.AppendAllText(path, "==== " + where + " " + DateTime.Now + " ====\r\n"
                    + (ex != null ? ex.ToString() : "(no exception object)") + "\r\n\r\n");
            }
            catch { }
        }
    }

    // Keeps the process alive across multiple top-level windows (launch window
    // + one window per open server), quitting only when the last one closes.
    // Mirrors the Mac AppDelegate's multi-window lifetime.
    public class AppContext : ApplicationContext
    {
        int _open;

        public AppContext()
        {
            LaunchForm launch = new LaunchForm();
            Register(launch);
            launch.Show();
        }

        public void Register(Form f)
        {
            _open++;
            f.FormClosed += delegate(object s, FormClosedEventArgs e)
            {
                _open--;
                if (_open <= 0) ExitThread();
            };
        }
    }
}
