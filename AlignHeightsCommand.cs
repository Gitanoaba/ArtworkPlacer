using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtworkPlacer
{
    // ─── ALINEAR ALTURAS DE ARTWORK YA COLOCADO ──────────────────────────────
    // Sube o baja cada pieza (sin moverla a lo largo del muro) para que su centro, borde inferior
    // o superior quede a la misma altura sobre el nivel de su muro.
    [Transaction(TransactionMode.Manual)]
    public class AlignHeightsCommand : IExternalCommand
    {
        private const string ToolName = "Align Artwork Heights";
        private const string Marker = "ArtworkPlacer";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null || uidoc.Document.IsFamilyDocument)
            {
                TaskDialog.Show(ToolName, "Open a project (not a family) to use this tool.");
                return Result.Cancelled;
            }
            Document doc = uidoc.Document;

            // Piezas seleccionadas; si no hay, todo el artwork visible en la vista activa
            List<FamilyInstance> pieces = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id)).OfType<FamilyInstance>().ToList();
            string source = "selected";
            if (pieces.Count == 0)
            {
                // Las muestras de las secciones Artwork_* son la biblioteca del template: no se tocan
                HashSet<ElementId> library = LibraryPieces(doc);
                pieces = new FilteredElementCollector(doc, doc.ActiveView.Id)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(fi => IsArtwork(fi) && !library.Contains(fi.Id))
                    .ToList();
                source = "in this view (nothing was selected; the Artwork_* section samples are left alone)";
            }
            if (pieces.Count == 0)
            {
                TaskDialog.Show(ToolName, "No artwork found. Select the pieces to align, or open a view where they are visible.");
                return Result.Cancelled;
            }

            Units units = doc.GetUnits();
            Func<string, double?> parse = s => UnitFormatUtils.TryParse(units, SpecTypeId.Length, s, out double v) ? v : (double?)null;
            string Fmt(double ft) => UnitFormatUtils.Format(units, SpecTypeId.Length, ft, false);
            PlaceArtworkCommand.InitDefaults(Fmt);

            var dlg = new AlignDialog(pieces.Count, source, PlaceArtworkCommand.Settings, parse);
            if (dlg.ShowDialog() != true) return Result.Cancelled;

            int moved = 0, alreadyOk = 0;
            var skipped = new List<string>();

            using (var t = new Transaction(doc, "Align artwork heights"))
            {
                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new PlaceArtworkCommand.WarningSwallower());
                t.SetFailureHandlingOptions(fho);
                t.Start();

                foreach (FamilyInstance fi in pieces)
                {
                    string label = $"{fi.Symbol.FamilyName} : {fi.Symbol.Name} ({fi.Id.Value})";
                    try
                    {
                        Level level = ArtGeometry.ReferenceLevel(doc, fi);
                        if (level == null) { skipped.Add($"{label}: no level"); continue; }
                        if (!ArtGeometry.ZRange(fi, out double z0, out double z1)) { skipped.Add($"{label}: no geometry"); continue; }

                        double delta = ArtGeometry.AlignDelta(dlg.Align, level.ProjectElevation + dlg.ArtHeight, z0, z1);
                        if (Math.Abs(delta) < 1e-4) { alreadyOk++; continue; }

                        ElementTransformUtils.MoveElement(doc, fi.Id, XYZ.BasisZ * delta);
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        skipped.Add($"{label}: {ex.Message}");
                    }
                }

                t.Commit();
            }

            var report = new TaskDialog(ToolName)
            {
                MainInstruction = $"Moved {moved} piece(s); {alreadyOk} were already at the right height.",
                MainContent = skipped.Count == 0 ? "" : $"{skipped.Count} piece(s) could not be aligned (see details).",
                ExpandedContent = string.Join("\n", skipped)
            };
            report.Show();
            return Result.Succeeded;
        }

        private static HashSet<ElementId> LibraryPieces(Document doc)
        {
            var ids = new HashSet<ElementId>();
            var sections = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection)).Cast<ViewSection>()
                .Where(v => !v.IsTemplate && PlaceArtworkCommand.LooksLikeArtwork(v.Name));
            foreach (ViewSection view in sections)
                foreach (ElementId id in new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance)).ToElementIds())
                    ids.Add(id);
            return ids;
        }

        // Artwork = colocado por la herramienta, o de una familia que se llama "artwork"
        private static bool IsArtwork(FamilyInstance fi) =>
            fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == Marker ||
            PlaceArtworkCommand.LooksLikeArtwork(fi.Symbol.FamilyName);
    }
}
