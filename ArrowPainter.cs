using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;

namespace ArtworkPlacer
{
    // ─── FLECHAS DE LA VISTA PREVIA ──────────────────────────────────────────
    // Líneas de detalle en planta, líneas de modelo en 3D. Se dibujan dentro de un
    // TransactionGroup que se deshace al final, así que nunca quedan en el modelo.
    internal class ArrowPainter
    {
        private static readonly Color OkColor = new Color(230, 0, 90);
        private static readonly Color BlockedColor = new Color(150, 150, 150);

        private readonly Document _doc;
        private readonly View _view;
        private readonly Dictionary<long, SketchPlane> _planes = new Dictionary<long, SketchPlane>();

        public ArrowPainter(Document doc, View view)
        {
            _doc = doc;
            _view = view;
        }

        // Largo de la flecha: ~12 mm en papel en planta, 60 cm en 3D
        private double Length
        {
            get
            {
                double len = _view is ViewPlan
                    ? UnitUtils.ConvertToInternalUnits(Math.Max(1, _view.Scale) * 12, UnitTypeId.Millimeters)
                    : UnitUtils.ConvertToInternalUnits(60, UnitTypeId.Centimeters);
                return Math.Min(Math.Max(len, UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Centimeters)),
                                UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Centimeters));
            }
        }

        // En planta se dibuja al nivel de la vista; en 3D, a la altura de la pieza
        private double Z(XYZ onFace) => _view is ViewPlan vp && vp.GenLevel != null ? vp.GenLevel.ProjectElevation : onFace.Z;

        // Flecha en forma de "T": la barra queda pegada a la cara, la punta toca la barra
        public List<ElementId> Draw(XYZ onFace, XYZ normal, XYZ along, bool blocked)
        {
            double len = Length;
            double head = len * 0.3;

            double z = Z(onFace);
            XYZ tip = new XYZ(onFace.X, onFace.Y, z) + normal * (len * 0.03);
            XYZ n = normal, a = along;

            var segments = new[]
            {
                Tuple.Create(tip + n * len, tip),                                  // cuerpo
                Tuple.Create(tip + n * head + a * head * 0.6, tip),                // punta
                Tuple.Create(tip + n * head - a * head * 0.6, tip),
                Tuple.Create(tip - a * head * 1.2, tip + a * head * 1.2)           // barra sobre la cara
            };

            var ogs = new OverrideGraphicSettings()
                .SetProjectionLineColor(blocked ? BlockedColor : OkColor)
                .SetProjectionLineWeight(blocked ? 4 : 9);

            var ids = new List<ElementId>();
            foreach (var seg in segments)
            {
                Line line = Line.CreateBound(seg.Item1, seg.Item2);
                CurveElement ce = _view is ViewPlan
                    ? (CurveElement)_doc.Create.NewDetailCurve(_view, line)
                    : _doc.Create.NewModelCurve(line, PlaneAt(z));
                try { _view.SetElementOverrides(ce.Id, ogs); } catch { }
                ids.Add(ce.Id);
            }
            return ids;
        }

        // Texto junto a la cola de la flecha (solo en planta); null si no se pudo
        public ElementId Label(XYZ onFace, XYZ normal, string text)
        {
            if (!(_view is ViewPlan)) return null;
            ElementId typeId = _doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (typeId == ElementId.InvalidElementId) return null;

            XYZ p = new XYZ(onFace.X, onFace.Y, Z(onFace)) + normal * (Length * 1.25);
            try
            {
                var opts = new TextNoteOptions(typeId) { HorizontalAlignment = HorizontalTextAlignment.Center };
                return TextNote.Create(_doc, _view.Id, p, text, opts).Id;
            }
            catch
            {
                return null;
            }
        }

        private SketchPlane PlaneAt(double z)
        {
            long key = (long)Math.Round(z * 1000);
            if (!_planes.TryGetValue(key, out SketchPlane sp) || !sp.IsValidObject)
            {
                sp = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
                _planes[key] = sp;
            }
            return sp;
        }
    }
}
