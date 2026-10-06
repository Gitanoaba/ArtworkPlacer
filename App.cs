using Autodesk.Revit.UI;
using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace ArtworkPlacer
{
    // ─── RIBBON PANEL (se carga al arrancar Revit) ───────────────────────────
    public class App : IExternalApplication
    {
        private const string TabName = "Qbiq Tools";

        public Result OnStartup(UIControlledApplication app)
        {
            // La pestaña puede existir ya si otro add-in nuestro la creó
            try { app.CreateRibbonTab(TabName); } catch (Exception) { }

            RibbonPanel panel = app.CreateRibbonPanel(TabName, "Artwork");

            PushButtonData buttonData = new PushButtonData(
                "PlaceArtwork",
                "Place\nArtwork",
                typeof(App).Assembly.Location,
                "ArtworkPlacer.PlaceArtworkCommand"
            );

            buttonData.ToolTip = "Automatically places an artwork piece centered on the longest free segment of each selected wall.";
            buttonData.LongDescription =
                "1. Select the walls (or click the button and pick them).\n" +
                "2. Choose the artwork types: they rotate from wall to wall.\n" +
                "3. Check the arrows and click any wall to switch the face it goes on.";

            // Cargar ícono desde la misma carpeta que la DLL
            string iconPath = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location), "ArtworkPlacer.png");
            if (File.Exists(iconPath))
            {
                BitmapImage icon = new BitmapImage(new Uri(iconPath));
                buttonData.LargeImage = icon;
                buttonData.Image = icon;
            }

            panel.AddItem(buttonData);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }
}
