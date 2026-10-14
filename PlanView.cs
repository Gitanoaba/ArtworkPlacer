using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ArtworkPlacer;

public class PlanView : FrameworkElement
{
	private static readonly Brush RoomFill = Freeze(new SolidColorBrush(Color.FromRgb(252, 252, 253)));

	private static readonly Brush HoverFill = Theme.Canvas;

	private static readonly Brush ArtFill = Theme.AccentSoft;

	private static readonly Brush TvFill = Freeze(new SolidColorBrush(Color.FromRgb(228, 231, 236)));

	private static readonly Brush WallBrush = Freeze(new SolidColorBrush(Color.FromRgb(58, 66, 80)));

	private static readonly Pen SelectPen = Freeze(new Pen(Theme.Accent, 2.5) { LineJoin = PenLineJoin.Round });

	private static readonly Pen ProblemPen = Freeze(new Pen(Theme.Danger, 1.5) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0), LineJoin = PenLineJoin.Round });

	private static readonly Pen SkippedPen = Freeze(new Pen(Theme.Muted, 1.5) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) });

	private static readonly Typeface Regular = new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

	private static readonly Typeface Medium = new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

	private PlanLevel _level;

	private static readonly Pen OpenFacePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(147, 176, 255))), 2.0) { DashStyle = new DashStyle(new double[] { 2.5, 1.5 }, 0), StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat });

	private static readonly Pen BlockedFacePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(196, 200, 207))), 1.5) { DashStyle = new DashStyle(new double[] { 1, 2 }, 0) });

	private List<PlanMarker> _markers = new List<PlanMarker>();

	private List<PlanFace> _faces = new List<PlanFace>();

	private PlanFace _hoverFace;

	public event Action<PlanFace> FaceClicked;

	private double _scale = 10.0;

	private Vector _offset;

	private bool _fitted;

	private Point? _down;

	private Point _last;

	private bool _panning;

	private Point _mouse;

	private PlanRoom _hoverRoom;

	private PlanMarker _hoverMarker;

	public event Action<PlanRoom, bool> RoomClicked;

	public event Action<PlanMarker> MarkerClicked;

	public event Action BackgroundClicked;

	public PlanView()
	{
		ClipToBounds = true;
		Focusable = true;
		Cursor = Cursors.Arrow;
	}

	private static T Freeze<T>(T f) where T : Freezable
	{
		f.Freeze();
		return f;
	}

	public void Show(PlanLevel level, List<PlanMarker> markers, List<PlanFace> faces = null)
	{
		_faces = faces ?? new List<PlanFace>();
		_hoverFace = null;
		_hoverMarker = null;
		if (level != _level)
		{
			_fitted = false;
		}
		_level = level;
		_markers = markers ?? new List<PlanMarker>();
		InvalidateVisual();
	}

	public void Fit()
	{
		_fitted = false;
		InvalidateVisual();
	}

	private IEnumerable<Point> AllPoints()
	{
		return _level.Rooms.SelectMany((PlanRoom r) => r.Loops.SelectMany((Point[] l) => l)).Concat(_level.Walls.SelectMany((PlanWall w) => w.Path));
	}

	private void FitNow()
	{
		List<Point> pts = AllPoints().ToList();
		if (pts.Count == 0 || ActualWidth < 10.0)
		{
			return;
		}
		double x0 = pts.Min((Point p) => p.X), x1 = pts.Max((Point p) => p.X);
		double y0 = pts.Min((Point p) => p.Y), y1 = pts.Max((Point p) => p.Y);
		double pad = 36.0;
		_scale = Math.Min((ActualWidth - pad * 2.0) / Math.Max(1.0, x1 - x0), (ActualHeight - pad * 2.0) / Math.Max(1.0, y1 - y0));
		_offset = new Vector(ActualWidth / 2.0 - (x0 + x1) / 2.0 * _scale, ActualHeight / 2.0 + (y0 + y1) / 2.0 * _scale);
		_fitted = true;
	}

	private Point ToScreen(Point p)
	{
		return new Point(p.X * _scale + _offset.X, -p.Y * _scale + _offset.Y);
	}

	private Point ToWorld(Point s)
	{
		return new Point((s.X - _offset.X) / _scale, -(s.Y - _offset.Y) / _scale);
	}

	private Geometry RoomGeometry(PlanRoom room)
	{
		StreamGeometry g = new StreamGeometry { FillRule = FillRule.EvenOdd };
		using (StreamGeometryContext ctx = g.Open())
		{
			foreach (Point[] loop in room.Loops.Where((Point[] l) => l.Length > 2))
			{
				ctx.BeginFigure(ToScreen(loop[0]), isFilled: true, isClosed: true);
				ctx.PolyLineTo(loop.Skip(1).Select(ToScreen).ToList(), isStroked: true, isSmoothJoin: false);
			}
		}
		g.Freeze();
		return g;
	}

	protected override void OnRender(DrawingContext dc)
	{
		dc.DrawRectangle(Theme.Panel, null, new Rect(0.0, 0.0, ActualWidth, ActualHeight));
		if (_level == null)
		{
			return;
		}
		if (!_fitted)
		{
			FitNow();
		}
		double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
		foreach (PlanRoom room in _level.Rooms)
		{
			Brush fill = (room.Use == RoomUse.Artwork) ? ArtFill : ((room.Use == RoomUse.Tv) ? TvFill : ((room == _hoverRoom) ? HoverFill : RoomFill));
			dc.DrawGeometry(fill, null, RoomGeometry(room));
		}
		foreach (PlanWall wall in _level.Walls.Where((PlanWall w) => w.Path.Length > 1))
		{
			Pen pen = new Pen(WallBrush, Math.Max(1.5, wall.Thickness * _scale)) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Miter };
			StreamGeometry g = new StreamGeometry();
			using (StreamGeometryContext ctx = g.Open())
			{
				ctx.BeginFigure(ToScreen(wall.Path[0]), isFilled: false, isClosed: false);
				ctx.PolyLineTo(wall.Path.Skip(1).Select(ToScreen).ToList(), isStroked: true, isSmoothJoin: false);
			}
			dc.DrawGeometry(null, pen, g);
		}
		foreach (PlanDoor door in _level.Doors)
		{
			DrawDoor(dc, door);
		}
		foreach (PlanRoom room in _level.Rooms.Where((PlanRoom r) => r.Problem != null && !r.Selected))
		{
			dc.DrawGeometry(null, ProblemPen, RoomGeometry(room));
		}
		foreach (PlanRoom room in _level.Rooms.Where((PlanRoom r) => r.Selected))
		{
			dc.DrawGeometry(null, SelectPen, RoomGeometry(room));
		}
		foreach (PlanFace f in _faces.Where((PlanFace f) => f.LevelId == _level.Id && (f.Allowed || f == _hoverFace)))
		{
			DrawFace(dc, f);
		}
		foreach (PlanMarker m in _markers.Where((PlanMarker m) => m.LevelId == _level.Id))
		{
			DrawMarker(dc, m);
		}
		foreach (PlanRoom room in _level.Rooms)
		{
			DrawLabel(dc, room, dpi);
		}
		if (_hoverMarker != null)
		{
			DrawTip(dc, _hoverMarker.Label, MarkerHint(_hoverMarker), dpi);
		}
		else if (_hoverFace != null)
		{
			DrawTip(dc, _hoverFace.Title, _hoverFace.WallType + (_hoverFace.Core ? ", core wall. " : ". ") + FaceHint(_hoverFace), dpi);
		}
	}

	// Puerta: abertura en blanco sobre el muro y la hoja abierta con su barrido
	private void DrawDoor(DrawingContext dc, PlanDoor door)
	{
		Vector along = door.Along;
		along.Normalize();
		Vector facing = door.Facing;
		if (facing.LengthSquared < 1E-09)
		{
			facing = new Vector(0.0 - along.Y, along.X);
		}
		facing.Normalize();
		Point c = door.Center;
		Point hinge = c - along * (door.Width / 2.0);
		Point latch = c + along * (door.Width / 2.0);
		Point open = hinge + facing * door.Width;
		dc.DrawLine(DoorGapPen(), ToScreen(hinge), ToScreen(latch));
		dc.DrawLine(DoorPen, ToScreen(hinge), ToScreen(open));
		Point h = ToScreen(hinge);
		Vector toOpen = ToScreen(open) - h;
		Vector toLatch = ToScreen(latch) - h;
		SweepDirection sweep = (Vector.CrossProduct(toOpen, toLatch) > 0.0) ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
		StreamGeometry swing = new StreamGeometry();
		using (StreamGeometryContext ctx = swing.Open())
		{
			ctx.BeginFigure(ToScreen(open), isFilled: false, isClosed: false);
			double r = door.Width * _scale;
			ctx.ArcTo(ToScreen(latch), new Size(r, r), 0.0, isLargeArc: false, sweep, isStroked: true, isSmoothJoin: false);
		}
		swing.Freeze();
		dc.DrawGeometry(null, DoorSwingPen, swing);
	}

	private static readonly Pen DoorPen = Freeze(new Pen(Theme.Danger, 1.6));

	private static readonly Pen DoorSwingPen = Freeze(new Pen(Theme.Danger, 1.0) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) });

	private Pen DoorGapPen()
	{
		return new Pen(Theme.Panel, Math.Max(3.0, 0.6 * _scale)) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
	}

	private RoomUse UseOf(long roomId)
	{
		return _level?.Rooms.FirstOrDefault((PlanRoom r) => r.Id == roomId)?.Use ?? RoomUse.None;
	}

	private static string MarkerHint(PlanMarker m)
	{
		if (m.Skipped)
		{
			return "Left out. Click to put it back.";
		}
		if (m.Use == RoomUse.Tv)
		{
			return m.CanMove ? "Click to move it to the next wall, or click a dashed wall." : "Only wall that fits";
		}
		return "Click to leave this one out";
	}

	private string FaceHint(PlanFace f)
	{
		if (!f.Allowed)
		{
			return (f.Reason ?? "Nothing goes here") + ".";
		}
		bool tv = UseOf(f.RoomId) == RoomUse.Tv;
		if (f.On && f.Problem != null)
		{
			return f.Problem + ". Click to take it off.";
		}
		if (f.On)
		{
			return tv ? "The TV is on this wall." : ((f.Suggested ? "Suggested. " : "") + "Artwork goes here. Click to take it off.");
		}
		return tv ? "Click to move the TV here." : "Click to put artwork here.";
	}

	private Point[] FaceLine(PlanFace f)
	{
		Vector n = f.Normal;
		n.Normalize();
		Vector nn = new Vector(n.X, -n.Y) * 3.5;
		return new[] { ToScreen(f.A) + nn, ToScreen(f.B) + nn };
	}

	private void DrawFace(DrawingContext dc, PlanFace f)
	{
		Point[] l = FaceLine(f);
		bool hot = f == _hoverFace;
		Pen pen;
		if (!f.Allowed)
		{
			pen = BlockedFacePen;
		}
		else if (f.On && f.Problem != null)
		{
			pen = new Pen(Theme.Danger, hot ? 4.5 : 3.0);
		}
		else if (f.On)
		{
			pen = new Pen((UseOf(f.RoomId) == RoomUse.Tv) ? Theme.Dark : Theme.Accent, hot ? 4.5 : 3.0);
		}
		else
		{
			pen = hot ? new Pen(Theme.Accent, 3.0) : OpenFacePen;
		}
		dc.DrawLine(pen, l[0], l[1]);
	}

	private void DrawMarker(DrawingContext dc, PlanMarker m)
	{
		bool tv = m.Use == RoomUse.Tv;
		double depth = tv ? 7.0 : 5.0;
		Vector along = m.Along;
		along.Normalize();
		Vector n = m.Normal;
		n.Normalize();
		Point c = ToScreen(m.Center);
		Vector a = new Vector(along.X, -along.Y) * Math.Max(6.0, m.Width * _scale) / 2.0;
		Vector nn = new Vector(n.X, -n.Y);
		Point p0 = c - a + nn * 1.5;
		Point p1 = c + a + nn * 1.5;
		StreamGeometry g = new StreamGeometry();
		using (StreamGeometryContext ctx = g.Open())
		{
			ctx.BeginFigure(p0, isFilled: true, isClosed: true);
			ctx.PolyLineTo(new[] { p1, p1 + nn * depth, p0 + nn * depth }, isStroked: true, isSmoothJoin: false);
		}
		g.Freeze();
		bool hot = m == _hoverMarker;
		if (m.Skipped)
		{
			dc.DrawGeometry(Theme.Panel, SkippedPen, g);
			return;
		}
		Brush fill = tv ? Theme.Dark : (hot ? Theme.AccentStrong : Theme.Accent);
		dc.DrawGeometry(fill, hot ? new Pen(Theme.Panel, 1.0) : null, g);
		if (tv)
		{
			Point mid = c + nn * (depth + 7.0);
			dc.DrawEllipse(Theme.Dark, null, mid, 2.2, 2.2);
		}
	}

	private void DrawLabel(DrawingContext dc, PlanRoom room, double dpi)
	{
		Point c = ToScreen(room.Label);
		List<Point> pts = room.Loops.FirstOrDefault()?.Select(ToScreen).ToList();
		double width = (pts == null || pts.Count == 0) ? 0.0 : (pts.Max((Point p) => p.X) - pts.Min((Point p) => p.X));
		FormattedText name = Text(room.Name, Medium, 12.0, Theme.Ink, dpi);
		FormattedText sub = Text(room.Number, Regular, 11.0, Theme.Muted, dpi);
		if (width < 28.0)
		{
			return;
		}
		if (name.Width > width - 8.0)
		{
			name.MaxTextWidth = Math.Max(10.0, width - 8.0);
			name.MaxLineCount = 1;
			name.Trimming = TextTrimming.CharacterEllipsis;
		}
		string badge = (room.Use == RoomUse.Tv) ? "TV" : ((room.Use == RoomUse.Artwork) ? "Artwork" : null);
		FormattedText chip = (badge != null) ? Text(badge, Medium, 10.5, (room.Use == RoomUse.Tv) ? Brushes.White : Brushes.White, dpi) : null;
		double h = name.Height + ((sub.Text.Length > 0) ? sub.Height : 0.0) + ((chip != null) ? (chip.Height + 6.0) : 0.0);
		double y = c.Y - h / 2.0;
		dc.DrawText(name, new Point(c.X - name.WidthIncludingTrailingWhitespace / 2.0, y));
		y += name.Height;
		if (sub.Text.Length > 0)
		{
			dc.DrawText(sub, new Point(c.X - sub.Width / 2.0, y));
			y += sub.Height;
		}
		if (chip != null)
		{
			Rect r = new Rect(c.X - chip.Width / 2.0 - 6.0, y + 3.0, chip.Width + 12.0, chip.Height + 2.0);
			dc.DrawRoundedRectangle((room.Use == RoomUse.Tv) ? Theme.Dark : Theme.Accent, null, r, 8.0, 8.0);
			dc.DrawText(chip, new Point(r.X + 6.0, r.Y + 1.0));
		}
		if (room.Problem != null && width >= 110.0)
		{
			FormattedText warn = Text(room.Problem, Regular, 10.5, Theme.Danger, dpi);
			warn.MaxTextWidth = Math.Max(40.0, width - 10.0);
			warn.TextAlignment = TextAlignment.Center;
			warn.MaxLineCount = 2;
			warn.Trimming = TextTrimming.CharacterEllipsis;
			dc.DrawText(warn, new Point(c.X - warn.MaxTextWidth / 2.0, y + ((chip != null) ? (chip.Height + 8.0) : 3.0)));
		}
	}

	private void DrawTip(DrawingContext dc, string label, string hint, double dpi)
	{
		FormattedText title = Text(label, Medium, 12.0, Theme.Ink, dpi);
		FormattedText sub = Text(hint, Regular, 11.0, Theme.Muted, dpi);
		double w = Math.Max(title.Width, sub.Width) + 20.0;
		double h = title.Height + sub.Height + 14.0;
		double x = Math.Min(_mouse.X + 14.0, ActualWidth - w - 6.0);
		double y = Math.Min(_mouse.Y + 16.0, ActualHeight - h - 6.0);
		Rect r = new Rect(x, y, w, h);
		dc.DrawRoundedRectangle(Freeze(new SolidColorBrush(Color.FromArgb(24, 16, 24, 40))), null, new Rect(x, y + 2.0, w, h), 8.0, 8.0);
		dc.DrawRoundedRectangle(Theme.Panel, new Pen(Theme.Line, 1.0), r, 8.0, 8.0);
		dc.DrawText(title, new Point(x + 10.0, y + 7.0));
		dc.DrawText(sub, new Point(x + 10.0, y + 7.0 + title.Height));
	}

	private static FormattedText Text(string s, Typeface face, double size, Brush brush, double dpi)
	{
		return new FormattedText(s ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, dpi);
	}

	private PlanRoom RoomAt(Point screen)
	{
		Point w = ToWorld(screen);
		return _level?.Rooms.LastOrDefault((PlanRoom r) => r.Loops.Count((Point[] l) => Inside(l, w)) % 2 == 1);
	}

	private PlanMarker MarkerAt(Point screen)
	{
		if (_level == null)
		{
			return null;
		}
		PlanMarker best = null;
		double bestD = 8.0;
		foreach (PlanMarker m in _markers.Where((PlanMarker m) => m.LevelId == _level.Id))
		{
			Vector along = m.Along;
			along.Normalize();
			Point c = ToScreen(m.Center);
			Vector a = new Vector(along.X, -along.Y) * Math.Max(6.0, m.Width * _scale) / 2.0;
			double d = DistanceToSegment(screen, c - a, c + a);
			if (d < bestD)
			{
				bestD = d;
				best = m;
			}
		}
		return best;
	}

	private static double DistanceToSegment(Point p, Point a, Point b)
	{
		Vector ab = b - a;
		double len = ab.LengthSquared;
		double t = (len < 1E-09) ? 0.0 : Math.Max(0.0, Math.Min(1.0, (p - a) * ab / len));
		return (p - (a + ab * t)).Length;
	}

	private static bool Inside(Point[] poly, Point p)
	{
		bool inside = false;
		for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
		{
			if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
			{
				inside = !inside;
			}
		}
		return inside;
	}

	protected override void OnRenderSizeChanged(SizeChangedInfo info)
	{
		base.OnRenderSizeChanged(info);
		if (!info.PreviousSize.IsEmpty && info.PreviousSize.Width > 0.0)
		{
			_offset += new Vector((info.NewSize.Width - info.PreviousSize.Width) / 2.0, (info.NewSize.Height - info.PreviousSize.Height) / 2.0);
		}
	}

	protected override void OnMouseWheel(MouseWheelEventArgs e)
	{
		Point s = e.GetPosition(this);
		Point w = ToWorld(s);
		_scale = Math.Max(0.5, Math.Min(400.0, _scale * ((e.Delta > 0) ? 1.15 : (1.0 / 1.15))));
		_offset = new Vector(s.X - w.X * _scale, s.Y + w.Y * _scale);
		InvalidateVisual();
		e.Handled = true;
	}

	protected override void OnMouseDown(MouseButtonEventArgs e)
	{
		Focus();
		_down = e.GetPosition(this);
		_last = _down.Value;
		_panning = e.ChangedButton != MouseButton.Left;
		CaptureMouse();
		e.Handled = true;
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		_mouse = e.GetPosition(this);
		if (_down.HasValue && IsMouseCaptured)
		{
			if (!_panning && (_mouse - _down.Value).Length > 4.0)
			{
				_panning = true;
			}
			if (_panning)
			{
				_offset += _mouse - _last;
				_last = _mouse;
				Cursor = Cursors.SizeAll;
				InvalidateVisual();
				return;
			}
		}
		PlanMarker marker = MarkerAt(_mouse);
		PlanFace face = (marker == null) ? FaceAt(_mouse) : null;
		PlanRoom room = (marker == null && face == null) ? RoomAt(_mouse) : null;
		if (marker != _hoverMarker || face != _hoverFace || room != _hoverRoom || marker != null || face != null)
		{
			_hoverMarker = marker;
			_hoverFace = face;
			_hoverRoom = room;
			Cursor = (marker != null || (face != null && face.Allowed) || room != null) ? Cursors.Hand : Cursors.Arrow;
			InvalidateVisual();
		}
	}

	private PlanFace FaceAt(Point screen)
	{
		if (_level == null)
		{
			return null;
		}
		// Las caras bloqueadas también responden al mouse, para mostrar por qué no se pueden usar
		return (from f in _faces
			where f.LevelId == _level.Id
			let l = FaceLine(f)
			let d = DistanceToSegment(screen, l[0], l[1])
			where d < 6.0
			orderby f.Allowed descending, d
			select f).FirstOrDefault();
	}

	protected override void OnMouseUp(MouseButtonEventArgs e)
	{
		bool click = _down.HasValue && !_panning;
		_down = null;
		_panning = false;
		ReleaseMouseCapture();
		Cursor = Cursors.Arrow;
		if (!click || e.ChangedButton != MouseButton.Left)
		{
			return;
		}
		Point p = e.GetPosition(this);
		bool add = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
		PlanMarker marker = MarkerAt(p);
		if (marker != null)
		{
			MarkerClicked?.Invoke(marker);
			return;
		}
		PlanFace face = FaceAt(p);
		if (face != null)
		{
			FaceClicked?.Invoke(face);
			return;
		}
		PlanRoom room = RoomAt(p);
		if (room != null)
		{
			RoomClicked?.Invoke(room, add);
		}
		else
		{
			BackgroundClicked?.Invoke();
		}
	}

	protected override void OnMouseLeave(MouseEventArgs e)
	{
		_hoverMarker = null;
		_hoverFace = null;
		_hoverRoom = null;
		InvalidateVisual();
	}
}
