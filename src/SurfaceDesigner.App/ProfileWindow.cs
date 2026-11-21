using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SurfaceDesigner.Core;
using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.App;

/// <summary>Надлъжен профил по трасе и напречни разрези с площи и обеми (метод на средните площи).</summary>
public sealed class ProfileWindow : Window
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static readonly Color ExistingColor = Color.FromRgb(0x8B, 0x5A, 0x2B);
    private static readonly Color[] Palette =
    {
        // без червено и синьо – те са за изкоп и насип
        Color.FromRgb(0x1A, 0x8A, 0x3A), Color.FromRgb(0x88, 0x41, 0x9D), Color.FromRgb(0xE6, 0x7E, 0x22),
        Color.FromRgb(0x00, 0x8C, 0x8C), Color.FromRgb(0x33, 0x33, 0x33),
    };

    private readonly Func<Project> _project;
    private readonly List<Point2> _line;
    private readonly double _length;

    // надлъжен профил
    private readonly ProfileChart _profileChart = new();
    private readonly WrapPanel _surfaceChecks = new();
    private readonly TextBox _veBox = new() { Width = 50, Text = "" };
    private readonly TextBlock _profileInfo = new() { Margin = new Thickness(8, 4, 8, 4) };
    private string? _existingName;
    private string? _designName;

    // напречни разрези
    private readonly ProfileChart _sectionChart = new() { XTitle = "Отстояние от оста, m (− ляво, + дясно)" };
    private readonly ComboBox _egBox = new() { Width = 150, DisplayMemberPath = "Name", Margin = new Thickness(2) };
    private readonly ComboBox _designBox = new() { Width = 150, DisplayMemberPath = "Name", Margin = new Thickness(2) };
    private readonly ComboBox _stationBox = new() { Width = 110, Margin = new Thickness(2) };
    private readonly TextBox _intervalBox = new() { Width = 50, Text = "20", Margin = new Thickness(2) };
    private readonly TextBox _leftBox = new() { Width = 50, Text = "25", Margin = new Thickness(2) };
    private readonly TextBox _rightBox = new() { Width = 50, Text = "25", Margin = new Thickness(2) };
    private readonly TextBlock _sectionInfo = new() { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
    private List<SectionResult> _sections = new();

    private sealed record SectionResult(CrossSectionLine Line, List<ProfilePoint> Existing, List<ProfilePoint> Design, SectionAreas Areas);

    /// <summary>Позиция в плана под курсора в графиката (null – няма).</summary>
    public event Action<Point2?>? MarkerChanged;

    /// <summary>Линиите на разрезите и индексът на текущия (null – няма разрези).</summary>
    public event Action<List<(Point2, Point2)>?, int>? SectionsChanged;

    public ProfileWindow(Func<Project> project, List<Point2> line, string? existingName, string? designName)
    {
        _project = project;
        _line = line;
        _length = Alignment.Length(line);
        _existingName = existingName;
        _designName = designName;

        Title = $"Профил и разрези – трасе {_length:F2} m";
        Width = 1100;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _profileChart.XFormat = Alignment.FormatStation;
        _profileChart.HoverChanged += s => MarkerChanged?.Invoke(s is double v ? Alignment.PointAt(_line, v).Point : null);
        _sectionChart.XFormat = s => s.ToString("+0.0;-0.0;0", Ci);
        _sectionChart.HoverChanged += OnSectionHover;

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Надлъжен профил", Content = BuildProfileTab() });
        tabs.Items.Add(new TabItem { Header = "Напречни разрези", Content = BuildSectionsTab() });
        Content = tabs;

        Closed += (_, _) =>
        {
            MarkerChanged?.Invoke(null);
            SectionsChanged?.Invoke(null, -1);
        };
        Loaded += (_, _) => { RefreshSurfaces(); UpdateProfile(); };
    }

    // =====================================================================
    //  Надлъжен профил
    // =====================================================================
    private UIElement BuildProfileTab()
    {
        var top = new WrapPanel { Margin = new Thickness(4) };
        top.Children.Add(new TextBlock { Text = "Повърхнини:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) });
        top.Children.Add(_surfaceChecks);
        top.Children.Add(new TextBlock { Text = "   Вертикално преувеличение (празно = авто):", VerticalAlignment = VerticalAlignment.Center });
        _veBox.Margin = new Thickness(4, 2, 4, 2);
        _veBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) UpdateProfile(); };
        _veBox.LostFocus += (_, _) => UpdateProfile();
        top.Children.Add(_veBox);
        top.Children.Add(MakeButton("Обнови", (_, _) => { RefreshSurfaces(); UpdateProfile(); }));
        top.Children.Add(MakeButton("Експорт CSV...", (_, _) => ExportProfileCsv()));

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_profileInfo, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(_profileInfo);
        dock.Children.Add(_profileChart);
        return dock;
    }

    private void RefreshSurfaces()
    {
        var p = _project();
        var checkedNames = _surfaceChecks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToHashSet();
        bool first = _surfaceChecks.Children.Count == 0;
        _surfaceChecks.Children.Clear();
        foreach (var s in p.Surfaces.Where(s => s.TriangleCount > 0))
        {
            bool on = first ? s.Name == _existingName || s.Name == _designName || p.Surfaces.Count <= 2 : checkedNames.Contains(s.Name);
            var cb = new CheckBox { Content = s.Name, IsChecked = on, Margin = new Thickness(4, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            cb.Click += (_, _) => UpdateProfile();
            _surfaceChecks.Children.Add(cb);
        }

        var surfaces = p.Surfaces.Where(s => s.TriangleCount > 0).ToList();
        _egBox.ItemsSource = surfaces;
        _designBox.ItemsSource = surfaces;
        _egBox.SelectedItem = surfaces.FirstOrDefault(s => s.Name == _existingName) ?? surfaces.FirstOrDefault();
        _designBox.SelectedItem = surfaces.FirstOrDefault(s => s.Name == _designName && s.Name != _existingName)
                                  ?? surfaces.FirstOrDefault(s => s != _egBox.SelectedItem);
    }

    private Color ColorFor(string name, ref int otherIndex) =>
        name == _existingName ? ExistingColor : Palette[otherIndex++ % Palette.Length];

    private void UpdateProfile()
    {
        var p = _project();
        var names = _surfaceChecks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList();
        _profileChart.Series.Clear();
        _profileChart.ExistingIndex = _profileChart.DesignIndex = -1;
        int k = 0;
        foreach (var name in names)
        {
            var s = p.FindSurface(name);
            if (s == null) continue;
            if (name == _existingName) _profileChart.ExistingIndex = _profileChart.Series.Count;
            else if (_profileChart.DesignIndex < 0) _profileChart.DesignIndex = _profileChart.Series.Count;
            _profileChart.Series.Add(new ChartSeries(name, ColorFor(name, ref k), ProfileSampler.Sample(s, _line)));
        }

        _profileChart.VerticalExaggeration = NumberParser.TryParse(_veBox.Text, out var ve) && ve > 0 ? ve : null;
        _profileChart.Refresh();

        var sb = new StringBuilder(string.Create(Ci, $"Дължина: {_length:F2} m ({Alignment.FormatStation(_length)})"));
        foreach (var s in _profileChart.Series)
        {
            var z = s.Points.Where(q => q.Z.HasValue).Select(q => q.Z!.Value).ToList();
            if (z.Count > 0) sb.Append(string.Create(Ci, $"   |   {s.Name}: {z.Min():F2} … {z.Max():F2}"));
        }

        if (_profileChart.ExistingIndex >= 0 && _profileChart.DesignIndex >= 0)
        {
            var a = ProfileSampler.Areas(_profileChart.Series[_profileChart.ExistingIndex].Points, _profileChart.Series[_profileChart.DesignIndex].Points);
            sb.Append(string.Create(Ci, $"   |   площ в профила: изкоп {a.Cut:F2} m², насип {a.Fill:F2} m²"));
        }

        Dispatcher.BeginInvoke(
            () => _profileInfo.Text = sb + string.Create(Ci, $"   |   верт. преувеличение ×{_profileChart.ActualExaggeration:F1}"),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ExportProfileCsv()
    {
        if (_profileChart.Series.Count == 0) return;
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "profil.csv" };
        if (dlg.ShowDialog(this) != true) return;
        var stations = _profileChart.Series.SelectMany(s => s.Points.Select(q => q.Station)).Distinct().OrderBy(s => s).ToList();
        var sb = new StringBuilder("Километраж;Км;X;Y;" + string.Join(";", _profileChart.Series.Select(s => s.Name)) + "\n");
        foreach (var st in stations)
        {
            var pt = Alignment.PointAt(_line, st).Point;
            sb.Append(string.Create(Ci, $"{st:F3};{Alignment.FormatStation(st)};{pt.X:F3};{pt.Y:F3}"));
            foreach (var s in _profileChart.Series)
                sb.Append(';').Append(ProfileSampler.ElevationAtStation(s.Points, st)?.ToString("F3", Ci) ?? "");
            sb.Append('\n');
        }

        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    // =====================================================================
    //  Напречни разрези
    // =====================================================================
    private UIElement BuildSectionsTab()
    {
        var top = new WrapPanel { Margin = new Thickness(4) };
        void Lbl(string t) => top.Children.Add(new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) });
        Lbl("Терен:"); top.Children.Add(_egBox);
        Lbl("Проект:"); top.Children.Add(_designBox);
        Lbl("Интервал, m:"); top.Children.Add(_intervalBox);
        Lbl("Ширина вляво, m:"); top.Children.Add(_leftBox);
        Lbl("вдясно, m:"); top.Children.Add(_rightBox);
        top.Children.Add(MakeButton("Генерирай", (_, _) => GenerateSections()));

        var nav = new WrapPanel { Margin = new Thickness(4, 0, 4, 4) };
        nav.Children.Add(MakeButton("◀", (_, _) => Step(-1)));
        nav.Children.Add(_stationBox);
        nav.Children.Add(MakeButton("▶", (_, _) => Step(+1)));
        nav.Children.Add(MakeButton("Експорт на таблицата (CSV)...", (_, _) => ExportSectionsCsv()));
        _stationBox.SelectionChanged += (_, _) => ShowSection(_stationBox.SelectedIndex);

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(nav, Dock.Top);
        DockPanel.SetDock(_sectionInfo, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(nav);
        dock.Children.Add(_sectionInfo);
        dock.Children.Add(_sectionChart);
        _sectionInfo.Text = "Изберете терен и проект и натиснете „Генерирай“. Площите и обемите се смятат по метода на средните площи.";
        return dock;
    }

    private void GenerateSections()
    {
        if (_egBox.SelectedItem is not Surface eg || _designBox.SelectedItem is not Surface design)
        {
            MessageBox.Show(this, "Изберете повърхнини за терен и проект.", Title);
            return;
        }

        if (!NumberParser.TryParse(_intervalBox.Text, out var interval) || interval <= 0 ||
            !NumberParser.TryParse(_leftBox.Text, out var left) || left < 0 ||
            !NumberParser.TryParse(_rightBox.Text, out var right) || right < 0 || left + right <= 0)
        {
            MessageBox.Show(this, "Невалидни интервал или ширини.", Title);
            return;
        }

        if (_length / interval > 5000) { MessageBox.Show(this, "Твърде много разрези – увеличете интервала.", Title); return; }

        _sections = Alignment.CrossSections(_line, interval, left, right).Select(cs =>
        {
            var seg = new[] { cs.Left, cs.Right };
            var pe = Offset(ProfileSampler.Sample(eg, seg), cs.LeftWidth);
            var pd = Offset(ProfileSampler.Sample(design, seg), cs.LeftWidth);
            return new SectionResult(cs, pe, pd, ProfileSampler.Areas(pe, pd));
        }).ToList();

        _stationBox.ItemsSource = _sections.Select(s => Alignment.FormatStation(s.Line.Station)).ToList();
        _sectionChart.Series.Clear();
        _stationBox.SelectedIndex = _sections.Count > 0 ? 0 : -1;
        SectionsChanged?.Invoke(_sections.Select(s => (s.Line.Left, s.Line.Right)).ToList(), _stationBox.SelectedIndex);
    }

    /// <summary>Километражът по разреза става отстояние от оста (− вляво).</summary>
    private static List<ProfilePoint> Offset(List<ProfilePoint> profile, double leftWidth) =>
        profile.Select(p => p with { Station = p.Station - leftWidth }).ToList();

    private void Step(int d)
    {
        if (_sections.Count == 0) return;
        _stationBox.SelectedIndex = Math.Clamp(_stationBox.SelectedIndex + d, 0, _sections.Count - 1);
    }

    private (double Cut, double Fill) Volumes(int upTo)
    {
        double cut = 0, fill = 0;
        for (int i = 1; i <= upTo && i < _sections.Count; i++)
        {
            double ds = _sections[i].Line.Station - _sections[i - 1].Line.Station;
            cut += (_sections[i].Areas.Cut + _sections[i - 1].Areas.Cut) / 2 * ds;
            fill += (_sections[i].Areas.Fill + _sections[i - 1].Areas.Fill) / 2 * ds;
        }

        return (cut, fill);
    }

    private void ShowSection(int i)
    {
        if (i < 0 || i >= _sections.Count) return;
        var s = _sections[i];
        string egName = ((Surface)_egBox.SelectedItem).Name, dName = ((Surface)_designBox.SelectedItem).Name;
        _sectionChart.Series.Clear();
        _sectionChart.Series.Add(new ChartSeries(egName, ExistingColor, s.Existing));
        _sectionChart.Series.Add(new ChartSeries(dName, Palette[0], s.Design));
        _sectionChart.ExistingIndex = 0;
        _sectionChart.DesignIndex = 1;
        _sectionChart.VerticalExaggeration = NumberParser.TryParse(_veBox.Text, out var ve) && ve > 0 ? ve : null;
        _sectionChart.Refresh();

        var (tc, tf) = Volumes(_sections.Count - 1);
        var (cc, cf) = Volumes(i);
        _sectionInfo.Text = string.Create(
            Ci,
            $"Разрез {Alignment.FormatStation(s.Line.Station)}:  изкоп {s.Areas.Cut:F2} m²,  насип {s.Areas.Fill:F2} m²\n" +
            $"Натрупано до тук:  изкоп {cc:F1} m³,  насип {cf:F1} m³      " +
            $"Общо за трасето ({_sections.Count} разреза):  изкоп {tc:F1} m³,  насип {tf:F1} m³,  баланс {tf - tc:+0.0;-0.0;0} m³");
        SectionsChanged?.Invoke(_sections.Select(x => (x.Line.Left, x.Line.Right)).ToList(), i);
    }

    private void OnSectionHover(double? offset)
    {
        int i = _stationBox.SelectedIndex;
        if (offset is not double o || i < 0 || i >= _sections.Count) { MarkerChanged?.Invoke(null); return; }
        var l = _sections[i].Line;
        double width = l.LeftWidth + l.RightWidth;
        MarkerChanged?.Invoke(l.Left + (l.Right - l.Left) * ((o + l.LeftWidth) / width));
    }

    private void ExportSectionsCsv()
    {
        if (_sections.Count == 0) { MessageBox.Show(this, "Първо генерирайте разрезите.", Title); return; }
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "razrezi.csv" };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder("Километраж;Км;Площ изкоп, m2;Площ насип, m2;Обем изкоп, m3;Обем насип, m3;Натрупан изкоп, m3;Натрупан насип, m3\n");
        double accC = 0, accF = 0;
        for (int i = 0; i < _sections.Count; i++)
        {
            double vc = 0, vf = 0;
            if (i > 0)
            {
                double ds = _sections[i].Line.Station - _sections[i - 1].Line.Station;
                vc = (_sections[i].Areas.Cut + _sections[i - 1].Areas.Cut) / 2 * ds;
                vf = (_sections[i].Areas.Fill + _sections[i - 1].Areas.Fill) / 2 * ds;
            }

            accC += vc; accF += vf;
            var s = _sections[i];
            sb.Append(string.Create(
                Ci,
                $"{s.Line.Station:F3};{Alignment.FormatStation(s.Line.Station)};{s.Areas.Cut:F3};{s.Areas.Fill:F3};{vc:F3};{vf:F3};{accC:F3};{accF:F3}\n"));
        }

        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2) };
        b.Click += click;
        return b;
    }
}
