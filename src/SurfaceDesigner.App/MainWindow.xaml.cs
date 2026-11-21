using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using SurfaceDesigner.Core;
using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Grading;
using SurfaceDesigner.Core.IO;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.App;

public partial class MainWindow : Window
{
    private const string ProjectFilter = "Проект Surface Designer (*.sdproj)|*.sdproj|Всички файлове (*.*)|*.*";
    private const int MaxUndo = 50;

    private Project _project = new();
    private string? _projectPath;
    private Surface? _active;
    private List<Point2>? _selection;
    private VolumeResult? _lastVolumes;
    private GradingModel? _gradingModel;
    private bool _gradingModelValid;
    private bool _updatingUi;
    private bool _dirty;
    private readonly List<(string Json, string? Active, int Platform)> _undo = new();

    // Автоматичен запис: копие на незаписаните промени на всеки AutosaveInterval
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(2);
    private readonly AutosaveStore? _autosave;
    private readonly System.Windows.Threading.DispatcherTimer _autosaveTimer = new() { Interval = AutosaveInterval };
    private int _changeVersion;     // увеличава се при всяка промяна на проекта
    private int _autosavedVersion;  // версията в последното копие (или в записания файл)
    private bool _autosaving;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            _autosave = new AutosaveStore(AutosaveStore.DefaultDirectory);
            _autosaveTimer.Tick += async (_, _) => await AutosaveAsync();
            _autosaveTimer.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _autosave = null; // без автоматичен запис, но програмата работи
        }
        _designTimer.Tick += async (_, _) =>
        {
            _designTimer.Stop();
            await RegenerateLinkedDesignsAsync();
        };

        View.AddPointRequested += OnAddPoint;
        View.MovePointRequested += OnMovePoint;
        View.DeletePointRequested += OnDeletePoint;
        View.PointEditRequested += OnEditPoint;
        View.SwapEdgeRequested += OnSwapEdge;
        View.PolygonCompleted += OnPolygonCompleted;
        View.PlatformEditStarting += OnPlatformEditStarting;
        View.PlatformGeometryChanged += OnPlatformGeometryChanged;
        View.PlatformClicked += p => PlatformList.SelectedItem = p;
        View.CursorMoved += OnCursorMoved;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Home && Keyboard.FocusedElement is not TextBox) { View.ZoomExtents(); e.Handled = true; }
        };
        Closing += (_, e) =>
        {
            e.Cancel = !ConfirmDiscard();
            if (!e.Cancel) ClearAutosave(); // записано или съзнателно отхвърлено – копието не трябва
        };
        Closed += (_, _) => _autosave?.Dispose();
        Loaded += (_, _) =>
        {
            if (OfferAutosaveRecovery()) return;
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
            {
                if (!OpenProjectFile(Path.GetFullPath(args[1]))) RefreshAll(zoom: true);
            }
            else
                RefreshAll(zoom: true);
        };
    }

    // =====================================================================
    //  Общи помощни методи
    // =====================================================================

    private Platform? SelectedPlatform => PlatformList.SelectedItem as Platform;

    private void RefreshAll(bool zoom = false)
    {
        _updatingUi = true;
        try
        {
            if (_active != null && !_project.Surfaces.Contains(_active)) _active = null;
            _active ??= _project.Surfaces.FirstOrDefault();

            SurfaceList.ItemsSource = null;
            SurfaceList.ItemsSource = _project.Surfaces;
            SurfaceList.SelectedItem = _active;

            ExistingBox.ItemsSource = null;
            ExistingBox.ItemsSource = _project.Surfaces;
            ExistingBox.SelectedItem = _project.ExistingSurface;

            var selPlatform = View.SelectedPlatform;
            PlatformList.ItemsSource = null;
            PlatformList.ItemsSource = _project.Platforms;
            PlatformList.SelectedItem = selPlatform != null && _project.Platforms.Contains(selPlatform) ? selPlatform : null;

            var selBreakline = View.SelectedBreakline;
            BreaklineList.ItemsSource = null;
            BreaklineList.ItemsSource = _active?.Breaklines;
            BreaklineList.SelectedItem = selBreakline != null && _active != null && _active.Breaklines.Contains(selBreakline) ? selBreakline : null;

            var selBoundary = View.SelectedBoundary;
            BoundaryList.ItemsSource = null;
            BoundaryList.ItemsSource = _active?.Boundaries;
            BoundaryList.SelectedItem = selBoundary != null && _active != null && _active.Boundaries.Contains(selBoundary) ? selBoundary : null;
        }
        finally { _updatingUi = false; }

        View.Project = _project;
        View.ActiveSurface = _active;
        View.SelectedPlatform = SelectedPlatform;
        View.SelectedBreakline = BreaklineList.SelectedItem as Breakline;
        View.SelectedBoundary = BoundaryList.SelectedItem as SurfaceBoundary;
        View.SelectionPolygon = _selection;
        View.CutFill = _lastVolumes?.Grid;
        ReadViewOptions();
        UpdateStats();
        LoadPlatformFields();
        UpdateTitle();

        if (zoom) View.ZoomExtents(); else View.RebuildScene();
    }

    private void UpdateTitle()
    {
        string name = _projectPath != null ? Path.GetFileName(_projectPath) : "Нов проект";
        Title = $"{name}{(_dirty ? " *" : "")} – Surface Designer";
    }

    /// <summary>Запомня състоянието за „Отмени“ и маркира проекта като променен.</summary>
    private void PushUndo()
    {
        _undo.Add((ProjectSerializer.ToJson(_project), _active?.Name, SelectedPlatform != null ? _project.Platforms.IndexOf(SelectedPlatform) : -1));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        MarkDirty();
    }

    /// <summary>Маркира проекта като променен (заглавието със „*“ и следващия автоматичен запис).</summary>
    private void MarkDirty()
    {
        _dirty = true;
        _changeVersion++;
    }

    /// <summary>Данните са променени – обновяване на изгледа и анулиране на остарели резултати.</summary>
    private void DataChanged(bool affectsGrading = true)
    {
        DetachManuallyEditedDesigns();
        if (affectsGrading)
        {
            _gradingModelValid = false;
            _lastVolumes = null;
            ScheduleDesignRegeneration();
        }
        RefreshAll();
    }

    // =====================================================================
    //  Автоматично преизчисляване на свързаните проектни повърхнини
    // =====================================================================

    private readonly System.Windows.Threading.DispatcherTimer _designTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _designJob;

    /// <summary>Отбелязва текущото състояние на свързаните повърхнини като „генерирано“ (след зареждане/отмяна).</summary>
    private void SyncDesignLinks()
    {
        foreach (var d in _project.Surfaces)
        {
            if (d.GradingSource is not { } src) continue;
            src.GeneratedVersion = d.Version;
            var eg = _project.FindSurface(src.ExistingSurfaceName);
            src.LastInputKey = eg != null ? src.InputKey(eg, _project.Platforms) : null;
        }
    }

    /// <summary>Свързана повърхнина, редактирана ръчно, се отделя – иначе преизчисляването би изтрило редакциите.</summary>
    private void DetachManuallyEditedDesigns()
    {
        foreach (var d in _project.Surfaces)
        {
            if (d.GradingSource is not { } src || src.GeneratedVersion < 0 || d.Version == src.GeneratedVersion) continue;
            d.GradingSource = null;
            Info($"„{d.Name}“ е редактирана ръчно и вече не се обновява автоматично от платформите " +
                 "(може да се свърже отново с отметката в „Информация“).");
        }
    }

    private void ScheduleDesignRegeneration()
    {
        if (!_project.Surfaces.Any(s => s.GradingSource != null)) return;
        _designTimer.Stop();
        _designTimer.Start();
    }

    private async Task RegenerateLinkedDesignsAsync()
    {
        int job = ++_designJob;
        foreach (var d in _project.Surfaces.Where(s => s.GradingSource != null).ToList())
        {
            var src = d.GradingSource!;
            var eg = _project.FindSurface(src.ExistingSurfaceName);
            if (eg == null || ReferenceEquals(eg, d))
            {
                Info($"„{d.Name}“ не може да се обнови: липсва теренът „{src.ExistingSurfaceName}“.");
                continue;
            }
            string key = src.InputKey(eg, _project.Platforms);
            if (key == src.LastInputKey) continue; // входните данни не са се променили

            // моментни копия – изчислението е във фонов режим, а потребителят може да продължи да редактира
            var egCopy = eg.Clone();
            var platforms = _project.Platforms.Select(p => p.Clone()).ToList();
            var linkCopy = new Surface(d.Name) { GradingSource = src.Copy() };
            Info($"Обновяване на „{d.Name}“...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Surface? result;
            try
            {
                result = platforms.Count == 0
                    ? new Surface(d.Name)
                    : await Task.Run(() => DesignSurfaceBuilder.Rebuild(linkCopy, egCopy, platforms));
            }
            catch (Exception ex)
            {
                Info($"„{d.Name}“ не може да се обнови: {ex.Message}");
                continue;
            }

            // по време на изчислението е започнало ново (по-актуално) – този резултат се пропуска
            if (job != _designJob || !_project.Surfaces.Contains(d) || !ReferenceEquals(d.GradingSource, src)) return;
            if (result == null) continue;

            d.ReplaceContentFrom(result);
            src.GeneratedVersion = d.Version;
            src.LastInputKey = key;
            _gradingModelValid = false;
            _lastVolumes = null;
            MarkDirty();
            RefreshAll();
            Info($"„{d.Name}“ е обновена автоматично ({d.TriangleCount} триъгълника, {sw.ElapsedMilliseconds} ms).");
        }
    }

    private void DesignLinkBox_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) return;
        if (DesignLinkBox.IsChecked != true)
        {
            PushUndo();
            _active.GradingSource = null;
            DataChanged(false);
            Info($"„{_active.Name}“ вече не се обновява автоматично.");
            return;
        }

        var eg = _project.ExistingSurface;
        if (eg == null || ReferenceEquals(eg, _active))
        {
            Warn("Изберете съществуващ терен (раздел „Вертикално планиране“), различен от тази повърхнина.");
            DesignLinkBox.IsChecked = false;
            return;
        }
        if (!NumberParser.TryParse(DesignStepBox.Text, out var step) || step < 0.1) step = 1;
        if (MessageBox.Show(this,
                $"„{_active.Name}“ ще се преизчисли от терена „{eg.Name}“ и платформите и ще се обновява автоматично.\n" +
                "Ръчните промени по нея ще бъдат заменени. Продължаване?",
                "Свързване с платформите", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            DesignLinkBox.IsChecked = false;
            return;
        }
        PushUndo();
        _active.IsDesign = true;
        _active.GradingSource = new GradingSource(eg.Name, step, ZoneOnlyBox.IsChecked == true) { GeneratedVersion = _active.Version };
        DataChanged(); // насрочва преизчисляването
    }

    private GradingModel? GetGradingModel()
    {
        if (_gradingModelValid) return _gradingModel;
        _gradingModelValid = true;
        var eg = _project.ExistingSurface;
        _gradingModel = eg != null && eg.TriangleCount > 0 && _project.Platforms.Count > 0
            ? new GradingModel(eg, _project.Platforms)
            : null;
        return _gradingModel;
    }

    private bool TryRead(TextBox box, string name, out double value, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
    {
        if (NumberParser.TryParse(box.Text, out value) && value >= min && value <= max) return true;
        MessageBox.Show(this, $"Невалидна стойност за „{name}“.", "Въвеждане", MessageBoxButton.OK, MessageBoxImage.Warning);
        box.Focus();
        box.SelectAll();
        return false;
    }

    private void Info(string text) => InfoText.Text = text;

    private void Warn(string text) => MessageBox.Show(this, text, "Surface Designer", MessageBoxButton.OK, MessageBoxImage.Information);

    private bool RequireActive()
    {
        if (_active != null) return true;
        Warn("Няма активна повърхнина. Импортирайте точки или създайте нова повърхнина.");
        return false;
    }

    private Polygon2? RequireSelection()
    {
        if (_selection is { Count: >= 3 }) return new Polygon2(_selection);
        Warn("Първо очертайте полигон с инструмента „Полигон за редакция“.");
        return null;
    }

    private async Task RunBusy(string message, Func<Task> action)
    {
        Cursor = Cursors.Wait;
        RightPanel.IsEnabled = false;
        Info(message);
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Грешка", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            Cursor = null;
            RightPanel.IsEnabled = true;
        }
    }

    private void SetTool(ToolMode mode)
    {
        foreach (var rb in Tools.Items.OfType<RadioButton>())
            if ((string)rb.Tag == mode.ToString()) rb.IsChecked = true;
    }

    private static double MeanElevation(Surface s, Polygon2 poly)
    {
        var b = poly.Bounds;
        double step = Math.Max(Math.Max(b.Width, b.Height) / 30, 0.1);
        double sum = 0;
        int n = 0;
        for (double y = b.MinY + step / 2; y < b.MaxY; y += step)
            for (double x = b.MinX + step / 2; x < b.MaxX; x += step)
            {
                if (!poly.Contains(x, y)) continue;
                var z = s.ElevationAt(x, y);
                if (z.HasValue) { sum += z.Value; n++; }
            }
        if (n == 0)
            foreach (var v in poly.Vertices)
                if (s.ElevationAt(v.X, v.Y) is { } z) { sum += z; n++; }
        return n > 0 ? Math.Round(sum / n, 2) : 0;
    }

    // =====================================================================
    //  Файл
    // =====================================================================

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(this, "Проектът има незаписани промени. Да се запишат ли?", "Surface Designer",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveProject(_projectPath);
        return true;
    }

    private void LoadProject(Project project, string? path)
    {
        _project = project;
        _projectPath = path;
        _active = project.Surfaces.FirstOrDefault();
        _selection = null;
        _lastVolumes = null;
        _gradingModelValid = false;
        _undo.Clear();
        _dirty = false;
        ClearAutosave();
        View.SelectedPlatform = project.Platforms.FirstOrDefault();
        ResultsText.Text = "";
        SyncDesignLinks();
        _contourBoxesSurface = null;
        LoadViewSettings();
        RefreshAll(zoom: true);
    }

    private void New_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (ConfirmDiscard()) LoadProject(new Project(), null);
    }

    private void Open_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = ProjectFilter };
        if (dlg.ShowDialog(this) != true) return;
        OpenProjectFile(dlg.FileName);
    }

    /// <summary>
    /// Отваря проект. Ако файлът е повреден и до него има резервно копие (.bak), предлага да се отвори то –
    /// проектът остава със същия път, т.е. „Запиши“ заменя повредения файл.
    /// </summary>
    private bool OpenProjectFile(string path)
    {
        Project project;
        try
        {
            project = ProjectSerializer.Load(path);
        }
        catch (Exception ex)
        {
            string bak = ProjectSerializer.BackupPath(path);
            if (!File.Exists(bak))
            {
                MessageBox.Show(this, $"Проектът не може да се отвори:\n{path}\n\n{ex.Message}", "Грешка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var answer = MessageBox.Show(this,
                $"Проектът не може да се отвори:\n{path}\n\n{ex.Message}\n\n" +
                $"Има резервно копие с предишната записана версия от {File.GetLastWriteTime(bak):dd.MM.yyyy HH:mm}. " +
                "Да се отвори ли то?\n\nПри „Запиши“ то ще замени повредения файл.",
                "Повреден проект", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return false;

            try
            {
                project = ProjectSerializer.Load(bak);
            }
            catch (Exception bakEx)
            {
                MessageBox.Show(this, $"И резервното копие не може да се отвори:\n{bak}\n\n{bakEx.Message}", "Грешка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            LoadProject(project, path);
            MarkDirty();
            UpdateTitle();
            Info($"Отворено резервно копие: {bak}. Запишете проекта, за да замени повредения файл.");
            return true;
        }

        LoadProject(project, path);
        Info($"Отворен: {path}");
        return true;
    }

    // ---------------------------------------------------------------------
    //  Автоматичен запис
    // ---------------------------------------------------------------------

    private async Task AutosaveAsync()
    {
        if (_autosave == null || _autosaving || !_dirty || _changeVersion == _autosavedVersion) return;
        _autosaving = true;
        try
        {
            int version = _changeVersion;
            int generation = _autosave.Generation;
            string json = ProjectSerializer.ToJson(_project); // в UI нишката – консистентно състояние
            string? path = _projectPath;
            if (await Task.Run(() => _autosave.Save(json, path, generation)))
                _autosavedVersion = version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Info($"Автоматичният запис не успя: {ex.Message}");
        }
        finally { _autosaving = false; }
    }

    private void ClearAutosave()
    {
        _autosavedVersion = _changeVersion;
        try { _autosave?.Clear(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// При стартиране предлага автоматично копие, останало от незатворена нормално сесия (напр. срив).
    /// Връща true, ако е възстановен проект.
    /// </summary>
    private bool OfferAutosaveRecovery()
    {
        if (_autosave == null) return false;
        IReadOnlyList<AutosaveEntry> entries;
        try { entries = _autosave.FindRecoverable(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        foreach (var entry in entries)
        {
            string name = entry.OriginalPath != null ? Path.GetFileName(entry.OriginalPath) : "Нов (незаписан) проект";
            var answer = MessageBox.Show(this,
                "Програмата не е била затворена нормално. Намерено е автоматично копие с незаписани промени:\n\n" +
                $"{name}\nот {entry.SavedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}\n\n" +
                "Да – възстанови го\nНе – изтрий копието\nОтказ – остави го за следващото стартиране",
                "Възстановяване", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.No) { _autosave.Discard(entry); continue; }
            if (answer != MessageBoxResult.Yes) continue;

            Project project;
            try { project = ProjectSerializer.Load(entry.ProjectFile); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Копието не може да се отвори:\n{ex.Message}", "Грешка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                continue;
            }

            LoadProject(project, entry.OriginalPath);
            MarkDirty();
            UpdateTitle();
            // Първо собствено копие, после изтриване на старото – промените не остават без копие нито за миг
            try
            {
                if (_autosave.Save(ProjectSerializer.ToJson(_project), _projectPath, _autosave.Generation))
                    _autosavedVersion = _changeVersion;
                _autosave.Discard(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Info($"Възстановено автоматично копие от {entry.SavedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}. Запишете проекта.");
            return true;
        }
        return false;
    }

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e) => SaveProject(_projectPath);

    private void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e) => SaveProject(null);

    private bool SaveProject(string? path)
    {
        if (path == null)
        {
            var dlg = new SaveFileDialog { Filter = ProjectFilter, DefaultExt = ".sdproj" };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        ProjectSerializer.Save(_project, path);
        _projectPath = path;
        _dirty = false;
        ClearAutosave();
        UpdateTitle();
        Info($"Записан: {path}");
        return true;
    }

    private void Undo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_undo.Count == 0) { Info("Няма какво да се отмени."); return; }
        var (json, active, platform) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _project = ProjectSerializer.FromJson(json);
        _active = active != null ? _project.FindSurface(active) : null;
        View.SelectedPlatform = platform >= 0 && platform < _project.Platforms.Count ? _project.Platforms[platform] : null;
        MarkDirty();
        _designJob++; // текущо изчисление (ако има) е за отменено състояние
        SyncDesignLinks();
        _contourBoxesSurface = null;
        LoadViewSettings();
        DataChanged();
        Info("Действието е отменено.");
    }

    private void ImportPoints_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Точки (*.txt;*.csv;*.xyz;*.pts;*.dat)|*.txt;*.csv;*.xyz;*.pts;*.dat|Всички файлове (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        var geo = MessageBox.Show(this,
            "Файлът в геодезическа координатна система ли е (X – север, Y – изток)?\n\n" +
            "Да – колоните се четат като „Номер X(север) Y(изток) H“\nНе – като „X(изток) Y(север) Z“",
            "Импорт на точки", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (geo == MessageBoxResult.Cancel) return;

        var pts = PointFileReader.Read(dlg.FileName, geo == MessageBoxResult.Yes);
        if (pts.Count == 0) { Warn("Не са намерени точки във файла."); return; }

        PushUndo();
        var name = _project.UniqueSurfaceName(Path.GetFileNameWithoutExtension(dlg.FileName));
        var s = new Surface(name, pts);
        _project.Surfaces.Add(s);
        _project.ExistingSurfaceName ??= name;
        _active = s;
        DataChanged();
        View.ZoomExtents();
        Info($"Импортирани {pts.Count} точки в „{name}“ ({s.TriangleCount} триъгълника).");
    }

    private async void ImportLandXml_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "LandXML (*.xml)|*.xml|Всички файлове (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        string file = dlg.FileName;

        await RunBusy("Импорт от LandXML...", async () =>
        {
            var result = await Task.Run(() =>
            {
                var r = LandXmlReader.Read(file);
                foreach (var s in r.Surfaces) s.EnsureTriangulated();
                return r;
            });
            if (result.Surfaces.Count == 0)
            {
                Warn(string.Join("\n", result.Messages));
                return;
            }

            PushUndo();
            foreach (var s in result.Surfaces)
            {
                s.Name = _project.UniqueSurfaceName(s.Name);
                _project.Surfaces.Add(s);
            }
            _project.ExistingSurfaceName ??= result.Surfaces[0].Name;
            _active = result.Surfaces[0];
            DataChanged();
            View.ZoomExtents();
            Info($"Импортирани {result.Surfaces.Count} повърхнини от {Path.GetFileName(file)}.");
            MessageBox.Show(this, string.Join("\n\n", result.Messages), "Импорт от LandXML",
                MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void ExportPoints_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive()) return;
        var dlg = new SaveFileDialog { Filter = "Текстов файл (*.txt)|*.txt|CSV (*.csv)|*.csv", FileName = _active!.Name };
        if (dlg.ShowDialog(this) != true) return;
        var geo = MessageBox.Show(this, "Запис в геодезическа система (X – север, Y – изток)?", "Експорт",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        PointFileWriter.Write(dlg.FileName, _active.Points, geo);
        Info($"Записани {_active.Points.Count} точки.");
    }

    private void ExportLandXml_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Surfaces.Count == 0) { Warn("Няма повърхнини за експорт."); return; }
        var dlg = new SaveFileDialog { Filter = "LandXML (*.xml)|*.xml", FileName = "surfaces.xml" };
        if (dlg.ShowDialog(this) != true) return;
        LandXmlWriter.Write(dlg.FileName, _project.Surfaces.Where(s => s.TriangleCount > 0));
        Info($"LandXML записан: {dlg.FileName}");
    }

    private void ExportCutFill_Click(object sender, RoutedEventArgs e)
    {
        if (_lastVolumes == null) { Warn("Първо изчислете обемите."); return; }
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "kartograma.csv" };
        if (dlg.ShowDialog(this) != true) return;

        var g = _lastVolumes.Grid;
        var sb = new StringBuilder();
        sb.AppendLine("Колона;Ред;X център;Y център;Работна кота h, m;Изкоп, m3;Насип, m3");
        var ci = CultureInfo.InvariantCulture;
        for (int iy = 0; iy < g.Ny; iy++)
            for (int ix = 0; ix < g.Nx; ix++)
            {
                int i = g.Index(ix, iy);
                if (double.IsNaN(g.MeanDepth[i])) continue;
                var c = g.CellBounds(ix, iy).Center;
                sb.AppendLine(string.Create(ci, $"{ix + 1};{iy + 1};{c.X:F3};{c.Y:F3};{g.MeanDepth[i]:F3};{g.Cut[i]:F3};{g.Fill[i]:F3}"));
            }
        sb.AppendLine(string.Create(ci, $";;;;Общо;{_lastVolumes.Cut:F3};{_lastVolumes.Fill:F3}"));
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        Info($"Картограмата е записана: {dlg.FileName}");
    }

    private void SampleTerrain_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        var s = SampleTerrain.Create(_project.UniqueSurfaceName("Терен (пример)"));
        _project.Surfaces.Add(s);
        _project.ExistingSurfaceName ??= s.Name;
        _active = s;
        DataChanged();
        View.ZoomExtents();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void ZoomExtents_Click(object sender, RoutedEventArgs e) => View.ZoomExtents();

    // =====================================================================
    //  Инструменти в плана
    // =====================================================================

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (View == null) return;
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<ToolMode>(tag, out var mode))
        {
            View.Tool = mode;
            Info(mode switch
            {
                ToolMode.AddPoint => "Щракнете в плана, за да добавите точка.",
                ToolMode.MovePoint => "Влачете точка; щракване без влачене – промяна на котата.",
                ToolMode.DeletePoint => "Щракнете върху точка, за да я изтриете.",
                ToolMode.SwapEdge => "Завъртане на ребро: посочете ребро – оранжево = може (зелено – новият диагонал), червено = не може.",
                ToolMode.DrawBreakline => "Структурна линия: ляв бутон – връх (прилепва към точки), десен бутон/Enter – край.",
                ToolMode.DrawProfile => "Профил: начертайте трасе (ляв бутон – връх, десен бутон/Enter – край).",
                ToolMode.DrawBoundary => "Граница: ляв бутон – връх, десен бутон/Enter – край; после изберете типа.",
                ToolMode.DrawSelection => "Очертайте полигон: ляв бутон – връх, десен бутон/Enter – край.",
                ToolMode.DrawPlatform => "Очертайте контура на платформата: ляв бутон – връх, десен бутон/Enter – край.",
                ToolMode.EditPlatform => "Влачете върховете (проектът се обновява на живо); кръгчетата в средата на страните добавят връх; десен бутон върху връх го изтрива.",
                _ => "Влачене – преместване; колелце – мащаб; Home – покажи всичко.",
            });
        }
    }

    private void OnCursorMoved(Point2 w)
    {
        var sb = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"X = {w.X:F3}   Y = {w.Y:F3}"));
        if (_active?.ElevationAt(w.X, w.Y) is { } z)
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"   Z = {z:F3}"));
        var model = GetGradingModel();
        if (model != null && model.InfluenceBounds.Contains(w.X, w.Y) && model.DesignElevation(w.X, w.Y) is { } zd)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"   Zпр = {zd:F3}"));
            if (model.Existing.ElevationAt(w.X, w.Y) is { } eg)
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"   h = {zd - eg:+0.000;-0.000;0.000}"));
        }
        CursorText.Text = sb.ToString();
    }

    private void OnAddPoint(Point2 w)
    {
        if (_active == null)
        {
            PushUndo();
            _active = new Surface(_project.UniqueSurfaceName("Повърхнина"));
            _project.Surfaces.Add(_active);
            _project.ExistingSurfaceName ??= _active.Name;
        }
        double def = _active.ElevationAt(w.X, w.Y) ?? (_active.Points.Count > 0 ? _active.Points[^1].Z : 0);
        var z = InputDialog.AskNumber(this, "Нова точка",
            string.Create(CultureInfo.InvariantCulture, $"Кота на точката (X = {w.X:F3}, Y = {w.Y:F3}):"), Math.Round(def, 3));
        if (z == null) return;
        PushUndo();
        _active.AddPoint(new Point3(w.X, w.Y, z.Value));
        DataChanged();
    }

    private void OnMovePoint(int index, Point2 w)
    {
        if (_active == null) return;
        PushUndo();
        var p = _active.Points[index];
        _active.SetPoint(index, p with { X = w.X, Y = w.Y });
        DataChanged();
    }

    private void OnEditPoint(int index)
    {
        if (_active == null) return;
        var p = _active.Points[index];
        var z = InputDialog.AskNumber(this, "Кота на точка", "Нова кота:", p.Z);
        if (z == null) return;
        PushUndo();
        _active.SetPoint(index, p with { Z = z.Value });
        DataChanged();
    }

    private void OnDeletePoint(int index)
    {
        if (_active == null) return;
        PushUndo();
        _active.RemovePointAt(index);
        DataChanged();
    }

    private void OnPolygonCompleted(ToolMode mode, List<Point2> polygon)
    {
        if (mode == ToolMode.DrawSelection)
        {
            _selection = polygon;
            RefreshAll();
            var poly = new Polygon2(polygon);
            Info(string.Create(CultureInfo.InvariantCulture, $"Полигон: площ {poly.Area:F2} m², периметър {poly.Perimeter:F2} m."));
            return;
        }
        if (mode == ToolMode.DrawPlatform) CreatePlatform(polygon);
        if (mode == ToolMode.DrawBreakline) CreateBreakline(polygon);
        if (mode == ToolMode.DrawBoundary) CreateBoundary(polygon);
        if (mode == ToolMode.DrawProfile) OpenProfile(polygon);
    }

    private void CreatePlatform(List<Point2> boundary)
    {
        var poly = new Polygon2(boundary);
        var eg = _project.ExistingSurface;
        PushUndo();
        var p = new Platform
        {
            Name = $"Платформа {_project.Platforms.Count + 1}",
            Boundary = boundary,
            BaseElevation = eg != null ? MeanElevation(eg, poly) : 0,
        };
        _project.Platforms.Add(p);
        View.SelectedPlatform = p;
        foreach (TabItem t in RightPanel.Items) if ((string)t.Header == "Вертикално планиране") RightPanel.SelectedItem = t;
        DataChanged();
        Info(string.Create(CultureInfo.InvariantCulture,
            $"Създадена „{p.Name}“: площ {poly.Area:F2} m², кота {p.BaseElevation:F2} (средна от терена)."));
    }

    private void OnPlatformEditStarting(Platform p)
    {
        PushUndo(); // една стъпка „Отмени“ за цялото влачене
        View.SelectedPlatform = p;
        _updatingUi = true;
        PlatformList.SelectedItem = p;
        _updatingUi = false;
    }

    /// <summary>
    /// Контурът на платформа се променя: по време на влаченето – само насрочване на преизчисляването
    /// (изпълнява се, щом мишката спре), а при пускане – пълно обновяване.
    /// </summary>
    private void OnPlatformGeometryChanged(Platform p, bool final)
    {
        _gradingModelValid = false;
        _lastVolumes = null;
        if (final)
        {
            DataChanged();
            Info($"Контурът на „{p.Name}“ е променен ({p.Boundary.Count} върха).");
            return;
        }
        if (View.CutFill != null) { View.CutFill = null; View.RebuildScene(); } // картограмата вече не е актуална
        ScheduleDesignRegeneration();
        LoadPlatformFields();
        View.InvalidateVisual();
    }

    // =====================================================================
    //  Повърхнини
    // =====================================================================

    private void SurfaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        _active = SurfaceList.SelectedItem as Surface;
        RefreshAll();
    }

    // ---------- видимост на повърхнините ----------

    private void SurfaceVisible_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: Surface s }) return;
        // привързването вече е сменило свойството – за „Отмени“ записваме състоянието отпреди щракването
        bool now = s.IsVisible;
        s.IsVisible = !now;
        PushUndo();
        s.IsVisible = now;
        RefreshAll();
        if (ReferenceEquals(s, _active) && !s.IsVisible)
            Info($"Активната повърхнина „{s.Name}“ е скрита – инструментите за редакция продължават да работят върху нея.");
    }

    private void ShowAllSurfaces_Click(object sender, RoutedEventArgs e) =>
        SetVisibility(_ => true);

    private void ShowOnlyActive_Click(object sender, RoutedEventArgs e) =>
        SetVisibility(s => ReferenceEquals(s, _active));

    /// <summary>Сменя видимостта на повърхнините като една стъпка за „Отмени“ (само ако нещо се променя).</summary>
    private void SetVisibility(Func<Surface, bool> visible)
    {
        if (_project.Surfaces.All(s => s.IsVisible == visible(s))) return;
        PushUndo();
        foreach (var s in _project.Surfaces) s.IsVisible = visible(s);
        RefreshAll();
    }

    private void IsDesignBox_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) { IsDesignBox.IsChecked = false; return; }
        PushUndo();
        _active.IsDesign = IsDesignBox.IsChecked == true;
        DataChanged(false);
    }

    private void UpdateStats()
    {
        IsDesignBox.IsEnabled = _active != null;
        DesignLinkBox.Visibility = _active?.IsDesign == true ? Visibility.Visible : Visibility.Collapsed;
        DesignLinkBox.IsChecked = _active?.GradingSource != null;
        DesignLinkBox.ToolTip = _active?.GradingSource is { } gs
            ? string.Create(CultureInfo.InvariantCulture,
                $"Терен: „{gs.ExistingSurfaceName}“, стъпка {gs.GridStep:0.##} m, {(gs.ZoneOnly ? "само зоната на планирането" : "пълно копие на терена")}")
            : "Свържете повърхнината с терена и платформите, за да се обновява автоматично";
        IsDesignBox.IsChecked = _active?.IsDesign == true;
        if (_active == null) { StatsText.Text = "Няма активна повърхнина."; MaxEdgeBox.Text = "0"; return; }
        var s = _active.GetStatistics();
        var ci = CultureInfo.InvariantCulture;
        string failed = _active.FailedBreaklineSegments > 0 ? $"  (⚠ невмъкнати отсечки: {_active.FailedBreaklineSegments})" : "";
        StatsText.Text = s.PointCount == 0 && s.BreaklineCount == 0
            ? "Празна повърхнина – добавете точки."
            : string.Create(ci,
                $"Точки:        {s.PointCount}\n" +
                $"Стр. линии:   {s.BreaklineCount}{failed}\n" +
                $"Граници:      {s.BoundaryCount}\n" +
                $"Триъгълници:  {s.TriangleCount}\n" +
                $"Кота мин/макс: {s.MinZ:F3} / {s.MaxZ:F3}\n" +
                $"Средна кота:  {s.MeanZ:F3}\n" +
                $"Площ 2D:      {s.Area2D:F2} m²\n" +
                $"Площ 3D:      {s.Area3D:F2} m²\n" +
                $"X: {s.Bounds.MinX:F2} … {s.Bounds.MaxX:F2}\n" +
                $"Y: {s.Bounds.MinY:F2} … {s.Bounds.MaxY:F2}");
        MaxEdgeBox.Text = _active.MaxEdgeLength.ToString("0.###", ci);
        SwapsText.Text = _active.EdgeSwaps.Count == 0
            ? "Завъртания на ребра: няма"
            : $"Завъртания на ребра: {_active.EdgeSwaps.Count}" +
              (_active.FailedEdgeSwaps > 0 ? $" (⚠ неприложени: {_active.FailedEdgeSwaps})" : "");
    }

    // ---------- завъртане на ребра ----------

    private void OnSwapEdge(int a, int b)
    {
        if (_active == null) return;
        if (!_active.CanSwapEdge(a, b, out _, out _, out var reason))
        {
            Info(reason ?? "Реброто не може да се завърти.");
            return;
        }
        PushUndo();
        _active.SwapEdge(a, b, out _);
        DataChanged();
        Info($"Реброто е завъртяно. Общо завъртания: {_active.EdgeSwaps.Count}.");
    }

    private void SwapTool_Click(object sender, RoutedEventArgs e) => SetTool(ToolMode.SwapEdge);

    private void RemoveFailedSwaps_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || _active.FailedEdgeSwaps == 0) { Info("Няма неприложени завъртания."); return; }
        PushUndo();
        int n = _active.RemoveFailedEdgeSwaps();
        DataChanged();
        Info($"Премахнати {n} неприложени завъртания.");
    }

    private void ClearSwaps_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || _active.EdgeSwaps.Count == 0) return;
        PushUndo();
        _active.SetEdgeSwaps(Array.Empty<EdgeSwap>());
        DataChanged();
        Info("Всички завъртания са премахнати – триангулацията е отново на Делоне.");
    }

    private void NewSurface_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.AskText(this, "Нова повърхнина", "Име:", _project.UniqueSurfaceName("Повърхнина"));
        if (string.IsNullOrWhiteSpace(name)) return;
        PushUndo();
        _active = new Surface(_project.UniqueSurfaceName(name.Trim()));
        _project.Surfaces.Add(_active);
        _project.ExistingSurfaceName ??= _active.Name;
        DataChanged(false);
        SetTool(ToolMode.AddPoint);
    }

    private void DuplicateSurface_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive()) return;
        PushUndo();
        _active = _active!.Clone(_project.UniqueSurfaceName(_active.Name + " – копие"));
        _project.Surfaces.Add(_active);
        DataChanged(false);
    }

    private void RenameSurface_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive()) return;
        var name = InputDialog.AskText(this, "Преименуване", "Ново име:", _active!.Name)?.Trim();
        if (string.IsNullOrEmpty(name) || name == _active.Name) return;
        if (_project.FindSurface(name) != null) { Warn("Вече има повърхнина с това име."); return; }
        PushUndo();
        if (_project.ExistingSurfaceName == _active.Name) _project.ExistingSurfaceName = name;
        foreach (var d in _project.Surfaces)
            if (d.GradingSource is { } link && link.ExistingSurfaceName == _active.Name) link.ExistingSurfaceName = name;
        _active.Name = name;
        DataChanged(false);
    }

    private void DeleteSurface_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive()) return;
        if (MessageBox.Show(this, $"Да се изтрие ли „{_active!.Name}“?", "Изтриване", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        PushUndo();
        _project.Surfaces.Remove(_active);
        if (_project.ExistingSurfaceName == _active.Name) _project.ExistingSurfaceName = _project.Surfaces.FirstOrDefault()?.Name;
        _active = null;
        DataChanged();
    }

    private void ApplyMaxEdge_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive() || !TryRead(MaxEdgeBox, "Макс. дължина на ребро", out var v, 0)) return;
        PushUndo();
        _active!.MaxEdgeLength = v;
        DataChanged();
    }

    private void ReadViewOptions()
    {
        View.ShowPoints = ShowPointsBox.IsChecked == true;
        View.ShowTriangles = ShowTrianglesBox.IsChecked == true;
        View.ShowContours = ShowContoursBox.IsChecked == true;
        if (_contourBoxesSurface != _active) { LoadContourSettings(); _contourBoxesSurface = _active; }
        View.ShowContourLabels = ContourLabelsBox.IsChecked == true;
        View.ShowBackgroundLabels = BackgroundLabelsBox.IsChecked == true;
        BackgroundLabelsBox.IsEnabled = ContourLabelsBox.IsChecked == true;
        View.BackgroundSurfaces = _project.Surfaces.Where(s => s.IsVisible && !ReferenceEquals(s, _active)).ToList();
        View.ShowCutFill = ShowCutFillBox.IsChecked == true;
        View.ColorMode = (ColorMode)Math.Max(ColorModeBox.SelectedIndex, 0);
        ApplyContourSettings();
    }

    // ---------- стъпка на хоризонталите ----------

    private const int MaxContourLevels = 500;

    /// <summary>Повърхнината, чиито настройки са показани в полетата за стъпка.</summary>
    private Surface? _contourBoxesSurface;

    /// <summary>Чете стъпката и „главна – всяка“ от полетата и ги записва в активната повърхнина.</summary>
    private void ApplyContourSettings()
    {
        var s = _active;
        if (s == null) { ContourHint.Visibility = Visibility.Collapsed; return; }
        bool newInterval = NumberParser.TryParse(ContourIntervalBox.Text, out var ci) && ci > 0 && ci != s.ContourInterval;
        bool newEvery = int.TryParse(MajorEveryBox.Text.Trim(), out var every) && every >= 1 && every != s.MajorContourEvery;
        if (newInterval || newEvery)
        {
            PushUndo(); // една стъпка за „Отмени“ (Ctrl+Z) на промяна
            if (newInterval) s.ContourInterval = ci;
            if (newEvery) s.MajorContourEvery = every;
            UpdateTitle();
        }

        // предупреждение, ако стъпката е твърде гъста за денивелацията на повърхнината
        double used = s.EffectiveContourInterval(MaxContourLevels);
        if (used > s.ContourInterval)
        {
            var st = s.GetStatistics();
            ContourHint.Text = string.Create(CultureInfo.InvariantCulture,
                $"Стъпка {s.ContourInterval:0.###} m дава над {MaxContourLevels} хоризонтали при денивелация {st.MaxZ - st.MinZ:0.#} m – използва се {used:0.###} m.");
            ContourHint.Visibility = Visibility.Visible;
        }
        else ContourHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>Показва настройките на активната повърхнина в полетата.</summary>
    private void LoadContourSettings()
    {
        bool was = _updatingUi;
        _updatingUi = true;
        ContourIntervalBox.IsEnabled = MajorEveryBox.IsEnabled = _active != null;
        if (_active != null)
        {
            ContourIntervalBox.Text = _active.ContourInterval.ToString("0.###", CultureInfo.InvariantCulture);
            MajorEveryBox.Text = _active.MajorContourEvery.ToString(CultureInfo.InvariantCulture);
        }
        ContourStepLabel.Text = _active != null ? $"Хоризонтали на „{_active.Name}“ – стъпка, m:" : "Стъпка на хоризонталите, m:";
        _updatingUi = was;
    }

    private void ContourSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _updatingUi) return;
        // при избор от списъка текстът се обновява след събитието
        Dispatcher.BeginInvoke(() =>
        {
            ApplyContourSettings();
            View.RebuildScene();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ContourSetting_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ContourSetting_Changed(sender, e);
    }

    private void ViewOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _updatingUi) return;
        var settings = ViewSettingsFromUi();
        if (settings != _project.View)
        {
            PushUndo(); // записва състоянието отпреди промяната (настройките в проекта още са старите)
            _project.View = settings;
        }
        ReadViewOptions();
        View.RebuildScene();
    }

    // ---------- настройки на изгледа (в проекта; отменят се с Ctrl+Z) ----------

    private ViewSettings ViewSettingsFromUi() => new()
    {
        ShowPoints = ShowPointsBox.IsChecked == true,
        ShowTriangles = ShowTrianglesBox.IsChecked == true,
        ShowContours = ShowContoursBox.IsChecked == true,
        ShowContourLabels = ContourLabelsBox.IsChecked == true,
        ShowBackgroundLabels = BackgroundLabelsBox.IsChecked == true,
        ShowCutFill = ShowCutFillBox.IsChecked == true,
        ColorMode = Math.Max(ColorModeBox.SelectedIndex, 0),
    };

    /// <summary>Показва настройките на изгледа от проекта в отметките (при зареждане/отмяна).</summary>
    private void LoadViewSettings()
    {
        bool was = _updatingUi;
        _updatingUi = true;
        var v = _project.View;
        ShowPointsBox.IsChecked = v.ShowPoints;
        ShowTrianglesBox.IsChecked = v.ShowTriangles;
        ShowContoursBox.IsChecked = v.ShowContours;
        ContourLabelsBox.IsChecked = v.ShowContourLabels;
        BackgroundLabelsBox.IsChecked = v.ShowBackgroundLabels;
        ShowCutFillBox.IsChecked = v.ShowCutFill;
        ColorModeBox.SelectedIndex = Math.Clamp(v.ColorMode, 0, ColorModeBox.Items.Count - 1);
        _updatingUi = was;
    }

    private void ApplyOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ViewOption_Changed(sender, e);
    }

    // ---------- редакция ----------

    private void RaiseLower_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive() || RequireSelection() is not { } poly || !TryRead(DeltaZBox, "Δh", out var dz)) return;
        PushUndo();
        int n = SurfaceEditor.RaiseLower(_active!, poly, dz);
        DataChanged();
        Info($"Променени {n} точки с {dz:+0.###;-0.###} m.");
    }

    private void Flatten_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive() || RequireSelection() is not { } poly || !TryRead(FlattenZBox, "Кота", out var z)) return;
        PushUndo();
        int n = SurfaceEditor.Flatten(_active!, poly, z);
        DataChanged();
        Info($"Изравнени {n} точки на кота {z:F3}; добавени точки по контура.");
    }

    private void Smooth_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive() || !TryRead(SmoothBox, "Итерации", out var it, 1, 100)) return;
        PushUndo();
        Polygon2? region = _selection is { Count: >= 3 } ? new Polygon2(_selection) : null;
        int n = SurfaceEditor.Smooth(_active!, (int)it, 0.5, region);
        DataChanged();
        Info($"Изгладени {n} точки ({(int)it} итерации).");
    }

    private void Resample_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive() || !TryRead(ResampleBox, "Стъпка", out var step, 0.01)) return;
        var b = _active!.Bounds;
        if (b.Width / step * (b.Height / step) > 2_000_000) { Warn("Твърде малка стъпка за тази площ."); return; }
        PushUndo();
        var s = SurfaceEditor.ResampleToGrid(_active, step,
            _project.UniqueSurfaceName(string.Create(CultureInfo.InvariantCulture, $"{_active.Name} (мрежа {step:0.##} m)")));
        _project.Surfaces.Add(s);
        _active = s;
        DataChanged(false);
    }

    private void DeleteInside_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive() || RequireSelection() is not { } poly) return;
        PushUndo();
        int n = SurfaceEditor.DeleteInside(_active!, poly);
        DataChanged();
        Info($"Изтрити {n} точки.");
    }

    private void DeleteOutside_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive() || RequireSelection() is not { } poly) return;
        PushUndo();
        int n = SurfaceEditor.DeleteOutside(_active!, poly);
        DataChanged();
        Info($"Изтрити {n} точки извън полигона.");
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _selection = null;
        RefreshAll();
    }

    // =====================================================================
    //  Вертикално планиране
    // =====================================================================

    // ---------- структурни линии ----------

    private Surface EnsureActiveSurface()
    {
        if (_active != null) return _active;
        _active = new Surface(_project.UniqueSurfaceName("Повърхнина"));
        _project.Surfaces.Add(_active);
        _project.ExistingSurfaceName ??= _active.Name;
        return _active;
    }

    private void ReportBreaklineResult()
    {
        if (_active is { FailedBreaklineSegments: > 0 } s)
            Warn($"{s.FailedBreaklineSegments} отсечки от структурните линии не можаха да се вмъкнат в триангулацията " +
                 "(напр. линия извън обхвата на повърхнината или изродена геометрия).");
    }

    private void CreateBreakline(List<Point2> vertices)
    {
        var surface = _active;
        bool closed = vertices.Count > 3 && Point2.Distance(vertices[0], vertices[^1]) < 1e-9;
        var ask = MessageBox.Show(this,
            "Котите на върховете да се вземат ли от повърхнината?\n\n" +
            "Да – от повърхнината (за върхове извън нея ще бъдете запитани)\n" +
            "Не – въвеждане на кота за всеки връх",
            "Структурна линия", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (ask == MessageBoxResult.Cancel) return;

        var pts = new List<Point3>();
        double last = 0;
        for (int i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            if (closed && i == vertices.Count - 1) { pts.Add(pts[0]); break; }
            double? z = surface?.ElevationAt(v.X, v.Y);
            if (ask == MessageBoxResult.No || z == null)
            {
                z = InputDialog.AskNumber(this, "Структурна линия",
                    string.Create(CultureInfo.InvariantCulture, $"Кота на връх {i + 1} от {vertices.Count} (X = {v.X:F3}, Y = {v.Y:F3}):"),
                    Math.Round(z ?? last, 3));
                if (z == null) return;
            }
            last = z.Value;
            pts.Add(new Point3(v.X, v.Y, z.Value));
        }

        PushUndo();
        var s = EnsureActiveSurface();
        var line = new Breakline($"СЛ {s.Breaklines.Count + 1}", pts);
        s.AddBreakline(line);
        View.SelectedBreakline = line;
        DataChanged();
        ReportBreaklineResult();
        Info($"Добавена „{line.Name}“: {line.Vertices.Count} върха, {line.Length2D:F2} m.");
    }

    private Breakline? SelectedBreakline => BreaklineList.SelectedItem as Breakline;

    private void BreaklineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        View.SelectedBreakline = SelectedBreakline;
        View.InvalidateVisual();
    }

    private void BreaklineList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_active == null || SelectedBreakline is not { } b) return;
        var name = InputDialog.AskText(this, "Структурна линия", "Име:", b.Name)?.Trim();
        if (string.IsNullOrEmpty(name) || name == b.Name) return;
        PushUndo();
        var renamed = b.WithName(name);
        _active.SetBreakline(_active.Breaklines.ToList().IndexOf(b), renamed);
        View.SelectedBreakline = renamed;
        DataChanged(false);
    }

    private void DrawBreakline_Click(object sender, RoutedEventArgs e) => SetTool(ToolMode.DrawBreakline);

    private void ImportBreaklines_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Текстови файлове (*.txt;*.csv;*.xyz;*.dat)|*.txt;*.csv;*.xyz;*.dat|Всички файлове (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        var geo = MessageBox.Show(this,
            "Файлът в геодезическа координатна система ли е (X – север, Y – изток)?\n\n" +
            "Всяка линия е поредица от редове с координати; линиите се разделят с празен ред или ред без координати (напр. END).",
            "Импорт на структурни линии", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (geo == MessageBoxResult.Cancel) return;

        var lines = PointFileReader.ReadPolylines(dlg.FileName, geo == MessageBoxResult.Yes);
        if (lines.Count == 0) { Warn("Не са намерени линии с поне 2 върха."); return; }

        PushUndo();
        var s = EnsureActiveSurface();
        string baseName = Path.GetFileNameWithoutExtension(dlg.FileName);
        int k = 1;
        foreach (var l in lines) s.AddBreakline(new Breakline($"{baseName} {k++}", l));
        DataChanged();
        ReportBreaklineResult();
        Info($"Импортирани {lines.Count} структурни линии в „{s.Name}“.");
    }

    private void EditBreakline_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || SelectedBreakline is not { } b) { Warn("Изберете структурна линия от списъка."); return; }
        var ci = CultureInfo.InvariantCulture;
        var text = string.Join(Environment.NewLine, b.Vertices.Select(v => string.Create(ci, $"{v.X:F3} {v.Y:F3} {v.Z:F3}")));
        var result = InputDialog.AskMultiline(this, $"Върхове на „{b.Name}“",
            "Един връх на ред: X Y Z (затворена линия – последният връх = първия):", text);
        if (result == null) return;

        var pts = PointFileReader.Parse(result.Split('\n'));
        if (pts.Count < 2) { Warn("Нужни са поне 2 върха."); return; }
        PushUndo();
        var updated = new Breakline(b.Name, pts);
        _active.SetBreakline(_active.Breaklines.ToList().IndexOf(b), updated);
        View.SelectedBreakline = updated;
        DataChanged();
        ReportBreaklineResult();
    }

    private void DeleteBreakline_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || SelectedBreakline is not { } b) { Warn("Изберете структурна линия от списъка."); return; }
        PushUndo();
        _active.RemoveBreaklineAt(_active.Breaklines.ToList().IndexOf(b));
        View.SelectedBreakline = null;
        DataChanged();
    }

    private void DeleteAllBreaklines_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || _active.Breaklines.Count == 0) return;
        if (MessageBox.Show(this, $"Да се изтрият ли всички {_active.Breaklines.Count} структурни линии на „{_active.Name}“?",
                "Изтриване", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        PushUndo();
        _active.SetBreaklines(Array.Empty<Breakline>());
        View.SelectedBreakline = null;
        DataChanged();
    }

    // ---------- граници ----------

    private static readonly string[] BoundaryTypeOptions =
    {
        "Външна – остава само повърхнината вътре",
        "Скриваща – „дупка“ (сграда, водоем, зона без данни)",
        "Показваща – връща повърхнината вътре в скрита зона",
    };

    private SurfaceBoundary? SelectedBoundary => BoundaryList.SelectedItem as SurfaceBoundary;

    private void CreateBoundary(List<Point2> polygon)
    {
        if (_active == null) { Warn("Няма активна повърхнина."); return; }
        int defaultType = _active.Boundaries.Any(b => b.Type == BoundaryType.Outer) ? 1 : 0;
        var choice = InputDialog.AskChoice(this, "Граница на повърхнина", "Тип на границата:", BoundaryTypeOptions, defaultType);
        if (choice == null) return;
        var type = (BoundaryType)choice.Value;
        string typeName = SurfaceBoundary.TypeName(type);

        PushUndo();
        var b = new SurfaceBoundary($"{char.ToUpper(typeName[0])}{typeName[1..]} {_active.Boundaries.Count + 1}", type, polygon);
        _active.AddBoundary(b);
        View.SelectedBoundary = b;
        DataChanged();
        Info(string.Create(CultureInfo.InvariantCulture,
            $"Добавена граница „{b.Name}“ ({typeName}, {b.Polygon.Area:F1} m²). Площ на повърхнината: {_active.GetStatistics().Area2D:F1} m²."));
    }

    private void BoundaryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        View.SelectedBoundary = SelectedBoundary;
        View.InvalidateVisual();
    }

    private void ReplaceBoundary(SurfaceBoundary old, SurfaceBoundary updated)
    {
        PushUndo();
        _active!.SetBoundary(_active.Boundaries.ToList().IndexOf(old), updated);
        View.SelectedBoundary = updated;
        DataChanged();
    }

    private void BoundaryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_active == null || SelectedBoundary is not { } b) return;
        var name = InputDialog.AskText(this, "Граница", "Име:", b.Name)?.Trim();
        if (string.IsNullOrEmpty(name) || name == b.Name) return;
        ReplaceBoundary(b, b.With(name: name));
    }

    private void DrawBoundary_Click(object sender, RoutedEventArgs e) => SetTool(ToolMode.DrawBoundary);

    private void BoundaryFromHull_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive()) return;
        var pts = _active!.Points.Select(p => p.XY)
            .Concat(_active.Breaklines.SelectMany(b => b.Vertices.Select(v => v.XY))).ToList();
        if (pts.Count < 3) { Warn("Нужни са поне 3 точки."); return; }

        double suggested = Math.Round(DataHull.SuggestMaxEdge(pts), 1);
        var max = InputDialog.AskNumber(this, "Граница по контура на данните",
            "Максимална дължина на външно ребро, m.\n" +
            "Външните триъгълници с по-дълги ребра се отрязват (по-малка стойност – по-плътно прилягане).\n" +
            "0 – изпъкнала обвивка. Предложението е 3 × медианата на ребрата:", suggested);
        if (max == null) return;

        var ring = DataHull.Compute(pts, max.Value);
        if (ring.Count < 3) { Warn("Не може да се построи контур."); return; }
        PushUndo();
        var b = new SurfaceBoundary("Контур на данните", BoundaryType.Outer, ring);
        _active.AddBoundary(b);
        View.SelectedBoundary = b;
        DataChanged();
        Info(string.Create(CultureInfo.InvariantCulture,
            $"Добавена външна граница по контура на данните: {ring.Count} върха, {b.Polygon.Area:F1} m²."));
    }

    // ---------- профил и разрези ----------

    private void OpenProfile(List<Point2> line)
    {
        if (_project.Surfaces.All(s => s.TriangleCount == 0)) { Warn("Няма повърхнини с триангулация."); return; }
        View.ProfileLine = line;
        View.SectionLines = null;
        View.InvalidateVisual();

        var existing = _project.ExistingSurface?.Name;
        var design = _active != null && _active.Name != existing ? _active.Name
            : _project.Surfaces.FirstOrDefault(s => s.Name != existing && s.TriangleCount > 0)?.Name;
        var w = new ProfileWindow(() => _project, line, existing, design) { Owner = this };
        w.MarkerChanged += p => { View.Marker = p; View.InvalidateVisual(); };
        w.SectionsChanged += (lines, i) => { View.SectionLines = lines; View.CurrentSection = i; View.InvalidateVisual(); };
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(View.ProfileLine, line)) View.ProfileLine = null;
            View.InvalidateVisual();
        };
        w.Show();
        SetTool(ToolMode.Pan);
    }

    // ---------- DXF ----------

    private void ExportContoursDxf_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive() || _active!.TriangleCount == 0) return;
        double interval = _active.EffectiveContourInterval(MaxContourLevels);
        int every = _active.MajorContourEvery;
        var h = InputDialog.AskNumber(this, "Хоризонтали в DXF",
            string.Create(CultureInfo.InvariantCulture,
                $"Хоризонтали през {interval:0.###} m (всяка {every}-а е главна) – от „Изглед“.\nВисочина на надписите на котите, m (0 – без надписи):"),
            Math.Max(Math.Round(interval * 2, 1), 0.5));
        if (h == null) return;
        var tri = MessageBox.Show(this, "Да се включат ли и триъгълниците (ребрата на TIN като 3D линии на отделен слой)?",
            "Хоризонтали в DXF", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (tri == MessageBoxResult.Cancel) return;
        var dlg = new SaveFileDialog { Filter = "DXF (*.dxf)|*.dxf", FileName = $"{_active.Name} - horizontali.dxf" };
        if (dlg.ShowDialog(this) != true) return;
        int n = DxfWriter.WriteContours(dlg.FileName, _active, interval, every, h.Value, Math.Max(50, h.Value * 60),
            includeTriangles: tri == MessageBoxResult.Yes);
        Info($"Записани {n} хоризонтали{(tri == MessageBoxResult.Yes ? " и триъгълниците" : "")}" +
             $"{(_active.IsDesign ? " (проектни слоеве PROEKTNI_*, червено)" : "")} в {dlg.FileName}");
    }

    private void ExportTinDxf_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireActive() || _active!.TriangleCount == 0) return;
        var dlg = new SaveFileDialog { Filter = "DXF (*.dxf)|*.dxf", FileName = $"{_active.Name} - TIN.dxf" };
        if (dlg.ShowDialog(this) != true) return;
        DxfWriter.WriteTin(dlg.FileName, _active);
        Info($"TIN ({_active.TriangleCount} триъгълника) е записан в {dlg.FileName}");
    }

    private void BoundaryFromSelection_Click(object sender, RoutedEventArgs e)
    {
        if (RequireSelection() == null) return;
        var poly = new List<Point2>(_selection!);
        _selection = null;
        CreateBoundary(poly);
    }

    private void BoundaryType_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || SelectedBoundary is not { } b) { Warn("Изберете граница от списъка."); return; }
        var choice = InputDialog.AskChoice(this, "Граница", $"Тип на „{b.Name}“:", BoundaryTypeOptions, (int)b.Type);
        if (choice == null || (BoundaryType)choice.Value == b.Type) return;
        ReplaceBoundary(b, b.With(type: (BoundaryType)choice.Value));
    }

    private void BoundaryUp_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || SelectedBoundary is not { } b) return;
        var list = _active.Boundaries.ToList();
        int i = list.IndexOf(b);
        if (i <= 0) return;
        PushUndo();
        (list[i - 1], list[i]) = (list[i], list[i - 1]);
        _active.SetBoundaries(list);
        DataChanged();
    }

    private void DeleteBoundary_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || SelectedBoundary is not { } b) { Warn("Изберете граница от списъка."); return; }
        PushUndo();
        _active.RemoveBoundaryAt(_active.Boundaries.ToList().IndexOf(b));
        View.SelectedBoundary = null;
        DataChanged();
    }

    private void ExistingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || ExistingBox.SelectedItem is not Surface s) return;
        PushUndo();
        _project.ExistingSurfaceName = s.Name;
        DataChanged();
    }

    private void PlatformList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        View.SelectedPlatform = SelectedPlatform;
        LoadPlatformFields();
        View.InvalidateVisual();
    }

    private void LoadPlatformFields()
    {
        var p = SelectedPlatform;
        PlatformGroup.IsEnabled = p != null;
        var ci = CultureInfo.InvariantCulture;
        PlNameBox.Text = p?.Name ?? "";
        PlElevBox.Text = p?.BaseElevation.ToString("0.000", ci) ?? "";
        PlSlopeXBox.Text = p?.SlopeXPercent.ToString("0.###", ci) ?? "";
        PlSlopeYBox.Text = p?.SlopeYPercent.ToString("0.###", ci) ?? "";
        PlCutBox.Text = p?.CutSlopeRatio.ToString("0.###", ci) ?? "";
        PlFillBox.Text = p?.FillSlopeRatio.ToString("0.###", ci) ?? "";
        if (p is { Boundary.Count: >= 3 })
        {
            var poly = p.GetPolygon();
            var r = p.GetReferencePoint();
            double zmin = poly.Vertices.Min(v => p.PlaneElevation(v.X, v.Y));
            double zmax = poly.Vertices.Max(v => p.PlaneElevation(v.X, v.Y));
            PlInfoText.Text = string.Create(ci,
                $"Площ: {poly.Area:F2} m², периметър: {poly.Perimeter:F2} m\n" +
                $"Кота в т. ({r.X:F2}; {r.Y:F2}); по контура: {zmin:F3} … {zmax:F3}");
        }
        else PlInfoText.Text = "";
    }

    private void NewPlatform_Click(object sender, RoutedEventArgs e) => SetTool(ToolMode.DrawPlatform);

    private void PlatformFromSelection_Click(object sender, RoutedEventArgs e)
    {
        if (RequireSelection() == null) return;
        var b = new List<Point2>(_selection!);
        _selection = null;
        CreatePlatform(b);
    }

    private void DeletePlatform_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlatform is not { } p) { Warn("Изберете платформа."); return; }
        PushUndo();
        _project.Platforms.Remove(p);
        View.SelectedPlatform = null;
        DataChanged();
    }

    private void ApplyPlatform_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlatform is not { } p) return;
        if (!TryRead(PlElevBox, "Проектна кота", out var z) ||
            !TryRead(PlSlopeXBox, "Наклон по X", out var sx, -100, 100) ||
            !TryRead(PlSlopeYBox, "Наклон по Y", out var sy, -100, 100) ||
            !TryRead(PlCutBox, "Откос в изкоп", out var mc, 0.01, 100) ||
            !TryRead(PlFillBox, "Откос в насип", out var mf, 0.01, 100))
            return;
        PushUndo();
        p.Name = string.IsNullOrWhiteSpace(PlNameBox.Text) ? p.Name : PlNameBox.Text.Trim();
        p.BaseElevation = z;
        p.SlopeXPercent = sx;
        p.SlopeYPercent = sy;
        p.CutSlopeRatio = mc;
        p.FillSlopeRatio = mf;
        DataChanged();
        Info($"„{p.Name}“ е обновена. Натиснете „Изчисли обеми“ за нова картограма.");
    }

    private void PlatformMeanElevation_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlatform is not { } p || _project.ExistingSurface is not { } eg) return;
        PlElevBox.Text = MeanElevation(eg, p.GetPolygon()).ToString("0.000", CultureInfo.InvariantCulture);
    }

    private bool PrepareGrading(out Surface existing, out double cell)
    {
        existing = _project.ExistingSurface!;
        cell = 0;
        if (existing == null || existing.TriangleCount == 0) { Warn("Изберете съществуващ терен с поне 3 точки."); return false; }
        if (_project.Platforms.Count == 0) { Warn("Няма платформи. Очертайте платформа с „Нова платформа“."); return false; }
        return TryRead(CellBox, "Клетка", out cell, 0.05, 1000);
    }

    private async void ComputeVolumes_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareGrading(out var eg, out var cell) || !TryRead(SwellBox, "Коеф. на разрохкване", out var k, 0.5, 2)) return;
        var platforms = _project.Platforms.Select(p => p.Clone()).ToList();
        await RunBusy("Изчисляване на обемите...", async () =>
        {
            var v = await Task.Run(() => VolumeCalculator.Compute(new GradingModel(eg, platforms), cell));
            _lastVolumes = v;
            ResultsText.Text = FormatVolumes("Вертикално планиране – обеми", v, k, cell);
            View.CutFill = v.Grid;
            View.RebuildScene();
            Info("Обемите са изчислени.");
        });
    }

    private async void Balance_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlatform is not { } target) { Warn("Изберете платформа за балансиране."); return; }
        if (!PrepareGrading(out var eg, out var cell) || !TryRead(SwellBox, "Коеф. на разрохкване", out var k, 0.5, 2)) return;

        int idx = _project.Platforms.IndexOf(target);
        var clones = _project.Platforms.Select(p => p.Clone()).ToList();
        await RunBusy($"Балансиране на „{target.Name}“...", async () =>
        {
            var r = await Task.Run(() => EarthworkBalancer.Balance(eg, clones, clones[idx], cell, k));
            PushUndo();
            target.BaseElevation = r.BaseElevation;
            _gradingModelValid = false;
            _lastVolumes = r.Volumes;
            ScheduleDesignRegeneration();
            RefreshAll();
            ResultsText.Text =
                string.Create(CultureInfo.InvariantCulture, $"Балансирана кота на „{target.Name}“: {r.BaseElevation:F3} m\n") +
                (r.Converged ? "" : "(внимание: не е достигната пълна сходимост)\n") + "\n" +
                FormatVolumes("Обеми след балансиране", r.Volumes, k, cell);
            Info($"Балансирано за {r.Iterations} итерации.");
        });
    }

    private async void BuildDesign_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareGrading(out var eg, out _) || !TryRead(DesignStepBox, "Стъпка на проектната TIN", out var step, 0.1, 100)) return;
        var platforms = _project.Platforms.Select(p => p.Clone()).ToList();
        var name = _project.UniqueSurfaceName("Проект");
        bool zoneOnly = ZoneOnlyBox.IsChecked == true;
        await RunBusy("Създаване на проектна повърхнина...", async () =>
        {
            var s = await Task.Run(() =>
            {
                var d = DesignSurfaceBuilder.Build(eg, platforms, step, name, zoneOnly);
                d.EnsureTriangulated();
                return d;
            });
            PushUndo();
            _project.Surfaces.Add(s);
            _active = s;
            if (s.GradingSource is { } src)
            {
                src.GeneratedVersion = s.Version;
                src.LastInputKey = src.InputKey(eg, platforms);
            }
            RefreshAll();
            Info($"Създадена „{name}“: {s.Points.Count} точки, {s.TriangleCount} триъгълника. " +
                 "Обновява се автоматично при промяна на платформите или терена.");
        });
    }

    private async void CompareSurfaces_Click(object sender, RoutedEventArgs e)
    {
        var eg = _project.ExistingSurface;
        if (eg == null || _active == null || ReferenceEquals(eg, _active))
        {
            Warn("Изберете различни „Съществуващ терен“ (тук) и активна повърхнина (раздел „Повърхнини“).");
            return;
        }
        if (!TryRead(CellBox, "Клетка", out var cell, 0.05, 1000) || !TryRead(SwellBox, "Коеф. на разрохкване", out var k, 0.5, 2)) return;
        var design = _active;
        await RunBusy("Изчисляване на обемите между повърхнините...", async () =>
        {
            var v = await Task.Run(() => VolumeCalculator.Compute(eg, design, cell));
            _lastVolumes = v;
            ResultsText.Text = FormatVolumes($"„{eg.Name}“ → „{design.Name}“", v, k, cell);
            View.CutFill = v.Grid;
            View.RebuildScene();
            Info("Обемите са изчислени.");
        });
    }

    private static string FormatVolumes(string title, VolumeResult v, double swell, double cell)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine(new string('─', 38));
        sb.AppendLine(string.Create(ci, $"Изкоп:  {v.Cut,14:F2} m³  ({v.CutArea:F1} m²)"));
        sb.AppendLine(string.Create(ci, $"Насип:  {v.Fill,14:F2} m³  ({v.FillArea:F1} m²)"));
        sb.AppendLine(string.Create(ci, $"Насип − изкоп: {v.Net,10:F2} m³"));
        if (Math.Abs(swell - 1) > 1e-9)
        {
            double net = v.Fill - v.Cut * swell;
            sb.AppendLine(string.Create(ci, $"С разрохкване k = {swell:0.###}:"));
            sb.AppendLine(string.Create(ci, $"  изкоп·k = {v.Cut * swell:F2} m³"));
            sb.AppendLine(string.Create(ci, $"  {(net > 0 ? "недостиг (доставка)" : "излишък (извозване)")}: {Math.Abs(net):F2} m³"));
        }
        else
        {
            sb.AppendLine(v.Net > 0
                ? string.Create(ci, $"Недостиг (доставка): {v.Net:F2} m³")
                : string.Create(ci, $"Излишък (извозване): {-v.Net:F2} m³"));
        }
        sb.AppendLine(string.Create(ci, $"Площ с данни: {v.Area:F1} m²"));
        sb.Append(string.Create(ci, $"Картограма: {v.Grid.Nx} × {v.Grid.Ny} клетки по {cell:0.##} m"));
        return sb.ToString();
    }
}
