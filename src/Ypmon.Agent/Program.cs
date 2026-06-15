using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Ypmon.Agent.Services;
using Ypmon.Agent.Ui;

namespace Ypmon.Agent;

internal static class Program
{
    // Системный мьютекс: гарантирует, что задания выполняет только один экземпляр
    // (служба ИЛИ открытое окно), без дублирования бэкапов и отчётов.
    private const string MutexName = @"Global\YpmonAgentWorker";

    /// <summary>Запущены ли мы как служба Windows (используется самообновлением).</summary>
    public static bool IsServiceMode { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        var isService = WindowsServiceHelpers.IsWindowsService();
        var forceConsole = args.Contains("--console");
        IsServiceMode = isService;

        if (isService || forceConsole)
        {
            // Фоновый режим (служба Windows): выполняем задания и шлём отчёты, окна нет.
            using var serviceMutex = TryAcquire(out _);
            BuildHost(args).Run();
            return;
        }

        // Интерактивный режим (двойной клик): окно настроек.
        // Если рабочий экземпляр (служба) ещё не запущен — поднимаем фоновые задания,
        // чтобы агент функционировал, пока открыто окно. Иначе только правим настройки.
        using var mutex = TryAcquire(out var acquired);
        IHost? host = null;
        if (acquired)
        {
            host = BuildHost(args);
            host.Start();
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(workerRunningHere: acquired));

        if (host is not null)
        {
            try { host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); } catch { }
            host.Dispose();
        }
    }

    private static Mutex? TryAcquire(out bool acquired)
    {
        try
        {
            var m = new Mutex(false, MutexName);
            try { acquired = m.WaitOne(TimeSpan.Zero, false); }
            catch (AbandonedMutexException) { acquired = true; }
            return m;
        }
        catch
        {
            // Нет прав на глобальный объект — не запускаем фоновую работу здесь,
            // чтобы не дублировать задания службы. Окно остаётся доступным для настройки.
            acquired = false;
            return null;
        }
    }

    private static IHost BuildHost(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseContentRoot(AppContext.BaseDirectory)
            .UseWindowsService(o => o.ServiceName = "YpmonAgent")
            .ConfigureServices(services =>
            {
                services.AddSingleton<ConfigStore>();
                services.AddSingleton<BackupRunner>();
                services.AddSingleton<Reporter>();
                services.AddHostedService(sp => sp.GetRequiredService<BackupRunner>());
                services.AddHostedService(sp => sp.GetRequiredService<Reporter>());
                services.AddHostedService<UpdateService>();
            })
            .Build();
}
