using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArtworkPlacer
{
    // ─── COMANDO PRINCIPAL ───────────────────────────────────────────────────
    [Transaction(TransactionMode.Manual)]
    public class PlaceArtworkCommand : IExternalCommand
    {
        private const string ToolName = "Place Artwork";

        // Se recuerdan entre corridas dentro de la misma sesión de Revit
        private static readonly DialogSettings _settings = new DialogSettings();

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null || uidoc.Document.IsFamilyDocument)
            {
                TaskDialog.Show(ToolName, "Open a project (not a family) to use this tool.");
                return Result.Cancelled;
            }
            Document doc = uidoc.Document;

            List<Wall> walls = GetWalls(uidoc);
            if (walls == null) return Result.Cancelled;
            if (walls.Count == 0)
            {
                TaskDialog.Show(ToolName, "No walls were selected.");
                return Result.Cancelled;
            }

            // Longitudes en las unidades del proyecto (cm, m, pies…)
            Units units = doc.GetUnits();
            Func<string, double?> parse = s => UnitFormatUtils.TryParse(units, SpecTypeId.Length, s, out double v) ? v : (double?)null;
            string Fmt(double ft) => UnitFormatUtils.Format(units, SpecTypeId.Length, ft, false);
            if (_settings.HeightText == null) _settings.HeightText = Fmt(UnitUtils.ConvertToInternalUnits(150, UnitTypeId.Centimeters));
            if (_settings.ClearText == null) _settings.ClearText = Fmt(UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Centimeters));
            if (_settings.GapText == null) _settings.GapText = Fmt(UnitUtils.ConvertToInternalUnits(60, UnitTypeId.Centimeters));

            Func<string[], Tuple<List<TypeItem>, HashSet<long>>> loadRfa = paths =>
            {
                var newIds = new HashSet<long>();
                using (var t = new Transaction(doc, "Load artwork families"))
                {
                    t.Start();
                    foreach (string path in paths)
                    {
                        Family fam;
                        if (!doc.LoadFamily(path, out fam))
                        {
                            // Ya estaba cargada: marcar sus tipos igual
                            string name = Path.GetFileNameWithoutExtension(path);
                            fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                                .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        }
                        if (fam != null)
                            foreach (ElementId id in fam.GetFamilySymbolIds()) newIds.Add(id.Value);
                    }
                    t.Commit();
                }
                return Tuple.Create(CollectTypes(doc), newIds);
            };

            List<TypeItem> types = CollectTypes(doc);
            var dlg = new ArtworkDialog(walls.Count, types, CollectCollections(doc, types), _settings, parse, loadRfa);
            if (dlg.ShowDialog() != true) return Result.Cancelled;

            List<FamilySymbol> symbols = dlg.SelectedIds
                .Select(id => doc.GetElement(new ElementId(id)) as FamilySymbol)
                .Where(s => s != null)
                .ToList();

            var placer = new WallPlacer(doc, symbols, dlg.CenterHeight, dlg.Clearance, dlg.Gap, dlg.MaxPerFace, _settings.Side, Fmt);
            var placedIds = new List<ElementId>();
            var skipped = new List<string>();

            // Cara inicial de cada muro (luego se puede cambiar muro por muro en la vista previa)
            var choice = new Dictionary<ElementId, List<int>>();
            foreach (Wall wall in walls)
            {
                List<int> signs = placer.InitialSides(wall, out string error);
                if (signs == null) skipped.Add($"{WallLabel(wall)}: {error}");
                else choice[wall.Id] = signs;
            }
            walls = walls.Where(w => choice.ContainsKey(w.Id)).ToList();
            if (walls.Count == 0)
            {
                TaskDialog.Show(ToolName, "None of the selected walls can be used:\n\n" + string.Join("\n", skipped));
                return Result.Cancelled;
            }

            if (_settings.Preview && !new SidePreview(uidoc, placer, walls, choice, ToolName).Run())
                return Result.Cancelled;

            using (var t = new Transaction(doc, "Place artwork"))
            {
                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(fho);
                t.Start();

                foreach (FamilySymbol s in symbols)
                    if (!s.IsActive) s.Activate();
                doc.Regenerate();

                foreach (Wall wall in walls)
                {
                    using (var st = new SubTransaction(doc))
                    {
                        st.Start();
                        try
                        {
                            placedIds.AddRange(placer.PlaceOnWall(wall, choice[wall.Id], skipped));
                            st.Commit();
                        }
                        catch (Exception ex)
                        {
                            st.RollBack();
                            skipped.Add($"{WallLabel(wall)}: error – {ex.Message}");
                        }
                    }
                }

                t.Commit();
            }

            if (placedIds.Count > 0)
                uidoc.Selection.SetElementIds(placedIds);

            var report = new TaskDialog(ToolName)
            {
                MainInstruction = $"Placed {placedIds.Count} artwork piece(s) on {walls.Count} wall(s).",
                MainContent = skipped.Count == 0
                    ? "The new pieces are selected so you can review them."
                    : $"{skipped.Count} wall face(s) were skipped (see details). The new pieces are selected.",
                ExpandedContent = string.Join("\n", skipped)
            };
            report.Show();

            return Result.Succeeded;
        }

        internal static string WallLabel(Wall w) => $"Wall {w.Id.Value} ({w.Name})";

        // Muros preseleccionados, o pedir selección
        private static List<Wall> GetWalls(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            var pre = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id)).OfType<Wall>().ToList();
            if (pre.Count > 0) return pre;

            try
            {
                var refs = uidoc.Selection.PickObjects(ObjectType.Element, new WallFilter(),
                    "Select the walls to place artwork on, then click Finish");
                return refs.Select(r => doc.GetElement(r)).OfType<Wall>()
                    .GroupBy(w => w.Id).Select(g => g.First()).ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        // Todos los tipos que se pueden colocar contra un muro (face-based, hospedados o sueltos)
        private static List<TypeItem> CollectTypes(Document doc)
        {
            var excluded = new HashSet<long> { (long)BuiltInCategory.OST_Doors, (long)BuiltInCategory.OST_Windows };

            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Category != null && !excluded.Contains(s.Category.Id.Value))
                .Where(s => KindOf(s) != null)
                .Select(s =>
                {
                    string label = $"{s.FamilyName} : {s.Name}";
                    return new TypeItem
                    {
                        Id = s.Id.Value,
                        Label = label,
                        Kind = $"{s.Category.Name}, {KindOf(s)}",
                        IsChecked = _settings.LastChecked.Count > 0
                            ? _settings.LastChecked.Contains(s.Id.Value)
                            : LooksLikeArtwork(s.FamilyName)
                    };
                })
                .OrderBy(i => i.Label)
                .ToList();
        }

        // Secciones "Artwork_*" del template: cada una es una colección de tipos, ordenados de izquierda a derecha
        private static List<ArtCollection> CollectCollections(Document doc, List<TypeItem> types)
        {
            var placeable = new HashSet<long>(types.Select(t => t.Id));
            var result = new List<ArtCollection>();

            var sections = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection)).Cast<ViewSection>()
                .Where(v => !v.IsTemplate && LooksLikeArtwork(v.Name))
                .OrderBy(v => v.Name);

            foreach (ViewSection view in sections)
            {
                XYZ right = view.RightDirection;
                var ids = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(fi => placeable.Contains(fi.Symbol.Id.Value))
                    .Select(fi => new { fi.Symbol.Id, X = PositionAlong(fi, view, right) })
                    .OrderBy(x => x.X)
                    .Select(x => x.Id.Value)
                    .Distinct()
                    .ToList();
                if (ids.Count > 0) result.Add(new ArtCollection { Name = view.Name, Ids = ids });
            }
            return result;
        }

        private static double PositionAlong(Element e, View view, XYZ right)
        {
            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            return bb == null ? 0 : ((bb.Min + bb.Max) / 2).DotProduct(right);
        }

        private static bool LooksLikeArtwork(string familyName)
        {
            string n = familyName.Replace(" ", "").Replace("_", "").ToLowerInvariant();
            return n.Contains("artwork");
        }

        internal static string KindOf(FamilySymbol s)
        {
            switch (s.Family.FamilyPlacementType)
            {
                case FamilyPlacementType.WorkPlaneBased: return "face-based";
                case FamilyPlacementType.OneLevelBasedHosted: return "wall-hosted";
                case FamilyPlacementType.OneLevelBased: return "unhosted";
                default: return null;
            }
        }

        private class WallFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Wall;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        // Ocultar advertencias (p.ej. instancias duplicadas) para no frenar la corrida
        private class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (FailureMessageAccessor f in a.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }
    }

    // ─── LÓGICA DE COLOCACIÓN ────────────────────────────────────────────────
    internal class WallPlacer
    {
        private const double Eps = 0.01;               // pies
        private const string Marker = "ArtworkPlacer";   // se escribe en Comments de cada pieza colocada

        private readonly Document _doc;
        private readonly List<FamilySymbol> _symbols;
        private readonly HashSet<ElementId> _artFamilyIds;
        private readonly double? _centerHeight;
        private readonly double _clearance;
        private readonly double _gap;
        private readonly int _maxPerFace;
        private readonly SideMode _sideMode;
        private readonly Func<double, string> _fmt;
        private readonly Phase _phase;
        private readonly Dictionary<ElementId, List<FamilyInstance>> _hostedByWall;
        private readonly Dictionary<ElementId, double> _widths = new Dictionary<ElementId, double>();   // ancho real de cada tipo (-1 = no se pudo colocar)
        private int _typeIndex;

        public WallPlacer(Document doc, List<FamilySymbol> symbols, double? centerHeight, double clearance, double gap, int maxPerFace, SideMode sideMode, Func<double, string> fmt)
        {
            _doc = doc;
            _symbols = symbols;
            _artFamilyIds = new HashSet<ElementId>(symbols.Select(s => s.Family.Id));
            _centerHeight = centerHeight;
            _clearance = clearance;
            _gap = gap;
            _maxPerFace = Math.Max(1, maxPerFace);
            _sideMode = sideMode;
            _fmt = fmt;

            ElementId phaseId = doc.ActiveView?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
            _phase = phaseId != null ? doc.GetElement(phaseId) as Phase : null;

            _hostedByWall = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(fi => fi.Host is Wall)
                .GroupBy(fi => fi.Host.Id)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        // Geometría de una cara del muro: +1 = exterior, -1 = interior
        private class Side
        {
            public int Sign;
            public double V;                                   // posición de la cara sobre la normal
            public List<Reference> Refs = new List<Reference>();
            public List<PlanarFace> Faces = new List<PlanarFace>();
            public List<Tuple<double, double>> Blocked = new List<Tuple<double, double>>();
            public bool HasArt;
            public string Name => Sign > 0 ? "exterior face" : "interior face";
        }

        // Caja del elemento en coordenadas locales del muro: U a lo largo, V normal, Z vertical
        private class Box { public double U0, U1, V0, V1, Z0, Z1; }

        private XYZ _p0, _d, _n;

        // Resultado del análisis de un muro (deja _p0/_d/_n listos para LocalBox)
        private class WallGeom
        {
            public Level Level;
            public double Length;
            public double RefZ;                                // altura de referencia para obstáculos y flechas
            public Dictionary<int, Side> Sides = new Dictionary<int, Side>();
        }

        // Un punto de la vista previa: dónde quedaría la pieza en una cara
        public class Spot
        {
            public int Sign;
            public XYZ OnFace;          // a la altura RefZ
            public XYZ Normal;          // hacia afuera de esa cara
            public XYZ Along;           // dirección del muro
            public bool Blocked;        // sin tramo libre o ya tiene artwork
        }

        private WallGeom Analyze(Wall wall, out string error)
        {
            error = null;
            if (wall.WallType.Kind != WallKind.Basic) { error = "curtain/stacked walls are not supported"; return null; }
            if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line line)) { error = "curved walls are not supported"; return null; }
            if (!(_doc.GetElement(wall.LevelId) is Level level)) { error = "wall has no level"; return null; }

            _p0 = line.GetEndPoint(0);
            _d = line.Direction;
            _n = wall.Orientation;                 // apunta hacia la cara exterior

            var g = new WallGeom
            {
                Level = level,
                Length = line.Length,
                RefZ = level.ProjectElevation + (_centerHeight ?? UnitUtils.ConvertToInternalUnits(150, UnitTypeId.Centimeters))
            };
            AddSide(wall, ShellLayerType.Exterior, +1, g.Sides);
            AddSide(wall, ShellLayerType.Interior, -1, g.Sides);
            if (g.Sides.Count == 0) { error = "no planar faces found"; return null; }

            CollectObstacles(wall, g.Sides, g.RefZ);
            return g;
        }

        // Caras elegidas según el modo del diálogo (+1 exterior, -1 interior)
        public List<int> InitialSides(Wall wall, out string error)
        {
            WallGeom g = Analyze(wall, out error);
            return g == null ? null : ChooseSides(wall, g.Level, g.Length, g.Sides).Select(s => s.Sign).ToList();
        }

        public bool HasBothSides(Wall wall) => Analyze(wall, out _)?.Sides.Count == 2;

        // Un lugar para una pieza: centro sobre el muro y ancho máximo disponible
        private class Slot { public double Center, MaxWidth; }

        // Para cada cara: dónde iría cada pieza (una flecha por pieza en la vista previa)
        public List<Spot> Spots(Wall wall)
        {
            var spots = new List<Spot>();
            WallGeom g = Analyze(wall, out _);
            if (g == null) return spots;

            foreach (Side side in g.Sides.Values)
            {
                List<Slot> slots = side.HasArt ? new List<Slot>() : Slots(wall, g, side);
                if (slots.Count == 0) slots.Add(new Slot { Center = g.Length / 2, MaxWidth = 0 });
                foreach (Slot slot in slots)
                {
                    XYZ p = _p0 + _d * slot.Center + _n * side.V;
                    spots.Add(new Spot
                    {
                        Sign = side.Sign,
                        OnFace = new XYZ(p.X, p.Y, g.RefZ),
                        Normal = _n * side.Sign,
                        Along = _d,
                        Blocked = slot.MaxWidth <= 0 || NothingFits(slot.MaxWidth)
                    });
                }
            }
            return spots;
        }

        public List<ElementId> PlaceOnWall(Wall wall, List<int> signs, List<string> skipped)
        {
            var placed = new List<ElementId>();
            string label = PlaceArtworkCommand.WallLabel(wall);

            WallGeom g = Analyze(wall, out string error);
            if (g == null) { skipped.Add($"{label}: {error}"); return placed; }

            foreach (int sign in signs)
            {
                if (!g.Sides.TryGetValue(sign, out Side side)) continue;
                if (side.HasArt) { skipped.Add($"{label}, {side.Name}: already has artwork"); continue; }

                List<Slot> slots = Slots(wall, g, side);
                if (slots.Count == 0) { skipped.Add($"{label}, {side.Name}: no free wall segment"); continue; }

                foreach (Slot slot in slots)
                {
                    FamilyInstance fi = PlaceAt(wall, g.Level, side, slot.Center, slot.MaxWidth, out string reason);
                    if (fi == null) skipped.Add($"{label}, {side.Name}: {reason}");
                    else placed.Add(fi.Id);
                }
            }
            return placed;
        }

        // Una pieza: centrada en el tramo libre más largo.
        // Varias: en cada tramo libre (de mayor a menor) caben n = largo / (ancho + separación) piezas,
        // repartidas parejo; se usa el ancho del tipo más ancho para que cualquier tipo de la rotación quepa.
        private List<Slot> Slots(Wall wall, WallGeom g, Side side)
        {
            var slots = new List<Slot>();
            List<Tuple<double, double>> segments = FreeSegments(g.Length, side.Blocked);
            if (segments.Count == 0) return slots;

            var longest = segments[0];
            var single = new Slot { Center = (longest.Item1 + longest.Item2) / 2, MaxWidth = longest.Item2 - longest.Item1 };
            if (_maxPerFace == 1) return new List<Slot> { single };

            double width = WidestType(wall, g, side, single.Center);
            if (width <= 0) return new List<Slot> { single };

            int remaining = _maxPerFace;
            foreach (var seg in segments)
            {
                double len = seg.Item2 - seg.Item1;
                int n = Math.Min(remaining, (int)Math.Floor((len + Eps) / (width + _gap)));
                if (n < 1 && width <= len + Eps) n = 1;   // cabe una, aunque sin la separación completa
                if (n < 1) continue;

                double pitch = len / n;
                for (int i = 0; i < n; i++)
                    slots.Add(new Slot { Center = seg.Item1 + pitch * (i + 0.5), MaxWidth = n == 1 ? len : pitch - _gap });
                remaining -= n;
                if (remaining == 0) break;
            }

            // Si no cupo nada, dejar el tramo más largo para que el reporte diga por qué
            if (slots.Count == 0) slots.Add(single);
            return slots.OrderBy(sl => sl.Center).ToList();
        }

        // Mide cada tipo una sola vez (lo coloca, lee su caja y lo borra) y devuelve el ancho mayor
        private double WidestType(Wall wall, WallGeom g, Side side, double center)
        {
            foreach (FamilySymbol sym in _symbols)
            {
                if (_widths.ContainsKey(sym.Id)) continue;
                double w = -1;
                try
                {
                    FamilyInstance fi = Create(sym, wall, g.Level, side, PointOnFace(g.Level, side, center, sym), _n * side.Sign);
                    if (fi != null)
                    {
                        _doc.Regenerate();
                        Box box = LocalBox(fi);
                        if (box != null) w = box.U1 - box.U0;
                        _doc.Delete(fi.Id);
                    }
                }
                catch { }
                _widths[sym.Id] = w;
            }
            return _symbols.Select(sy => _widths[sy.Id]).DefaultIfEmpty(-1).Max();
        }

        // Solo es true si ya medimos todos los tipos y ninguno cabe
        private bool NothingFits(double maxWidth) =>
            _symbols.All(sy => _widths.TryGetValue(sy.Id, out double w) && (w <= 0 || w > maxWidth + Eps));

        private XYZ PointOnFace(Level level, Side side, double u, FamilySymbol sym)
        {
            XYZ onFace = _p0 + _d * u + _n * side.V;
            return new XYZ(onFace.X, onFace.Y, level.ProjectElevation + (_centerHeight ?? DefaultElevation(sym)));
        }

        private void AddSide(Wall wall, ShellLayerType shell, int sign, Dictionary<int, Side> sides)
        {
            IList<Reference> refs;
            try { refs = HostObjectUtils.GetSideFaces(wall, shell); }
            catch { return; }

            var side = new Side { Sign = sign };
            foreach (Reference r in refs)
            {
                if (wall.GetGeometryObjectFromReference(r) is PlanarFace f)
                {
                    side.Refs.Add(r);
                    side.Faces.Add(f);
                }
            }
            if (side.Faces.Count == 0) return;
            side.V = (side.Faces[0].Origin - _p0).DotProduct(_n);
            sides[sign] = side;
        }

        // Puertas, ventanas, aberturas, cosas ya colgadas en el muro y muros que lo tocan
        private void CollectObstacles(Wall wall, Dictionary<int, Side> sides, double refZ)
        {
            var insertIds = new HashSet<ElementId>(wall.FindInserts(true, false, true, true));

            foreach (ElementId id in insertIds)
            {
                Box b = LocalBox(_doc.GetElement(id));
                if (b == null) continue;
                foreach (Side s in sides.Values) s.Blocked.Add(Tuple.Create(b.U0, b.U1));
            }

            if (_hostedByWall.TryGetValue(wall.Id, out var hosted))
            {
                foreach (FamilyInstance fi in hosted)
                {
                    if (insertIds.Contains(fi.Id)) continue;
                    Box b = LocalBox(fi);
                    if (b == null) continue;
                    foreach (Side s in AffectedSides(b, sides, true))
                    {
                        s.Blocked.Add(Tuple.Create(b.U0, b.U1));
                        if (_artFamilyIds.Contains(fi.Symbol.Family.Id) ||
                            fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == Marker)
                            s.HasArt = true;
                    }
                }
            }

            BoundingBoxXYZ wb = wall.get_BoundingBox(null);
            if (wb == null) return;
            var pad = new XYZ(0.1, 0.1, 0.1);
            var others = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(wb.Min - pad, wb.Max + pad)))
                .Where(e => e.Id != wall.Id && !insertIds.Contains(e.Id));

            foreach (Element other in others)
            {
                Box b = LocalBox(other);
                if (b == null || b.Z0 > refZ || b.Z1 < refZ) continue;
                foreach (Side s in AffectedSides(b, sides, false))
                    s.Blocked.Add(Tuple.Create(b.U0, b.U1));
            }
        }

        // Una caja bloquea una cara si sobresale de ella; si queda dentro del muro, opcionalmente bloquea ambas
        private static IEnumerable<Side> AffectedSides(Box b, Dictionary<int, Side> sides, bool insideBlocksBoth)
        {
            var hit = new List<Side>();
            if (sides.TryGetValue(+1, out Side ext) && b.V1 > ext.V + Eps) hit.Add(ext);
            if (sides.TryGetValue(-1, out Side inn) && b.V0 < inn.V - Eps) hit.Add(inn);
            if (hit.Count == 0 && insideBlocksBoth) hit.AddRange(sides.Values);
            return hit;
        }

        private IEnumerable<Side> ChooseSides(Wall wall, Level level, double length, Dictionary<int, Side> sides)
        {
            switch (_sideMode)
            {
                case SideMode.Exterior: return sides.Where(kv => kv.Key > 0).Select(kv => kv.Value);
                case SideMode.Interior: return sides.Where(kv => kv.Key < 0).Select(kv => kv.Value);
                case SideMode.Both: return sides.Values;
            }

            // Auto: la cara que mira hacia un room; si hay rooms en ambos lados, el más grande
            XYZ mid = _p0 + _d * (length / 2);
            double z = level.ProjectElevation + 3.0;
            Side best = null;
            double bestArea = -1;
            foreach (Side s in sides.Values)
            {
                XYZ probe = mid + _n * (s.V + s.Sign * 1.0);
                probe = new XYZ(probe.X, probe.Y, z);
                Room room = _phase != null ? _doc.GetRoomAtPoint(probe, _phase) : _doc.GetRoomAtPoint(probe);
                if (room != null && room.Area > bestArea) { best = s; bestArea = room.Area; }
            }
            if (best == null) sides.TryGetValue(-1, out best);   // sin rooms: cara interior
            if (best == null) best = sides.Values.First();
            return new[] { best };
        }

        // Tramos libres de la cara, del más largo al más corto
        private List<Tuple<double, double>> FreeSegments(double length, List<Tuple<double, double>> blocked)
        {
            var intervals = blocked
                .Select(b => Tuple.Create(Math.Max(0, b.Item1 - _clearance), Math.Min(length, b.Item2 + _clearance)))
                .Where(b => b.Item2 > b.Item1)
                .OrderBy(b => b.Item1);

            var free = new List<Tuple<double, double>>();
            double cursor = _clearance;
            foreach (var iv in intervals)
            {
                if (iv.Item1 - cursor > Eps) free.Add(Tuple.Create(cursor, iv.Item1));
                cursor = Math.Max(cursor, iv.Item2);
            }
            if (length - _clearance - cursor > Eps) free.Add(Tuple.Create(cursor, length - _clearance));
            return free.OrderByDescending(f => f.Item2 - f.Item1).ToList();
        }

        // Prueba los tipos en orden hasta que uno quepa en maxWidth; luego lo centra con su caja real
        private FamilyInstance PlaceAt(Wall wall, Level level, Side side, double center, double maxWidth, out string reason)
        {
            XYZ faceN = _n * side.Sign;
            double widest = 0;

            for (int k = 0; k < _symbols.Count; k++)
            {
                FamilySymbol sym = _symbols[(_typeIndex + k) % _symbols.Count];

                // Si ya sabemos que no cabe, no hace falta colocarlo para probar
                if (_widths.TryGetValue(sym.Id, out double known) && known > maxWidth + Eps)
                {
                    widest = Math.Max(widest, known);
                    continue;
                }

                FamilyInstance fi = Create(sym, wall, level, side, PointOnFace(level, side, center, sym), faceN);
                if (fi == null) continue;
                _doc.Regenerate();

                Box box = LocalBox(fi);
                if (box == null) { _doc.Delete(fi.Id); continue; }

                double width = box.U1 - box.U0;
                _widths[sym.Id] = width;
                if (width > maxWidth + Eps)
                {
                    widest = Math.Max(widest, width);
                    _doc.Delete(fi.Id);
                    continue;
                }

                XYZ move = _d * (center - (box.U0 + box.U1) / 2);
                if (_centerHeight.HasValue)
                    move += XYZ.BasisZ * (level.ProjectElevation + _centerHeight.Value - (box.Z0 + box.Z1) / 2);
                if (!move.IsZeroLength())
                    ElementTransformUtils.MoveElement(_doc, fi.Id, move);

                fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Marker);

                _typeIndex = (_typeIndex + k + 1) % _symbols.Count;
                reason = null;
                return fi;
            }

            reason = widest > 0
                ? $"free space ({_fmt(maxWidth)}) is narrower than the artwork ({_fmt(widest)})"
                : "none of the types could be placed on this face";
            return null;
        }

        private FamilyInstance Create(FamilySymbol sym, Wall wall, Level level, Side side, XYZ pt, XYZ faceN)
        {
            switch (sym.Family.FamilyPlacementType)
            {
                case FamilyPlacementType.WorkPlaneBased:
                {
                    // Elegir la cara que contiene el punto (un muro puede tener la cara partida)
                    Reference faceRef = side.Refs[0];
                    for (int i = 0; i < side.Faces.Count; i++)
                        if (side.Faces[i].Project(pt) != null) { faceRef = side.Refs[i]; break; }

                    // X de la familia horizontal, Y hacia arriba
                    XYZ refDir = XYZ.BasisZ.CrossProduct(faceN);
                    return _doc.Create.NewFamilyInstance(faceRef, pt, refDir, sym);
                }

                case FamilyPlacementType.OneLevelBasedHosted:
                {
                    FamilyInstance fi = _doc.Create.NewFamilyInstance(pt, sym, wall, level, StructuralType.NonStructural);
                    _doc.Regenerate();
                    if (fi.FacingOrientation.DotProduct(faceN) < 0 && fi.CanFlipFacing) fi.flipFacing();
                    return fi;
                }

                case FamilyPlacementType.OneLevelBased:
                {
                    XYZ basePt = new XYZ(pt.X, pt.Y, level.ProjectElevation);
                    FamilyInstance fi = _doc.Create.NewFamilyInstance(basePt, sym, level, StructuralType.NonStructural);
                    fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)?.Set(pt.Z - level.ProjectElevation);
                    _doc.Regenerate();
                    double angle = fi.FacingOrientation.AngleOnPlaneTo(faceN, XYZ.BasisZ);
                    if (Math.Abs(angle) > 1e-6)
                        ElementTransformUtils.RotateElement(_doc, fi.Id, Line.CreateBound(basePt, basePt + XYZ.BasisZ), angle);
                    return fi;
                }
            }
            return null;
        }

        // "Default Elevation" del tipo, si existe
        private static double DefaultElevation(FamilySymbol sym)
        {
            Parameter p = sym.LookupParameter("Default Elevation");
            if (p != null && p.StorageType == StorageType.Double) return p.AsDouble();
            return UnitUtils.ConvertToInternalUnits(150, UnitTypeId.Centimeters);
        }

        private Box LocalBox(Element e)
        {
            BoundingBoxXYZ bb = e?.get_BoundingBox(null);
            if (bb == null) return null;

            var box = new Box
            {
                U0 = double.MaxValue, U1 = double.MinValue,
                V0 = double.MaxValue, V1 = double.MinValue,
                Z0 = double.MaxValue, Z1 = double.MinValue
            };
            for (int i = 0; i < 8; i++)
            {
                XYZ c = new XYZ(
                    (i & 1) == 0 ? bb.Min.X : bb.Max.X,
                    (i & 2) == 0 ? bb.Min.Y : bb.Max.Y,
                    (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                c = bb.Transform.OfPoint(c);
                XYZ q = c - _p0;
                double u = q.DotProduct(_d), v = q.DotProduct(_n);
                box.U0 = Math.Min(box.U0, u); box.U1 = Math.Max(box.U1, u);
                box.V0 = Math.Min(box.V0, v); box.V1 = Math.Max(box.V1, v);
                box.Z0 = Math.Min(box.Z0, c.Z); box.Z1 = Math.Max(box.Z1, c.Z);
            }
            return box;
        }
    }
}
