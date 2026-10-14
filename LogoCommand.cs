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
		string Cm(double cm) => UnitFormatUtils.Format(units, SpecTypeId.Length, UnitUtils.ConvertToInternalUnits(cm, UnitTypeId.Centimeters), forEditing: false);
		_widthText = _widthText ?? Cm(120.0);
		_heightText = _heightText ?? Cm(150.0);

		List<Wall> walls = uidoc.Selection.GetElementIds().Select((ElementId id) => doc.GetElement(id)).OfType<Wall>().ToList();
		List<Room> receptions = (from Room r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
			where r.Location != null && r.Area > 0.0 && LogoPlacer.IsReception(r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString())
			orderby r.Area descending
			select r).ToList();
		string receptionNames = (receptions.Count == 0) ? null : string.Join(", ", receptions.Select((Room r) => r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()).Distinct().Take(3));
		List<LogoGuide> guides = LogoPlacer.FindGuides(doc);

		LogoWindow window = new LogoWindow(LogoPlacer.FindLogo(doc), receptionNames, walls.Count, guides.Count, _widthText, _heightText, parse);
		if (window.ShowDialog() != true)
		{
			return Result.Cancelled;
		}

		switch (window.Action)
		{
		case LogoAction.Snap:
			return Snap(doc, guides);
		case LogoAction.RemoveGuides:
			RemoveGuides(doc, guides);
			return Result.Succeeded;
		}

		_widthText = Cm(UnitUtils.ConvertFromInternalUnits(window.LogoWidth, UnitTypeId.Centimeters));
		_heightText = Cm(UnitUtils.ConvertFromInternalUnits(window.CenterHeight, UnitTypeId.Centimeters));
		double logoHeight = window.LogoWidth * Aspect(window.LogoPath);
		LogoPlacer placer = new LogoPlacer(doc, UnitUtils.ConvertToInternalUnits(30.0, UnitTypeId.Centimeters));

		List<LogoSpot> spots = new List<LogoSpot>();
		List<string> skipped = new List<string>();
		if (window.UseReception)
		{
			LogoSpot spot = placer.ReceptionSpot(receptions, window.CenterHeight, window.LogoWidth, logoHeight);
			if (spot == null)
			{
				TaskDialog.Show(ToolName, "No reception or lobby wall has room for a logo that wide (walls with windows don't count). Try a smaller width, or select the walls yourself.");
				return Result.Cancelled;
			}
			spots.Add(spot);
		}
		else
		{
			foreach (Wall wall in walls)
			{
				LogoSpot spot = placer.SpotOn(wall, null, null, null, window.CenterHeight, window.LogoWidth, logoHeight, out var why);
				if (spot == null)
				{
					skipped.Add($"{PlaceArtworkCommand.WallLabel(wall)}: {why}");
				}
				else
				{
					spot.Label = PlaceArtworkCommand.WallLabel(wall);
					spots.Add(spot);
				}
			}
		}
		if (spots.Count == 0)
		{
			TaskDialog.Show(ToolName, "None of the walls has room for the logo:\n\n" + string.Join("\n", skipped));
			return Result.Cancelled;
		}

		List<ElementId> drawn = new List<ElementId>();
		using (Transaction t = new Transaction(doc, "Place Logo: mark the spot"))
		{
			t.Start();
			guides.ForEach((LogoGuide g) => doc.Delete(g.Curves));
			spots.ForEach((LogoSpot s) => drawn.AddRange(LogoPlacer.DrawGuide(doc, s)));
			t.Commit();
		}

		ShowIn3D(uidoc, drawn);
		try
		{
			System.Windows.Clipboard.SetText(window.LogoPath);
		}
		catch
		{
		}

		bool hasType = LogoPlacer.LogoDecalTypes(doc).Count > 0;
		PostableCommand next = window.ImageAlreadySet ? PostableCommand.PlaceDecal : PostableCommand.DecalTypes;
		string steps = window.ImageAlreadySet
			? "1. Place Decal starts now. Pick \"logo\" in Properties.\n2. Click inside the pink rectangle.\n3. Run Place Logo again and click \"Fit placed logo to guide\" to center and size it."
			: ("1. Decal Types opens now. " + (hasType ? "Select \"logo\"" : "Create a new decal type named \"logo\"") + ".\n2. Click \"…\" next to Source, paste the path (Ctrl+V) in File name, and click Open. Do the same for Cutouts → Image file.\n3. Click OK, then Insert → Decal → Place Decal, pick \"logo\" and click inside the pink rectangle.\n4. Run Place Logo again and click \"Fit placed logo to guide\" to center and size it.");
		TaskDialog dialog = new TaskDialog(ToolName)
		{
			MainInstruction = (spots.Count == 1) ? ("Marked the logo spot on " + spots[0].Label + ".") : $"Marked {spots.Count} logo spots.",
			MainContent = "The image path is copied to the clipboard:\n" + window.LogoPath + "\n\n" + steps,
			ExpandedContent = string.Join("\n", skipped)
		};
		dialog.Show();

		RevitCommandId id = RevitCommandId.LookupPostableCommandId(next);
		if (id != null && uiapp.CanPostCommand(id))
		{
			uiapp.PostCommand(id);
		}
		return Result.Succeeded;
	}

	private static Result Snap(Document doc, List<LogoGuide> guides)
	{
		List<Element> decals = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DecalElement).WhereElementIsNotElementType().ToList();
		List<string> notes = new List<string>();
		int fitted = 0;
		using (Transaction t = new Transaction(doc, "Place Logo: fit to guide"))
		{
			PlaceArtworkCommand.SwallowWarnings(t);
			t.Start();
			foreach (LogoGuide g in guides)
			{
				using SubTransaction sub = new SubTransaction(doc);
				sub.Start();
				try
				{
					string why = LogoPlacer.Snap(doc, g, decals);
					if (why == null)
					{
						doc.Delete(g.Curves);
						sub.Commit();
						fitted++;
					}
					else
					{
						sub.RollBack();
						notes.Add(why);
					}
				}
				catch (Exception ex)
				{
					sub.RollBack();
					notes.Add("Revit didn't let the decal move or resize: " + ex.Message);
				}
			}
			t.Commit();
		}
		TaskDialog.Show(ToolName, (fitted > 0) ? ($"Centered and sized {fitted} logo(s) and removed their guides." + ((notes.Count > 0) ? ("\n\n" + string.Join("\n", notes.Distinct())) : "")) : ("Nothing was fitted:\n\n" + string.Join("\n", notes.Distinct())));
		return Result.Succeeded;
	}

	private static void RemoveGuides(Document doc, List<LogoGuide> guides)
	{
		using Transaction t = new Transaction(doc, "Place Logo: remove guides");
		t.Start();
		guides.ForEach((LogoGuide g) => doc.Delete(g.Curves));
		t.Commit();
	}

	// Alto / ancho de la imagen, para que la guía tenga la proporción del logo
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

	// Las guías son verticales: se ven en 3D, no en planta
	private static void ShowIn3D(UIDocument uidoc, List<ElementId> ids)
	{
		Document doc = uidoc.Document;
		if (!(uidoc.ActiveView is View3D))
		{
			View3D view = (from v in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
				where !v.IsTemplate && !v.IsPerspective
				orderby (v.Name == "{3D}") ? 0 : 1
				select v).FirstOrDefault();
			if (view != null)
			{
				uidoc.ActiveView = view;
			}
		}
		if (ids.Count > 0)
		{
			uidoc.ShowElements(ids);
		}
	}
}
