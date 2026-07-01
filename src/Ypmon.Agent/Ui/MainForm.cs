using System.Text;
using Ypmon.Agent.Services;
using Ypmon.Shared;

namespace Ypmon.Agent.Ui;

/// <summary>
/// Окно настройки агента. Всё хранится локально в config.json; агент не слушает порт
/// и не принимает команд от сервера. Настройка службы и её заданий — только здесь.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ConfigStore _store = new();
    private AgentConfig _cfg;
    private readonly bool _workerRunningHere;
    private bool _loading;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private enum JobKind { Pg, File, Folder }
    private sealed record JobRef(JobKind Kind, int Index);

    // Подключение
    private TextBox _serverUrl = null!, _apiKey = null!, _pgDumpPath = null!;
    private NumericUpDown _statusH = null!, _statusM = null!, _statusS = null!;
    private NumericUpDown _availH = null!, _availM = null!, _availS = null!;
    private Label _hostState = null!;

    // Задания (объединённые)
    private DataGridView _jobGrid = null!;
    private bool _refreshingList;
    private readonly List<JobRef> _jobRefs = new();
    private Panel _pgPanel = null!, _filePanel = null!, _folderPanel = null!, _emptyPanel = null!;
    private PostgresBackupJob? _curPg;
    private FileArchiveJob? _curFile;
    private FolderMonitorJob? _curFolder;

    // Поля Postgres
    private TextBox _pgName = null!, _pgHost = null!, _pgUser = null!, _pgPass = null!, _pgDir = null!, _pgArgs = null!;
    private ComboBox _pgDb = null!;
    private NumericUpDown _pgPort = null!, _pgRetention = null!, _pgInterval = null!;
    private CheckBox _pgEnabled = null!;
    private GroupBox _pgConnGroup = null!;
    private Panel _pgConnSummary = null!;
    private Label _pgConnSummaryLabel = null!;

    // Поля Файлы
    private TextBox _fileName = null!, _fileSources = null!, _fileDir = null!, _fileNetUser = null!, _fileNetPass = null!;
    private NumericUpDown _fileRetention = null!, _fileInterval = null!;
    private CheckBox _fileEnabled = null!;

    // Поля Мониторинг папки
    private TextBox _folName = null!, _folBackupDir = null!, _folPattern = null!, _folLogsDir = null!, _folLogsPattern = null!, _folNetUser = null!, _folNetPass = null!;
    private NumericUpDown _folMaxAge = null!;
    private CheckBox _folEnabled = null!;

    // MSSQL
    private CheckBox _mssqlEnabled = null!;
    private TextBox _mssqlFolder = null!, _mssqlPattern = null!;

    // Журнал событий Windows
    private CheckBox _evtEnabled = null!, _evtWarnings = null!;
    private TextBox _evtLogs = null!, _evtExclude = null!;
    private NumericUpDown _evtMax = null!, _evtLookback = null!;

    // Служба / обновления
    private TextBox _svcName = null!, _svcUser = null!, _svcPass = null!;
    private Label _svcStatus = null!;
    private CheckBox _autoUpdate = null!;

    // Статус / Отчёт
    private TextBox _statusBox = null!;
    private DataGridView _reportGrid = null!;

    public MainForm(bool workerRunningHere)
    {
        _workerRunningHere = workerRunningHere;
        _cfg = _store.Load();

        Text = "YPMon — Агент (настройка)";
        Width = 800;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildConnectionTab());
        tabs.TabPages.Add(BuildJobsTab());
        tabs.TabPages.Add(BuildMssqlTab());
        tabs.TabPages.Add(BuildEventLogTab());
        tabs.TabPages.Add(BuildReportTab());
        tabs.TabPages.Add(BuildServiceTab());
        tabs.TabPages.Add(BuildStatusTab());

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        var btnSave = new Button { Text = "💾 Сохранить всё", Width = 150, Height = 32, Left = 12, Top = 9 };
        btnSave.Click += (_, _) => SaveAll(true);
        var btnRun = new Button { Text = "▶️ Выполнить бэкап сейчас", Width = 200, Height = 32, Left = 170, Top = 9 };
        btnRun.Click += (_, _) => RunNow();
        var btnHelp = new Button { Text = "❓ Справка", Width = 110, Height = 32, Left = 378, Top = 9 };
        btnHelp.Click += (_, _) => ShowHelp();
        bottom.Controls.Add(btnSave);
        bottom.Controls.Add(btnRun);
        bottom.Controls.Add(btnHelp);

        Controls.Add(tabs);
        Controls.Add(bottom);

        LoadToControls();
    }

    // ---------- Подключение ----------
    private TabPage BuildConnectionTab()
    {
        var page = new TabPage("Подключение");
        var t = NewTable();
        _serverUrl = AddText(t, "Адрес сервера YPMon (http://10.0.0.1:8080)");
        _apiKey = AddText(t, "API-ключ сервера");
        (_statusH, _statusM, _statusS) = AddDuration(t, "Отчёт о состоянии ПК (ч / м / с)");
        (_availH, _availM, _availS) = AddDuration(t, "Отчёт о доступности сервера (ч / м / с)");
        _pgDumpPath = AddText(t, "Путь к pg_dump (пусто = автопоиск)");
        var btnPgDump = new Button { Text = "🔎 Найти / проверить pg_dump", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        btnPgDump.Click += (_, _) =>
        {
            var (ok, msg, path) = PgDumpResolver.Check(_pgDumpPath.Text.Trim());
            if (ok && path is not null) _pgDumpPath.Text = path;
            Info(msg);
        };
        AddFull(t, btnPgDump);

        _hostState = new Label { AutoSize = true, ForeColor = _workerRunningHere ? Color.Green : Color.DimGray, Margin = new Padding(3, 8, 3, 3) };
        _hostState.Text = _workerRunningHere
            ? "Фоновая работа активна: пока окно открыто, задания выполняются и отчёты отправляются."
            : "Задания выполняет установленная служба YpmonAgent. Изменения применятся автоматически.";
        AddFull(t, _hostState);

        var btnTest = new Button { Text = "🔌 Проверить соединение с сервером", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        btnTest.Click += (_, _) => TestConnection();
        AddFull(t, btnTest);

        var note = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(480, 0), Margin = new Padding(3, 10, 3, 3) };
        note.Text = "Безопасность: агент устанавливает только исходящее соединение к серверу и " +
                    "не принимает никаких команд. Управление заданиями возможно лишь из этого окна.";
        AddFull(t, note);

        page.Controls.Add(t);
        return page;
    }

    // ---------- Задания архивации: таблица заданий сверху + свойства снизу ----------
    private TabPage BuildJobsTab()
    {
        var page = new TabPage("Задания архивации");

        // Свойства выбранного задания (заполняют нижнюю часть)
        var host = new Panel { Dock = DockStyle.Fill };
        _pgPanel = BuildPgPanel();
        _filePanel = BuildFilePanel();
        _folderPanel = BuildFolderPanel();
        _emptyPanel = new Panel { Dock = DockStyle.Fill };
        _emptyPanel.Controls.Add(new Label { Text = "Выберите задание в таблице или добавьте новое.", AutoSize = true, Left = 16, Top = 16, ForeColor = Color.DimGray });
        host.Controls.Add(_pgPanel);
        host.Controls.Add(_filePanel);
        host.Controls.Add(_folderPanel);
        host.Controls.Add(_emptyPanel);
        _pgPanel.Visible = _filePanel.Visible = _folderPanel.Visible = false;

        // Таблица заданий (как список задач в Exiland Backup)
        _jobGrid = new DataGridView
        {
            Dock = DockStyle.Top,
            Height = 190,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle
        };
        _jobGrid.Columns.Add("type", "Тип");
        _jobGrid.Columns.Add("name", "Название");
        _jobGrid.Columns.Add("enabled", "Вкл");
        _jobGrid.Columns.Add("sched", "Расписание");
        _jobGrid.Columns["type"]!.FillWeight = 22;
        _jobGrid.Columns["name"]!.FillWeight = 38;
        _jobGrid.Columns["enabled"]!.FillWeight = 10;
        _jobGrid.Columns["sched"]!.FillWeight = 30;
        _jobGrid.SelectionChanged += (_, _) => { if (!_refreshingList) SelectJob(_jobGrid.CurrentRow?.Index ?? -1); };

        // Тулбар
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(4) };
        var addPg = new Button { Text = "+ PostgreSQL", AutoSize = true };
        addPg.Click += (_, _) => AddPgJob();
        var addFile = new Button { Text = "+ Архив папок/файлов", AutoSize = true };
        addFile.Click += (_, _) => AddFileJob();
        var addFolder = new Button { Text = "+ Мониторинг папки", AutoSize = true };
        addFolder.Click += (_, _) => AddFolderJob();
        var delBtn = new Button { Text = "Удалить", AutoSize = true, ForeColor = Color.Firebrick };
        delBtn.Click += (_, _) => DeleteJob();
        toolbar.Controls.AddRange(new Control[] { addPg, addFile, addFolder, delBtn });

        // Порядок добавления: host (Fill) первым, затем grid и toolbar сверху
        page.Controls.Add(host);
        page.Controls.Add(_jobGrid);
        page.Controls.Add(toolbar);
        return page;
    }

    private Panel BuildFolderPanel()
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var t = NewTable();
        var head = new Label { Text = "Задание: Мониторинг готовой папки бэкапов (AOMEI и др.)", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        AddFull(t, head);
        _folName = AddText(t, "Название");
        _folEnabled = AddCheck(t, "Включено");
        _folBackupDir = AddBrowse(t, "Папка с бэкапами (можно сетевую \\\\сервер\\папка)");
        _folPattern = AddText(t, "Маска файлов (напр. *.adi, *.bak, *.zip, *.*)");
        _folMaxAge = AddNum(t, "Тревога, если свежий бэкап старше N часов (0 = выкл)", 0, 100000);
        _folLogsDir = AddBrowse(t, "Папка логов (опционально)");
        _folLogsPattern = AddText(t, "Маска логов (*.txt)");
        _folNetUser = AddText(t, "Учётная запись Windows для сетевых папок (DOMAIN\\User)");
        _folNetPass = AddText(t, "Пароль учётной записи", true);
        var btnAccess = new Button { Text = "🔐 Проверить доступ", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        btnAccess.Click += (_, _) => FolderCheckAccess();
        AddFull(t, btnAccess);
        var note = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(480, 0), Margin = new Padding(3, 6, 3, 3) };
        note.Text = "Агент сам ничего не создаёт — только читает количество, объём и дату последней копии " +
                    "из этой папки и шлёт в отчёт. Подходит для бэкапов, сделанных AOMEI и любыми программами.";
        AddFull(t, note);
        WireFolderEvents();
        p.Controls.Add(t);
        return p;
    }

    private void MssqlCheck()
    {
        var cfg = new MssqlLogConfig
        {
            Enabled = _mssqlEnabled.Checked,
            LogFolder = _mssqlFolder.Text.Trim(),
            FilePattern = string.IsNullOrWhiteSpace(_mssqlPattern.Text) ? "*.txt" : _mssqlPattern.Text.Trim()
        };
        if (string.IsNullOrWhiteSpace(cfg.LogFolder)) { Info("Укажите папку с логами."); return; }
        if (!Directory.Exists(cfg.LogFolder)) { Info("❌ Папка не найдена: " + cfg.LogFolder); return; }

        var logs = MssqlLogReader.ListLogs(new MssqlLogConfig { Enabled = true, LogFolder = cfg.LogFolder, FilePattern = cfg.FilePattern });
        if (logs.Count == 0) { Info("Папка доступна, но логи по маске не найдены."); return; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Найдено логов: {logs.Count}. Последние:");
        foreach (var l in logs.Take(5))
        {
            var content = MssqlLogReader.ReadTail(l.FullPath, 8 * 1024);
            sb.AppendLine($"• {l.Name} ({l.Modified:yyyy-MM-dd HH:mm}) → {MssqlLogReader.DetectOutcome(content)}");
        }
        Info(sb.ToString());
    }

    private void FolderCheckAccess()
    {
        CommitCurrent();
        if (_curFolder is null) { Info("Выберите задание мониторинга папки."); return; }
        var sb = new System.Text.StringBuilder();
        foreach (var path in new[] { _curFolder.BackupFolder, _curFolder.LogsFolder }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var (ok, m) = NetShare.CheckAccess(path, _curFolder.NetworkUsername, _curFolder.NetworkPassword);
            sb.AppendLine($"{(ok ? "✅" : "❌")} {path} — {m}");
        }
        if (sb.Length == 0) sb.AppendLine("Не указаны папки.");
        Info("Проверка доступа:\n\n" + sb);
    }

    private Panel BuildPgPanel()
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var t = NewTable();
        var head = new Label { Text = "Задание: Бэкап PostgreSQL", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        AddFull(t, head);
        _pgName = AddText(t, "Название");
        _pgEnabled = AddCheck(t, "Включено");

        // Реквизиты подключения — в сворачиваемой группе (#2: скрываются после успешной проверки)
        _pgConnGroup = new GroupBox { Text = "Реквизиты подключения", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(3) };
        var ct = NewTable();
        ct.Dock = DockStyle.Top; ct.AutoSize = true;
        _pgHost = AddText(ct, "Хост");
        _pgPort = AddNum(ct, "Порт", 1, 65535);
        _pgUser = AddText(ct, "Пользователь");
        _pgPass = AddText(ct, "Пароль", true);
        var btnPgTest = new Button { Text = "🔌 Проверить соединение и получить список БД", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        btnPgTest.Click += (_, _) => PgTestConnection();
        AddFull(ct, btnPgTest);
        _pgConnGroup.Controls.Add(ct);
        AddFull(t, _pgConnGroup);

        // Свёрнутый вид: краткая строка + кнопка «Изменить»
        _pgConnSummary = new Panel { AutoSize = true, Visible = false, Margin = new Padding(3) };
        _pgConnSummaryLabel = new Label { AutoSize = true, ForeColor = Color.Green, Location = new Point(0, 6) };
        var btnEdit = new Button { Text = "Изменить", AutoSize = true, Location = new Point(0, 24) };
        btnEdit.Click += (_, _) => SetPgConnCollapsed(false);
        _pgConnSummary.Controls.Add(_pgConnSummaryLabel);
        _pgConnSummary.Controls.Add(btnEdit);
        AddFull(t, _pgConnSummary);

        _pgDb = AddCombo(t, "База данных (выберите из списка или впишите)");
        _pgDir = AddBrowse(t, "Папка для бэкапов");
        _pgRetention = AddNum(t, "Хранить копий (0 = без удаления)", 0, 10000);
        _pgInterval = AddNum(t, "Интервал, мин", 0, 1000000);
        _pgArgs = AddText(t, "Доп. аргументы pg_dump");
        WirePgEvents();
        p.Controls.Add(t);
        return p;
    }

    private Panel BuildFilePanel()
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var t = NewTable();
        var head = new Label { Text = "Задание: Архив папок/файлов (ZIP)", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        AddFull(t, head);
        _fileName = AddText(t, "Название");
        _fileEnabled = AddCheck(t, "Включено");
        _fileSources = AddMultiline(t, "Источники (папки/файлы; можно сетевые \\\\сервер\\папка)");
        _fileDir = AddBrowse(t, "Папка для архивов (можно сетевую \\\\сервер\\папка)");
        _fileRetention = AddNum(t, "Хранить архивов (0 = без удаления)", 0, 10000);
        _fileInterval = AddNum(t, "Интервал, мин", 0, 1000000);
        _fileNetUser = AddText(t, "Учётная запись Windows для сетевых папок (DOMAIN\\User)");
        _fileNetPass = AddText(t, "Пароль учётной записи", true);
        var btnAccess = new Button { Text = "🔐 Проверить доступ к папкам", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        btnAccess.Click += (_, _) => FileCheckAccess();
        AddFull(t, btnAccess);
        WireFileEvents();
        p.Controls.Add(t);
        return p;
    }

    // ---------- MSSQL ----------
    private TabPage BuildMssqlTab()
    {
        var page = new TabPage("Логи MSSQL");
        var t = NewTable();
        _mssqlEnabled = AddCheck(t, "Включить просмотр логов MSSQL");
        _mssqlFolder = AddBrowse(t, "Папка с логами");
        _mssqlPattern = AddText(t, "Маска файлов (*.txt)");
        var btnCheck = new Button { Text = "🔎 Проверить чтение логов", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        btnCheck.Click += (_, _) => MssqlCheck();
        AddFull(t, btnCheck);
        var note = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(480, 0), Margin = new Padding(3, 10, 3, 3) };
        note.Text = "Агент читает логи из указанной папки, определяет статус (ок/ошибка) " +
                    "и включает его в отчёт серверу. Список логов виден на вкладке «Отчёт».";
        AddFull(t, note);
        page.Controls.Add(t);
        return page;
    }

    // ---------- Журнал событий Windows ----------
    private TabPage BuildEventLogTab()
    {
        var page = new TabPage("Журнал Windows");
        var t = NewTable();
        _evtEnabled = AddCheck(t, "Читать журнал событий Windows и передавать ошибки серверу");
        _evtLogs = AddText(t, "Журналы через запятую (System, Application)");
        _evtWarnings = AddCheck(t, "Передавать и предупреждения (не только ошибки)");
        _evtMax = AddNum(t, "Максимум записей за отчёт (на журнал)", 1, 1000);
        _evtLookback = AddNum(t, "При первом запуске смотреть назад, часов", 1, 100000);
        _evtExclude = AddText(t, "Исключить источники через запятую (необязательно)");

        var btnCheck = new Button { Text = "🔎 Показать последние ошибки журнала", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        btnCheck.Click += (_, _) => EventLogPreview();
        AddFull(t, btnCheck);

        var note = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(500, 0), Margin = new Padding(3, 8, 3, 3) };
        note.Text = "Агент собирает новые ошибки (и, по желанию, предупреждения) из указанных журналов Windows " +
                    "и включает их в отчёт серверу. Только чтение, система не изменяется. " +
                    "Ошибки видны на странице сервера в веб-интерфейсе.";
        AddFull(t, note);
        page.Controls.Add(t);
        return page;
    }

    private void EventLogPreview()
    {
        if (!OperatingSystem.IsWindows()) { Info("Чтение журнала доступно только в Windows."); return; }
        var logs = (_evtLogs.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (logs.Count == 0) logs = new List<string> { "System", "Application" };
        try
        {
            var reader = new Ypmon.Agent.Services.EventLogReaderService(_store);
            // Предпросмотр не должен сдвигать отметку — читаем во временную конфигурацию с чистым состоянием.
            var previewCfg = new AgentConfig
            {
                EventLog = new EventLogConfig
                {
                    Enabled = true,
                    Logs = logs,
                    IncludeWarnings = _evtWarnings.Checked,
                    MaxEntriesPerReport = (int)_evtMax.Value,
                    LookbackHoursOnFirstRun = (int)_evtLookback.Value,
                    ExcludeSources = (_evtExclude.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                }
            };
            var list = reader.PreviewRecent(previewCfg, 20);
            if (list.Count == 0) { Info("За выбранный период ошибок в журнале не найдено."); return; }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Найдено записей: {list.Count}. Последние:");
            sb.AppendLine();
            foreach (var e in list.Take(20))
            {
                var m = e.Message.Replace("\r", " ").Replace("\n", " ");
                if (m.Length > 160) m = m[..160] + "…";
                sb.AppendLine($"[{e.Level}] {e.TimeCreated.LocalDateTime:yyyy-MM-dd HH:mm} {e.LogName}/{e.Source} (ID {e.EventId})");
                sb.AppendLine("   " + m);
            }
            Info(sb.ToString());
        }
        catch (Exception ex) { Info("Не удалось прочитать журнал: " + ex.Message); }
    }

    // ---------- Отчёт (история созданных архивов) ----------
    private TabPage BuildReportTab()
    {
        var page = new TabPage("Отчёт");
        _reportGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = SystemColors.Window
        };
        _reportGrid.Columns.Add("job", "Задание");
        _reportGrid.Columns.Add("type", "Тип");
        _reportGrid.Columns.Add("file", "Файл архива");
        _reportGrid.Columns.Add("date", "Дата создания");
        _reportGrid.Columns.Add("size", "Размер");
        _reportGrid.Columns["size"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        _reportGrid.Columns["date"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;

        var top = new Panel { Dock = DockStyle.Top, Height = 38 };
        var btn = new Button { Text = "⟳ Обновить", Left = 6, Top = 6, Width = 120 };
        btn.Click += (_, _) => RefreshReport();
        top.Controls.Add(btn);

        page.Controls.Add(_reportGrid);
        page.Controls.Add(top);
        page.Enter += (_, _) => RefreshReport();
        return page;
    }

    // ---------- Статус ----------
    private TabPage BuildStatusTab()
    {
        var page = new TabPage("Статус");
        _statusBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9F) };
        var top = new Panel { Dock = DockStyle.Top, Height = 38 };
        var btn = new Button { Text = "⟳ Обновить статус", Left = 6, Top = 6, Width = 160 };
        btn.Click += (_, _) => RefreshStatus();
        top.Controls.Add(btn);
        page.Controls.Add(_statusBox);
        page.Controls.Add(top);
        page.Enter += (_, _) => RefreshStatus();
        return page;
    }

    // ================= Загрузка/сохранение =================
    private void LoadToControls()
    {
        _loading = true;
        _serverUrl.Text = _cfg.ServerUrl;
        _apiKey.Text = _cfg.ApiKey;
        SetDur(_statusH, _statusM, _statusS, _cfg.ReportIntervalSeconds);
        SetDur(_availH, _availM, _availS, _cfg.AvailabilityIntervalSeconds);
        _pgDumpPath.Text = _cfg.PgDumpPath;
        _mssqlEnabled.Checked = _cfg.Mssql.Enabled;
        _mssqlFolder.Text = _cfg.Mssql.LogFolder;
        _mssqlPattern.Text = _cfg.Mssql.FilePattern;
        var evt = _cfg.EventLog ?? new EventLogConfig();
        _evtEnabled.Checked = evt.Enabled;
        _evtLogs.Text = string.Join(", ", evt.Logs ?? new());
        _evtWarnings.Checked = evt.IncludeWarnings;
        _evtMax.Value = Clamp(evt.MaxEntriesPerReport, 1, 1000);
        _evtLookback.Value = Clamp(evt.LookbackHoursOnFirstRun, 1, 100000);
        _evtExclude.Text = string.Join(", ", evt.ExcludeSources ?? new());
        _svcName.Text = string.IsNullOrWhiteSpace(_cfg.ServiceName) ? "YpmonAgent" : _cfg.ServiceName;
        _autoUpdate.Checked = _cfg.AutoUpdate;
        _loading = false;

        RefreshJobList();
        if (_jobRefs.Count > 0) SelectRow(0);
        else ShowPanel(null);
        RefreshSvcStatus();
    }

    private void SaveAll(bool notify)
    {
        CommitCurrent();
        _cfg.ServerUrl = _serverUrl.Text.Trim();
        _cfg.ApiKey = _apiKey.Text.Trim();
        _cfg.ReportIntervalSeconds = Math.Max(15, DurSeconds(_statusH, _statusM, _statusS));
        _cfg.AvailabilityIntervalSeconds = Math.Max(10, DurSeconds(_availH, _availM, _availS));
        _cfg.PgDumpPath = _pgDumpPath.Text.Trim();
        _cfg.Mssql.Enabled = _mssqlEnabled.Checked;
        _cfg.Mssql.LogFolder = _mssqlFolder.Text.Trim();
        _cfg.Mssql.FilePattern = string.IsNullOrWhiteSpace(_mssqlPattern.Text) ? "*.txt" : _mssqlPattern.Text.Trim();
        _cfg.EventLog ??= new EventLogConfig();
        _cfg.EventLog.Enabled = _evtEnabled.Checked;
        _cfg.EventLog.Logs = SplitCsv(_evtLogs.Text, "System", "Application");
        _cfg.EventLog.IncludeWarnings = _evtWarnings.Checked;
        _cfg.EventLog.MaxEntriesPerReport = (int)_evtMax.Value;
        _cfg.EventLog.LookbackHoursOnFirstRun = (int)_evtLookback.Value;
        _cfg.EventLog.ExcludeSources = SplitCsv(_evtExclude.Text);
        _cfg.ServiceName = string.IsNullOrWhiteSpace(_svcName.Text) ? "YpmonAgent" : _svcName.Text.Trim();
        _cfg.AutoUpdate = _autoUpdate.Checked;

        _store.Save(_cfg);
        if (notify)
            MessageBox.Show(this, "Настройки сохранены в config.json.\nРабочий процесс применит их автоматически.",
                "YPMon", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ================= Список заданий =================
    private void RefreshJobList()
    {
        var prev = _jobGrid.CurrentRow?.Index ?? -1;
        _refreshingList = true;
        _jobRefs.Clear();
        _jobGrid.Rows.Clear();
        foreach (var j in _cfg.PostgresJobs)
        {
            _jobRefs.Add(new JobRef(JobKind.Pg, _jobRefs.Count(r => r.Kind == JobKind.Pg)));
            _jobGrid.Rows.Add("PostgreSQL", j.Name, j.Enabled ? "✓" : "—", $"каждые {j.IntervalMinutes} мин");
        }
        foreach (var j in _cfg.FileArchiveJobs)
        {
            _jobRefs.Add(new JobRef(JobKind.File, _jobRefs.Count(r => r.Kind == JobKind.File)));
            _jobGrid.Rows.Add("Архив папок", j.Name, j.Enabled ? "✓" : "—", $"каждые {j.IntervalMinutes} мин");
        }
        foreach (var j in _cfg.FolderMonitorJobs)
        {
            _jobRefs.Add(new JobRef(JobKind.Folder, _jobRefs.Count(r => r.Kind == JobKind.Folder)));
            _jobGrid.Rows.Add("Мониторинг", j.Name, j.Enabled ? "✓" : "—", "при отчёте");
        }
        _refreshingList = false;
        if (prev >= 0 && prev < _jobGrid.Rows.Count) SelectRow(prev);
    }

    private void SelectRow(int i)
    {
        if (i < 0 || i >= _jobGrid.Rows.Count) return;
        _jobGrid.ClearSelection();
        _jobGrid.Rows[i].Selected = true;
        _jobGrid.CurrentCell = _jobGrid.Rows[i].Cells[1];
    }

    private void SelectJob(int idx)
    {
        CommitCurrent();
        _curPg = null; _curFile = null; _curFolder = null;
        if (idx < 0 || idx >= _jobRefs.Count) { ShowPanel(null); return; }
        var r = _jobRefs[idx];
        switch (r.Kind)
        {
            case JobKind.Pg:
                _curPg = _cfg.PostgresJobs[r.Index]; PopulatePg(_curPg); ShowPanel(_pgPanel); break;
            case JobKind.File:
                _curFile = _cfg.FileArchiveJobs[r.Index]; PopulateFile(_curFile); ShowPanel(_filePanel); break;
            case JobKind.Folder:
                _curFolder = _cfg.FolderMonitorJobs[r.Index]; PopulateFolder(_curFolder); ShowPanel(_folderPanel); break;
        }
    }

    private void ShowPanel(Panel? p)
    {
        _pgPanel.Visible = p == _pgPanel;
        _filePanel.Visible = p == _filePanel;
        _folderPanel.Visible = p == _folderPanel;
        _emptyPanel.Visible = p is null;
        p?.BringToFront();
        if (p is null) _emptyPanel.BringToFront();
    }

    private void CommitCurrent()
    {
        if (_loading) return;
        if (_curPg is not null)
        {
            _curPg.Name = _pgName.Text; _curPg.Enabled = _pgEnabled.Checked;
            _curPg.Host = _pgHost.Text; _curPg.Port = (int)_pgPort.Value;
            _curPg.Database = _pgDb.Text; _curPg.Username = _pgUser.Text; _curPg.Password = _pgPass.Text;
            _curPg.BackupDir = _pgDir.Text; _curPg.RetentionCount = (int)_pgRetention.Value;
            _curPg.IntervalMinutes = (int)_pgInterval.Value; _curPg.ExtraArgs = _pgArgs.Text;
        }
        else if (_curFile is not null)
        {
            _curFile.Name = _fileName.Text; _curFile.Enabled = _fileEnabled.Checked;
            _curFile.SourcePaths = _fileSources.Lines.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            _curFile.ArchiveDir = _fileDir.Text;
            _curFile.RetentionCount = (int)_fileRetention.Value;
            _curFile.IntervalMinutes = (int)_fileInterval.Value;
            _curFile.NetworkUsername = _fileNetUser.Text; _curFile.NetworkPassword = _fileNetPass.Text;
        }
        else if (_curFolder is not null)
        {
            _curFolder.Name = _folName.Text; _curFolder.Enabled = _folEnabled.Checked;
            _curFolder.BackupFolder = _folBackupDir.Text; _curFolder.FilePattern = _folPattern.Text;
            _curFolder.WarnIfOlderThanHours = (int)_folMaxAge.Value;
            _curFolder.LogsFolder = _folLogsDir.Text; _curFolder.LogsPattern = _folLogsPattern.Text;
            _curFolder.NetworkUsername = _folNetUser.Text; _curFolder.NetworkPassword = _folNetPass.Text;
        }
    }

    private void PopulatePg(PostgresBackupJob j)
    {
        _loading = true;
        _pgName.Text = j.Name; _pgEnabled.Checked = j.Enabled;
        _pgHost.Text = j.Host; _pgPort.Value = Clamp(j.Port, 1, 65535);
        _pgDb.Text = j.Database; _pgUser.Text = j.Username; _pgPass.Text = j.Password;
        _pgDir.Text = j.BackupDir; _pgRetention.Value = Clamp(j.RetentionCount, 0, 10000);
        _pgInterval.Value = Clamp(j.IntervalMinutes, 0, 1000000); _pgArgs.Text = j.ExtraArgs;
        _loading = false;
        SetPgConnCollapsed(false); // при выборе задания показываем реквизиты
    }

    private void PopulateFile(FileArchiveJob j)
    {
        _loading = true;
        _fileName.Text = j.Name; _fileEnabled.Checked = j.Enabled;
        _fileSources.Text = string.Join(Environment.NewLine, j.SourcePaths);
        _fileDir.Text = j.ArchiveDir;
        _fileRetention.Value = Clamp(j.RetentionCount, 0, 10000);
        _fileInterval.Value = Clamp(j.IntervalMinutes, 0, 1000000);
        _fileNetUser.Text = j.NetworkUsername; _fileNetPass.Text = j.NetworkPassword;
        _loading = false;
    }

    private void WirePgEvents()
    {
        EventHandler h = (_, _) => { CommitCurrent(); UpdateCurrentItemText(); };
        foreach (var c in new Control[] { _pgName, _pgHost, _pgUser, _pgPass, _pgDb, _pgDir, _pgArgs }) c.TextChanged += h;
        foreach (var n in new[] { _pgPort, _pgRetention, _pgInterval }) n.ValueChanged += h;
        _pgEnabled.CheckedChanged += h;
    }

    private void WireFileEvents()
    {
        EventHandler h = (_, _) => { CommitCurrent(); UpdateCurrentItemText(); };
        foreach (var c in new Control[] { _fileName, _fileSources, _fileDir, _fileNetUser, _fileNetPass }) c.TextChanged += h;
        foreach (var n in new[] { _fileRetention, _fileInterval }) n.ValueChanged += h;
        _fileEnabled.CheckedChanged += h;
    }

    private void PopulateFolder(FolderMonitorJob j)
    {
        _loading = true;
        _folName.Text = j.Name; _folEnabled.Checked = j.Enabled;
        _folBackupDir.Text = j.BackupFolder; _folPattern.Text = j.FilePattern;
        _folMaxAge.Value = Clamp(j.WarnIfOlderThanHours, 0, 100000);
        _folLogsDir.Text = j.LogsFolder; _folLogsPattern.Text = j.LogsPattern;
        _folNetUser.Text = j.NetworkUsername; _folNetPass.Text = j.NetworkPassword;
        _loading = false;
    }

    private void WireFolderEvents()
    {
        EventHandler h = (_, _) => { CommitCurrent(); UpdateCurrentItemText(); };
        foreach (var c in new Control[] { _folName, _folBackupDir, _folPattern, _folLogsDir, _folLogsPattern, _folNetUser, _folNetPass }) c.TextChanged += h;
        _folMaxAge.ValueChanged += h;
        _folEnabled.CheckedChanged += h;
    }

    private void UpdateCurrentItemText()
    {
        if (_loading) return;
        var idx = _jobGrid.CurrentRow?.Index ?? -1;
        if (idx < 0 || idx >= _jobRefs.Count) return;
        var r = _jobRefs[idx];
        var row = _jobGrid.Rows[idx];
        switch (r.Kind)
        {
            case JobKind.Pg:
                row.Cells["name"].Value = _cfg.PostgresJobs[r.Index].Name;
                row.Cells["enabled"].Value = _cfg.PostgresJobs[r.Index].Enabled ? "✓" : "—";
                break;
            case JobKind.File:
                row.Cells["name"].Value = _cfg.FileArchiveJobs[r.Index].Name;
                row.Cells["enabled"].Value = _cfg.FileArchiveJobs[r.Index].Enabled ? "✓" : "—";
                break;
            default:
                row.Cells["name"].Value = _cfg.FolderMonitorJobs[r.Index].Name;
                row.Cells["enabled"].Value = _cfg.FolderMonitorJobs[r.Index].Enabled ? "✓" : "—";
                break;
        }
    }

    private void AddPgJob()
    {
        CommitCurrent();
        _cfg.PostgresJobs.Add(new PostgresBackupJob { Name = "Postgres " + (_cfg.PostgresJobs.Count + 1) });
        RefreshJobList();
        SelectRow(_cfg.PostgresJobs.Count - 1); // pg-задания идут первыми
    }

    private void AddFileJob()
    {
        CommitCurrent();
        _cfg.FileArchiveJobs.Add(new FileArchiveJob { Name = "Папки " + (_cfg.FileArchiveJobs.Count + 1) });
        RefreshJobList();
        SelectRow(_cfg.PostgresJobs.Count + _cfg.FileArchiveJobs.Count - 1);
    }

    private void AddFolderJob()
    {
        CommitCurrent();
        _cfg.FolderMonitorJobs.Add(new FolderMonitorJob { Name = "Бэкапы " + (_cfg.FolderMonitorJobs.Count + 1) });
        RefreshJobList();
        SelectRow(_jobRefs.Count - 1);
    }

    private void DeleteJob()
    {
        var idx = _jobGrid.CurrentRow?.Index ?? -1;
        if (idx < 0 || idx >= _jobRefs.Count) return;
        var r = _jobRefs[idx];
        _loading = true; // подавляем коммит во время удаления
        switch (r.Kind)
        {
            case JobKind.Pg: _cfg.PostgresJobs.RemoveAt(r.Index); break;
            case JobKind.File: _cfg.FileArchiveJobs.RemoveAt(r.Index); break;
            case JobKind.Folder: _cfg.FolderMonitorJobs.RemoveAt(r.Index); break;
        }
        _curPg = null; _curFile = null; _curFolder = null;
        _loading = false;
        RefreshJobList();
        if (_jobRefs.Count > 0) SelectRow(Math.Min(idx, _jobRefs.Count - 1));
        else ShowPanel(null);
    }

    // ================= Отчёт (созданные архивы) =================
    private void RefreshReport()
    {
        CommitCurrent();
        _reportGrid.Rows.Clear();
        var rows = new List<(string job, string type, string file, DateTime date, long size)>();

        foreach (var j in _cfg.PostgresJobs)
            foreach (var f in ListFiles(j.BackupDir, Sanitize(j.Database) + "_*"))
                rows.Add((j.Name, "PostgreSQL", f.Name, f.CreationTime, f.Length));

        foreach (var j in _cfg.FileArchiveJobs)
            foreach (var f in ListFiles(j.ArchiveDir, Sanitize(j.Name) + "_*.zip"))
                rows.Add((j.Name, "Папки", f.Name, f.CreationTime, f.Length));

        foreach (var r in rows.OrderByDescending(x => x.date))
            _reportGrid.Rows.Add(r.job, r.type, r.file, r.date.ToString("yyyy-MM-dd HH:mm:ss"), Bytes(r.size));

        if (rows.Count == 0)
            _reportGrid.Rows.Add("—", "—", "Архивы ещё не создавались", "", "");
    }

    private static IEnumerable<FileInfo> ListFiles(string? dir, string pattern)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return Enumerable.Empty<FileInfo>();
        try { return new DirectoryInfo(dir).GetFiles(pattern); }
        catch { return Enumerable.Empty<FileInfo>(); }
    }

    // ================= Служба и обновления =================
    private TabPage BuildServiceTab()
    {
        var page = new TabPage("Служба");
        var t = NewTable();

        var h1 = new Label { Text = "Установка службы Windows", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        AddFull(t, h1);
        _svcName = AddText(t, "Имя службы");
        _svcUser = AddText(t, "Учётная запись запуска (DOMAIN\\User, пусто = LocalSystem)");
        _svcPass = AddText(t, "Пароль учётной записи", true);

        var rowBtns = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        var bInstall = new Button { Text = "Установить / переустановить", AutoSize = true };
        bInstall.Click += (_, _) => SvcInstall();
        var bUninstall = new Button { Text = "Удалить", AutoSize = true };
        bUninstall.Click += (_, _) => SvcAction(() => ServiceManager.Uninstall(_svcName.Text.Trim()), "Удаление службы");
        var bStart = new Button { Text = "Запустить", AutoSize = true };
        bStart.Click += (_, _) => SvcAction(() => ServiceManager.Start(_svcName.Text.Trim()), "Запуск службы");
        var bStop = new Button { Text = "Остановить", AutoSize = true };
        bStop.Click += (_, _) => SvcAction(() => ServiceManager.Stop(_svcName.Text.Trim()), "Остановка службы");
        var bRefresh = new Button { Text = "Обновить статус", AutoSize = true };
        bRefresh.Click += (_, _) => RefreshSvcStatus();
        rowBtns.Controls.AddRange(new Control[] { bInstall, bUninstall, bStart, bStop, bRefresh });
        AddFull(t, rowBtns);

        _svcStatus = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        AddFull(t, _svcStatus);

        var note = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(500, 0), Margin = new Padding(3, 4, 3, 10) };
        note.Text = "Для установки требуются права администратора (появится запрос UAC). " +
                    "Указанная учётная запись должна иметь право «Вход в качестве службы».";
        AddFull(t, note);

        var h2 = new Label { Text = "Обновления агента", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 8, 3, 6) };
        AddFull(t, h2);
        _autoUpdate = AddCheck(t, "Автоматически обновлять агента с сервера (раз в день)");
        var bUpd = new Button { Text = "🔄 Проверить обновления сейчас", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        bUpd.Click += (_, _) => CheckUpdates();
        AddFull(t, bUpd);
        var note2 = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(500, 0), Margin = new Padding(3, 4, 3, 3) };
        note2.Text = "Новую версию агента администратор кладёт на сервере в папку agent-updates. " +
                     "Служба раз в день проверяет версию, скачивает и устанавливает её с перезапуском.";
        AddFull(t, note2);

        page.Controls.Add(t);
        return page;
    }

    private void RefreshSvcStatus()
    {
        if (_svcStatus is null) return;
        var (exists, running, status) = ServiceManager.Query(_svcName.Text.Trim());
        _svcStatus.Text = exists
            ? $"Статус службы «{_svcName.Text.Trim()}»: {status}" + (running ? " ✅" : "")
            : $"Служба «{_svcName.Text.Trim()}» не установлена";
        _svcStatus.ForeColor = running ? Color.Green : (exists ? Color.DarkOrange : Color.DimGray);
    }

    private void SvcInstall()
    {
        SaveAll(false);
        var name = _svcName.Text.Trim();
        var user = string.IsNullOrWhiteSpace(_svcUser.Text) ? null : _svcUser.Text.Trim();
        var log = ServiceManager.Install(name, "YPMon Agent", user, _svcPass.Text);
        RefreshSvcStatus();
        var (_, running, _) = ServiceManager.Query(name);
        if (running)
        {
            Info("Служба установлена и запущена.\n\n" + log + "\n\nОкно закроется, чтобы не дублировать задания службы.");
            Application.Exit();
        }
        else
        {
            Info("Результат установки:\n\n" + log +
                 "\n\nЕсли служба не запустилась — проверьте учётную запись и право «Вход в качестве службы».");
        }
    }

    private void SvcAction(Func<string> action, string title)
    {
        var log = action();
        RefreshSvcStatus();
        Info(title + ":\n\n" + log);
    }

    private async void CheckUpdates()
    {
        SaveAll(false);
        try
        {
            var info = await UpdateInstaller.CheckAsync(_cfg);
            if (!info.Available) { Info("На сервере нет загруженного обновления агента."); return; }
            if (!UpdateInstaller.IsNewer(info.Version, AgentVersion))
            {
                Info($"Установлена актуальная версия ({AgentVersion}). На сервере: {info.Version}.");
                return;
            }
            if (MessageBox.Show(this,
                    $"Доступна новая версия {info.Version} (текущая {AgentVersion}). Скачать и установить сейчас?",
                    "Обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            Cursor = Cursors.WaitCursor;
            string installer;
            try { installer = await UpdateInstaller.DownloadAsync(_cfg); }
            finally { Cursor = Cursors.Default; }

            UpdateInstaller.RunInstaller(installer, silent: false);
            Info("Установщик обновления запущен. Подтвердите установку (UAC) — агент обновится и перезапустится.\nЭто окно сейчас закроется.");
            Application.Exit();
        }
        catch (Exception ex) { Info("Не удалось проверить/установить обновление: " + ex.Message); }
    }

    private static string AgentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    // ================= Проверка Postgres / доступа =================
    private async void PgTestConnection()
    {
        CommitCurrent();
        if (_curPg is null) { Info("Выберите задание PostgreSQL."); return; }
        var host = _pgHost.Text.Trim(); var port = (int)_pgPort.Value;
        var user = _pgUser.Text; var pass = _pgPass.Text;
        var (ok, msg, dbs) = await Task.Run(() => PgInspector.ListDatabases(host, port, user, pass));
        if (ok)
        {
            var cur = _pgDb.Text;
            _pgDb.Items.Clear();
            foreach (var d in dbs) _pgDb.Items.Add(d);
            _pgDb.Text = cur;
            SetPgConnCollapsed(true); // #2: после успешного подключения скрываем реквизиты
        }
        Info(msg);
    }

    private void SetPgConnCollapsed(bool collapsed)
    {
        _pgConnGroup.Visible = !collapsed;
        _pgConnSummary.Visible = collapsed;
        if (collapsed)
            _pgConnSummaryLabel.Text = $"✓ {_pgUser.Text}@{_pgHost.Text}:{(int)_pgPort.Value} — подключено";
    }

    private async void FileCheckAccess()
    {
        CommitCurrent();
        if (_curFile is null) { Info("Выберите задание архивации файлов."); return; }
        var user = _fileNetUser.Text; var pass = _fileNetPass.Text;
        var paths = _curFile.SourcePaths.Concat(new[] { _curFile.ArchiveDir })
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0) { Info("Не указаны источники и папка архивов."); return; }

        var sb = new StringBuilder();
        await Task.Run(() =>
        {
            foreach (var p in paths)
            {
                var (ok, m) = NetShare.CheckAccess(p, user, pass);
                sb.AppendLine($"{(ok ? "✅" : "❌")} {p} — {m}");
            }
        });
        Info("Проверка доступа:\n\n" + sb);
    }

    private void ShowHelp()
    {
        var help = new Form
        {
            Text = "Справка — YPMon Агент",
            Width = 720, Height = 600, StartPosition = FormStartPosition.CenterParent,
            Font = new Font("Segoe UI", 9F)
        };
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        box.Text = HelpText();
        box.Select(0, 0);
        help.Controls.Add(box);
        help.ShowDialog(this);
    }

    private static string HelpText() =>
        "YPMon — Агент\r\n" +
        $"Версия: {AgentVersion}\r\n\r\n" +
        "НАЗНАЧЕНИЕ\r\n" +
        "Агент выполняет резервное копирование на сервере и отправляет итоги на центральный сервер YPMon.\r\n" +
        "Соединение строго ИСХОДЯЩЕЕ: агент не слушает порты и не принимает команд от сервера.\r\n\r\n" +
        "ПОДКЛЮЧЕНИЕ\r\n" +
        "Укажите адрес сервера YPMon и API-ключ (берётся в веб-интерфейсе сервера: Клиенты → сервер →\r\n" +
        "«Данные для подключения агента»). Кнопка «Проверить соединение» проверяет доступность сервера.\r\n\r\n" +
        "ЗАДАНИЯ АРХИВАЦИИ\r\n" +
        "Единый список заданий двух типов:\r\n" +
        " • Бэкап PostgreSQL — укажите хост, порт, пользователя, пароль и нажмите «Проверить соединение\r\n" +
        "   и получить список БД», затем выберите базу из списка. Папка для дампов, число копий, интервал.\r\n" +
        "   Используется pg_dump (путь задаётся на вкладке «Подключение»).\r\n" +
        " • Архив папок/файлов — список папок/файлов (в т.ч. сетевых \\\\сервер\\папка) пакуется в ZIP.\r\n" +
        "   Для сетевых папок укажите учётную запись Windows (логин/пароль) и проверьте доступ.\r\n\r\n" +
        "RETENTION (срок хранения)\r\n" +
        "У каждого задания — «Хранить копий N». При создании нового архива лишние старые удаляются,\r\n" +
        "пока не останется N (например, при N=7 создание 8-го удалит самый старый). 0 — не удалять.\r\n\r\n" +
        "ЛОГИ MSSQL\r\n" +
        "Укажите папку с логами архивации MSSQL — агент определит статус (ок/ошибка) и включит в отчёт.\r\n\r\n" +
        "ОТЧЁТ\r\n" +
        "Таблица созданных архивов: задание, тип, файл, дата создания, размер.\r\n\r\n" +
        "СЛУЖБА WINDOWS\r\n" +
        "Можно установить агента как службу с заданным именем и учётной записью (логин/пароль) прямо из окна\r\n" +
        "(потребуется подтверждение UAC). Учётной записи нужно право «Вход в качестве службы».\r\n\r\n" +
        "ОБНОВЛЕНИЯ\r\n" +
        "При включённом автообновлении служба раз в день проверяет новую версию на сервере (папка\r\n" +
        "agent-updates), скачивает и устанавливает её с перезапуском. Также есть ручная проверка.\r\n\r\n" +
        "БЕЗОПАСНОСТЬ\r\n" +
        "Настройки хранятся локально в config.json рядом с exe. Управление возможно только из этого окна.\r\n";

    // ================= Статус (из snapshot.json) =================
    private void RefreshStatus()
    {
        var s = _store.LoadSnapshot();
        if (s.LastReportAt is null)
        {
            _statusBox.Text = "Отчёты ещё не формировались.\n\n" +
                "Статус появится после первого цикла работы агента (служба YpmonAgent " +
                "или это окно при активной фоновой работе).";
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"Последний отчёт: {s.LastReportAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Связь с сервером: {(s.LastReportAccepted ? "есть" : "нет")}  ({s.LastReportMessage})");
        sb.AppendLine($"Клиент / Сервер: {(s.ResolvedClientName ?? "—")} / {(s.ResolvedServerName ?? "—")}");
        if (!string.IsNullOrWhiteSpace(s.LastError)) sb.AppendLine($"Ошибка: {s.LastError}");
        sb.AppendLine();

        var r = s.LastReport;
        if (r is not null)
        {
            sb.AppendLine($"Машина: {r.MachineName}   Агент: {r.AgentVersion}");
            sb.AppendLine($"Доступность БД: {(r.ServerAvailable ? "ок" : "недоступна")}  ({r.AvailabilityMessage})");
            sb.AppendLine();
            sb.AppendLine("Задания:");
            foreach (var j in r.Jobs)
                sb.AppendLine($"  • [{Outcome(j.Outcome)}] {j.Name} — копий: {j.BackupCount}, {Bytes(j.TotalSizeBytes)}  {j.Message}");
            sb.AppendLine();
            sb.AppendLine("Диски:");
            foreach (var d in r.Disks)
                sb.AppendLine($"  • {d.Name}: своб. {Bytes(d.FreeBytes)} из {Bytes(d.TotalBytes)} ({d.UsedPercent}%)");
        }
        _statusBox.Text = sb.ToString();
    }

    // ================= Действия =================
    private async void TestConnection()
    {
        SaveAll(false);
        var url = _serverUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) { Info("Укажите адрес сервера."); return; }
        try
        {
            var resp = await Http.GetAsync(url.TrimEnd('/') + "/api/ping");
            Info(resp.IsSuccessStatusCode
                ? $"✅ Сервер доступен (HTTP {(int)resp.StatusCode})."
                : $"❌ Сервер ответил HTTP {(int)resp.StatusCode}.");
        }
        catch (Exception ex) { Info("❌ Нет связи: " + ex.Message); }
    }

    private void RunNow()
    {
        SaveAll(false);
        _store.SignalRunNow();
        Info("Команда поставлена в очередь. Все включённые задания выполнятся в ближайшие ~20 секунд " +
             "(их выполняет рабочий процесс — служба или это окно).");
    }

    // ================= Хелперы UI =================
    private static TableLayoutPanel NewTable()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(10),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };

    private static TextBox AddText(TableLayoutPanel t, string label, bool password = false)
    {
        t.Controls.Add(Lbl(label));
        var box = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = password, Margin = new Padding(3, 4, 10, 4) };
        t.Controls.Add(box);
        return box;
    }

    private static ComboBox AddCombo(TableLayoutPanel t, string label)
    {
        t.Controls.Add(Lbl(label));
        var cb = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(3, 4, 10, 4) };
        t.Controls.Add(cb);
        return cb;
    }

    private static TextBox AddMultiline(TableLayoutPanel t, string label)
    {
        t.Controls.Add(Lbl(label));
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 90, ScrollBars = ScrollBars.Vertical, Margin = new Padding(3, 4, 10, 4) };
        t.Controls.Add(box);
        return box;
    }

    private static TextBox AddBrowse(TableLayoutPanel t, string label)
    {
        t.Controls.Add(Lbl(label));
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 30, Margin = new Padding(0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        var box = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
        var btn = new Button { Text = "Обзор…", Dock = DockStyle.Fill, Margin = new Padding(3, 3, 10, 3) };
        btn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog();
            if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
            if (dlg.ShowDialog() == DialogResult.OK) box.Text = dlg.SelectedPath;
        };
        panel.Controls.Add(box); panel.Controls.Add(btn);
        t.Controls.Add(panel);
        return box;
    }

    private static NumericUpDown AddNum(TableLayoutPanel t, string label, int min, int max)
    {
        t.Controls.Add(Lbl(label));
        var n = new NumericUpDown { Minimum = min, Maximum = max, Width = 140, Margin = new Padding(3, 4, 3, 4), Anchor = AnchorStyles.Left };
        t.Controls.Add(n);
        return n;
    }

    private static (NumericUpDown h, NumericUpDown m, NumericUpDown s) AddDuration(TableLayoutPanel t, string label)
    {
        t.Controls.Add(Lbl(label));
        var fp = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
        NumericUpDown Mk(int max, string suffix)
        {
            var n = new NumericUpDown { Minimum = 0, Maximum = max, Width = 56, Margin = new Padding(2, 4, 0, 4) };
            fp.Controls.Add(n);
            fp.Controls.Add(new Label { Text = suffix, AutoSize = true, Margin = new Padding(2, 8, 8, 0) });
            return n;
        }
        var h = Mk(999, "ч"); var m = Mk(59, "м"); var s = Mk(59, "с");
        t.Controls.Add(fp);
        return (h, m, s);
    }

    private static int DurSeconds(NumericUpDown h, NumericUpDown m, NumericUpDown s)
        => (int)h.Value * 3600 + (int)m.Value * 60 + (int)s.Value;

    private static void SetDur(NumericUpDown h, NumericUpDown m, NumericUpDown s, int totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        h.Value = Math.Min(999, totalSeconds / 3600);
        m.Value = totalSeconds % 3600 / 60;
        s.Value = totalSeconds % 60;
    }

    private static CheckBox AddCheck(TableLayoutPanel t, string label)
    {
        t.Controls.Add(new Label { Width = 1 });
        var c = new CheckBox { Text = label, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        t.Controls.Add(c);
        return c;
    }

    private static void AddFull(TableLayoutPanel t, Control c)
    {
        t.Controls.Add(new Label { Width = 1 });
        t.Controls.Add(c);
    }

    private void Info(string msg) => MessageBox.Show(this, msg, "YPMon", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static decimal Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));

    private static string Outcome(JobOutcome o) => o switch
    {
        JobOutcome.Ok => "ОК",
        JobOutcome.Warning => "ВНИМ",
        JobOutcome.Error => "ОШИБКА",
        _ => "—"
    };

    private static string Sanitize(string s)
        => string.Concat((s ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Разбор строки «a, b, c» в список; если пусто — значения по умолчанию.</summary>
    private static List<string> SplitCsv(string? text, params string[] defaults)
    {
        var list = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return list.Count > 0 ? list : defaults.ToList();
    }

    private static string Bytes(long b)
    {
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" }; double x = b; int i = 0;
        while (x >= 1024 && i < u.Length - 1) { x /= 1024; i++; }
        return $"{x:0.#} {u[i]}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        CommitCurrent();
        base.OnFormClosing(e);
    }
}
