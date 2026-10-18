using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ArtworkPlacer;

[Transaction(TransactionMode.Manual)]
public class StudioCommand : IExternalCommand
{
	private static readonly StudioSettings _settings = new StudioSettings();

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIDocument uidoc = commandData.Application.ActiveUIDocument;
		if (uidoc == null || uidoc.Document.IsFamilyDocument)
		{
			TaskDialog.Show("Room Studio", "Open a project (not a family) to use Room Studio.");
			return Result.Cancelled;
		}
		// El JSON del diseño se recuerda por proyecto mientras Revit esté abierto
		string docKey = string.IsNullOrEmpty(uidoc.Document.PathName) ? uidoc.Document.Title : uidoc.Document.PathName;
		DesignLayout layout = null;
		if (_settings.Layouts.TryGetValue(docKey, out var remembered) && File.Exists(remembered))
		{
			layout = TryRead(remembered);
		}
		// Cargar o quitar el JSON vuelve a armar el plano: se reabre la ventana con los rooms nuevos
		while (true)
		{
			RevitStudioHost host = new RevitStudioHost(uidoc, commandData.Application.Application.VersionNumber, layout);
			if (host.Levels.Count == 0)
			{
				TaskDialog.Show("Room Studio", "This model has no walls or rooms to plan on.");
				return Result.Cancelled;
			}
			_settings.HeightText = _settings.HeightText ?? host.Cm(150.0);
			_settings.ClearText = _settings.ClearText ?? host.Cm(30.0);
			_settings.GapText = _settings.GapText ?? host.Cm(60.0);
			_settings.TvBottomText = _settings.TvBottomText ?? host.Cm(100.0);
			StudioWindow window = new StudioWindow(host, _settings);
			// Ventana hija de Revit: flota delante del modelo y se centra sobre él
			new System.Windows.Interop.WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
			window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
			bool? result = window.ShowDialog();
			if (window.RequestedLayout != null)
			{
				DesignLayout read = TryRead(window.RequestedLayout);
				if (read != null)
				{
					layout = read;
					_settings.Layouts[docKey] = window.RequestedLayout;
				}
				continue;
			}
			if (window.RequestedRevitRooms)
			{
				layout = null;
				_settings.Layouts.Remove(docKey);
				continue;
			}
			if (result != true)
			{
				return Result.Cancelled;
			}
			host.Place(window.Skipped);
			return Result.Succeeded;
		}
	}

	private static DesignLayout TryRead(string path)
	{
		try
		{
			return DesignLayout.Read(path);
		}
		catch (Exception ex)
		{
			TaskDialog.Show("Room Studio", "Couldn't read " + Path.GetFileName(path) + ": " + ex.Message);
			return null;
		}
	}
}
