using System.Net.Http;
using System.Net.Http.Json;
using Ypmon.Agent.Services;
using Ypmon.Shared;

namespace Ypmon.Agent.Ui;

/// <summary>
/// Окно настройки агента с боковым меню (сайдбар) и областью контента.
/// Агент устанавливает только исходящее соединение к серверу и не принимает команд.
/// </summary>
public class MainForm : Form
{
    private readonly ConfigStore _store = new();
    private AgentConfig _cfg;
    private readonly bool _workerRunningHere;
    private bool _loading;

    // Навигация
    private Panel _content = null!;
    private readonly List<(Button btn, Control page)> _nav = new();
    private Button? _activeBtn;
    private static readonly Color NavActive = Color.FromArgb(210, 224, 245);
    private static readonly Color NavBg = Color.FromArgb(244, 246, 249);

    // Подключение
    private TextBox _serverUrl = null!, _apiKey = null!;
    private NumericUpDown _reportSec = null!, _heartbeatSec = null!;

    // Задания архивации (папки)
    private DataGridView _folderGrid = null!;
    private Panel _folderPanel = null!, _folderEmpty = null!;
    private Label _folHeader = null!;
    private TextBox _folName = null!, _folPath = null!, _folUser = null!, _folPass = null!;
    private CheckBox _folEnabled = null!;
    private MonitoredFolder? _curFolder;
    private int _editIndex = -2;      // -2 = ничего, -1 = новое задание, >=0 = редактирование существующего
    private bool _refreshing, _suppressSelect;

    // Журнал Windows
    private CheckBox _evtEnabled = null!, _evtWarnings = null!;
    private TextBox _evtLogs = null!;
    private NumericUpDown _evtMax = null!, _evtLookback = null!;

    // Служба
    private TextBox _svcName = null!, _svcUser = null!, _svcPass = null!;
    private Label _svcStatus = null!;
    private CheckBox _autoUpdate = null!;

    // Отчёты
    private TextBox _statusBox = null!;

    public MainForm(bool workerRunningHere)
    {
        _workerRunningHere = workerRunningHere;
        _cfg = _store.Load();

        Text = "YPMonitor — Агент " + Reporter.Version;
        Width = 900; Height = 620;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        BackColor = Color.White;

        // Нижняя панель с кнопкой сохранения
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = NavBg };
        var save = new Button { Text = "💾 Сохранить настройки", AutoSize = true, Left = 12, Top = 9 };
        save.Click += (_, _) => SaveAll(true);
        bottom.Controls.Add(save);

        // Область контента (справа)
        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };

        // Сайдбар (слева)
        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, Width = 240, BackColor = NavBg,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10, 12, 10, 12)
        };

        // Порядок добавления: Fill → Left → Bottom (для корректного докинга)
        Controls.Add(_content);
        Controls.Add(sidebar);
        Controls.Add(bottom);

        AddNav(sidebar, "Настройки подключения к серверу", BuildConnectionPage());
        AddNav(sidebar, "Задания архивации", BuildJobsPage());
        AddNav(sidebar, "Информация об отчётах", BuildReportInfoPage());
        AddNav(sidebar, "Настройка приложения", BuildAppSettingsPage());
        AddNav(sidebar, "О приложении", BuildAboutPage());

        LoadToControls();
        ShowPage(0);
    }

    // ================= Навигация =================
    private void AddNav(FlowLayoutPanel sidebar, string text, Control page)
    {
        var btn = new Button
        {
            Text = text, Width = 214, Height = 46, Margin = new Padding(0, 0, 0, 6),
            FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0), BackColor = Color.White, UseVisualStyleBackColor = false,
            Font = new Font(Font.FontFamily, 9.5f)
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(210, 214, 220);
        btn.FlatAppearance.BorderSize = 1;
        var idx = _nav.Count;
        btn.Click += (_, _) => ShowPage(idx);
        sidebar.Controls.Add(btn);

        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _content.Controls.Add(page);
        _nav.Add((btn, page));
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < _nav.Count; i++)
        {
            var active = i == index;
            _nav[i].page.Visible = active;
            _nav[i].btn.BackColor = active ? NavActive : Color.White;
            _nav[i].btn.Font = new Font(_nav[i].btn.Font, active ? FontStyle.Bold : FontStyle.Regular);
            if (active) { _nav[i].page.BringToFront(); _activeBtn = _nav[i].btn; }
        }
        // Обновляем данные для страниц, которым это нужно
        if (index == 2) RefreshStatus();
    }

    // ================= Подключение =================
    private Control BuildConnectionPage()
    {
        var t = NewTable();
        AddTitle(t, "Настройки подключения к серверу");
        _serverUrl = AddText(t, "Адрес сервера YPMon (http://10.0.0.1:8081)");
        _apiKey = AddText(t, "API-ключ сервера");
        _reportSec = AddNum(t, "Интервал отчёта о состоянии, секунд (по умолч. 21600 = 6 ч)", 30, 604800);
        _heartbeatSec = AddNum(t, "Интервал проверки связи, секунд", 15, 86400);

        var btnTest = new Button { Text = "🔌 Проверить соединение с сервером", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        btnTest.Click += (_, _) => TestConnection();
        AddFull(t, btnTest);

        var state = new Label { AutoSize = true, ForeColor = _workerRunningHere ? Color.Green : Color.DimGray, Margin = new Padding(3, 8, 3, 3) };
        state.Text = _workerRunningHere
            ? "Фоновая работа активна: пока окно открыто, отчёты отправляются."
            : "Отчёты отправляет установленная служба YpmonAgent. Изменения применяются автоматически.";
        AddFull(t, state);

        var note = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(560, 0), Margin = new Padding(3, 10, 3, 3) };
        note.Text = "Безопасность: агент устанавливает только исходящее соединение к серверу и не принимает команд.";
        AddFull(t, note);
        return t;
    }

    // ================= Задания архивации =================
    private Control BuildJobsPage()
    {
        var page = new Panel { Dock = DockStyle.Fill };

        var host = new Panel { Dock = DockStyle.Fill };
        _folderPanel = BuildFolderPanel();
        _folderEmpty = new Panel { Dock = DockStyle.Fill };
        _folderEmpty.Controls.Add(new Label { Text = "Выберите задание в списке или добавьте новое.", AutoSize = true, Left = 16, Top = 16, ForeColor = Color.DimGray });
        host.Controls.Add(_folderPanel);
        host.Controls.Add(_folderEmpty);
        _folderPanel.Visible = false;

        _folderGrid = new DataGridView
        {
            Dock = DockStyle.Top, Height = 170, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle
        };
        _folderGrid.Columns.Add("name", "Название");
        _folderGrid.Columns.Add("path", "Папка");
        _folderGrid.Columns.Add("enabled", "Вкл");
        _folderGrid.Columns["name"]!.FillWeight = 28;
        _folderGrid.Columns["path"]!.FillWeight = 60;
        _folderGrid.Columns["enabled"]!.FillWeight = 12;
        _folderGrid.SelectionChanged += (_, _) => { if (!_refreshing) SelectFolder(_folderGrid.CurrentRow?.Index ?? -1); };

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(0, 4, 0, 4) };
        var title = new Label { Text = "Задания архивации (наблюдаемые папки бэкапов)", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 4, 3, 8) };
        toolbar.Controls.Add(title);
        toolbar.SetFlowBreak(title, true);
        var add = new Button { Text = "+ Добавить папку", AutoSize = true };
        add.Click += (_, _) => AddFolder();
        var del = new Button { Text = "Удалить", AutoSize = true, ForeColor = Color.Firebrick };
        del.Click += (_, _) => DeleteFolder();
        toolbar.Controls.Add(add);
        toolbar.Controls.Add(del);

        page.Controls.Add(host);
        page.Controls.Add(_folderGrid);
        page.Controls.Add(toolbar);
        return page;
    }

    private Panel BuildFolderPanel()
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var t = NewTable();
        _folHeader = new Label { Text = "Параметры задания", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        AddFull(t, _folHeader);
        _folName = AddText(t, "Название");
        _folEnabled = AddCheck(t, "Включено");
        _folPath = AddBrowse(t, "Папка с бэкапами (можно сетевую \\\\сервер\\папка)");
        _folUser = AddText(t, "Учётная запись Windows для сетевой папки (DOMAIN\\User)");
        _folPass = AddText(t, "Пароль учётной записи", true);
        var btnAccess = new Button { Text = "🔐 Проверить доступ и содержимое", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        btnAccess.Click += (_, _) => FolderCheckAccess();
        AddFull(t, btnAccess);

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
        var btnSave = new Button { Text = "💾 Сохранить задачу мониторинга", AutoSize = true, BackColor = Color.FromArgb(210, 224, 245) };
        btnSave.Click += (_, _) => SaveFolderTask();
        var btnCancel = new Button { Text = "Отмена", AutoSize = true };
        btnCancel.Click += (_, _) => CancelFolderEdit();
        buttons.Controls.AddRange(new Control[] { btnSave, btnCancel });
        AddFull(t, buttons);

        var note = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(540, 0), Margin = new Padding(3, 6, 3, 3) };
        note.Text = "Агент только читает: количество файлов (любого формата), объём и дату самого свежего. " +
                    "Изменения применяются после «Сохранить задачу мониторинга». " +
                    "Порог «нет новых файлов N дней» и алерт настраиваются на сервере.";
        AddFull(t, note);

        p.Controls.Add(t);
        return p;
    }

    private void RefreshFolderList(int selectIndex = -1)
    {
        _refreshing = true;
        _folderGrid.Rows.Clear();
        foreach (var f in _cfg.Folders)
            _folderGrid.Rows.Add(f.Name, f.Path, f.Enabled ? "✓" : "—");
        _refreshing = false;
        if (selectIndex >= 0 && selectIndex < _folderGrid.Rows.Count)
        {
            _suppressSelect = true;
            _folderGrid.ClearSelection();
            _folderGrid.Rows[selectIndex].Selected = true;
            _folderGrid.CurrentCell = _folderGrid.Rows[selectIndex].Cells[0];
            _suppressSelect = false;
        }
    }

    // Клик по строке списка — открываем существующее задание в режиме редактирования.
    private void SelectFolder(int idx)
    {
        if (_suppressSelect) return;
        if (idx < 0 || idx >= _cfg.Folders.Count) { ShowFolderPanel(false); _editIndex = -2; _curFolder = null; return; }
        _editIndex = idx;
        var src = _cfg.Folders[idx];
        _curFolder = new MonitoredFolder
        {
            Name = src.Name, Enabled = src.Enabled, Path = src.Path,
            NetworkUsername = src.NetworkUsername, NetworkPassword = src.NetworkPassword
        };
        PopulateForm(_curFolder, "✏️ Редактирование задания: " + src.Name);
    }

    private void PopulateForm(MonitoredFolder f, string header)
    {
        _loading = true;
        _folHeader.Text = header;
        _folName.Text = f.Name; _folEnabled.Checked = f.Enabled;
        _folPath.Text = f.Path;
        _folUser.Text = f.NetworkUsername; _folPass.Text = f.NetworkPassword;
        _loading = false;
        ShowFolderPanel(true);
    }

    private void ShowFolderPanel(bool show)
    {
        _folderPanel.Visible = show;
        _folderEmpty.Visible = !show;
        if (show) _folderPanel.BringToFront(); else _folderEmpty.BringToFront();
    }

    // «Добавить папку» — новое, ещё не сохранённое задание.
    private void AddFolder()
    {
        _suppressSelect = true;
        _folderGrid.ClearSelection();
        _suppressSelect = false;
        _editIndex = -1;
        _curFolder = new MonitoredFolder { Name = "Новое задание", Enabled = true };
        PopulateForm(_curFolder, "➕ Новое задание мониторинга (не сохранено)");
    }

    // Явное сохранение текущего задания (нового или редактируемого).
    private void SaveFolderTask()
    {
        if (_curFolder is null) { Info("Нажмите «Добавить папку» или выберите задание в списке."); return; }
        var name = _folName.Text.Trim();
        if (name.Length == 0) { Info("Укажите название задания."); return; }
        _curFolder.Name = name;
        _curFolder.Enabled = _folEnabled.Checked;
        _curFolder.Path = _folPath.Text.Trim();
        _curFolder.NetworkUsername = _folUser.Text.Trim();
        _curFolder.NetworkPassword = _folPass.Text;

        int sel;
        if (_editIndex >= 0 && _editIndex < _cfg.Folders.Count)
        {
            _cfg.Folders[_editIndex] = _curFolder; sel = _editIndex;
        }
        else
        {
            _cfg.Folders.Add(_curFolder); sel = _cfg.Folders.Count - 1; _editIndex = sel;
        }
        _store.Save(_cfg);
        RefreshFolderList(sel);
        _folHeader.Text = "✏️ Редактирование задания: " + _curFolder.Name;
        Info("Задача мониторинга сохранена.");
    }

    private void CancelFolderEdit()
    {
        _curFolder = null; _editIndex = -2;
        _suppressSelect = true; _folderGrid.ClearSelection(); _suppressSelect = false;
        ShowFolderPanel(false);
    }

    private void DeleteFolder()
    {
        var idx = _folderGrid.CurrentRow?.Index ?? -1;
        if (idx < 0 || idx >= _cfg.Folders.Count)
        {
            if (_editIndex >= 0 && _editIndex < _cfg.Folders.Count) idx = _editIndex;
            else { Info("Выберите задание в списке."); return; }
        }
        if (MessageBox.Show(this, $"Удалить задание «{_cfg.Folders[idx].Name}»?", "Удаление",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _cfg.Folders.RemoveAt(idx);
        _store.Save(_cfg);
        _curFolder = null; _editIndex = -2;
        RefreshFolderList();
        ShowFolderPanel(false);
    }

    private void FolderCheckAccess()
    {
        var temp = new MonitoredFolder
        {
            Name = _folName.Text.Trim(), Enabled = true, Path = _folPath.Text.Trim(),
            NetworkUsername = _folUser.Text.Trim(), NetworkPassword = _folPass.Text
        };
        if (string.IsNullOrWhiteSpace(temp.Path)) { Info("Укажите папку."); return; }
        var (ok, msg) = NetShare.CheckAccess(temp.Path, temp.NetworkUsername, temp.NetworkPassword);
        if (!ok) { Info("❌ " + msg); return; }
        var st = new FolderMonitorService().Check(temp);
        Info(st.Accessible
            ? $"✅ Доступ есть.\n{st.Message}\nОбъём: {Bytes(st.TotalSizeBytes)}"
            : "❌ " + st.Message);
    }

    // ================= Информация об отчётах =================
    private Control BuildReportInfoPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        _statusBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
        var top = new Panel { Dock = DockStyle.Top, Height = 40 };
        var title = new Label { Text = "Информация об отчётах", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Left = 4, Top = 4 };
        var btn = new Button { Text = "⟳ Обновить", Left = 4, Top = 22, Width = 0, AutoSize = true };
        btn.Click += (_, _) => RefreshStatus();
        top.Controls.Add(title);
        top.Controls.Add(btn);
        page.Controls.Add(_statusBox);
        page.Controls.Add(top);
        return page;
    }

    private void RefreshStatus()
    {
        if (_statusBox is null) return;
        var s = _store.LoadSnapshot();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Версия агента: " + Reporter.Version);
        sb.AppendLine("Последний отчёт: " + (s.LastReportAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"));
        sb.AppendLine("Принят сервером: " + (s.LastReportAccepted ? "да" : "нет") + (s.LastReportMessage is null ? "" : " (" + s.LastReportMessage + ")"));
        if (!string.IsNullOrWhiteSpace(s.ResolvedClientName) || !string.IsNullOrWhiteSpace(s.ResolvedServerName))
            sb.AppendLine($"Клиент/сервер: {s.ResolvedClientName} / {s.ResolvedServerName}");
        if (!string.IsNullOrWhiteSpace(s.LastError)) sb.AppendLine("Ошибка: " + s.LastError);
        sb.AppendLine();

        if (s.LastReport is { } r)
        {
            sb.AppendLine("=== Папки ===");
            if (r.Folders.Count == 0) sb.AppendLine("(нет наблюдаемых папок)");
            foreach (var f in r.Folders)
            {
                sb.AppendLine($"• {f.Name} [{(f.Accessible ? "доступна" : "НЕТ ДОСТУПА")}]");
                sb.AppendLine($"    {f.Path}");
                sb.AppendLine($"    файлов: {f.FileCount}, объём: {Bytes(f.TotalSizeBytes)}, свежий: " +
                              (f.LastBackupAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "—") +
                              (f.LastFileName is null ? "" : $" ({f.LastFileName})"));
            }
            sb.AppendLine();
            sb.AppendLine("=== Ошибки журнала Windows (в последнем отчёте) ===");
            if (r.EventLogErrors.Count == 0) sb.AppendLine("(нет)");
            foreach (var e in r.EventLogErrors.Take(30))
                sb.AppendLine($"[{e.Level}] {e.TimeCreated.LocalDateTime:yyyy-MM-dd HH:mm} {e.LogName}/{e.Source} (ID {e.EventId})");
        }
        _statusBox.Text = sb.ToString();
    }

    // ================= Настройка приложения =================
    private Control BuildAppSettingsPage()
    {
        var t = NewTable();
        AddTitle(t, "Настройка приложения");

        AddSection(t, "Журнал событий Windows");
        _evtEnabled = AddCheck(t, "Читать журнал событий Windows и передавать ошибки серверу");
        _evtLogs = AddText(t, "Журналы через запятую (System, Application)");
        _evtWarnings = AddCheck(t, "Передавать и предупреждения (не только ошибки)");
        _evtMax = AddNum(t, "Максимум записей за отчёт (на журнал)", 1, 1000);
        _evtLookback = AddNum(t, "При первом запуске смотреть назад, часов", 1, 100000);
        var btnEvt = new Button { Text = "🔎 Показать последние ошибки журнала", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        btnEvt.Click += (_, _) => EventLogPreview();
        AddFull(t, btnEvt);

        AddSection(t, "Служба Windows");
        _svcName = AddText(t, "Имя службы");
        _svcUser = AddText(t, "Учётная запись запуска (DOMAIN\\User, пусто = LocalSystem)");
        _svcPass = AddText(t, "Пароль учётной записи", true);
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
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
        row.Controls.AddRange(new Control[] { bInstall, bUninstall, bStart, bStop, bRefresh });
        AddFull(t, row);
        _svcStatus = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        AddFull(t, _svcStatus);

        AddSection(t, "Обновления агента");
        _autoUpdate = AddCheck(t, "Автоматически обновлять агента с сервера (раз в день)");
        var bUpd = new Button { Text = "🔄 Проверить обновления сейчас", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        bUpd.Click += (_, _) => CheckUpdates();
        AddFull(t, bUpd);
        return t;
    }

    private void EventLogPreview()
    {
        if (!OperatingSystem.IsWindows()) { Info("Чтение журнала доступно только в Windows."); return; }
        var logs = SplitCsv(_evtLogs.Text, "System", "Application");
        try
        {
            var reader = new EventLogReaderService(_store);
            var cfg = new AgentConfig
            {
                EventLog = new EventLogConfig
                {
                    Enabled = true, Logs = logs, IncludeWarnings = _evtWarnings.Checked,
                    MaxEntriesPerReport = (int)_evtMax.Value, LookbackHoursOnFirstRun = (int)_evtLookback.Value
                }
            };
            var list = reader.PreviewRecent(cfg, 20);
            if (list.Count == 0) { Info("За выбранный период ошибок в журнале не найдено."); return; }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Найдено записей: {list.Count}. Последние:\n");
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
            Info("Служба установлена и запущена.\n\n" + log + "\n\nОкно закроется, чтобы не дублировать работу службы.");
            Application.Exit();
        }
        else
            Info("Результат установки:\n\n" + log + "\n\nЕсли служба не запустилась — проверьте учётную запись и право «Вход в качестве службы».");
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
            if (!UpdateInstaller.IsNewer(info.Version, Reporter.Version))
            {
                Info($"Установлена актуальная версия ({Reporter.Version}). На сервере: {info.Version}.");
                return;
            }
            if (MessageBox.Show(this, $"Доступна новая версия {info.Version} (текущая {Reporter.Version}). Скачать и установить?",
                    "Обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            Cursor = Cursors.WaitCursor;
            string installer;
            try { installer = await UpdateInstaller.DownloadAsync(_cfg); }
            finally { Cursor = Cursors.Default; }

            UpdateInstaller.RunInstaller(installer, silent: false);
            Info("Установщик запущен. Подтвердите установку (UAC). Это окно закроется.");
            Application.Exit();
        }
        catch (Exception ex) { Info("Не удалось проверить/установить обновление: " + ex.Message); }
    }

    // ================= О приложении =================
    private Control BuildAboutPage()
    {
        var t = NewTable();
        AddTitle(t, "О приложении");
        var lbl = new Label { AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(3, 6, 3, 3), Font = new Font(Font.FontFamily, 9.5f) };
        lbl.Text =
            "YPMonitor — Агент\n" +
            $"Версия: {Reporter.Version}\n\n" +
            "Назначение: агент следит за папками с резервными копиями (количество файлов, объём, дата\n" +
            "самого свежего) и собирает ошибки из журнала событий Windows, отправляя их на сервер YPMonitor.\n\n" +
            "Безопасность: агент устанавливает только исходящее соединение к серверу, не слушает сетевые\n" +
            "порты и не выполняет команд. Настройка возможна только из этого окна (файл config.json).\n\n" +
            "Обновление: с сервера, вкладка «Настройка приложения» → «Проверить обновления».";
        AddFull(t, lbl);
        return t;
    }

    // ================= Тест соединения =================
    private async void TestConnection()
    {
        SaveAll(false);
        if (string.IsNullOrWhiteSpace(_cfg.ServerUrl) || string.IsNullOrWhiteSpace(_cfg.ApiKey))
        {
            Info("Укажите адрес сервера и API-ключ."); return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var report = new AgentReportDto { MachineName = Environment.MachineName, AgentVersion = Reporter.Version, IsHeartbeat = true };
            using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.ServerUrl.TrimEnd('/') + "/api/report")
            { Content = JsonContent.Create(report) };
            req.Headers.Add("X-Api-Key", _cfg.ApiKey);
            var resp = await http.SendAsync(req);
            var ack = await resp.Content.ReadFromJsonAsync<ReportAckDto>();
            if (ack?.Accepted == true)
                Info($"✅ Соединение установлено.\nКлиент: {ack.ClientName}\nСервер: {ack.ServerName}");
            else
                Info($"❌ Сервер ответил: HTTP {(int)resp.StatusCode}. {ack?.Message}");
        }
        catch (Exception ex) { Info("❌ Не удалось подключиться: " + ex.Message); }
    }

    // ================= Загрузка/сохранение =================
    private void LoadToControls()
    {
        _loading = true;
        _serverUrl.Text = _cfg.ServerUrl;
        _apiKey.Text = _cfg.ApiKey;
        _reportSec.Value = Clamp(_cfg.ReportIntervalSeconds, 30, 604800);
        _heartbeatSec.Value = Clamp(_cfg.HeartbeatIntervalSeconds, 15, 86400);

        var evt = _cfg.EventLog ?? new EventLogConfig();
        _evtEnabled.Checked = evt.Enabled;
        _evtLogs.Text = string.Join(", ", evt.Logs ?? new());
        _evtWarnings.Checked = evt.IncludeWarnings;
        _evtMax.Value = Clamp(evt.MaxEntriesPerReport, 1, 1000);
        _evtLookback.Value = Clamp(evt.LookbackHoursOnFirstRun, 1, 100000);

        _svcName.Text = string.IsNullOrWhiteSpace(_cfg.ServiceName) ? "YpmonAgent" : _cfg.ServiceName;
        _autoUpdate.Checked = _cfg.AutoUpdate;
        _loading = false;

        RefreshFolderList();
        ShowFolderPanel(false);
        RefreshSvcStatus();
    }

    private void SaveAll(bool notify)
    {
        _cfg.ServerUrl = _serverUrl.Text.Trim();
        _cfg.ApiKey = _apiKey.Text.Trim();
        _cfg.ReportIntervalSeconds = (int)_reportSec.Value;
        _cfg.HeartbeatIntervalSeconds = (int)_heartbeatSec.Value;

        _cfg.EventLog ??= new EventLogConfig();
        _cfg.EventLog.Enabled = _evtEnabled.Checked;
        _cfg.EventLog.Logs = SplitCsv(_evtLogs.Text, "System", "Application");
        _cfg.EventLog.IncludeWarnings = _evtWarnings.Checked;
        _cfg.EventLog.MaxEntriesPerReport = (int)_evtMax.Value;
        _cfg.EventLog.LookbackHoursOnFirstRun = (int)_evtLookback.Value;

        _cfg.ServiceName = string.IsNullOrWhiteSpace(_svcName.Text) ? "YpmonAgent" : _svcName.Text.Trim();
        _cfg.AutoUpdate = _autoUpdate.Checked;

        _store.Save(_cfg);
        if (notify)
            MessageBox.Show(this, "Настройки сохранены в config.json.\nРабочий процесс применит их автоматически.",
                "YPMon", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
    }

    // ================= Хелперы UI =================
    private static TableLayoutPanel NewTable()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(12) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        return t;
    }

    private static void AddTitle(TableLayoutPanel t, string text)
        => AddFull(t, new Label { Text = text, Font = new Font(t.Font.FontFamily, 13, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 3, 3, 10) });

    private static void AddSection(TableLayoutPanel t, string text)
        => AddFull(t, new Label { Text = text, Font = new Font(t.Font.FontFamily, 10.5f, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 14, 3, 6) });

    private static TextBox AddText(TableLayoutPanel t, string label, bool password = false)
    {
        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        var box = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = password, Margin = new Padding(3, 3, 3, 3) };
        var r = t.RowCount++;
        t.Controls.Add(lbl, 0, r);
        t.Controls.Add(box, 1, r);
        return box;
    }

    private static TextBox AddBrowse(TableLayoutPanel t, string label)
    {
        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        var panel = new Panel { Dock = DockStyle.Fill, Height = 28, Margin = new Padding(3, 3, 3, 3) };
        var box = new TextBox { Dock = DockStyle.Fill };
        var btn = new Button { Text = "…", Dock = DockStyle.Right, Width = 34 };
        btn.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog();
            if (!string.IsNullOrWhiteSpace(box.Text)) d.SelectedPath = box.Text;
            if (d.ShowDialog() == DialogResult.OK) box.Text = d.SelectedPath;
        };
        panel.Controls.Add(box);
        panel.Controls.Add(btn);
        var r = t.RowCount++;
        t.Controls.Add(lbl, 0, r);
        t.Controls.Add(panel, 1, r);
        return box;
    }

    private static CheckBox AddCheck(TableLayoutPanel t, string label)
    {
        var box = new CheckBox { Text = label, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        var r = t.RowCount++;
        t.Controls.Add(box, 0, r);
        t.SetColumnSpan(box, 2);
        return box;
    }

    private static NumericUpDown AddNum(TableLayoutPanel t, string label, int min, int max)
    {
        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        var num = new NumericUpDown { Minimum = min, Maximum = max, Dock = DockStyle.Left, Width = 130, Margin = new Padding(3, 3, 3, 3) };
        var r = t.RowCount++;
        t.Controls.Add(lbl, 0, r);
        t.Controls.Add(num, 1, r);
        return num;
    }

    private static void AddFull(TableLayoutPanel t, Control c)
    {
        var r = t.RowCount++;
        t.Controls.Add(c, 0, r);
        t.SetColumnSpan(c, 2);
    }

    private static decimal Clamp(int v, int min, int max) => Math.Min(Math.Max(v, min), max);

    private static List<string> SplitCsv(string? text, params string[] defaults)
    {
        var list = (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return list.Count > 0 ? list : defaults.ToList();
    }

    private static string Bytes(long b)
    {
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" }; double x = b; int i = 0;
        while (x >= 1024 && i < u.Length - 1) { x /= 1024; i++; }
        return $"{x:0.#} {u[i]}";
    }

    private void Info(string msg) => MessageBox.Show(this, msg, "YPMon", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
