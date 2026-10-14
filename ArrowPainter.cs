using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ArtworkPlacer;

internal class ArrowPainter
{
	private static readonly Color OkColor = new Color(230, 0, 90);

	private static readonly Color BlockedColor = new Color(150, 150, 150);

	private readonly Document _doc;

	private readonly View _view;

	private readonly Dictionary<long, SketchPlane> _planes = new Dictionary<long, SketchPlane>();

	private double Length => Math.Min(Math.Max((_view is ViewPlan) ? UnitUtils.ConvertToInternalUnits(Math.Max(1, _view.Scale) * 12, UnitTypeId.Millimeters) : UnitUtils.ConvertToInternalUnits(60.0, UnitTypeId.Centimeters), UnitUtils.ConvertToInternalUnits(30.0, UnitTypeId.Centimeters)), UnitUtils.ConvertToInternalUnits(200.0, UnitTypeId.Centimeters));

	public ArrowPainter(Document doc, View view)
	{
		_doc = doc;
		_view = view;
	}

	private double Z(XYZ onFace)
	{
		if (!(_view is ViewPlan { GenLevel: not null } viewPlan))
		{
			return onFace.Z;
		}
		return viewPlan.GenLevel.ProjectElevation;
	}

	public List<ElementId> Draw(XYZ onFace, XYZ normal, XYZ along, bool blocked)
	{
		double length = Length;
		double num = length * 0.3;
		double z = Z(onFace);
		XYZ xYZ = new XYZ(onFace.X, onFace.Y, z) + normal * (length * 0.03);
		Tuple<XYZ, XYZ>[] array = new Tuple<XYZ, XYZ>[4]
		{
			Tuple.Create(xYZ + normal * length, xYZ),
			Tuple.Create(xYZ + normal * num + along * num * 0.6, xYZ),
			Tuple.Create(xYZ + normal * num - along * num * 0.6, xYZ),
			Tuple.Create(xYZ - along * num * 1.2, xYZ + along * num * 1.2)
		};
		OverrideGraphicSettings overrideGraphicSettings = new OverrideGraphicSettings().SetProjectionLineColor(blocked ? BlockedColor : OkColor).SetProjectionLineWeight(blocked ? 4 : 9);
		List<ElementId> list = new List<ElementId>();
		Tuple<XYZ, XYZ>[] array2 = array;
		foreach (Tuple<XYZ, XYZ> tuple in array2)
		{
			Line geometryCurve = Line.CreateBound(tuple.Item1, tuple.Item2);
			CurveElement curveElement = ((_view is ViewPlan) ? ((CurveElement)_doc.Create.NewDetailCurve(_view, geometryCurve)) : ((CurveElement)_doc.Create.NewModelCurve(geometryCurve, PlaneAt(z))));
			try
			{
				_view.SetElementOverrides(curveElement.Id, overrideGraphicSettings);
			}
			catch
			{
			}
			list.Add(curveElement.Id);
		}
		return list;
	}

	public ElementId Label(XYZ onFace, XYZ normal, string text)
	{
		if (!(_view is ViewPlan))
		{
			return null;
		}
		ElementId defaultElementTypeId = _doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
		if (defaultElementTypeId == ElementId.InvalidElementId)
		{
			return null;
		}
		XYZ position = new XYZ(onFace.X, onFace.Y, Z(onFace)) + normal * (Length * 1.25);
		try
		{
			TextNoteOptions options = new TextNoteOptions(defaultElementTypeId)
			{
				HorizontalAlignment = HorizontalTextAlignment.Center
			};
			return TextNote.Create(_doc, _view.Id, position, text, options).Id;
		}
		catch
		{
			return null;
		}
	}

	private SketchPlane PlaneAt(double z)
	{
		long key = (long)Math.Round(z * 1000.0);
		if (!_planes.TryGetValue(key, out var value) || !value.IsValidObject)
		{
			value = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0.0, 0.0, z)));
			_planes[key] = value;
		}
		return value;
	}
}
