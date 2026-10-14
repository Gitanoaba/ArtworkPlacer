using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ArtworkPlacer;

[Transaction(TransactionMode.Manual)]
public class PlaceTvCommand : IExternalCommand
{
	private const string ToolName = "Place TV";

	private static readonly TvSettings _settings = new TvSettings();

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIDocument activeUIDocument = commandData.Application.ActiveUIDocument;
		if (activeUIDocument == null || activeUIDocument.Document.IsFamilyDocument)
		{
			TaskDialog.Show("Place TV", "Open a project (not a family) to use this tool.");
			return Result.Cancelled;
		}
		Document doc = activeUIDocument.Document;
		List<Wall> walls = PlaceArtworkCommand.GetWalls(activeUIDocument, "Select the walls of the meeting rooms (or one wall per TV), then click Finish");
		if (walls == null)
		{
			return Result.Cancelled;
		}
		if (walls.Count == 0)
		{
			TaskDialog.Show("Place TV", "No walls were selected.");
			return Result.Cancelled;
		}
		Units units = doc.GetUnits();
		Func<string, double?> parseLength = (string s) => (!UnitFormatUtils.TryParse(units, SpecTypeId.Length, s, out var value)) ? ((double?)null) : new double?(value);
		if (_settings.BottomText == null)
		{
			_settings.BottomText = Fmt(UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Centimeters));
		}
		if (_settings.ClearText == null)
		{
			_settings.ClearText = Fmt(UnitUtils.ConvertToInternalUnits(30.0, UnitTypeId.Centimeters));
		}
		Func<FamilySymbol, bool> isDefault = (FamilySymbol s) => LooksLikeTv(s.FamilyName);
		Func<string[], Tuple<List<TypeItem>, HashSet<long>>> loadRfa = (string[] paths) =>
		{
			HashSet<long> item = PlaceArtworkCommand.LoadFamilies(doc, paths, "Load TV families");
			return Tuple.Create(PlaceArtworkCommand.CollectTypes(doc, isDefault, _settings.LastChecked), item);
		};
		TvDialog tvDialog = new TvDialog(walls.Count, PlaceArtworkCommand.CollectTypes(doc, isDefault, _settings.LastChecked), _settings, parseLength, loadRfa);
		if (tvDialog.ShowDialog() != true)
		{
			return Result.Cancelled;
		}
		List<FamilySymbol> list = (from id in tvDialog.SelectedIds
			select doc.GetElement(new ElementId(id)) as FamilySymbol into s
			where s != null
			select s).ToList();
		TvPlacer placer = new TvPlacer(doc, list, tvDialog.Clearance, tvDialog.BottomHeight, _settings.AutoSize, tvDialog.DepthFactor, Fmt);
		List<string> list2 = new List<string>();
		using (Transaction transaction = new Transaction(doc, "Measure TV types"))
		{
			PlaceArtworkCommand.SwallowWarnings(transaction);
			transaction.Start();
			foreach (FamilySymbol item2 in list)
			{
				if (!item2.IsActive)
				{
					item2.Activate();
				}
			}
			doc.Regenerate();
			placer.MeasureTypes(walls);
			transaction.RollBack();
		}
		List<FamilySymbol> unmeasured = placer.Unmeasured;
		if (unmeasured.Count == list.Count)
		{
			TaskDialog.Show("Place TV", "None of the checked TV types could be placed on the selected walls.");
			return Result.Cancelled;
		}
		foreach (FamilySymbol item3 in unmeasured)
		{
			list2.Add(item3.FamilyName + " : " + item3.Name + ": could not be placed on these walls, not used");
		}
		List<FoundRoom> list3 = RoomFinder.Find(walls, out var loose);
		List<Outline> doors = DoorBoxes(doc);
		List<TvTarget> list4 = list3.OrderByDescending((FoundRoom r) => r.Area).Select((FoundRoom r, int i) => placer.RoomTarget(r, i + 1, doors)).Concat(loose.Select(placer.WallTarget))
			.ToList();
		if (list4.All((TvTarget t) => t.Chosen == null))
		{
			TaskDialog.Show("Place TV", "No TV can be placed on the selected walls:\n\n" + string.Join("\n", list4.Select((TvTarget t) => t.Label + ": " + t.Note)));
			return Result.Cancelled;
		}
		if (_settings.Preview && !new TvPreview(activeUIDocument, list4, "Place TV").Run())
		{
			return Result.Cancelled;
		}
		List<ElementId> list5 = new List<ElementId>();
		using (Transaction transaction2 = new Transaction(doc, "Place TVs"))
		{
			PlaceArtworkCommand.SwallowWarnings(transaction2);
			transaction2.Start();
			foreach (FamilySymbol item4 in list)
			{
				if (!item4.IsActive)
				{
					item4.Activate();
				}
			}
			doc.Regenerate();
			foreach (TvTarget item5 in list4)
			{
				TvSpot chosen = item5.Chosen;
				if (chosen == null)
				{
					list2.Add(item5.Label + ": " + (item5.Note ?? "no TV"));
					continue;
				}
				using SubTransaction subTransaction = new SubTransaction(doc);
				subTransaction.Start();
				try
				{
					FamilyInstance familyInstance = placer.Place(chosen, out var reason);
					if (familyInstance == null)
					{
						list2.Add(item5.Label + ": " + reason);
					}
					else
					{
						list5.Add(familyInstance.Id);
					}
					subTransaction.Commit();
				}
				catch (Exception ex)
				{
					subTransaction.RollBack();
					list2.Add(item5.Label + ": error – " + ex.Message);
				}
			}
			transaction2.Commit();
		}
		if (list5.Count > 0)
		{
			activeUIDocument.Selection.SetElementIds(list5);
		}
		TaskDialog taskDialog = new TaskDialog("Place TV");
		taskDialog.MainInstruction = $"Placed {list5.Count} TV(s): {list3.Count} room(s) found, {loose.Count} single wall(s).";
		taskDialog.MainContent = ((list2.Count == 0) ? "The new TVs are selected so you can review them." : $"{list2.Count} item(s) skipped (see details). The new TVs are selected.");
		taskDialog.ExpandedContent = string.Join("\n", list2);
		taskDialog.Show();
		return Result.Succeeded;
		string Fmt(double ft)
		{
			return UnitFormatUtils.Format(units, SpecTypeId.Length, ft, forEditing: false);
		}
	}

	internal static bool LooksLikeTv(string familyName)
	{
		string text = familyName.ToLowerInvariant();
		if (!text.Contains("tv"))
		{
			return text.Contains("television");
		}
		return true;
	}

	internal static List<Outline> DoorBoxes(Document doc)
	{
		return (from e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType()
			select e.get_BoundingBox((View)null) into bb
			where bb != null
			select new Outline(bb.Min, bb.Max)).ToList();
	}
}
