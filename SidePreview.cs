using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtworkPlacer
{
    // ─── VISTA PREVIA: flechas que muestran en qué cara va cada pieza ────────
    // Las flechas viven dentro de un TransactionGroup que se deshace al final,
    // así que no quedan en el modelo ni en el historial de Undo.
    internal class SidePreview
    {
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
            _initial = choice.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            _title = title;
        }

        // true = colocar, false = cancelar
        public bool Run()
        {
            _view = _doc.ActiveView;
            if (!(_view is ViewPlan) && !(_view is View3D))
            {
                var ask = new TaskDialog(_title)
                {
                    MainInstruction = "The arrow preview only works in plan and 3D views.",
                    MainContent = "Place the artwork anyway, without a preview?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                };
                return ask.Show() == TaskDialogResult.Yes;
            }

            _painter = new ArrowPainter(_doc, _view);
            using (var tg = new TransactionGroup(_doc, "Artwork preview"))
            {
                tg.Start();
                try
                {
                    Redraw(_walls);
                    while (true)
                    {
                        try
                        {
                            Reference r = _uidoc.Selection.PickObject(ObjectType.Element, new PreviewFilter(this),
                                "Click a wall or its arrow to switch faces · ESC when done");
                            ElementId id = r.ElementId;
                            if (_arrowToWall.TryGetValue(id, out ElementId wallId)) id = wallId;
                            Wall wall = _walls.FirstOrDefault(w => w.Id == id);
                            if (wall != null && Cycle(wall)) Redraw(new[] { wall });
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            switch (AskConfirm())
                            {
                                case TaskDialogResult.CommandLink1: return true;
                                case TaskDialogResult.CommandLink2: continue;
                                default: return false;
                            }
                        }
                    }
                }
                finally
                {
                    tg.RollBack();
                }
            }
        }

        private TaskDialogResult AskConfirm()
        {
            int pieces = _pieces.Values.Sum();
            int blocked = _blocked.Values.Sum();
            var td = new TaskDialog(_title)
            {
                MainInstruction = $"Place {pieces} artwork piece(s) on {_walls.Count} wall(s)?",
                MainContent = blocked > 0
                    ? $"{blocked} gray arrow(s): no free space there (or the face already has artwork), so they will be skipped."
                    : "Each arrow points at the face and spot where a piece will go."
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Place artwork");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Keep adjusting faces");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Cancel");
            return td.Show();
        }

        // Ciclo por muro: cara inicial → cara opuesta → ambas → cara inicial
        private bool Cycle(Wall wall)
        {
            if (!_placer.HasBothSides(wall)) return false;

            List<int> init = _initial[wall.Id];
            int first = init.Count == 1 ? init[0] : -1;
            var states = new List<List<int>> { new List<int> { first }, new List<int> { -first }, new List<int> { first, -first } };

            List<int> cur = _choice[wall.Id];
            int idx = states.FindIndex(s => s.Count == cur.Count && s.All(cur.Contains));
            _choice[wall.Id] = states[(idx + 1) % states.Count];
            return true;
        }

        private void Redraw(IEnumerable<Wall> walls)
        {
            using (var t = new Transaction(_doc, "Artwork arrows"))
            {
                t.Start();
                foreach (Wall wall in walls)
                {
                    if (_arrows.TryGetValue(wall.Id, out var old))
                    {
                        foreach (ElementId id in old) _arrowToWall.Remove(id);
                        _doc.Delete(old);
                    }

                    var ids = new List<ElementId>();
                    int blocked = 0, total = 0;
                    foreach (WallPlacer.Spot spot in _placer.Spots(wall).Where(s => _choice[wall.Id].Contains(s.Sign)))
                    {
                        total++;
                        if (spot.Blocked) blocked++;
                        ids.AddRange(_painter.Draw(spot.OnFace, spot.Normal, spot.Along, spot.Blocked));
                    }
                    _arrows[wall.Id] = ids;
                    _blocked[wall.Id] = blocked;
                    _pieces[wall.Id] = total - blocked;
                    foreach (ElementId id in ids) _arrowToWall[id] = wall.Id;
                }
                t.Commit();
            }
            _uidoc.RefreshActiveView();
        }

        private class PreviewFilter : ISelectionFilter
        {
            private readonly SidePreview _p;
            public PreviewFilter(SidePreview p) { _p = p; }
            public bool AllowElement(Element e) => _p._choice.ContainsKey(e.Id) || _p._arrowToWall.ContainsKey(e.Id);
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
