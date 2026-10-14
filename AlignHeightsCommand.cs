using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ArtworkPlacer;

[Transaction(TransactionMode.Manual)]
public class AlignHeightsCommand : IExternalCommand
{
	private const string ToolName = "Align Artwork Heights";

	private const string Marker = "ArtworkPlacer";

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIDocument activeUIDocument = commandData.Application.ActiveUIDocument;
		if (activeUIDocument == null || activeUIDocument.Document.IsFamilyDocument)
		{
			TaskDialog.Show("Align Artwork Heights", "Open a project (not a family) to use this tool.");
			return Result.Cancelled;
		}
		Document doc = activeUIDocument.Document;
		List<FamilyInstance> list = (from id in activeUIDocument.Selection.GetElementIds()
			select doc.GetElement(id)).OfType<FamilyInstance>().ToList();
		string source = "selected";
		if (list.Count == 0)
		{
			HashSet<ElementId> library = LibraryPieces(doc);
			list = (from FamilyInstance fi in new FilteredElementCollector(doc, doc.ActiveView.Id).OfClass(typeof(FamilyInstance))
				where IsArtwork(fi) && !library.Contains(fi.Id)
				select fi).ToList();
			source = "in this view (nothing was selected; the Artwork_* section samples are left alone)";
		}
		if (list.Count == 0)
		{
			TaskDialog.Show("Align Artwork Heights", "No artwork found. Select the pieces to align, or open a view where they are visible.");
			return Result.Cancelled;
		}
		Units units = doc.GetUnits();
		Func<string, double?> parseLength = (string s) => (!UnitFormatUtils.TryParse(units, SpecTypeId.Length, s, out var value)) ? ((double?)null) : new double?(value);
		PlaceArtworkCommand.InitDefaults(Fmt);
		AlignDialog alignDialog = new AlignDialog(list.Count, source, PlaceArtworkCommand.Settings, parseLength);
		if (alignDialog.ShowDialog() != true)
		{
			return Result.Cancelled;
		}
		int num = 0;
		int num2 = 0;
		List<string> list2 = new List<string>();
		using (Transaction transaction = new Transaction(doc, "Align artwork heights"))
		{
			FailureHandlingOptions failureHandlingOptions = transaction.GetFailureHandlingOptions();
			failureHandlingOptions.SetFailuresPreprocessor(new PlaceArtworkCommand.WarningSwallower());
			transaction.SetFailureHandlingOptions(failureHandlingOptions);
			transaction.Start();
			foreach (FamilyInstance item in list)
			{
				string text = $"{item.Symbol.FamilyName} : {item.Symbol.Name} ({item.Id.Value})";
				try
				{
					Level level = ArtGeometry.ReferenceLevel(doc, item);
					if (level == null)
					{
						list2.Add(text + ": no level");
						continue;
					}
					if (!ArtGeometry.ZRange(item, out var z, out var z2))
					{
						list2.Add(text + ": no geometry");
						continue;
					}
					double num3 = ArtGeometry.AlignDelta(alignDialog.Align, level.ProjectElevation + alignDialog.ArtHeight, z, z2);
					if (Math.Abs(num3) < 0.0001)
					{
						num2++;
						continue;
					}
					ElementTransformUtils.MoveElement(doc, item.Id, XYZ.BasisZ * num3);
					num++;
				}
				catch (Exception ex)
				{
					list2.Add(text + ": " + ex.Message);
				}
			}
			transaction.Commit();
		}
		TaskDialog taskDialog = new TaskDialog("Align Artwork Heights");
		taskDialog.MainInstruction = $"Moved {num} piece(s); {num2} were already at the right height.";
		taskDialog.MainContent = ((list2.Count == 0) ? "" : $"{list2.Count} piece(s) could not be aligned (see details).");
		taskDialog.ExpandedContent = string.Join("\n", list2);
		taskDialog.Show();
		return Result.Succeeded;
		string Fmt(double ft)
		{
			return UnitFormatUtils.Format(units, SpecTypeId.Length, ft, forEditing: false);
		}
	}

	private static HashSet<ElementId> LibraryPieces(Document doc)
	{
		HashSet<ElementId> hashSet = new HashSet<ElementId>();
		foreach (ViewSection item in from ViewSection v in new FilteredElementCollector(doc).OfClass(typeof(ViewSection))
			where !v.IsTemplate && PlaceArtworkCommand.LooksLikeArtwork(v.Name)
			select v)
		{
			foreach (ElementId item2 in new FilteredElementCollector(doc, item.Id).OfClass(typeof(FamilyInstance)).ToElementIds())
			{
				hashSet.Add(item2);
			}
		}
		return hashSet;
	}

	private static bool IsArtwork(FamilyInstance fi)
	{
		if (!(((Element)fi).get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == "ArtworkPlacer"))
		{
			return PlaceArtworkCommand.LooksLikeArtwork(fi.Symbol.FamilyName);
		}
		return true;
	}
}
