using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Ypmon.Agent.Services;
using Ypmon.Shared;

namespace Ypmon.Agent.Ui;

/// <summary>
/// Главная страница агента — сводка по этой машине: температура процессора, диски,
/// задания архивации, связь с сервером и состояние службы.
///
/// Разделение источников данных сделано намеренно:
///  • «дешёвые» локальные показатели (диски, температура, память, аптайм) читаются здесь и сейчас;
///  • задания архивации берутся из снимка, который пишет служба, — опрос сетевой шары может
///    подвесить интерфейс на секунды, если она недоступна.
/// </summary>
public class HomePage : UserControl
{
    private readonly ConfigStore _store;
    private readonly SpoolStore _spool = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };

    private readonly FlowLayoutPanel _cards;
    private Label _title = null!, _subtitle = null!;
    private Label _cpuValue = null!, _cpuNote = null!;
    private Panel _disksBody = null!, _jobsBody = null!, _netBody = null!, _linkBody = null!, _sysBody = null!;

    private static readonly Color Ink = Color.FromArgb(32, 38, 48);
    private static readonly Color Muted = Color.FromArgb(110, 118, 130);
    private static readonly Color Line = Color.FromArgb(214, 219, 226);
    private static readonly Color Ok = Color.FromArgb(31, 138, 76);
    private static readonly Color Warn = Color.FromArgb(197, 116, 12);
    private static readonly Color Err = Color.FromArgb(190, 48, 48);

    public HomePage(ConfigStore store)
    {
        _store = store;
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Padding = new Padding(14, 12, 14, 12);
        // Прокрутка только у панели карточек — иначе получаем две вложенные полосы прокрутки.
        AutoScroll = false;

        _cards = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, AutoScroll = true
        };

        var header = new Panel { Dock = DockStyle.Top, Height = 54 };
        _title = new Label { Font = new Font(Font.FontFamily, 14, FontStyle.Bold), ForeColor = Ink, AutoSize = true, Left = 2, Top = 2 };
        _subtitle = new Label { ForeColor = Muted, AutoSize = true, Left = 2, Top = 28 };
        header.Controls.Add(_title);
        header.Controls.Add(_subtitle);

        Controls.Add(_cards);
        Controls.Add(header);

        _cards.Controls.Add(BuildCpuCard());
        _cards.Controls.Add(BuildCard("💽 Диски", out _disksBody));
        _cards.Controls.Add(BuildCard("📦 Задания архивации", out _jobsBody));
        _cards.Controls.Add(BuildCard("🔗 Связь с сервером", out _linkBody));
        _cards.Controls.Add(BuildCard("🌐 Сеть", out _netBody));
        _cards.Controls.Add(BuildCard("🖥️ Система", out _sysBody));

        _timer.Tick += (_, _) => Refresh0();
        VisibleChanged += (_, _) => { if (Visible) { Refresh0(); _timer.Start(); } else _timer.Stop(); };
    }

    // ================= Карточки =================

    private Panel BuildCard(string title, out Panel body)
    {
        var card = new Panel
        {
            // Ширина подобрана так, чтобы две колонки помещались при штатной ширине окна
            // с учётом полосы прокрутки панели карточек.
            Width = 282, Height = 168, Margin = new Padding(0, 0, 10, 10),
            BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(12, 10, 12, 10)
        };
        body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var head = new Label
        {
            Text = title, Dock = DockStyle.Top, Height = 24,
            Font = new Font(Font.FontFamily, 10, FontStyle.Bold), ForeColor = Ink
        };
        card.Controls.Add(body);
        card.Controls.Add(head);
        return card;
    }

    private Panel BuildCpuCard()
    {
        var card = BuildCard("🔥 Температура процессора", out var body);
        _cpuValue = new Label { Dock = DockStyle.Top, Height = 42, Font = new Font(Font.FontFamily, 22, FontStyle.Bold), ForeColor = Ink };
        _cpuNote = new Label { Dock = DockStyle.Fill, ForeColor = Muted };
        body.Controls.Add(_cpuNote);
        body.Controls.Add(_cpuValue);
        return card;
    }

    /// <summary>Строка «подпись — значение» внутри карточки.</summary>
    private static void Row(Panel body, string label, string value, Color? color = null, bool bold = false)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 21 };
        row.Controls.Add(new Label
        {
            Text = value, Dock = DockStyle.Fill, ForeColor = color ?? Ink, AutoEllipsis = true,
            Font = bold ? new Font(row.Font, FontStyle.Bold) : row.Font, TextAlign = ContentAlignment.MiddleLeft
        });
        row.Controls.Add(new Label
        {
            Text = label, Dock = DockStyle.Left, Width = 118, ForeColor = Muted,
            TextAlign = ContentAlignment.MiddleLeft
        });
        body.Controls.Add(row);
        row.BringToFront();
    }

    /// <summary>Диск: подпись и полоса заполнения (занято — цветом по степени заполнения).</summary>
    private static void DiskRow(Panel body, DiskStatusDto d)
    {
        var usedPct = d.TotalBytes > 0 ? (d.TotalBytes - d.FreeBytes) * 100.0 / d.TotalBytes : 0;
        var color = usedPct >= 90 ? Err : usedPct >= 80 ? Warn : Ok;

        var row = new Panel { Dock = DockStyle.Top, Height = 38 };
        row.Controls.Add(new Label
        {
            Text = $"{d.Name} {(string.IsNullOrWhiteSpace(d.Label) ? "" : "(" + d.Label + ") ")}"
                 + $"— занято {usedPct:0}% из {Bytes(d.TotalBytes)}",
            Dock = DockStyle.Top, Height = 18, ForeColor = Ink, AutoEllipsis = true
        });

        var track = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Color.FromArgb(232, 236, 241), Margin = new Padding(0, 2, 0, 0) };
        var fill = new Panel { Dock = DockStyle.Left, BackColor = color, Width = 0 };
        track.Controls.Add(fill);
        // Ширина полосы известна только после раскладки — считаем от фактической ширины дорожки.
        track.Resize += (_, _) => fill.Width = (int)(track.ClientSize.Width * Math.Clamp(usedPct, 0, 100) / 100.0);
        row.Controls.Add(track);
        track.BringToFront();

        body.Controls.Add(row);
        row.BringToFront();
    }

    // ================= Обновление данных =================

    /// <summary>Перерисовать все карточки. Имя с нулём — чтобы не перекрывать Control.Refresh().</summary>
    public void Refresh0()
    {
        var cfg = _store.Load();
        var snap = _store.LoadSnapshot();

        _title.Text = Environment.MachineName;
        var who = new List<string>();
        if (!string.IsNullOrWhiteSpace(snap.ResolvedClientName)) who.Add(snap.ResolvedClientName!);
        if (!string.IsNullOrWhiteSpace(snap.ResolvedServerName)) who.Add(snap.ResolvedServerName!);
        _subtitle.Text = who.Count > 0
            ? string.Join(" · ", who) + "  —  как этот компьютер записан на сервере"
            : "сервер ещё не подтвердил, к какому клиенту относится этот компьютер";

        FillCpu(cfg, snap);
        FillDisks();
        FillJobs(cfg, snap);
        FillLink(cfg, snap);
        FillNetwork(cfg);
        FillSystem();
    }

    private void FillCpu(AgentConfig cfg, AgentSnapshot snap)
    {
        double? temp = null;
        string note;

        if (OperatingSystem.IsWindows())
        {
            try { temp = CpuTemperatureCollector.Read(); } catch { }
        }

        if (temp is not null)
        {
            note = $"Порог перегрева: {(cfg.CpuTempTriggerC > 0 ? cfg.CpuTempTriggerC + " °C" : "не задан")}";
        }
        else if (snap.LastReport?.CpuTemperatureC is { } fromService)
        {
            // Окно настроек часто работает без прав администратора и датчик прочитать не может,
            // а служба (под LocalSystem) может — берём её значение из снимка.
            temp = fromService;
            note = $"По данным службы от {Ago(snap.LastReportAt)}. "
                 + $"Порог перегрева: {(cfg.CpuTempTriggerC > 0 ? cfg.CpuTempTriggerC + " °C" : "не задан")}";
        }
        else
        {
            note = "Нет данных: " + (OperatingSystem.IsWindows()
                ? (CpuTemperatureCollector.LastUnavailableReason ?? "датчик недоступен")
                : "не Windows");
        }

        if (temp is { } t)
        {
            _cpuValue.Text = $"{t:0.#} °C";
            _cpuValue.ForeColor = cfg.CpuTempTriggerC > 0 && t >= cfg.CpuTempTriggerC ? Err
                                : cfg.CpuTempTriggerC > 0 && t >= cfg.CpuTempTriggerC - 10 ? Warn : Ok;
        }
        else
        {
            _cpuValue.Text = "—";
            _cpuValue.ForeColor = Muted;
        }
        _cpuNote.Text = note;
    }

    private void FillDisks()
    {
        _disksBody.Controls.Clear();
        var disks = Reporter.CollectDisks();
        if (disks.Count == 0) { Row(_disksBody, "", "нет данных о дисках", Muted); return; }
        foreach (var d in disks.OrderBy(x => x.Name)) DiskRow(_disksBody, d);
    }

    private void FillJobs(AgentConfig cfg, AgentSnapshot snap)
    {
        _jobsBody.Controls.Clear();

        var configured = cfg.Folders?.Count(f => f.Enabled) ?? 0;
        // Состояние заданий берём из снимка службы: опрос сетевой папки здесь
        // подвесил бы окно, если шара недоступна.
        var folders = snap.LastReport?.Folders ?? new List<FolderStatusDto>();

        Row(_jobsBody, "Настроено", configured.ToString(), bold: true);
        if (folders.Count == 0)
        {
            Row(_jobsBody, "Состояние", configured == 0
                ? "задания не настроены"
                : "служба ещё не присылала отчёт", Muted);
            return;
        }

        var bad = folders.Count(f => !f.Accessible);
        Row(_jobsBody, "Недоступно", bad == 0 ? "нет" : bad.ToString(), bad == 0 ? Ok : Err, bad > 0);
        Row(_jobsBody, "Файлов всего", folders.Sum(f => f.FileCount).ToString());
        Row(_jobsBody, "Объём копий", Bytes(folders.Sum(f => f.TotalSizeBytes)));

        var newest = folders.Where(f => f.LastBackupAt is not null).Max(f => f.LastBackupAt);
        Row(_jobsBody, "Свежая копия", newest is null ? "нет данных" : Ago(newest));

        foreach (var f in folders.Take(4))
            Row(_jobsBody, "• " + f.Name, f.Accessible
                ? $"{f.FileCount} файл., {Bytes(f.TotalSizeBytes)}"
                : "недоступна", f.Accessible ? Ink : Err);
    }

    private void FillLink(AgentConfig cfg, AgentSnapshot snap)
    {
        _linkBody.Controls.Clear();
        Row(_linkBody, "Сервер", string.IsNullOrWhiteSpace(cfg.ServerUrl) ? "не задан" : cfg.ServerUrl,
            string.IsNullOrWhiteSpace(cfg.ServerUrl) ? Err : Ink);

        var accepted = snap.LastReportAccepted;
        Row(_linkBody, "Последний отчёт", snap.LastReportAt is null ? "ещё не отправлялся" : Ago(snap.LastReportAt),
            snap.LastReportAt is null ? Muted : Ink);
        Row(_linkBody, "Результат", snap.LastReportMessage ?? (accepted ? "принят" : "нет данных"),
            accepted ? Ok : Warn, bold: true);

        var queued = _spool.Count();
        Row(_linkBody, "В очереди", queued == 0 ? "пусто" : $"{queued} отчёт(ов) не отправлено",
            queued == 0 ? Ok : Warn, queued > 0);

        if (!string.IsNullOrWhiteSpace(snap.LastError))
            Row(_linkBody, "Ошибка", snap.LastError!, Err);
    }

    private void FillNetwork(AgentConfig cfg)
    {
        _netBody.Controls.Clear();
        Row(_netBody, "Имя машины", Environment.MachineName, bold: true);

        var ips = LocalIps();
        Row(_netBody, "IP-адреса", ips.Count == 0 ? "не определены" : string.Join(", ", ips.Take(3)));
        Row(_netBody, "Интервал связи", Duration(cfg.HeartbeatIntervalSeconds));
        Row(_netBody, "Интервал отчёта", Duration(cfg.ReportIntervalSeconds));
    }

    private void FillSystem()
    {
        _sysBody.Controls.Clear();
        Row(_sysBody, "Версия агента", Reporter.Version, bold: true);

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Row(_sysBody, "Аптайм", $"{(int)up.TotalDays} дн. {up.Hours} ч. {up.Minutes} мин.");
        Row(_sysBody, "Загрузка с", DateTime.Now.Subtract(up).ToString("yyyy-MM-dd HH:mm"));

        var (freeMb, totalMb) = Memory();
        if (totalMb > 0)
        {
            var usedPct = (totalMb - freeMb) * 100.0 / totalMb;
            Row(_sysBody, "Память", $"занято {usedPct:0}% из {totalMb / 1024.0:0.#} ГБ",
                usedPct >= 90 ? Err : usedPct >= 80 ? Warn : Ink);
        }
        Row(_sysBody, "ОС", "Windows " + Environment.OSVersion.Version);
    }

    // ================= Сбор системных данных =================

    private static List<string> LocalIps()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                        list.Add(a.Address.ToString());
            }
        }
        catch { }
        return list;
    }

    /// <summary>Свободная и общая физическая память, МБ (0 — не удалось определить).</summary>
    private static (double freeMb, double totalMb) Memory()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                var free = Convert.ToDouble(mo["FreePhysicalMemory"]) / 1024.0;   // из КБ в МБ
                var total = Convert.ToDouble(mo["TotalVisibleMemorySize"]) / 1024.0;
                return (free, total);
            }
        }
        catch { }
        return (0, 0);
    }

    // ================= Форматирование =================

    private static string Bytes(long b)
    {
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = b; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    private static string Duration(int seconds)
    {
        if (seconds <= 0) return "—";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} ч." + (t.Minutes > 0 ? $" {t.Minutes} мин." : "");
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} мин." + (t.Seconds > 0 ? $" {t.Seconds} с." : "");
        return $"{(int)t.TotalSeconds} с.";
    }

    private static string Ago(DateTimeOffset? at)
    {
        if (at is null) return "—";
        var d = DateTimeOffset.UtcNow - at.Value;
        if (d.TotalSeconds < 60) return "только что";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} мин. назад";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} ч. назад";
        return $"{(int)d.TotalDays} дн. назад";
    }
}
