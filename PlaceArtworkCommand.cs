using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
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

            List<Wall> walls = GetWalls(uidoc, "Select the walls to place artwork on, then click Finish");
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

            Func<FamilySymbol, bool> isDefault = s => LooksLikeArtwork(s.FamilyName);
            Func<string[], Tuple<List<TypeItem>, HashSet<long>>> loadRfa = paths =>
            {
                HashSet<long> newIds = LoadFamilies(doc, paths, "Load artwork families");
                return Tuple.Create(CollectTypes(doc, isDefault, _settings.LastChecked), newIds);
            };

            List<TypeItem> types = CollectTypes(doc, isDefault, _settings.LastChecked);
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
                SwallowWarnings(t);
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
        internal static List<Wall> GetWalls(UIDocument uidoc, string prompt)
        {
            Document doc = uidoc.Document;
            var pre = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id)).OfType<Wall>().ToList();
            if (pre.Count > 0) return pre;

            try
            {
                var refs = uidoc.Selection.PickObjects(ObjectType.Element, new WallFilter(), prompt);
                return refs.Select(r => doc.GetElement(r)).OfType<Wall>()
                    .GroupBy(w => w.Id).Select(g => g.First()).ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        // Carga .rfa (o encuentra la familia si ya estaba cargada) y devuelve los ids de sus tipos
        internal static HashSet<long> LoadFamilies(Document doc, string[] paths, string transactionName)
        {
            var newIds = new HashSet<long>();
            using (var t = new Transaction(doc, transactionName))
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
            return newIds;
        }

        // Todos los tipos que se pueden colocar contra un muro (face-based, hospedados o sueltos).
        // Vienen marcados los de la última corrida o, la primera vez, los que cumplen isDefault.
        internal static List<TypeItem> CollectTypes(Document doc, Func<FamilySymbol, bool> isDefault, HashSet<long> lastChecked)
        {
            var excluded = new HashSet<long> { (long)BuiltInCategory.OST_Doors, (long)BuiltInCategory.OST_Windows };

            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Category != null && !excluded.Contains(s.Category.Id.Value))
                .Where(s => KindOf(s) != null)
                .Select(s => new TypeItem
                {
                    Id = s.Id.Value,
                    Label = $"{s.FamilyName} : {s.Name}",
                    Kind = $"{s.Category.Name}, {KindOf(s)}",
                    IsChecked = lastChecked.Count > 0 ? lastChecked.Contains(s.Id.Value) : isDefault(s)
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

        internal static void SwallowWarnings(Transaction t)
        {
            FailureHandlingOptions fho = t.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(new WarningSwallower());
            t.SetFailureHandlingOptions(fho);
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

    // ─── LÓGICA DE COLOCACIÓN DE ARTWORK ─────────────────────────────────────
    internal class WallPlacer : WallFaces
    {
        private const string Marker = "ArtworkPlacer";   // se escribe en Comments de cada pieza colocada

        private readonly double? _centerHeight;
        private readonly double _gap;
        private readonly int _maxPerFace;
        private readonly SideMode _sideMode;
        private readonly Func<double, string> _fmt;
        private int _typeIndex;

        public WallPlacer(Document doc, List<FamilySymbol> symbols, double? centerHeight, double clearance, double gap, int maxPerFace, SideMode sideMode, Func<double, string> fmt)
            : base(doc, symbols, clearance, Marker)
        {
            _centerHeight = centerHeight;
            _gap = gap;
            _maxPerFace = Math.Max(1, maxPerFace);
            _sideMode = sideMode;
            _fmt = fmt;
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

        private double RefHeight => _centerHeight ?? UnitUtils.ConvertToInternalUnits(150, UnitTypeId.Centimeters);

        private double Height(FamilySymbol sym) => _centerHeight ?? DefaultElevation(sym);

        // Caras elegidas según el modo del diálogo (+1 exterior, -1 interior)
        public List<int> InitialSides(Wall wall, out string error)
        {
            WallGeom g = Analyze(wall, RefHeight, out error);
            return g == null ? null : ChooseSides(g).Select(s => s.Sign).ToList();
        }

        public bool HasBothSides(Wall wall) => Analyze(wall, RefHeight, out _)?.Sides.Count == 2;

        // Un lugar para una pieza: centro sobre el muro y ancho máximo disponible
        private class Slot { public double Center, MaxWidth; }

        // Para cada cara: dónde iría cada pieza (una flecha por pieza en la vista previa)
        public List<Spot> Spots(Wall wall)
        {
            var spots = new List<Spot>();
            WallGeom g = Analyze(wall, RefHeight, out _);
            if (g == null) return spots;

            foreach (Side side in g.Sides.Values)
            {
                List<Slot> slots = side.HasExisting ? new List<Slot>() : Slots(wall, g, side);
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

            WallGeom g = Analyze(wall, RefHeight, out string error);
            if (g == null) { skipped.Add($"{label}: {error}"); return placed; }

            foreach (int sign in signs)
            {
                if (!g.Sides.TryGetValue(sign, out Side side)) continue;
                if (side.HasExisting) { skipped.Add($"{label}, {side.Name}: already has artwork"); continue; }

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
            List<Tuple<double, double>> segments = FreeSegments(0, g.Length, side.Blocked);
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

        // Mide cada tipo una sola vez y devuelve el ancho mayor
        private double WidestType(Wall wall, WallGeom g, Side side, double center)
        {
            foreach (FamilySymbol sym in _symbols)
                if (!_widths.ContainsKey(sym.Id))
                    _widths[sym.Id] = Measure(sym, wall, g, side, center, Height(sym));
            return _symbols.Select(sy => _widths[sy.Id]).DefaultIfEmpty(-1).Max();
        }

        // Solo es true si ya medimos todos los tipos y ninguno cabe
        private bool NothingFits(double maxWidth) =>
            _symbols.All(sy => _widths.TryGetValue(sy.Id, out double w) && (w <= 0 || w > maxWidth + Eps));

        private IEnumerable<Side> ChooseSides(WallGeom g)
        {
            switch (_sideMode)
            {
                case SideMode.Exterior: return g.Sides.Where(kv => kv.Key > 0).Select(kv => kv.Value);
                case SideMode.Interior: return g.Sides.Where(kv => kv.Key < 0).Select(kv => kv.Value);
                case SideMode.Both: return g.Sides.Values;
            }

            // Auto: la cara que mira hacia un room; si hay rooms en ambos lados, el más grande
            Side best = RoomSide(g);
            if (best == null) g.Sides.TryGetValue(-1, out best);   // sin rooms: cara interior
            if (best == null) best = g.Sides.Values.First();
            return new[] { best };
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

                FamilyInstance fi = Create(sym, wall, level, side, PointOnFace(level, side, center, Height(sym)), faceN);
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

                Mark(fi);

                _typeIndex = (_typeIndex + k + 1) % _symbols.Count;
                reason = null;
                return fi;
            }

            reason = widest > 0
                ? $"free space ({_fmt(maxWidth)}) is narrower than the artwork ({_fmt(widest)})"
                : "none of the types could be placed on this face";
            return null;
        }
    }
}
