using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace ArtworkPlacer;

[Transaction(TransactionMode.Manual)]
public class PlaceArtworkCommand : IExternalCommand
{
	private class WallFilter : ISelectionFilter
	{
		public bool AllowElement(Element elem)
		{
			return elem is Wall;
		}

		public bool AllowReference(Reference reference, XYZ position)
		{
			return false;
		}
	}

	internal class WarningSwallower : IFailuresPreprocessor
	{
		public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
		{
			foreach (FailureMessageAccessor failureMessage in a.GetFailureMessages())
			{
				if (failureMessage.GetSeverity() == FailureSeverity.Warning)
				{
					a.DeleteWarning(failureMessage);
				}
			}
			return FailureProcessingResult.Continue;
		}
	}

	private const string ToolName = "Place Artwork";

	private static readonly DialogSettings _settings = new DialogSettings();

	internal static DialogSettings Settings => _settings;

	internal static void InitDefaults(Func<double, string> fmt)
	{
		if (_settings.HeightDefaults == null)
		{
			_settings.HeightDefaults = new string[3]
			{
				Cm(150.0),
				Cm(110.0),
				Cm(200.0)
			};
		}
		if (_settings.HeightText == null)
		{
			_settings.HeightText = _settings.HeightDefaults[0];
		}
		if (_settings.ClearText == null)
		{
			_settings.ClearText = Cm(30.0);
		}
		if (_settings.GapText == null)
		{
			_settings.GapText = Cm(60.0);
		}
		string Cm(double cm)
		{
			return fmt(UnitUtils.ConvertToInternalUnits(cm, UnitTypeId.Centimeters));
		}
	}

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIDocument activeUIDocument = commandData.Application.ActiveUIDocument;
		if (activeUIDocument == null || activeUIDocument.Document.IsFamilyDocument)
		{
			TaskDialog.Show("Place Artwork", "Open a project (not a family) to use this tool.");
			return Result.Cancelled;
		}
		Document doc = activeUIDocument.Document;
		Units units = doc.GetUnits();
		Func<string, double?> parseLength = (string s) => (!UnitFormatUtils.TryParse(units, SpecTypeId.Length, s, out var value)) ? ((double?)null) : new double?(value);
		InitDefaults(Fmt);
		Func<FamilySymbol, bool> isDefault = (FamilySymbol s) => LooksLikeArtwork(s.FamilyName);
		Func<string[], Tuple<List<TypeItem>, HashSet<long>>> loadRfa = (string[] paths) =>
		{
			HashSet<long> item = LoadFamilies(doc, paths, "Load artwork families");
			return Tuple.Create(CollectTypes(doc, isDefault, _settings.LastChecked), item);
		};
		TypeThumbnails typeThumbnails = new TypeThumbnails(doc, commandData.Application.Application.VersionNumber);
		List<Wall> walls = SelectedWalls(activeUIDocument);
		ArtworkDialog artworkDialog;
		while (true)
		{
			List<TypeItem> list = CollectTypes(doc, isDefault, _settings.LastChecked);
			List<RoomChoice> rooms = RoomScope.Collect(doc, (double a) => UnitFormatUtils.Format(units, SpecTypeId.Area, a, forEditing: false), _settings.LastRooms);
			artworkDialog = new ArtworkDialog(walls.Count, rooms, list, CollectCollections(doc, list), _settings, parseLength, loadRfa, typeThumbnails.Get);
			if (artworkDialog.ShowDialog() != true)
			{
				return Result.Cancelled;
			}
			if (!artworkDialog.PickWallsRequested)
			{
				break;
			}
			walls = PickWalls(activeUIDocument, "Select the walls to place artwork on, then click Finish") ?? walls;
		}
		FaceRanges ranges = null;
		int roomCount = 0;
		if (artworkDialog.UseRooms)
		{
			ranges = new FaceRanges();
			roomCount = artworkDialog.SelectedRoomIds.Count;
			walls = RoomScope.Walls(doc, artworkDialog.SelectedRoomIds, ranges);
			if (walls.Count == 0)
			{
				TaskDialog.Show("Place Artwork", "The chosen rooms aren't bounded by any straight walls in this model.");
				return Result.Cancelled;
			}
		}
		List<FamilySymbol> list2 = (from id in artworkDialog.SelectedIds
			select doc.GetElement(new ElementId(id)) as FamilySymbol into s
			where s != null
			select s).ToList();
		WallPlacer wallPlacer = new WallPlacer(doc, list2, artworkDialog.ArtHeight, artworkDialog.Align, artworkDialog.Clearance, artworkDialog.Gap, artworkDialog.MaxPerFace, _settings.Side, Fmt)
		{
			Ranges = ranges
		};
		List<ElementId> list3 = new List<ElementId>();
		List<string> list4 = new List<string>();
		Dictionary<ElementId, List<int>> choice = new Dictionary<ElementId, List<int>>();
		foreach (Wall item2 in walls)
		{
			List<int> list5 = wallPlacer.InitialSides(item2, out var error);
			if (list5 == null)
			{
				list4.Add(WallLabel(item2) + ": " + error);
			}
			else
			{
				choice[item2.Id] = (ranges != null) ? ranges.Signs(item2.Id) : list5;
			}
		}
		walls = walls.Where((Wall w) => choice.ContainsKey(w.Id)).ToList();
		if (walls.Count == 0)
		{
			TaskDialog.Show("Place Artwork", "None of the selected walls can be used:\n\n" + string.Join("\n", list4));
			return Result.Cancelled;
		}
		if (_settings.Preview && !new SidePreview(activeUIDocument, wallPlacer, walls, choice, "Place Artwork").Run())
		{
			return Result.Cancelled;
		}
		using (Transaction transaction = new Transaction(doc, "Place artwork"))
		{
			SwallowWarnings(transaction);
			transaction.Start();
			foreach (FamilySymbol item3 in list2)
			{
				if (!item3.IsActive)
				{
					item3.Activate();
				}
			}
			doc.Regenerate();
			foreach (Wall item4 in walls)
			{
				using SubTransaction subTransaction = new SubTransaction(doc);
				subTransaction.Start();
				try
				{
					list3.AddRange(wallPlacer.PlaceOnWall(item4, choice[item4.Id], list4));
					subTransaction.Commit();
				}
				catch (Exception ex)
				{
					subTransaction.RollBack();
					list4.Add(WallLabel(item4) + ": error – " + ex.Message);
				}
			}
			transaction.Commit();
		}
		if (list3.Count > 0)
		{
			activeUIDocument.Selection.SetElementIds(list3);
		}
		TaskDialog taskDialog = new TaskDialog("Place Artwork");
		taskDialog.MainInstruction = (roomCount > 0) ? $"Placed {list3.Count} artwork piece(s) in {roomCount} room(s)." : $"Placed {list3.Count} artwork piece(s) on {walls.Count} wall(s).";
		taskDialog.MainContent = ((list4.Count == 0) ? "The new pieces are selected so you can review them." : $"{list4.Count} wall face(s) were skipped (see details). The new pieces are selected.");
		taskDialog.ExpandedContent = string.Join("\n", list4);
		taskDialog.Show();
		return Result.Succeeded;
		string Fmt(double ft)
		{
			return UnitFormatUtils.Format(units, SpecTypeId.Length, ft, forEditing: false);
		}
	}

	internal static string WallLabel(Wall w)
	{
		return $"Wall {w.Id.Value} ({w.Name})";
	}

	internal static List<Wall> GetWalls(UIDocument uidoc, string prompt)
	{
		List<Wall> list = SelectedWalls(uidoc);
		return (list.Count > 0) ? list : PickWalls(uidoc, prompt);
	}

	internal static List<Wall> SelectedWalls(UIDocument uidoc)
	{
		Document doc = uidoc.Document;
		return (from id in uidoc.Selection.GetElementIds()
			select doc.GetElement(id)).OfType<Wall>().ToList();
	}

	internal static List<Wall> PickWalls(UIDocument uidoc, string prompt)
	{
		Document doc = uidoc.Document;
		try
		{
			return (from w in (from r in uidoc.Selection.PickObjects(ObjectType.Element, new WallFilter(), prompt)
					select doc.GetElement(r)).OfType<Wall>()
				group w by w.Id into g
				select g.First()).ToList();
		}
		catch (Autodesk.Revit.Exceptions.OperationCanceledException)
		{
			return null;
		}
	}

	internal static HashSet<long> LoadFamilies(Document doc, string[] paths, string transactionName)
	{
		HashSet<long> hashSet = new HashSet<long>();
		using Transaction transaction = new Transaction(doc, transactionName);
		transaction.Start();
		foreach (string text in paths)
		{
			if (!doc.LoadFamily(text, out var family))
			{
				string name = Path.GetFileNameWithoutExtension(text);
				family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault((Family f) => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
			}
			if (family == null)
			{
				continue;
			}
			foreach (ElementId familySymbolId in family.GetFamilySymbolIds())
			{
				hashSet.Add(familySymbolId.Value);
			}
		}
		transaction.Commit();
		return hashSet;
	}

	internal static List<TypeItem> CollectTypes(Document doc, Func<FamilySymbol, bool> isDefault, HashSet<long> lastChecked)
	{
		HashSet<long> excluded = new HashSet<long> { -2000023L, -2000014L };
		return (from FamilySymbol s in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
			where s.Category != null && !excluded.Contains(s.Category.Id.Value)
			where KindOf(s) != null
			select new TypeItem
			{
				Id = s.Id.Value,
				Label = s.FamilyName + " : " + s.Name,
				Kind = s.Category.Name + ", " + KindOf(s),
				IsChecked = ((lastChecked.Count > 0) ? lastChecked.Contains(s.Id.Value) : isDefault(s))
			} into i
			orderby i.Label
			select i).ToList();
	}

	private static List<ArtCollection> CollectCollections(Document doc, List<TypeItem> types)
	{
		HashSet<long> placeable = new HashSet<long>(types.Select((TypeItem t) => t.Id));
		List<ArtCollection> list = new List<ArtCollection>();
		foreach (ViewSection view in from ViewSection v in new FilteredElementCollector(doc).OfClass(typeof(ViewSection))
			where !v.IsTemplate && LooksLikeArtwork(v.Name)
			orderby v.Name
			select v)
		{
			XYZ right = view.RightDirection;
			List<long> list2 = (from FamilyInstance fi in new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance))
				where placeable.Contains(fi.Symbol.Id.Value)
				select new
				{
					Id = fi.Symbol.Id,
					X = PositionAlong(fi, view, right)
				} into x
				orderby x.X
				select x.Id.Value).Distinct().ToList();
			if (list2.Count > 0)
			{
				list.Add(new ArtCollection
				{
					Name = view.Name,
					Ids = list2
				});
			}
		}
		return list;
	}

	private static double PositionAlong(Element e, View view, XYZ right)
	{
		BoundingBoxXYZ boundingBoxXYZ = e.get_BoundingBox((View)null);
		if (boundingBoxXYZ != null)
		{
			return ((boundingBoxXYZ.Min + boundingBoxXYZ.Max) / 2.0).DotProduct(right);
		}
		return 0.0;
	}

	internal static bool LooksLikeArtwork(string familyName)
	{
		return familyName.Replace(" ", "").Replace("_", "").ToLowerInvariant()
			.Contains("artwork");
	}

	internal static string KindOf(FamilySymbol s)
	{
		return s.Family.FamilyPlacementType switch
		{
			FamilyPlacementType.WorkPlaneBased => "face-based", 
			FamilyPlacementType.OneLevelBasedHosted => "wall-hosted", 
			FamilyPlacementType.OneLevelBased => "unhosted", 
			_ => null, 
		};
	}

	internal static void SwallowWarnings(Transaction t)
	{
		FailureHandlingOptions failureHandlingOptions = t.GetFailureHandlingOptions();
		failureHandlingOptions.SetFailuresPreprocessor(new WarningSwallower());
		t.SetFailureHandlingOptions(failureHandlingOptions);
	}
}
