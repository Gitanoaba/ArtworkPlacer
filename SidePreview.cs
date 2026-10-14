using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace ArtworkPlacer;

internal class SidePreview
{
	private class PreviewFilter : ISelectionFilter
	{
		private readonly SidePreview _p;

		public PreviewFilter(SidePreview p)
		{
			_p = p;
		}

		public bool AllowElement(Element e)
		{
			if (!_p._choice.ContainsKey(e.Id))
			{
				return _p._arrowToWall.ContainsKey(e.Id);
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

	private readonly WallPlacer _placer;

	private readonly List<Wall> _walls;

	private readonly Dictionary<ElementId, List<int>> _choice;

	private readonly Dictionary<ElementId, List<int>> _initial;

	private readonly string _title;

	private readonly Dictionary<ElementId, List<ElementId>> _arrows = new Dictionary<ElementId, List<ElementId>>();

	private readonly Dictionary<ElementId, ElementId> _arrowToWall = new Dictionary<ElementId, ElementId>();

	private readonly Dictionary<ElementId, int> _blocked = new Dictionary<ElementId, int>();

	private readonly Dictionary<ElementId, int> _pieces = new Dictionary<ElementId, int>();

	private View _view;

	private ArrowPainter _painter;

	public SidePreview(UIDocument uidoc, WallPlacer placer, List<Wall> walls, Dictionary<ElementId, List<int>> choice, string title)
	{
		_uidoc = uidoc;
		_doc = uidoc.Document;
		_placer = placer;
		_walls = walls;
		_choice = choice;
		_initial = choice.ToDictionary((KeyValuePair<ElementId, List<int>> kv) => kv.Key, (KeyValuePair<ElementId, List<int>> kv) => kv.Value.ToList());
		_title = title;
	}

	public bool Run()
	{
		_view = _doc.ActiveView;
		if (!(_view is ViewPlan) && !(_view is View3D))
		{
			return new TaskDialog(_title)
			{
				MainInstruction = "The arrow preview only works in plan and 3D views.",
				MainContent = "Place the artwork anyway, without a preview?",
				CommonButtons = (TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No)
			}.Show() == TaskDialogResult.Yes;
		}
		_painter = new ArrowPainter(_doc, _view);
		using TransactionGroup transactionGroup = new TransactionGroup(_doc, "Artwork preview");
		transactionGroup.Start();
		try
		{
			Redraw(_walls);
			while (true)
			{
				try
				{
					Reference reference = _uidoc.Selection.PickObject(ObjectType.Element, new PreviewFilter(this), "Click a wall or its arrow to switch faces · ESC when done");
					ElementId id = reference.ElementId;
					if (_arrowToWall.TryGetValue(id, out var value))
					{
						id = value;
					}
					Wall wall = _walls.FirstOrDefault((Wall w) => w.Id == id);
					if (wall != null && Cycle(wall))
					{
						Redraw(new Wall[1] { wall });
					}
				}
				catch (OperationCanceledException)
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
		int num = _pieces.Values.Sum();
		int num2 = _blocked.Values.Sum();
		TaskDialog taskDialog = new TaskDialog(_title);
		taskDialog.MainInstruction = $"Place {num} artwork piece(s) on {_walls.Count} wall(s)?";
		taskDialog.MainContent = ((num2 > 0) ? $"{num2} gray arrow(s): no free space there (or the face already has artwork), so they will be skipped." : "Each arrow points at the face and spot where a piece will go.");
		taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Place artwork");
		taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Keep adjusting faces");
		taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Cancel");
		return taskDialog.Show();
	}

	private bool Cycle(Wall wall)
	{
		if (!_placer.HasBothSides(wall))
		{
			return false;
		}
		List<int> list = _initial[wall.Id];
		int num = ((list.Count == 1) ? list[0] : (-1));
		List<List<int>> list2 = new List<List<int>>
		{
			new List<int> { num },
			new List<int> { -num },
			new List<int>
			{
				num,
				-num
			}
		};
		List<int> cur = _choice[wall.Id];
		int num2 = list2.FindIndex((List<int> s) => s.Count == cur.Count && s.All(cur.Contains));
		_choice[wall.Id] = list2[(num2 + 1) % list2.Count];
		return true;
	}

	private void Redraw(IEnumerable<Wall> walls)
	{
		using (Transaction transaction = new Transaction(_doc, "Artwork arrows"))
		{
			transaction.Start();
			foreach (Wall wall in walls)
			{
				if (_arrows.TryGetValue(wall.Id, out var value))
				{
					foreach (ElementId item in value)
					{
						_arrowToWall.Remove(item);
					}
					_doc.Delete(value);
				}
				List<ElementId> list = new List<ElementId>();
				int num = 0;
				int num2 = 0;
				foreach (WallPlacer.Spot item2 in from s in _placer.Spots(wall)
					where _choice[wall.Id].Contains(s.Sign)
					select s)
				{
					num2++;
					if (item2.Blocked)
					{
						num++;
					}
					list.AddRange(_painter.Draw(item2.OnFace, item2.Normal, item2.Along, item2.Blocked));
				}
				_arrows[wall.Id] = list;
				_blocked[wall.Id] = num;
				_pieces[wall.Id] = num2 - num;
				foreach (ElementId item3 in list)
				{
					_arrowToWall[item3] = wall.Id;
				}
			}
			transaction.Commit();
		}
		_uidoc.RefreshActiveView();
	}
}
