using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtworkPlacer
{
    // ─── COMANDO: TVs EN SALAS DE REUNIÓN ────────────────────────────────────
    [Transaction(TransactionMode.Manual)]
    public class PlaceTvCommand : IExternalCommand
    {
        private const string ToolName = "Place TV";

        // Se recuerdan entre corridas dentro de la misma sesión de Revit
        private static readonly TvSettings _settings = new TvSettings();

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null || uidoc.Document.IsFamilyDocument)
            {
                TaskDialog.Show(ToolName, "Open a project (not a family) to use this tool.");
                return Result.Cancelled;
            }
            Document doc = uidoc.Document;

            List<Wall> walls = PlaceArtworkCommand.GetWalls(uidoc,
                "Select the walls of the meeting rooms (or one wall per TV), then click Finish");
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
            if (_settings.BottomText == null) _settings.BottomText = Fmt(UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Centimeters));
            if (_settings.ClearText == null) _settings.ClearText = Fmt(UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Centimeters));

            Func<FamilySymbol, bool> isDefault = s => LooksLikeTv(s.FamilyName);
            Func<string[], Tuple<List<TypeItem>, HashSet<long>>> loadRfa = paths =>
            {
                HashSet<long> newIds = PlaceArtworkCommand.LoadFamilies(doc, paths, "Load TV families");
                return Tuple.Create(PlaceArtworkCommand.CollectTypes(doc, isDefault, _settings.LastChecked), newIds);
            };

            var dlg = new TvDialog(walls.Count, PlaceArtworkCommand.CollectTypes(doc, isDefault, _settings.LastChecked), _settings, parse, loadRfa);
            if (dlg.ShowDialog() != true) return Result.Cancelled;

            List<FamilySymbol> symbols = dlg.SelectedIds
                .Select(id => doc.GetElement(new ElementId(id)) as FamilySymbol)
                .Where(s => s != null)
                .ToList();

            var placer = new TvPlacer(doc, symbols, dlg.Clearance, dlg.BottomHeight, _settings.AutoSize, dlg.DepthFactor, Fmt);
            var skipped = new List<string>();

            // Medir el ancho real de cada TV: se colocan y se borran en una transacción que se deshace
            using (var t = new Transaction(doc, "Measure TV types"))
            {
                PlaceArtworkCommand.SwallowWarnings(t);
                t.Start();
                foreach (FamilySymbol s in symbols)
                    if (!s.IsActive) s.Activate();
                doc.Regenerate();
                placer.MeasureTypes(walls);
                t.RollBack();
            }
            List<FamilySymbol> unmeasured = placer.Unmeasured;
            if (unmeasured.Count == symbols.Count)
            {
                TaskDialog.Show(ToolName, "None of the checked TV types could be placed on the selected walls.");
                return Result.Cancelled;
            }
            foreach (FamilySymbol s in unmeasured)
                skipped.Add($"{s.FamilyName} : {s.Name}: could not be placed on these walls, not used");

            // Salas cerradas por los muros seleccionados; los muros que no cierran nada van uno por uno
            List<FoundRoom> rooms = RoomFinder.Find(walls, out List<Wall> loose);
            List<Outline> doors = DoorBoxes(doc);
            List<TvTarget> targets = rooms
                .OrderByDescending(r => r.Area)
                .Select((r, i) => placer.RoomTarget(r, i + 1, doors))
                .Concat(loose.Select(placer.WallTarget))
                .ToList();

            if (targets.All(t => t.Chosen == null))
            {
                TaskDialog.Show(ToolName, "No TV can be placed on the selected walls:\n\n" +
                    string.Join("\n", targets.Select(t => $"{t.Label}: {t.Note}")));
                return Result.Cancelled;
            }

            if (_settings.Preview && !new TvPreview(uidoc, targets, ToolName).Run())
                return Result.Cancelled;

            var placedIds = new List<ElementId>();
            using (var t = new Transaction(doc, "Place TVs"))
            {
                PlaceArtworkCommand.SwallowWarnings(t);
                t.Start();

                foreach (FamilySymbol s in symbols)
                    if (!s.IsActive) s.Activate();
                doc.Regenerate();

                foreach (TvTarget target in targets)
                {
                    TvSpot spot = target.Chosen;
                    if (spot == null) { skipped.Add($"{target.Label}: {target.Note ?? "no TV"}"); continue; }

                    using (var st = new SubTransaction(doc))
                    {
                        st.Start();
                        try
                        {
                            FamilyInstance fi = placer.Place(spot, out string reason);
                            if (fi == null) skipped.Add($"{target.Label}: {reason}");
                            else placedIds.Add(fi.Id);
                            st.Commit();
                        }
                        catch (Exception ex)
                        {
                            st.RollBack();
                            skipped.Add($"{target.Label}: error – {ex.Message}");
                        }
                    }
                }

                t.Commit();
            }

            if (placedIds.Count > 0)
                uidoc.Selection.SetElementIds(placedIds);

            var report = new TaskDialog(ToolName)
            {
                MainInstruction = $"Placed {placedIds.Count} TV(s): {rooms.Count} room(s) found, {loose.Count} single wall(s).",
                MainContent = skipped.Count == 0
                    ? "The new TVs are selected so you can review them."
                    : $"{skipped.Count} item(s) skipped (see details). The new TVs are selected.",
                ExpandedContent = string.Join("\n", skipped)
            };
            report.Show();

            return Result.Succeeded;
        }

        private static bool LooksLikeTv(string familyName)
        {
            string n = familyName.ToLowerInvariant();
            return n.Contains("tv") || n.Contains("television");
        }

        // Cajas de todas las puertas (también las de muros cortina), para saber dónde está la entrada de cada sala
        private static List<Outline> DoorBoxes(Document doc) =>
            new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .Select(e => e.get_BoundingBox(null))
                .Where(bb => bb != null)
                .Select(bb => new Outline(bb.Min, bb.Max))
                .ToList();
    }
}
