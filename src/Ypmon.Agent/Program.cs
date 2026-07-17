using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Ypmon.Agent.Services;
using Ypmon.Agent.Ui;

namespace Ypmon.Agent;

internal static class Program
{
    // Системный мьютекс: задания выполняет только один экземпляр (служба ИЛИ открытое окно).
    private const string MutexName = @"Global\YpmonAgentWorker";

    /// <summary>Запущены ли мы как служба Windows.</summary>
    public static bool IsServiceMode { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        // Режим провижининга из установщика: записать адрес сервера/ключ/имя службы и выйти.
        if (args.Contains("--provision"))
        {
            ProvisionFromArgs(args);
            return;
        }

        var isService = WindowsServiceHelpers.IsWindowsService();
        var forceConsole = args.Contains("--console");
        IsServiceMode = isService;

        if (isService || forceConsole)
        {
            using var serviceMutex = TryAcquire(out _);
            BuildHost(args).Run();
            return;
        }

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

    /// <summary>Записывает в config.json параметры установщика; существующий конфиг сохраняется.</summary>
    private static void ProvisionFromArgs(string[] args)
    {
        try
        {
            var store = new ConfigStore();
            var cfg = store.Load();
            var server = GetArg(args, "--server");
            var apiKey = GetArg(args, "--apikey");
            var service = GetArg(args, "--service");
            if (!string.IsNullOrWhiteSpace(server)) cfg.ServerUrl = server!.Trim();
            if (!string.IsNullOrWhiteSpace(apiKey)) cfg.ApiKey = apiKey!.Trim();
            if (!string.IsNullOrWhiteSpace(service)) cfg.ServiceName = service!.Trim();
            store.Save(cfg);
        }
        catch { }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
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
                services.AddSingleton<SpoolStore>();
                services.AddSingleton<FolderMonitorService>();
                services.AddSingleton<EventLogReaderService>();
                services.AddSingleton<RemoteCommandHandler>();   // [YPMON-REMOTE-CMD] вырезать после релиза
                services.AddSingleton<Reporter>();
                services.AddHostedService(sp => sp.GetRequiredService<Reporter>());
                services.AddHostedService<UpdateService>();
            })
            .Build();
}
