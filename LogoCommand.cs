using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace ArtworkPlacer;

[Transaction(TransactionMode.Manual)]
public class LogoCommand : IExternalCommand
{
	private const string ToolName = "Place Logo";

	private static string _widthText;

	private static string _heightText;

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIApplication uiapp = commandData.Application;
		UIDocument uidoc = uiapp.ActiveUIDocument;
		if (uidoc == null || uidoc.Document.IsFamilyDocument)
		{
			TaskDialog.Show(ToolName, "Open a project (not a family) to use Place Logo.");
			return Result.Cancelled;
		}
		Document doc = uidoc.Document;
		Units units = doc.GetUnits();
		Func<string, double?> parse = (string s) => UnitFormatUtils.TryParse(units, SpecTypeId.Length, s ?? "", out var v) ? new double?(v) : null;
		string Fmt(double feet) => UnitFormatUtils.Format(units, SpecTypeId.Length, feet, forEditing: false);
		string Cm(double cm) => Fmt(UnitUtils.ConvertToInternalUnits(cm, UnitTypeId.Centimeters));
		_widthText = _widthText ?? Cm(120.0);
		_heightText = _heightText ?? Cm(150.0);

		List<Wall> walls = uidoc.Selection.GetElementIds().Select((ElementId id) => doc.GetElement(id)).OfType<Wall>().ToList();
		List<Room> receptions = (from Room r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
			where r.Location != null && r.Area > 0.0 && LogoPlacer.IsReception(r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString())
			orderby r.Area descending
			select r).ToList();
		string receptionNames = (receptions.Count == 0) ? null : string.Join(", ", receptions.Select((Room r) => r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()).Distinct().Take(3));
		// Si Room Studio cargó el JSON del diseño, el muro del logo es el que se ve desde la entrada principal
		LogoTarget target = DesignMemory.LogoWalls.TryGetValue(StudioCommand.DocKey(doc), out var t0) && doc.GetElement(new ElementId(t0.WallId)) is Wall ? t0 : null;
		if (target != null)
		{
			receptionNames = "the wall facing the main entrance, from Room Studio";
		}

		LogoWindow window = new LogoWindow(LogoPlacer.FindLogo(uiapp, doc), receptionNames, walls.Count, _widthText, _heightText, parse);
		if (window.ShowDialog() != true)
		{
			return Result.Cancelled;
		}
		_widthText = Fmt(window.LogoWidth);
		_heightText = Fmt(window.CenterHeight);
		double width = window.LogoWidth;
		double height = width * Aspect(window.LogoPath);

		// 1. El muro: la recepción (el tramo libre más largo) o los muros seleccionados
		Dictionary<ElementId, List<int>> choice = new Dictionary<ElementId, List<int>>();
		if (window.UseReception)
		{
			LogoPlacer finder = new LogoPlacer(doc, UnitUtils.ConvertToInternalUnits(30.0, UnitTypeId.Centimeters));
			LogoSpot spot = (target == null) ? null : finder.SpotOn((Wall)doc.GetElement(new ElementId(target.WallId)), target.Sign, target.U0, target.U1, window.CenterHeight, width, height, out _);
			spot = spot ?? finder.ReceptionSpot(receptions, window.CenterHeight, width, height);
			if (spot == null)
			{
				TaskDialog.Show(ToolName, "No reception or lobby wall has room for a logo " + Fmt(width) + " wide (walls with windows don't count). Try a smaller width, or select the walls yourself and run Place Logo again.");
				return Result.Cancelled;
			}
			walls = new List<Wall> { spot.Wall };
			choice[spot.Wall.Id] = new List<int> { (spot.Normal.DotProduct(spot.Wall.Orientation) > 0.0) ? 1 : (-1) };
		}

		// 2. La familia y el material con la imagen del cliente
		FamilySymbol symbol;
		try
		{
			symbol = LogoFamily.Build(doc, uiapp.Application, width, height);
			if (symbol == null)
			{
				throw new InvalidOperationException("the logo family didn't load");
			}
			using Transaction t = new Transaction(doc, "Place Logo: logo material");
			t.Start();
			LogoFamily.Paint(doc, uiapp.Application, symbol, window.LogoPath, width, height);
			if (!symbol.IsActive)
			{
				symbol.Activate();
			}
			LogoPlacer.FindGuides(doc).ForEach((LogoGuide g) => doc.Delete(g.Curves));
			t.Commit();
		}
		catch (Exception ex)
		{
			TaskDialog.Show(ToolName, "Couldn't build the logo: " + ex.Message);
			return Result.Failed;
		}

		// Recepción con un logo ya colocado: recargar la familia y el material ya lo actualizó
		List<ElementId> existing = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
			.Where((FamilyInstance fi) => fi.Symbol.Family.Id == symbol.Family.Id).Select((FamilyInstance fi) => fi.Id).ToList();
		if (window.UseReception && existing.Count > 0)
		{
			uidoc.Selection.SetElementIds(existing);
			TaskDialog.Show(ToolName, $"Updated the logo already in the model ({Fmt(width)} × {Fmt(height)}). It's selected so you can check it. To add it on other walls, select them and run Place Logo again.");
			return Result.Succeeded;
		}

		// 3. Colocar como el artwork: una pieza por cara, centrada, con vista previa de la cara
		WallPlacer placer = new WallPlacer(doc, new List<FamilySymbol> { symbol }, window.CenterHeight, HeightAlign.Center, UnitUtils.ConvertToInternalUnits(30.0, UnitTypeId.Centimeters), 0.0, 1, SideMode.Auto, Fmt);
		List<string> skipped = new List<string>();
		foreach (Wall wall in walls.Where((Wall w) => !choice.ContainsKey(w.Id)).ToList())
		{
			List<int> signs = placer.InitialSides(wall, out var error);
			if (signs == null)
			{
				skipped.Add(PlaceArtworkCommand.WallLabel(wall) + ": " + error);
			}
			else
			{
				choice[wall.Id] = signs;
			}
		}
		walls = walls.Where((Wall w) => choice.ContainsKey(w.Id)).ToList();
		if (walls.Count == 0)
		{
			TaskDialog.Show(ToolName, "None of the selected walls can take the logo:\n\n" + string.Join("\n", skipped));
			return Result.Cancelled;
		}
		if (!new SidePreview(uidoc, placer, walls, choice, ToolName).Run())
		{
			return Result.Cancelled;
		}
		List<ElementId> placed = new List<ElementId>();
		using (Transaction t = new Transaction(doc, "Place Logo"))
		{
			PlaceArtworkCommand.SwallowWarnings(t);
			t.Start();
			foreach (Wall wall in walls)
			{
				using SubTransaction sub = new SubTransaction(doc);
				sub.Start();
				try
				{
					placed.AddRange(placer.PlaceOnWall(wall, choice[wall.Id], skipped));
					sub.Commit();
				}
				catch (Exception ex)
				{
					sub.RollBack();
					skipped.Add(PlaceArtworkCommand.WallLabel(wall) + ": " + ex.Message);
				}
			}
			if (t.Commit() != TransactionStatus.Committed)
			{
				TaskDialog.Show(ToolName, "Revit didn't accept the changes, so the logo wasn't placed.");
				return Result.Failed;
			}
		}
		if (placed.Count > 0)
		{
			uidoc.Selection.SetElementIds(placed);
		}
		TaskDialog report = new TaskDialog(ToolName)
		{
			MainInstruction = (placed.Count > 0) ? $"Placed the logo ({Fmt(width)} × {Fmt(height)})." : "The logo wasn't placed.",
			MainContent = (placed.Count > 0) ? "It's selected so you can check it. It's the \"Qbiq Logo\" family, with the image in the \"Qbiq Logo\" material." : "See the details for why.",
			ExpandedContent = string.Join("\n", skipped)
		};
		report.Show();
		return Result.Succeeded;
	}

	// Alto / ancho de la imagen, para que el logo tenga su proporción
	private static double Aspect(string path)
	{
		try
		{
			using System.Drawing.Image img = System.Drawing.Image.FromFile(path);
			return (img.Width > 0) ? ((double)img.Height / img.Width) : 1.0;
		}
		catch
		{
			return 1.0;
		}
	}
}
