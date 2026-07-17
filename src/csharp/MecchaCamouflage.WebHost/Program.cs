using MecchaCamouflage.Controller;

namespace MecchaCamouflage.WebHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var paths = new MecchaCamouflage.Core.AppPaths(VersionInfo.Current);
        DiagnosticsState.Initialize(paths, VersionInfo.Current);
#if MECCHA_RESEARCH_BUILD
        if (ResearchRunner.IsRequested(args))
        {
            Environment.ExitCode = ResearchRunner.RunAsync(args).GetAwaiter().GetResult();
            return;
        }
#endif
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => DiagnosticsState.RecordException("winforms_thread_exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                DiagnosticsState.RecordException("appdomain_unhandled_exception", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticsState.RecordException("task_unobserved_exception", args.Exception);
            args.SetObserved();
        };

        try
        {
            DiagnosticsState.SetStartupPhase("application_configuration");
            ApplicationConfiguration.Initialize();
            DiagnosticsState.SetStartupPhase("main_form_create");
            using var session = new HostSession(VersionInfo.Current);
            using var form = new MainForm(session);
            DiagnosticsState.SetStartupPhase("application_run");
            Application.Run(form);
            DiagnosticsState.SetStartupPhase("application_exit");
        }
        catch (Exception exception)
        {
            DiagnosticsState.RecordException("application_run_failed", exception);
            MessageBox.Show(
                "Meccha Mod Menu failed to start. Diagnostic logs were written to:" +
                Environment.NewLine + paths.DiagnosticsDirectory +
                Environment.NewLine + Environment.NewLine + exception.Message,
                "Meccha Mod Menu",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
