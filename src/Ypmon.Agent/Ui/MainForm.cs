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
    private NumericUpDown _reportH = null!, _reportM = null!;            // интервал отчёта: часы + минуты
    private NumericUpDown _hbH = null!, _hbM = null!, _hbS = null!;      // интервал связи: часы + минуты + секунды

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
    private TextBox _svcName = null!, _svcUser = null!, _svcPass = null!, _svcCmd = null!, _svcCmdPs = null!;
    private Label _svcStatus = null!;
    private CheckBox _autoUpdate = null!;
    private Label _cpuTempLabel = null!;
    private NumericUpDown _cpuTempTrigger = null!;

    // Отчёты
    private TextBox _statusBox = null!;

    // Главная (сводка по этой машине)
    private HomePage _home = null!;

    public MainForm(bool workerRunningHere)
    {
        _workerRunningHere = workerRunningHere;
        _cfg = _store.Load();

        Text = "YPMonitor — Агент " + Reporter.Version;
        Icon = LoadAppIcon();
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

        AddNav(sidebar, "🏠 Главная", _home = new HomePage(_store));
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
        (_reportH, _reportM, _) = AddDuration(t, "Как часто отправлять отчёт о состоянии (по умолч. 6 ч)", withSeconds: false, maxHours: 168);
        (_hbH, _hbM, _hbS) = AddDuration(t, "Как часто отправлять доступность агента (по умолч. 1 мин)", withSeconds: true, maxHours: 24);

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

        // Диски показываем «вживую» (это текущее состояние машины, не снимок отчёта).
        sb.AppendLine("=== Диски (сейчас) ===");
        var disks = Reporter.CollectDisks();
        if (disks.Count == 0) sb.AppendLine("(локальные диски не обнаружены)");
        foreach (var d in disks)
        {
            var usedPct = 100 - d.FreePercent;
            sb.AppendLine($"• {d.Name}{(d.Label is null ? "" : $" ({d.Label})")} — объём {Bytes(d.TotalBytes)}, " +
                          $"занято {usedPct:0}% , свободно {Bytes(d.FreeBytes)} ({d.FreePercent:0}%)");
        }
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
        bUninstall.Click += (_, _) => SvcAction(() => ServiceManager.Uninstall(_svcName.Text.Trim()), "Удаление службы", adviseInstall: false);
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

        // Запасной путь: если установка из окна не проходит (политики, антивирус, UAC),
        // эти команды можно выполнить вручную от имени администратора.
        AddFull(t, new Label
        {
            Text = "Если установка из окна не срабатывает — скопируйте команду и выполните её\n" +
                   "в командной строке (cmd) ИЛИ в PowerShell, запущенных от имени администратора\n" +
                   "(две строки — выполните по очереди):",
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 10, 3, 3)
        });
        _svcCmd = AddCommandBox(t);

        // Отдельная строка для старых систем: Windows Server 2012 / 2012 R2 и старше.
        // Там в PowerShell «sc» — псевдоним Set-Content, а оператор && не поддерживается,
        // поэтому даём нативные командлеты New-Service / Start-Service.
        AddFull(t, new Label
        {
            Text = "Windows Server 2012 / 2012 R2 и старше (нативно для PowerShell):",
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 8, 3, 3)
        });
        _svcCmdPs = AddCommandBox(t);

        _svcName.TextChanged += (_, _) => RefreshSvcCmd();
        _svcUser.TextChanged += (_, _) => RefreshSvcCmd();
        RefreshSvcCmd();

        AddSection(t, "Температура процессора");
        _cpuTempLabel = new Label
        {
            Text = "Текущая температура: измеряется…",
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 3)
        };
        AddFull(t, _cpuTempLabel);
        _cpuTempTrigger = AddNum(t, "Порог перегрева, °C (0 — не следить)", 0, 120);
        AddFull(t, new Label
        {
            Text = "При достижении порога агент сразу отправляет отчёт на сервер, не дожидаясь расписания,\n"
                 + "и сервер поднимает тревогу в Telegram. Возврат в норму — с запасом 5 °C.\n"
                 + "Датчик есть не на всех машинах: на серверном железе и в виртуальных машинах\n"
                 + "ACPI-термозона часто отсутствует — тогда будет «нет данных».",
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 8)
        });

        AddSection(t, "Обновления агента");
        _autoUpdate = AddCheck(t, "Автоматически обновлять агента с сервера (раз в день)");
        var bUpd = new Button { Text = "🔄 Проверить обновления сейчас", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        bUpd.Click += (_, _) => CheckUpdates();
        AddFull(t, bUpd);

        AddSection(t, "Логи агента");
        AddFull(t, new Label
        {
            Text = "Логи пишутся в папку log рядом с программой (файл на день, хранятся 30 дней).",
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 3)
        });
        var bLogs = new Button { Text = "📁 Открыть папку логов", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        bLogs.Click += (_, _) => OpenLogsFolder();
        AddFull(t, bLogs);
        return t;
    }

    private void OpenLogsFolder()
    {
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "log");
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Info("Не удалось открыть папку логов: " + ex.Message); }
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

    /// <summary>Многострочное поле с командой (только чтение) и кнопкой «Копировать». Возвращает поле.</summary>
    private TextBox AddCommandBox(TableLayoutPanel t)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Height = 48, Margin = new Padding(3, 3, 3, 3) };
        var box = new TextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, WordWrap = false,
            ScrollBars = ScrollBars.Horizontal, Font = new Font("Consolas", 8.5f)
        };
        var bCopy = new Button { Text = "Копировать", Dock = DockStyle.Right, AutoSize = true };
        bCopy.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(box.Text)) Clipboard.SetText(box.Text);
            bCopy.Text = "Скопировано";
            var tm = new System.Windows.Forms.Timer { Interval = 1500 };
            tm.Tick += (s2, _) => { bCopy.Text = "Копировать"; tm.Stop(); tm.Dispose(); };
            tm.Start();
        };
        panel.Controls.Add(box);
        panel.Controls.Add(bCopy);
        AddFull(t, panel);
        return box;
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

    private void RefreshSvcCmd()
    {
        if (_svcCmd is null) return;
        var name = _svcName.Text.Trim();
        var user = string.IsNullOrWhiteSpace(_svcUser.Text) ? null : _svcUser.Text.Trim();
        _svcCmd.Text = string.IsNullOrWhiteSpace(name) ? "" : ServiceManager.BuildInstallCommand(name, user);
        if (_svcCmdPs is not null)
            _svcCmdPs.Text = string.IsNullOrWhiteSpace(name) ? "" : ServiceManager.BuildInstallCommandPowerShell(name, user);
    }

    private void SvcInstall()
    {
        SaveAll(false);
        var name = _svcName.Text.Trim();

        // Проверяем до установки: неверное имя — самая частая причина, по которой sc create молча падает,
        // а последующий запуск выдаёт «1060: служба не существует».
        if (ServiceManager.ValidateName(name) is { } nameError)
        {
            Info("Не могу установить службу.\n\n" + nameError);
            return;
        }
        if (_svcPass.Text.Contains('"'))
        {
            Info("Не могу установить службу.\n\nПароль учётной записи содержит кавычку (\") — sc.exe такой пароль не примет. " +
                 "Смените пароль учётной записи или установите службу под LocalSystem (оставьте поля учётной записи пустыми).");
            return;
        }

        var user = string.IsNullOrWhiteSpace(_svcUser.Text) ? null : _svcUser.Text.Trim();
        var log = ServiceManager.Install(name, user, _svcPass.Text);
        RefreshSvcStatus();

        var (exists, running, status) = ServiceManager.Query(name);
        var hint = ServiceManager.Explain(log);

        if (running)
        {
            Info($"Служба «{name}» установлена и запущена.\n\nОкно закроется, чтобы не дублировать работу службы.");
            Application.Exit();
            return;
        }

        if (!exists)
        {
            // Создать службу не удалось — объясняем причину и предлагаем ручной путь.
            var msg = $"Служба «{name}» не создана.\n\n" +
                      (hint.Length > 0 ? hint + "\n\n" : "") +
                      "Что можно сделать:\n" +
                      "• подтвердить запрос UAC (нужны права администратора);\n" +
                      "• проверить, не блокирует ли установку антивирус или групповая политика;\n" +
                      "• выполнить команду установки вручную — она есть в поле ниже, под кнопками " +
                      "(скопируйте и запустите в командной строке от имени администратора).\n\n" +
                      "Ответ sc.exe:\n" + log;
            Info(msg);
            return;
        }

        // Служба создана, но не стартовала — почти всегда права учётной записи.
        Info($"Служба «{name}» создана, но не запустилась (статус: {status}).\n\n" +
             (hint.Length > 0 ? hint + "\n\n" : "") +
             "Чаще всего причина в учётной записи: неверный пароль либо нет права «Вход в качестве службы» " +
             "(secpol.msc → Локальные политики → Назначение прав пользователя → Вход в качестве службы).\n\n" +
             "Ответ sc.exe:\n" + log);
    }

    private void SvcAction(Func<string> action, string title, bool adviseInstall = true)
    {
        var name = _svcName.Text.Trim();
        // Не даём получить невнятное «1060» там, где причина очевидна заранее.
        if (!ServiceManager.Query(name).exists)
        {
            RefreshSvcStatus();
            Info($"{title}: служба «{name}» не установлена." + (adviseInstall
                ? "\n\nСначала нажмите «Установить / переустановить». Если установка не проходит — " +
                  "скопируйте команду из поля под кнопками и выполните её в командной строке от имени администратора."
                : ""));
            return;
        }

        var log = action();
        RefreshSvcStatus();
        var hint = ServiceManager.Explain(log);
        Info(title + ":\n\n" + (hint.Length > 0 ? hint + "\n\n" : "") + log);
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
        SetDuration((int)Clamp(_cfg.ReportIntervalSeconds, 60, 604800), _reportH, _reportM, null);
        SetDuration((int)Clamp(_cfg.HeartbeatIntervalSeconds, 15, 86400), _hbH, _hbM, _hbS);

        var evt = _cfg.EventLog ?? new EventLogConfig();
        _evtEnabled.Checked = evt.Enabled;
        _evtLogs.Text = string.Join(", ", evt.Logs ?? new());
        _evtWarnings.Checked = evt.IncludeWarnings;
        _evtMax.Value = Clamp(evt.MaxEntriesPerReport, 1, 1000);
        _evtLookback.Value = Clamp(evt.LookbackHoursOnFirstRun, 1, 100000);

        _svcName.Text = string.IsNullOrWhiteSpace(_cfg.ServiceName) ? "YpmonAgent" : _cfg.ServiceName;
        _autoUpdate.Checked = _cfg.AutoUpdate;
        _cpuTempTrigger.Value = Clamp(_cfg.CpuTempTriggerC, 0, 120);
        _loading = false;

        RefreshCpuTemp();

        RefreshFolderList();
        ShowFolderPanel(false);
        RefreshSvcStatus();
    }

    /// <summary>
    /// Показывает текущую температуру процессора. Читаем датчик прямо здесь: окно настроек и
    /// рабочий процесс агента — разные процессы, значение из службы сюда не попадает.
    /// </summary>
    private void RefreshCpuTemp()
    {
        if (!OperatingSystem.IsWindows()) { _cpuTempLabel.Text = "Текущая температура: недоступно (не Windows)"; return; }
        try
        {
            var t = Services.CpuTemperatureCollector.Read();
            _cpuTempLabel.Text = t is { } v
                ? $"Текущая температура: {v:0.#} °C"
                : "Текущая температура: нет данных — " + (Services.CpuTemperatureCollector.LastUnavailableReason ?? "датчик недоступен");
        }
        catch (Exception ex) { _cpuTempLabel.Text = "Текущая температура: ошибка чтения — " + ex.Message; }
    }

    private void SaveAll(bool notify)
    {
        _cfg.ServerUrl = _serverUrl.Text.Trim();
        _cfg.ApiKey = _apiKey.Text.Trim();
        _cfg.ReportIntervalSeconds = GetDuration(_reportH, _reportM, null, minSeconds: 60);       // минимум 1 мин
        _cfg.HeartbeatIntervalSeconds = GetDuration(_hbH, _hbM, _hbS, minSeconds: 15);            // минимум 15 сек

        _cfg.EventLog ??= new EventLogConfig();
        _cfg.EventLog.Enabled = _evtEnabled.Checked;
        _cfg.EventLog.Logs = SplitCsv(_evtLogs.Text, "System", "Application");
        _cfg.EventLog.IncludeWarnings = _evtWarnings.Checked;
        _cfg.EventLog.MaxEntriesPerReport = (int)_evtMax.Value;
        _cfg.EventLog.LookbackHoursOnFirstRun = (int)_evtLookback.Value;

        _cfg.ServiceName = string.IsNullOrWhiteSpace(_svcName.Text) ? "YpmonAgent" : _svcName.Text.Trim();
        _cfg.AutoUpdate = _autoUpdate.Checked;
        _cfg.CpuTempTriggerC = (int)_cpuTempTrigger.Value;

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

    /// <summary>Строка «интервал»: часы + минуты (+ секунды). Возвращает поля; секунды могут быть null.</summary>
    private static (NumericUpDown h, NumericUpDown m, NumericUpDown? s) AddDuration(
        TableLayoutPanel t, string label, bool withSeconds, int maxHours)
    {
        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };

        NumericUpDown Num(int max) => new() { Minimum = 0, Maximum = max, Width = 62, Margin = new Padding(3, 3, 2, 3) };
        Label Unit(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 8, 8, 3) };

        var h = Num(maxHours);
        var m = Num(59);
        row.Controls.Add(h); row.Controls.Add(Unit("ч"));
        row.Controls.Add(m); row.Controls.Add(Unit("мин"));
        NumericUpDown? s = null;
        if (withSeconds)
        {
            s = Num(59);
            row.Controls.Add(s); row.Controls.Add(Unit("сек"));
        }

        var r = t.RowCount++;
        t.Controls.Add(lbl, 0, r);
        t.Controls.Add(row, 1, r);
        return (h, m, s);
    }

    /// <summary>Раскладывает секунды в поля «ч/мин/сек».</summary>
    private static void SetDuration(int totalSeconds, NumericUpDown h, NumericUpDown m, NumericUpDown? s)
    {
        totalSeconds = Math.Max(0, totalSeconds);
        var hours = Math.Min(totalSeconds / 3600, (int)h.Maximum);
        var rest = totalSeconds - hours * 3600;
        h.Value = hours;
        m.Value = Math.Min(rest / 60, 59);
        if (s is not null) s.Value = rest % 60;
    }

    /// <summary>Собирает секунды из полей «ч/мин/сек» с нижним пределом.</summary>
    private static int GetDuration(NumericUpDown h, NumericUpDown m, NumericUpDown? s, int minSeconds)
        => Math.Max(minSeconds, (int)h.Value * 3600 + (int)m.Value * 60 + (int)(s?.Value ?? 0));

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

    /// <summary>Иконка окна из встроенного ресурса app.ico (single-file publish — файла на диске нет).</summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var s = asm.GetManifestResourceStream(name);
            return s is null ? null : new Icon(s);
        }
        catch { return null; }
    }

    private void Info(string msg) => MessageBox.Show(this, msg, "YPMon", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
