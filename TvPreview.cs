using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace ArtworkPlacer;

internal class TvPreview
{
	private class PreviewFilter : ISelectionFilter
	{
		private readonly TvPreview _p;

		public PreviewFilter(TvPreview p)
		{
			_p = p;
		}

		public bool AllowElement(Element e)
		{
			if (!_p._wallIds.Contains(e.Id))
			{
				return _p._markToTarget.ContainsKey(e.Id);
			}
			return true;
		}

		public bool AllowReference(Reference reference, XYZ position)
		{
			return false;
		}
	}

	private readonly UIDocument _uidoc;

	private readonly Document _doc;

	private readonly List<TvTarget> _targets;

	private readonly string _title;

	private readonly Dictionary<TvTarget, List<ElementId>> _drawn = new Dictionary<TvTarget, List<ElementId>>();

	private readonly Dictionary<ElementId, TvTarget> _markToTarget = new Dictionary<ElementId, TvTarget>();

	private readonly HashSet<ElementId> _wallIds;

	private View _view;

	private ArrowPainter _painter;

	public TvPreview(UIDocument uidoc, List<TvTarget> targets, string title)
	{
		_uidoc = uidoc;
		_doc = uidoc.Document;
		_targets = targets;
		_title = title;
		_wallIds = new HashSet<ElementId>(from s in targets.SelectMany((TvTarget t) => t.Spots)
			select s.Wall.Id);
	}

	public bool Run()
	{
		_view = _doc.ActiveView;
		if (!(_view is ViewPlan) && !(_view is View3D))
		{
			return new TaskDialog(_title)
			{
				MainInstruction = "The arrow preview only works in plan and 3D views.",
				MainContent = "Place the TVs anyway, without a preview?",
				CommonButtons = (TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No)
			}.Show() == TaskDialogResult.Yes;
		}
		_painter = new ArrowPainter(_doc, _view);
		using TransactionGroup transactionGroup = new TransactionGroup(_doc, "TV preview");
		transactionGroup.Start();
		try
		{
			Redraw(_targets);
			while (true)
			{
				try
				{
					Reference r = _uidoc.Selection.PickObject(ObjectType.Element, new PreviewFilter(this), "Click another wall of a room to move its TV there, or the TV's own wall/arrow to remove it · ESC when done");
					TvTarget tvTarget = Click(r);
					if (tvTarget != null)
					{
						Redraw(new TvTarget[1] { tvTarget });
					}
				}
				catch (Autodesk.Revit.Exceptions.OperationCanceledException)
				{
					switch (AskConfirm())
					{
					case TaskDialogResult.CommandLink1:
						return true;
					case TaskDialogResult.CommandLink2:
						break;
					default:
						return false;
					}
				}
			}
		}
		finally
		{
			transactionGroup.RollBack();
		}
	}

	private TaskDialogResult AskConfirm()
	{
		int num = _targets.Count((TvTarget t) => t.Chosen != null && t.Chosen.Problem == null);
		int num2 = _targets.Count - num;
		TaskDialog taskDialog = new TaskDialog(_title);
		taskDialog.MainInstruction = $"Place {num} TV(s)?";
		taskDialog.MainContent = ((num2 > 0) ? $"{num2} room(s)/wall(s) get no TV (gray arrows): no free space, already has one, or switched off." : "Each arrow points at the wall and spot where a TV will go.");
		taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Place TVs");
		taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Keep adjusting");
		taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Cancel");
		return taskDialog.Show();
	}

	private TvTarget Click(Reference r)
	{
		if (_markToTarget.TryGetValue(r.ElementId, out var value))
		{
			value.Toggle();
			return value;
		}
		var list = (from x in _targets.SelectMany((TvTarget t) => t.Spots.Select((TvSpot s, int i) => new
			{
				Target = t,
				Spot = s,
				Index = i
			}))
			where x.Spot.Wall.Id == r.ElementId
			select x).ToList();
		if (list.Count == 0)
		{
			return null;
		}
		var anon = list.FirstOrDefault(x => x.Target.Current != x.Index) ?? list[0];
		Wall wall = list[0].Spot.Wall;
		if (r.GlobalPoint != null && wall.Location is LocationCurve { Curve: Line curve })
		{
			XYZ xYZ = r.GlobalPoint - curve.GetEndPoint(0);
			double u = xYZ.DotProduct(curve.Direction);
			double v = xYZ.DotProduct(wall.Orientation);
			anon = list.OrderBy(x => Math.Abs(v - x.Spot.FaceV) + Math.Max(0.0, Math.Max(x.Spot.U0 - u, u - x.Spot.U1))).First();
		}
		if (anon.Target.Current == anon.Index)
		{
			anon.Target.Toggle();
		}
		else
		{
			anon.Target.Current = anon.Index;
		}
		return anon.Target;
	}

	private void Redraw(IEnumerable<TvTarget> targets)
	{
		using (Transaction transaction = new Transaction(_doc, "TV arrows"))
		{
			transaction.Start();
			foreach (TvTarget target in targets)
			{
				if (_drawn.TryGetValue(target, out var value))
				{
					foreach (ElementId item in value)
					{
						_markToTarget.Remove(item);
					}
					_doc.Delete(value);
				}
				List<ElementId> list = new List<ElementId>();
				TvSpot tvSpot = target.Chosen ?? ((target.Spots.Count > 0) ? target.Spots[Math.Min(target.LastOn, target.Spots.Count - 1)] : null);
				if (tvSpot?.OnFace != null)
				{
					bool flag = target.Chosen != null && tvSpot.Problem == null;
					list.AddRange(_painter.Draw(tvSpot.OnFace, tvSpot.Normal, tvSpot.Along, !flag));
					string text;
					if (target.Chosen == null)
					{
						text = "no TV";
					}
					else if (tvSpot.HasTv)
					{
						text = "has a TV";
					}
					else
					{
						text = ((tvSpot.Problem != null) ? "doesn't fit" : tvSpot.Symbol.Name);
					}
					ElementId elementId = _painter.Label(tvSpot.OnFace, tvSpot.Normal, text);
					if (elementId != null)
					{
						list.Add(elementId);
					}
				}
				_drawn[target] = list;
				foreach (ElementId item2 in list)
				{
					_markToTarget[item2] = target;
				}
			}
			transaction.Commit();
		}
		_uidoc.RefreshActiveView();
	}
}
