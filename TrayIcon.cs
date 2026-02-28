using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Diagnostics;

namespace LightroomSync
{
    // Hauptklasse des Programms.
    // Erstellt das Tray-Icon und verwaltet den Status.
    public class TrayIcon : ApplicationContext
    {
        // ==================== EIGENSCHAFTEN ====================

        private NotifyIcon trayIcon;       // Das Icon in der Taskleiste
        private System.Windows.Forms.Timer timer;  // Zeitgeber für regelmäßige Checks
        private AppConfig config;         // Unsere Konfiguration

        // Die vier Status-Icons
        private Icon iconGreen;   // Standby (bereit)
        private Icon iconRed;     // Keine Verbindung
        private Icon iconBlue;   // Lock erkannt (Lightroom aktiv)
        private Icon iconYellow; // Synchronisiere gerade

        private string status = "Standby";  // Aktueller Status

        // ==================== KONSTRUKTOR ====================

        // Wird beim Start des Programms aufgerufen.
        // Initialisiert alles: Icons, Config, Timer
        public TrayIcon()
        {
            // 1. Basis-Verzeichnis holen (wo die .exe liegt)
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 2. rclone.conf in den Programm-Ordner kopieren falls nicht vorhanden
            string rcloneConfigPath = Path.Combine(baseDir, "rclone.conf");
            if (!File.Exists(rcloneConfigPath))
            {
                // Versuche Standard-Ort zu finden
                string appdataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string defaultConfig = Path.Combine(appdataPath, "rclone", "rclone.conf");

                // Wenn Standard-Config existiert, kopiere sie
                if (File.Exists(defaultConfig))
                {
                    File.Copy(defaultConfig, rcloneConfigPath);
                }
            }

            // 3. Config laden
            string configPath = Path.Combine(baseDir, "config.txt");
            config = AppConfig.LoadFromFile(configPath, baseDir);

            // 4. Wenn keine Config existiert, erstelle Standard-Werte
            if (!File.Exists(configPath))
            {
                config.Save(configPath);
            }

            // 5. Erstelle die vier farbigen Icons
            iconGreen = CreateColoredIcon(Color.Green);
            iconRed = CreateColoredIcon(Color.Red);
            iconBlue = CreateColoredIcon(Color.DodgerBlue);
            iconYellow = CreateColoredIcon(Color.Orange);

            // 6. Erstelle das Tray-Icon
            trayIcon = new NotifyIcon()
            {
                Icon = iconGreen,
                Text = "Lightroom Sync - Start...",
                Visible = true  // Sichtbar machen
            };

            // 7. Erstelle das Kontext-Menü (Rechtsklick)
            ContextMenuStrip menu = new ContextMenuStrip();

            // Status-Anzeige (kann nicht geklickt werden)
            ToolStripMenuItem statusItem = new ToolStripMenuItem("Status: Start...");
            statusItem.Name = "statusItem";
            statusItem.Enabled = false;  // Grau (nicht klickbar)
            menu.Items.Add(statusItem);

            menu.Items.Add(new ToolStripSeparator());  // Trennlinie

            // Beenden-Button
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Beenden");
            exitItem.Click += (s, e) => {  // Lambda: Was passiert beim Klick
                trayIcon.Visible = false;
                Application.Exit();
            };
            menu.Items.Add(exitItem);

            // Menü zum Icon hinzufügen
            trayIcon.ContextMenuStrip = menu;

            // 8. Timer einrichten (für regelmäßige Status-Checks)
            timer = new System.Windows.Forms.Timer();
            timer.Interval = config.CheckInterval * 1000;  // Sekunden → Millisekunden
            timer.Tick += Timer_Tick;  // Methode die aufgerufen wird
            timer.Start();  // Timer starten

            // 9. Ersten Check sofort machen
            CheckStatus();
        }

        // ==================== EVENT-HANDLER ====================

        // Wird vom Timer aufgerufen (alle 15 Sekunden)
        private void Timer_Tick(object sender, EventArgs e)
        {
            CheckStatus();
        }

        // ==================== HAUPT-LOGIK ====================

        // Prüft den aktuellen Status des Systems
        // Wird regelmäßig vom Timer aufgerufen
        private void CheckStatus()
        {
            try
            {
                // === SCHRITT 1: Lock-Datei prüfen ===
                // Wenn Lightroom offen ist, existiert eine .lrcat.lock Datei
                if (Directory.Exists(config.LocalPath))
                {
                    // Suche nach *.lrcat.lock Dateien
                    string[] lockFiles = Directory.GetFiles(
                        config.LocalPath,
                        "*.lrcat.lock",
                        SearchOption.AllDirectories  // Auch in Unterordnern suchen
                    );

                    // Wenn Lock-Datei gefunden
                    if (lockFiles.Length > 0)
                    {
                        SetStatus("Lock");  // Icon Blau
                        return;  // Fertig, nichts anderes prüfen
                    }
                }

                // === SCHRITT 2: Prüfe ob rclone läuft ===
                // Wenn gerade synchronisiert wird
                Process[] rcloneProcesses = Process.GetProcessesByName("rclone");
                if (rcloneProcesses.Length > 0)
                {
                    SetStatus("Syncing");  // Icon Gelb
                    return;
                }

                // === SCHRITT 3: NAS-Verbindung prüfen ===
                // Baue den Remote-Pfad (z.B. "synology:Lightroom")
                string remoteFull = config.RemoteName;
                if (!string.IsNullOrEmpty(config.RemotePath))
                {
                    remoteFull += ":" + config.RemotePath;
                }

                // Prüfe ob rclone.exe existiert
                if (!File.Exists(config.RclonePath))
                {
                    SetStatus("NoConnection");
                    return;
                }

                // Prüfe ob rclone.conf existiert
                string rcloneConfigPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "rclone.conf"
                );
                if (!File.Exists(rcloneConfigPath))
                {
                    SetStatus("NoConnection");
                    return;
                }

                // Starte rclone Prozess um NAS zu prüfen
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = config.RclonePath;
                // --config gibt den Pfad zur rclone.conf an
                // lsd list directories (verbindet nur, keine Änderung)
                psi.Arguments = $"--config \"{rcloneConfigPath}\" lsd \"{remoteFull}\"";

                // Prozess-Einstellungen für Hintergrund-Ausführung
                psi.UseShellExecute = false;  // Kein Fenster
                psi.RedirectStandardOutput = true;  // Ausgabe umleiten
                psi.RedirectStandardError = true;   // Fehler umleiten
                psi.CreateNoWindow = true;  // Kein DOS-Fenster

                // Starte den Prozess
                using (Process p = Process.Start(psi))
                {
                    // Lese die Ausgabe
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();

                    // Warte maximal 5 Sekunden auf Antwort
                    bool exited = p.WaitForExit(5000);

                    // Wenn Timeout oder Fehler
                    if (!exited)
                    {
                        p.Kill();  // Prozess abschießen
                        SetStatus("NoConnection");
                        return;
                    }

                    // Wenn ExitCode != 0 oder keine Ausgabe
                    if (p.ExitCode != 0 || output.Length < 10)
                    {
                        SetStatus("NoConnection");
                        return;
                    }
                }

                // === ALLES OK ===
                SetStatus("Standby");  // Icon Grün

            }
            catch
            {
                // Bei jedem Fehler: Verbindung verloren
                SetStatus("NoConnection");
            }
        }

        // ==================== HILFSMETHODEN ====================

        // Ändert den Status und das Icon
        // newStatus --> Neuer Status-Name
        public void SetStatus(string newStatus)
        {
            // Nur ändern wenn Status anders ist
            if (status == newStatus) return;

            status = newStatus;

            // Icon und Text je nach Status setzen
            switch (status)
            {
                case "Standby":
                    trayIcon.Icon = iconGreen;
                    trayIcon.Text = "Lightroom Sync - Bereit";
                    break;
                case "NoConnection":
                    trayIcon.Icon = iconRed;
                    trayIcon.Text = "Lightroom Sync - Keine Verbindung!";
                    break;
                case "Lock":
                    trayIcon.Icon = iconBlue;
                    trayIcon.Text = "Lightroom Sync - Lightroom aktiv";
                    break;
                case "Syncing":
                    trayIcon.Icon = iconYellow;
                    trayIcon.Text = "Lightroom Sync - Synchronisiere...";
                    break;
            }

            // Menü-Text aktualisieren
            if (trayIcon.ContextMenuStrip != null)
            {
                ((ToolStripMenuItem)trayIcon.ContextMenuStrip.Items["statusItem"]).Text = "Status: " + status;
            }
        }

        // Erstellt ein einfaches farbiges Kreis-Icon
        // color --> Die Farbe des Kreises
        // returns --> Das fertige Icon
        private Icon CreateColoredIcon(Color color)
        {
            // Erstelle 32x32 Pixel Bitmap (Bild)
            Bitmap bitmap = new Bitmap(32, 32);

            using (Graphics g = Graphics.FromImage(bitmap))  // Graphics zum Zeichnen
            {
                g.Clear(Color.Transparent);  // Hintergrund durchsichtig

                using (Brush brush = new SolidBrush(color))  // Farb-Pinsel
                {
                    // Zeichne gefüllten Kreis (Ellipse)
                    g.FillEllipse(brush, 2, 2, 28, 28);
                }

                using (Pen pen = new Pen(Color.White, 2))  // Weißer Rand
                {
                    g.DrawEllipse(pen, 2, 2, 28, 28);
                }
            }

            // Icon aus Bitmap erstellen und zurückgeben
            return Icon.FromHandle(bitmap.GetHicon());
        }
    }
}