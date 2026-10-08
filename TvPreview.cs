using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtworkPlacer
{
    // ─── VISTA PREVIA DE TVs ─────────────────────────────────────────────────
    // Una flecha (y en planta, el tipo de TV) por sala. Clic en otro muro de la sala: la TV pasa ahí.
    // Clic en el muro o la flecha donde ya está: se quita (o se vuelve a poner).
    // Todo vive dentro de un TransactionGroup que se deshace al final.
    internal class TvPreview
    {
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
            _wallIds = new HashSet<ElementId>(targets.SelectMany(t => t.Spots).Select(s => s.Wall.Id));
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
                    MainContent = "Place the TVs anyway, without a preview?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                };
                return ask.Show() == TaskDialogResult.Yes;
            }

            _painter = new ArrowPainter(_doc, _view);
            using (var tg = new TransactionGroup(_doc, "TV preview"))
            {
                tg.Start();
                try
                {
                    Redraw(_targets);
                    while (true)
                    {
                        try
                        {
                            Reference r = _uidoc.Selection.PickObject(ObjectType.Element, new PreviewFilter(this),
                                "Click another wall of a room to move its TV there, or the TV's own wall/arrow to remove it · ESC when done");
                            TvTarget changed = Click(r);
                            if (changed != null) Redraw(new[] { changed });
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
            int on = _targets.Count(t => t.Chosen != null && t.Chosen.Problem == null);
            int off = _targets.Count - on;
            var td = new TaskDialog(_title)
            {
                MainInstruction = $"Place {on} TV(s)?",
                MainContent = off > 0
                    ? $"{off} room(s)/wall(s) get no TV (gray arrows): no free space, already has one, or switched off."
                    : "Each arrow points at the wall and spot where a TV will go."
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Place TVs");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Keep adjusting");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Cancel");
            return td.Show();
        }

        // Devuelve la sala/muro que cambió, o null
        private TvTarget Click(Reference r)
        {
            if (_markToTarget.TryGetValue(r.ElementId, out TvTarget marked))
            {
                marked.Toggle();
                return marked;
            }

            var options = _targets
                .SelectMany(t => t.Spots.Select((s, i) => new { Target = t, Spot = s, Index = i }))
                .Where(x => x.Spot.Wall.Id == r.ElementId)
                .ToList();
            if (options.Count == 0) return null;

            // El lugar de esa cara (y de ese tramo del muro) más cercano al clic
            var pick = options.FirstOrDefault(x => x.Target.Current != x.Index) ?? options[0];
            Wall wall = options[0].Spot.Wall;
            if (r.GlobalPoint != null && wall.Location is LocationCurve lc && lc.Curve is Line line)
            {
                XYZ q = r.GlobalPoint - line.GetEndPoint(0);
                double u = q.DotProduct(line.Direction), v = q.DotProduct(wall.Orientation);
                pick = options
                    .OrderBy(x => Math.Abs(v - x.Spot.FaceV) + Math.Max(0, Math.Max(x.Spot.U0 - u, u - x.Spot.U1)))
                    .First();
            }

            if (pick.Target.Current == pick.Index) pick.Target.Toggle();
            else pick.Target.Current = pick.Index;
            return pick.Target;
        }

        private void Redraw(IEnumerable<TvTarget> targets)
        {
            using (var t = new Transaction(_doc, "TV arrows"))
            {
                t.Start();
                foreach (TvTarget target in targets)
                {
                    if (_drawn.TryGetValue(target, out var old))
                    {
                        foreach (ElementId id in old) _markToTarget.Remove(id);
                        _doc.Delete(old);
                    }

                    var ids = new List<ElementId>();
                    TvSpot spot = target.Chosen ?? (target.Spots.Count > 0 ? target.Spots[Math.Min(target.LastOn, target.Spots.Count - 1)] : null);
                    if (spot?.OnFace != null)
                    {
                        bool ok = target.Chosen != null && spot.Problem == null;
                        ids.AddRange(_painter.Draw(spot.OnFace, spot.Normal, spot.Along, !ok));

                        string text = target.Chosen == null ? "no TV"
                            : spot.HasTv ? "has a TV"
                            : spot.Problem != null ? "doesn't fit"
                            : spot.Symbol.Name;
                        ElementId label = _painter.Label(spot.OnFace, spot.Normal, text);
                        if (label != null) ids.Add(label);
                    }

                    _drawn[target] = ids;
                    foreach (ElementId id in ids) _markToTarget[id] = target;
                }
                t.Commit();
            }
            _uidoc.RefreshActiveView();
        }

        private class PreviewFilter : ISelectionFilter
        {
            private readonly TvPreview _p;
            public PreviewFilter(TvPreview p) { _p = p; }
            public bool AllowElement(Element e) => _p._wallIds.Contains(e.Id) || _p._markToTarget.ContainsKey(e.Id);
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
