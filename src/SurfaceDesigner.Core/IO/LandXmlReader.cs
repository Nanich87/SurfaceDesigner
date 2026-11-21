using System.Globalization;
using System.Xml.Linq;
using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.IO;

/// <summary>Резултат от импорт на LandXML: повърхнините и съобщения (по едно или повече на повърхнина).</summary>
public sealed record LandXmlImportResult(List<Surface> Surfaces, List<string> Messages);

/// <summary>
/// Четене на TIN повърхнини от LandXML (1.0 / 1.1 / 1.2). Триангулацията от &lt;Faces&gt; се възпроизвежда точно:
/// липсващите спрямо триангулацията на Делоне ребра се получават с флипове, записани като завъртания на ребра,
/// а контурите на множеството от триъгълници стават граници (външни, скриващи за дупки, показващи за острови).
/// Координатите в LandXML са „север изток кота“.
/// </summary>
public static class LandXmlReader
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static LandXmlImportResult Read(string path) => Parse(XDocument.Load(path));

    public static LandXmlImportResult Parse(XDocument doc)
    {
        var surfaces = new List<Surface>();
        var messages = new List<string>();

        foreach (var se in doc.Descendants().Where(e => e.Name.LocalName == "Surface"))
        {
            string name = (string?)se.Attribute("name") ?? $"LandXML {surfaces.Count + 1}";
            var def = Child(se, "Definition");
            if (def == null) { messages.Add($"„{name}“: няма <Definition> – пропусната."); continue; }
            string type = (string?)def.Attribute("surfType") ?? "TIN";
            if (!type.Equals("TIN", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add($"„{name}“: тип „{type}“ не се поддържа (само TIN) – пропусната.");
                continue;
            }

            // точки: id → (изток, север, кота)
            var points = new List<(string Id, Point3 P)>();
            foreach (var p in Children(Child(def, "Pnts"), "P"))
            {
                var v = Numbers(p.Value);
                if (v.Count < 3) continue;
                points.Add(((string?)p.Attribute("id") ?? (points.Count + 1).ToString(Ci), new Point3(v[1], v[0], v[2])));
            }

            // триъгълници (невидимите, i="1", се пропускат)
            var faces = new List<string[]>();
            int hidden = 0;
            foreach (var f in Children(Child(def, "Faces"), "F"))
            {
                if ((string?)f.Attribute("i") == "1") { hidden++; continue; }
                var ids = f.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length >= 3) faces.Add(new[] { ids[0], ids[1], ids[2] });
            }

            // структурни линии от <SourceData> (PntList3D: „север изток кота“, PntList2D: „север изток“)
            var breaklines = new List<SourceBreakline>();
            foreach (var bl in Children(Child(Child(se, "SourceData"), "Breaklines"), "Breakline"))
            {
                var l3 = Child(bl, "PntList3D");
                var l2 = Child(bl, "PntList2D");
                var nums = Numbers((l3 ?? l2)?.Value ?? "");
                int dim = l3 != null ? 3 : 2;
                var v = new List<Point3>();
                for (int i = 0; i + dim - 1 < nums.Count; i += dim)
                    v.Add(new Point3(nums[i + 1], nums[i], dim == 3 ? nums[i + 2] : double.NaN));
                string blName = (string?)bl.Attribute("name") ?? (string?)bl.Attribute("desc") ?? $"Структурна линия {breaklines.Count + 1}";
                if (v.Count >= 2) breaklines.Add(new SourceBreakline(blName, v));
            }

            if (points.Count < 3) { messages.Add($"„{name}“: по-малко от 3 точки – пропусната."); continue; }
            Surface surface;
            if (faces.Count > 0)
                surface = FromTin(name, points, faces, breaklines, messages);
            else
            {
                // само точки – линиите (с коти) се вграждат директно
                var withZ = breaklines.Where(b => b.Vertices.All(p => !double.IsNaN(p.Z))).ToList();
                surface = new Surface(name, points.Select(p => p.P), withZ.Select(b => new Breakline(b.Name, b.Vertices)));
                if (withZ.Count > 0) messages.Add($"„{name}“: импортирани {withZ.Count} структурни линии.");
                if (withZ.Count < breaklines.Count)
                    messages.Add($"„{name}“: {breaklines.Count - withZ.Count} структурни линии без коти (PntList2D) – пропуснати.");
            }

            if (faces.Count == 0) messages.Add($"„{name}“: няма триъгълници – импортирани {points.Count} точки, триангулирани наново.");
            if (hidden > 0) messages.Add($"„{name}“: пропуснати {hidden} невидими триъгълника.");
            surfaces.Add(surface);
        }

        // Файл само с точки (CgPoints) → повърхнина от точките
        if (surfaces.Count == 0)
        {
            var cg = doc.Descendants().Where(e => e.Name.LocalName == "CgPoint")
                .Select(e => Numbers(e.Value)).Where(v => v.Count >= 3)
                .Select(v => new Point3(v[1], v[0], v[2])).ToList();
            if (cg.Count >= 3)
            {
                surfaces.Add(new Surface("Точки (LandXML)", cg));
                messages.Add($"Няма повърхнини – импортирани {cg.Count} точки (CgPoints) като нова повърхнина.");
            }
        }

        if (surfaces.Count == 0) messages.Add("Във файла няма TIN повърхнини или точки.");
        return new LandXmlImportResult(surfaces, messages);
    }

    /// <summary>Повърхнина, чиято триангулация съвпада точно с дадените триъгълници.</summary>
    private sealed record SourceBreakline(string Name, List<Point3> Vertices);

    private static Surface FromTin(
        string name,
        List<(string Id, Point3 P)> points,
        List<string[]> faceIds,
        List<SourceBreakline> sourceBreaklines,
        List<string> messages)
    {
        // само използваните точки; дублирани координати (до 0.1 мм) – към първата
        var used = new HashSet<string>(faceIds.SelectMany(f => f));
        var pts = new List<Point3>();
        var byId = new Dictionary<string, int>();
        var byKey = new Dictionary<(long, long), int>();
        foreach (var (id, p) in points)
        {
            if (!used.Contains(id) || byId.ContainsKey(id)) continue;
            var key = ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4));
            if (!byKey.TryGetValue(key, out int idx))
            {
                idx = pts.Count;
                pts.Add(p);
                byKey[key] = idx;
            }

            byId[id] = idx;
        }

        var faces = new List<(int A, int B, int C)>();
        int missing = 0, degenerate = 0;
        foreach (var f in faceIds)
        {
            if (!byId.TryGetValue(f[0], out int a) || !byId.TryGetValue(f[1], out int b) || !byId.TryGetValue(f[2], out int c))
            {
                missing++;
                continue;
            }

            double area = Point2.Cross(pts[b].XY - pts[a].XY, pts[c].XY - pts[a].XY);
            if (a == b || b == c || a == c || Math.Abs(area) < 1e-12) { degenerate++; continue; }
            faces.Add(area > 0 ? (a, b, c) : (a, c, b)); // обратно на часовниковата стрелка
        }

        if (missing > 0) messages.Add($"„{name}“: {missing} триъгълника сочат към несъществуващи точки – пропуснати.");
        if (degenerate > 0) messages.Add($"„{name}“: {degenerate} изродени триъгълника – пропуснати.");

        // 0. Структурни линии – проследени по ребрата на източника (котите – от точките на TIN)
        var breaklines = BreaklinesAlongFaces(name, sourceBreaklines, pts, faces, messages);

        var surface = new Surface(name, pts, breaklines);
        surface.EnsureTriangulated();

        // индексите на триъгълниците от източника → индекси на върховете на TIN (по координати; точките,
        // съвпадащи с върхове на структурни линии, са заменени от тях)
        var verts = surface.Vertices;
        var vertexByKey = new Dictionary<(long, long), int>();
        foreach (int vi in surface.Triangles) vertexByKey.TryAdd(Key(verts[vi].XY), vi);
        int V(int ptIndex) => vertexByKey.TryGetValue(Key(pts[ptIndex].XY), out int vi) ? vi : -1;

        // 1. Ребрата на източника – чрез флипове от ограничената триангулация (записват се като завъртания)
        var constrained = surface.GetEdges().Where(e => surface.IsBreaklineEdge(e.A, e.B)).Select(e => ConstrainedDelaunay.Key(e.A, e.B));
        var mesh = new TriMesh(verts.Select(v => v.XY).ToList(), verts.Select(v => v.Z).ToList(), surface.Triangles, constrained)
        {
            FlipLog = new List<(Point2, Point2)>(),
        };
        var edges = new HashSet<(int, int)>();
        int unmapped = 0;
        foreach (var (fa, fb, fc) in faces)
        {
            int a = V(fa), b = V(fb), c = V(fc);
            if (a < 0 || b < 0 || c < 0) { unmapped++; continue; }
            edges.Add(ConstrainedDelaunay.Key(a, b));
            edges.Add(ConstrainedDelaunay.Key(b, c));
            edges.Add(ConstrainedDelaunay.Key(c, a));
        }

        if (unmapped > 0) messages.Add($"„{name}“: {unmapped} триъгълника използват точки извън триангулацията.");
        int failed = 0;
        foreach (var (a, b) in edges)
            if (!mesh.HasEdge(a, b) && !mesh.InsertConstraint(a, b, restoreDelaunay: false))
            {
                if (failed++ == 0)
                {
                    messages.Add(string.Create(Ci, $"„{name}“: реброто {verts[a].XY}–{verts[b].XY} не може да се възпроизведе: {mesh.LastFailure}") +
                        (mesh.LastFailure == "пресича друго ограничение"
                            ? " – ребра от източника се пресичат, т.е. файлът съдържа застъпващи се (обърнати) триъгълници."
                            : "."));
                }
            }

        surface.SetEdgeSwaps(mesh.FlipLog!.Select(f => new EdgeSwap(f.A, f.B)));

        // 2. Контурите на множеството от триъгълници → граници (ако не покрива цялата изпъкнала обвивка)
        if (faces.Count != surface.TriangleCount)
            surface.SetBoundaries(BoundariesFromFaces(pts, faces));

        // 3. Проверка
        int diff = CompareTriangles(surface, pts, faces);
        if (diff == 0 && failed == 0)
        {
            messages.Add($"„{name}“: {pts.Count} точки, {faces.Count} триъгълника – триангулацията е възпроизведена точно" +
                         (surface.EdgeSwaps.Count > 0 ? $" ({surface.EdgeSwaps.Count} завъртания на ребра)" : "") +
                         (surface.Breaklines.Count > 0 ? $", {surface.Breaklines.Count} структурни линии" : "") +
                         (surface.Boundaries.Count > 0 ? $", {surface.Boundaries.Count} граници." : "."));
        }
        else
        {
            messages.Add($"„{name}“: {pts.Count} точки, {faces.Count} триъгълника – {diff} триъгълника се различават от източника " +
                         $"(невъзпроизведени ребра: {failed}).");
        }

        return surface;
    }

    private static (long X, long Y) Key(Point2 p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4));

    /// <summary>
    /// Структурните линии от източника, проследени по ребрата на триъгълниците: всеки връх се свързва с точка на TIN
    /// (до 5 мм), между два върха се минава по ребрата през всички точки, лежащи на отсечката (напр. пресичания
    /// с други линии). Отсечки, които не са ребра в източника, се изпускат (линията се разделя) – иначе биха
    /// променили триангулацията. Котите са тези на точките на TIN.
    /// </summary>
    private static List<Breakline> BreaklinesAlongFaces(
        string surfaceName,
        List<SourceBreakline> source,
        List<Point3> pts,
        List<(int A, int B, int C)> faces,
        List<string> messages)
    {
        var result = new List<Breakline>();
        if (source.Count == 0) return result;
        const double tol = 0.005, cell = 0.01;

        var grid = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < pts.Count; i++)
        {
            var c = ((long)Math.Floor(pts[i].X / cell), (long)Math.Floor(pts[i].Y / cell));
            if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>();
            l.Add(i);
        }

        int Match(Point2 p)
        {
            long cx = (long)Math.Floor(p.X / cell), cy = (long)Math.Floor(p.Y / cell);
            int best = -1;
            double bestD = tol;
            for (long dy = -1; dy <= 1; dy++)
            {
                for (long dx = -1; dx <= 1; dx++)
                {
                    if (grid.TryGetValue((cx + dx, cy + dy), out var l))
                    {
                        foreach (int i in l)
                        {
                            double d = Point2.Distance(pts[i].XY, p);
                            if (d <= bestD) { bestD = d; best = i; }
                        }
                    }
                }
            }

            return best;
        }

        var adj = new Dictionary<int, HashSet<int>>();
        void Link(int a, int b)
        {
            if (!adj.TryGetValue(a, out var s)) adj[a] = s = new HashSet<int>();
            s.Add(b);
        }

        foreach (var (a, b, c) in faces) { Link(a, b); Link(b, a); Link(b, c); Link(c, b); Link(c, a); Link(a, c); }

        // път по ребрата от a до b, през точки върху отсечката a–b (без a); null – няма такъв път
        List<int>? Chain(int a, int b)
        {
            var a1 = pts[a].XY;
            var d = pts[b].XY - a1;
            double len2 = Point2.Dot(d, d), len = Math.Sqrt(len2);
            var path = new List<int>();
            int cur = a;
            double tCur = 0;
            for (int guard = 0; guard < pts.Count && cur != b; guard++)
            {
                if (!adj.TryGetValue(cur, out var nb)) return null;
                if (nb.Contains(b)) { path.Add(b); return path; }
                int best = -1;
                double bestT = 2;
                foreach (int c in nb)
                {
                    var v = pts[c].XY - a1;
                    double t = Point2.Dot(v, d) / len2;
                    if (t <= tCur + 1e-12 || t >= 1) continue;
                    if (Math.Abs(Point2.Cross(d, v)) / len > tol) continue;
                    if (t < bestT) { bestT = t; best = c; }
                }

                if (best < 0) return null;
                path.Add(best);
                cur = best;
                tCur = bestT;
            }

            return cur == b ? path : null;
        }

        int unmatched = 0, dropped = 0;
        foreach (var bl in source)
        {
            var parts = new List<List<int>>();
            var run = new List<int>();
            void Flush()
            {
                if (run.Count >= 2) parts.Add(run);
                run = new List<int>();
            }

            foreach (var v in bl.Vertices)
            {
                int cur = Match(v.XY);
                if (cur < 0) { unmatched++; Flush(); continue; }
                if (run.Count == 0) { run.Add(cur); continue; }
                if (run[^1] == cur) continue;
                var path = Chain(run[^1], cur);
                if (path == null) { dropped++; Flush(); run.Add(cur); }
                else run.AddRange(path);
            }

            Flush();
            for (int k = 0; k < parts.Count; k++)
                result.Add(new Breakline(parts.Count == 1 ? bl.Name : $"{bl.Name} (част {k + 1})", parts[k].Select(i => pts[i])));
        }

        messages.Add($"„{surfaceName}“: импортирани {result.Count} структурни линии от {source.Count} в източника.");
        if (unmatched > 0)
            messages.Add($"„{surfaceName}“: {unmatched} върха на структурни линии не съвпадат с точки на повърхнината (обикновено са извън нея) – линиите са разделени там.");
        if (dropped > 0)
            messages.Add($"„{surfaceName}“: {dropped} отсечки на структурни линии не са ребра в триангулацията на източника – изпуснати.");
        return result;
    }

    /// <summary>
    /// Граници по контурите на множеството от триъгълници: външни контури (обратно на часовника) – външни граници,
    /// контури на дупки (по часовника) – скриващи, контури на острови в дупки – показващи; подредени по вложеност.
    /// </summary>
    private static List<SurfaceBoundary> BoundariesFromFaces(List<Point3> pts, List<(int A, int B, int C)> faces)
    {
        var count = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in faces)
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                count[ConstrainedDelaunay.Key(u, v)] = count.GetValueOrDefault(ConstrainedDelaunay.Key(u, v)) + 1;

        // насочени контурни ребра (вътрешността е отляво)
        var outgoing = new Dictionary<int, List<int>>();
        foreach (var (a, b, c) in faces)
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                if (count[ConstrainedDelaunay.Key(u, v)] == 1)
                {
                    if (!outgoing.TryGetValue(u, out var list)) outgoing[u] = list = new List<int>();
                    list.Add(v);
                }

        var loops = new List<List<int>>();
        foreach (int start in outgoing.Keys.ToList())
        {
            while (outgoing.TryGetValue(start, out var outs) && outs.Count > 0)
            {
                var loop = new List<int> { start };
                int cur = outs[^1];
                outs.RemoveAt(outs.Count - 1);
                int guard = 0;
                while (cur != start && guard++ < pts.Count + 10)
                {
                    loop.Add(cur);
                    if (!outgoing.TryGetValue(cur, out var next) || next.Count == 0) break;
                    int n = next[^1];
                    next.RemoveAt(next.Count - 1);
                    cur = n;
                }

                if (cur == start && loop.Count >= 3) loops.Add(loop);
            }
        }

        var polys = loops.Select(l => new Polygon2(l.Select(i => pts[i].XY))).ToList();
        var items = new List<(int Depth, SurfaceBoundary Boundary)>();
        for (int i = 0; i < loops.Count; i++)
        {
            var l = loops[i];

            // точка непосредствено отляво на първото ребро – в областта на триъгълниците
            var a = pts[l[0]].XY; var b = pts[l[1]].XY;
            var d = b - a;
            var probe = (a + b) * 0.5 + new Point2(-d.Y, d.X) * (1e-6 / Math.Max(d.Length, 1e-12) * Math.Max(d.Length, 1e-3));
            int depth = 0;
            for (int j = 0; j < polys.Count; j++)
                if (j != i && polys[j].Contains(probe)) depth++;

            bool ccw = polys[i].SignedArea > 0;
            var type = !ccw ? BoundaryType.Hide : depth == 0 ? BoundaryType.Outer : BoundaryType.Show;
            string label = type switch { BoundaryType.Outer => "Контур", BoundaryType.Hide => "Дупка", _ => "Остров" };
            items.Add((depth, new SurfaceBoundary($"{label} {i + 1} (LandXML)", type, polys[i].Vertices)));
        }

        return items.OrderBy(t => t.Depth).Select(t => t.Boundary).ToList();
    }

    /// <summary>Брой триъгълници, които се различават (по координати на върховете) между повърхнината и източника.</summary>
    private static int CompareTriangles(Surface s, List<Point3> pts, List<(int A, int B, int C)> faces)
    {
        static (long, long) K(Point2 p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4));
        static string T((long, long) a, (long, long) b, (long, long) c)
        {
            var arr = new[] { a, b, c };
            Array.Sort(arr);
            return string.Join(";", arr);
        }

        var source = new HashSet<string>(faces.Select(f => T(K(pts[f.A].XY), K(pts[f.B].XY), K(pts[f.C].XY))));
        var v = s.Vertices;
        var t = s.Triangles;
        var result = new HashSet<string>();
        for (int k = 0; k < t.Length; k += 3) result.Add(T(K(v[t[k]].XY), K(v[t[k + 1]].XY), K(v[t[k + 2]].XY)));
        return source.Except(result).Count() + result.Except(source).Count();
    }

    private static XElement? Child(XElement? e, string localName) =>
        e?.Elements().FirstOrDefault(c => c.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement? e, string localName) =>
        e?.Elements().Where(c => c.Name.LocalName == localName) ?? Enumerable.Empty<XElement>();

    private static List<double> Numbers(string text)
    {
        var list = new List<double>(3);
        foreach (var tok in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(tok, NumberStyles.Float, Ci, out var v)) list.Add(v);
        return list;
    }
}
