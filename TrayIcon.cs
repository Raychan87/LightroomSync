using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Diagnostics;

namespace LightroomSync
{
    public class TrayIcon : ApplicationContext
    {
        // Config
        private const int WATCHDOG_TIME = 30000; // Sekunden
        private const int DIFF_SEC = 5; // Sekunden

        // Variablen
        private NotifyIcon trayIcon;
        private System.Windows.Forms.Timer timer;
        private AppConfig config;
        private string rcloneConfigPath;

        private Icon iconGreen;
        private Icon iconRed;
        private Icon iconBlue;
        private Icon iconYellow;
        private Icon iconWhite;

        private string status = "Standby";
//        private bool isSyncing = false;
        private bool lockWarDa = false;

        public TrayIcon()
        {
            // ================= INITIALISIERUNG =================
            // 1. Basis-Verzeichnis holen (wo die .exe liegt)
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // Logger starten
            Log.Initialize(baseDir);
            Log.Info("=== Lightroom Watcher gestartet ===");

            // rclone.conf
            string rcloneConfigPath = Path.Combine(baseDir, "rclone.conf");
            if (!File.Exists(rcloneConfigPath))
            {
                string appdataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string defaultConfig = Path.Combine(appdataPath, "rclone", "rclone.conf");
                if (File.Exists(defaultConfig))
                {
                    File.Copy(defaultConfig, rcloneConfigPath);
                }
            }
            this.rcloneConfigPath = rcloneConfigPath;

            // Config laden
            string configPath = Path.Combine(baseDir, "config.txt");
            config = AppConfig.LoadFromFile(configPath, baseDir);
            if (!File.Exists(configPath))
            {
                config.Save(configPath);
            }

            // Icons
            iconGreen = CreateColoredIcon(Color.Green);
            iconRed = CreateColoredIcon(Color.Red);
            iconBlue = CreateColoredIcon(Color.DodgerBlue);
            iconYellow = CreateColoredIcon(Color.Orange);
            iconWhite = CreateColoredIcon(Color.White);

            // Tray-Icon
            trayIcon = new NotifyIcon()
            {
                Icon = iconGreen,
                Text = "Lightroom Sync - Start...",
                Visible = true
            };

            // Context-Menü
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem statusItem = new ToolStripMenuItem("Status: Start...");
            statusItem.Name = "statusItem";
            statusItem.Enabled = false;
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Beenden");
            exitItem.Click += (s, e) => {
                Log.Info("Beendet durch Benutzer");
                trayIcon.Visible = false;
                Application.Exit();
            };
            menu.Items.Add(exitItem);
            trayIcon.ContextMenuStrip = menu;

            // Timer
            timer = new System.Windows.Forms.Timer();
            timer.Interval = config.CheckInterval * 1000;
            timer.Tick += Timer_Tick;
            timer.Start();

            CheckStatus();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            CheckStatus();
        }

        private void CheckStatus()
        {
            try
            {
                bool CatalogSyncStart = false;
                string SyncDirection = "";

                // ================= 1. LOCK PRÜFEN =================
                if (Directory.Exists(config.LocalPath))
                {
                    string[] lockFiles = Directory.GetFiles(config.LocalPath, "*.lrcat.lock", SearchOption.AllDirectories);
                    if (lockFiles.Length > 0)
                    {
                        if (!lockWarDa)
                        {
                            Log.Info("Lock erkannt");
                            lockWarDa = true;
                        }
                        SetStatus("Lock");
                        return;
                    }
                }

                if (lockWarDa)
                {
                    Log.Info("Lock entfernt - Sync wird fortgesetzt");
                    lockWarDa = false;
                }

                // ================= 2. BEREITS SYNCING? =================
           //     if (isSyncing)
           //     {
           //         SetStatus("Syncing");
           //         return;
           //     }

                // ================= 3. NAS PRÜFEN =================
                string remoteFull = config.RemoteName;
                if (!string.IsNullOrEmpty(config.RemotePath))
                    remoteFull += ":" + config.RemotePath;

                if (!File.Exists(config.RclonePath) || !File.Exists(rcloneConfigPath))
                {
                    Log.Warn("Keine NAS-Verbindung");
                    SetStatus("NoConnection");
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = config.RclonePath;
                psi.Arguments = $"--config \"{rcloneConfigPath}\" lsd \"{remoteFull}\"";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;

                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(WATCHDOG_TIME);
                    if (p.ExitCode != 0 || output.Length < 10)
                    {
                        Log.Warn("NAS-Verbindung fehlgeschlagen");
                        SetStatus("NoConnection");
                        return;
                    }
                }

                // ================= 4. KATALOG PRÜFEN =================
                DateTime? localDate = GetLocalCatalogDate();
                DateTime? remoteDate = GetRemoteCatalogDate();

                // Änderungszeit von den Katalogen
                Log.Debug($"Check: Lokal={Log.FormatDateTime(localDate)} | NAS={Log.FormatDateTime(remoteDate)}");                

                // Wenn lokal ein Katalog existiert, dann...
                if (localDate != null)
                {
                    // Wenn Remote kein Katalog exestiert, dann
                    if (remoteDate == null)
                    {
                        Log.Info("Kein Remote-Katalog - Upload");
                        CatalogSyncStart = true;
                        SyncDirection = "upload";
                    }
                    // Wenn beide Kataloge exestieren, dann...
                    else
                    {
                        // Berechne die Differenz der Änderungszeiten in Sekunden
                        double diff = Math.Round(((DateTime)localDate - (DateTime)remoteDate).TotalSeconds, 0);

                        if (diff > DIFF_SEC)
                        {
                            Log.Info("PC neuer -> Upload");
                            CatalogSyncStart = true;
                            SyncDirection = "upload";
                        }
                        else if (diff < -DIFF_SEC)
                        {
                            Log.Info("NAS neuer -> Download");
                            CatalogSyncStart = true;
                            SyncDirection = "download";
                        }
                    }
                }
                //Wenn lokal kein Katalog existiert, dann...
                else
                {
                    // Wenn Remote ein Katalog exestiert, dann...
                    if (remoteDate != null)
                    {
                        Log.Info("Kein Lokaler Katalog - Download");
                        CatalogSyncStart = true;
                        SyncDirection = "download";
                    }
                    else
                    {
                        Log.Error("Katalog Sync Start");
                        SetStatus("Error");
                    }
                }

                // ================= 5. SYNC KATALOG =================
                if (CatalogSyncStart)
                {
                    Log.Debug("Katalog Sync Start");
            //        isSyncing = true;
                    SetStatus("Syncing");

                    // Katalog sync
                    if (localDate != null && (SyncDirection == "upload" || SyncDirection == "download"))
                    {
                        SyncCatalog(SyncDirection);
                    }

            //        isSyncing = false;
                    CatalogSyncStart = false;
                    Log.Debug("Katalog Sync End");
                    SetStatus("Standby");
                }

                // ================= 5.SYNC BACKUPS =================
                // Wird geprüft ob Backups synchronisiert werden muss.
                
                Log.Debug("Check Backups Start"); 

                if (CheckBackup())
                {
                    Log.Debug("Check Backups End");
                    SetStatus("Syncing");
                    Log.Debug("Backup Sync Start");
                    SyncBackups();
                    Log.Debug("Backup Sync End");
                }
                Log.Info("Sync loop End");
                SetStatus("Standby");

                // ================= 7. Wenn ein Fehler passiert,... =================
            }
            catch (Exception ex)
            {
                Log.Error($"Fehler: {ex.Message}");
                SetStatus("NoConnection");
            }
        }

        private void SyncCatalog(string direction)
        {
         //   isSyncing = true;
            SetStatus("Syncing");

            try
            {
                string remoteFull = config.RemoteName;
                if (!string.IsNullOrEmpty(config.RemotePath))
                    remoteFull += ":" + config.RemotePath;

                string tempLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rclone_temp.log");

                // .lrcat kopieren
                string[] lrcatFiles = Directory.GetFiles(config.LocalPath, "*.lrcat", SearchOption.TopDirectoryOnly);

                if (lrcatFiles.Length > 0)
                {
                    string lrcatFile = lrcatFiles[0];

                    ProcessStartInfo psiCopy = new ProcessStartInfo();
                    psiCopy.FileName = config.RclonePath;

                    if (direction == "upload")
                    {
                        psiCopy.Arguments = $"--config \"{rcloneConfigPath}\" copy \"{lrcatFile}\" {remoteFull} --update --metadata --log-file \"{tempLog}\" --log-level INFO";
                    }
                    else
                    {
                        psiCopy.Arguments = $"--config \"{rcloneConfigPath}\" copy {remoteFull} \"{config.LocalPath}\" --include \"*.lrcat\" --update --metadata --log-file \"{tempLog}\" --log-level INFO";
                    }

                    psiCopy.UseShellExecute = false;
                    psiCopy.RedirectStandardOutput = true;
                    psiCopy.RedirectStandardError = true;
                    psiCopy.CreateNoWindow = true;

                    Log.Info($"Kopiere *.lrcat: {direction}");
                    using (Process p = Process.Start(psiCopy))
                    {
                        p.WaitForExit(60000);
                    }
                    WriteRcloneStats(tempLog);
                }

                // Rest synchronisieren
                string excludeBackups = "--exclude \"Backups/**\"";

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = config.RclonePath;

                if (direction == "upload")
                {
                    psi.Arguments = $"--config \"{rcloneConfigPath}\" sync \"{config.LocalPath}\" {remoteFull} --size-only --update --metadata --log-file \"{tempLog}\" --log-level INFO";
                }
                else
                {
                    psi.Arguments = $"--config \"{rcloneConfigPath}\" sync {remoteFull} \"{config.LocalPath}\" --size-only --update --metadata --log-file \"{tempLog}\" --log-level INFO";
                }

                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;

                Log.Info($"SYNC: {direction} / full");
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(120000);
                }

                WriteRcloneStats(tempLog);
                Log.Info("Fertig");
            }
            catch (Exception ex)
            {
                Log.Error($"Sync-Fehler: {ex.Message}");
            }
            finally
            {
                isSyncing = false;
            }
        }

        // Prüft mit einem rclone bisync Dry-Run ob Änderungen in den Backups vorliegen, die synchronisiert werden müssten
        private bool CheckBackup()
        {
            try
            {
                // 1. Remote-Pfad zusammenbauen
                // Erstellt den kompletten Remote-Pfad, z.B. synology:Lightroom
                string remoteFull = config.RemoteName;
                if (!string.IsNullOrEmpty(config.RemotePath))
                    remoteFull += ":" + config.RemotePath; //synology:Lightroom

                // Dynamischer lokaler Backups-Pfad
                string includeBackups = $"--include \"{config.BackupsRelativePath}/**\" --include \"{config.BackupsRelativePath}/*/**\" --exclude \"*\"";

                //Debug Adresspfad
                Log.Debug($"Backup Check: {config.LocalPath}/{config.BackupsRelativePath} <-> {remoteFull}/{config.BackupsRelativePath}");

                // Temporäre Log-Datei für rclone-Ausgabe im Programm-Ordner
                string tempLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rclone_temp.log");

                // DRY-RUN: Prüfen ob Änderungen vorhanden sind
                ProcessStartInfo psi = new ProcessStartInfo(); //Startet einen neuen Prozess (rclone) unsichtbar im Hintergrund
                psi.FileName = config.RclonePath;
                psi.Arguments = $"--config \"{rcloneConfigPath}\" bisync \"{config.LocalPath}\" {remoteFull} --compare modtime,size --metadata --log-file \"{tempLog}\" --log-level INFO --dry-run {includeBackups}";
                psi.UseShellExecute = false; //Direkter Prozessstart
                psi.RedirectStandardOutput = true; //Ausgabe abfangen
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;  //Kein Fenster öffnen

                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(WATCHDOG_TIME);
                }

                // Log-Datei auf Transfers prüfen
                if (File.Exists(tempLog))
                {
                    string[] lines = File.ReadAllLines(tempLog);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if ((trimmed.Contains("Transferred:") && !trimmed.Contains("0 B / 0 B")) ||
                            trimmed.Contains("Copied") ||
                            trimmed.Contains("Deleted:"))
                        {
                            Log.Info("Backups: Änderungen erkannt (Dry-Run)");
                            return true;
                        }
                    }
                }

                Log.Info("Backups: Keine Änderungen erkannt");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"Backups-Check-Fehler: {ex.Message}");
                return false;
            }
        }        

        private void SyncBackups()
        {
            try
            {
                // 1. Remote-Pfad zusammenbauen
                //Erstellt den kompletten Remote-Pfad, z.B. synology:Lightroom
                string remoteFull = config.RemoteName;
                if (!string.IsNullOrEmpty(config.RemotePath))
                    remoteFull += ":" + config.RemotePath; //synology:Lightroom

                // Dynamischer lokaler Backups-Pfad
                string includeBackups = $"--include \"{config.BackupsRelativePath}/**\" --include \"{config.BackupsRelativePath}/*/**\" --exclude \"*\"";

                //Debug Adresspfad
                Log.Debug($"Backup Sync: {config.LocalPath}/{config.BackupsRelativePath} <-> {remoteFull}/{config.BackupsRelativePath}");

                //Temporäre Log-Datei für rclone-Ausgabe im Programm-Ordner
                string tempLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rclone_temp.log");

                ProcessStartInfo psi = new ProcessStartInfo(); //Startet einen neuen Prozess (rclone) unsichtbar im Hintergrund
                psi.FileName = config.RclonePath;
                psi.Arguments = $"--config \"{rcloneConfigPath}\" bisync \"{config.LocalPath}\" {remoteFull} --compare modtime,size --metadata --log-file \"{tempLog}\" --log-level INFO {includeBackups}";
                psi.UseShellExecute = false; //Direkter Prozessstart
                psi.RedirectStandardOutput = true; //Ausgabe abfangen
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;  //Kein Fenster öffnen

                //Prozess starten und maximal 60 Sekunden warten (Timeout)
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(WATCHDOG_TIME);
                }

                //Die Log-Datei auswerten und relevante Zeilen ins Log schreiben
                WriteRcloneStats(tempLog);
            }
            catch (Exception ex)
            {
                Log.Error($"Backups-Fehler: {ex.Message}");
            }
        }

        private void WriteRcloneStats(string logFile)
        {
            try
            {
                if (!File.Exists(logFile)) return;

                string[] lines = File.ReadAllLines(logFile);

                foreach (string line in lines)
                {
                    // Suche nach allen relevanten Zeilen
                    string trimmed = line.Trim();

                    // Copied ODER Deleted ODER Transferred (wenn nicht 0) ODER Elapsed
                    if (trimmed.Contains("Copied") ||
                        trimmed.Contains("Deleted") ||
                        (trimmed.Contains("Transferred:") && !trimmed.Contains("0 B / 0 B")) ||
                        trimmed.Contains("Elapsed time:"))
                    {
                        // Prüfe ob es eine relevante Datei ist
                        if (trimmed.Contains(".lrcat") ||
                            trimmed.Contains("Backups/") ||
                            trimmed.Contains("Lightroom-") ||
                            trimmed.Contains("Options") ||
                            trimmed.Contains("Manifest") ||
                            trimmed.Contains("Database") ||
                            trimmed.Contains("Deleted:") ||
                            trimmed.Contains("Transferred:") ||
                            trimmed.Contains("Elapsed time:"))
                        {
                            Log.Info("rclone: " + trimmed);
                        }
                    }
                }

                //Löscht das Logfile
               File.Delete(logFile);
            }
            catch { }
        }       

        private DateTime? GetLocalCatalogDate()
        {
            try
            {
                // Sucht im lokalen Verzeichnis nach .lrcat Dateien und gibt die letzte Änderungszeit der neuesten zurück
                string[] files = Directory.GetFiles(config.LocalPath, "*.lrcat", SearchOption.TopDirectoryOnly);

                // Wenn keine .lrcat Datei gefunden → gib null zurück
                if (files.Length == 0) return null;

                // Finde die neueste Datei 
                FileInfo newest = null;
                foreach (string file in files)
                {
                    FileInfo fi = new FileInfo(file);
                    if (newest == null || fi.LastWriteTime > newest.LastWriteTime)
                        newest = fi;
                }
                return newest?.LastWriteTime;
            }
            catch
            {
                return null;
            }
        }

        private DateTime? GetRemoteCatalogDate()
        {
            try
            {
                string remoteFull = config.RemoteName;
                if (!string.IsNullOrEmpty(config.RemotePath))
                    remoteFull += ":" + config.RemotePath;

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = config.RclonePath;
                psi.Arguments = $"--config \"{rcloneConfigPath}\" lsl {remoteFull} --include \"*.lrcat\"";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;

                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);

                    if (string.IsNullOrEmpty(output)) return null;

                    string[] lines = output.Split('\n');
                    DateTime? newestDate = null;

                    foreach (string line in lines)
                    {
                        // Nur Hauptverzeichnis (kein /)
                        if (line.Contains(".lrcat") && !line.Contains("/") && line.Contains("2026"))
                        {
                            int idx = line.IndexOf("2026");
                            if (idx > 0 && idx + 19 <= line.Length)
                            {
                                string datePart = line.Substring(idx, 19);
                                try
                                {
                                    DateTime dt = DateTime.ParseExact(datePart, "yyyy-MM-dd HH:mm:ss", null);
                                    if (newestDate == null || dt > newestDate)
                                        newestDate = dt;
                                }
                                catch { }
                            }
                        }
                    }
                    return newestDate;
                }
            }
            catch
            {
                return null;
            }
        }

        public void SetStatus(string newStatus)
        {
            if (status == newStatus) return;
            status = newStatus;

            switch (status)
            {
                case "Standby":
                    trayIcon.Icon = iconGreen;
                    trayIcon.Text = "Lightroom Sync - Standby";
                    break;
                case "NoConnection":
                    trayIcon.Icon = iconWhite;
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
                case "Error":
                    trayIcon.Icon = iconRed;
                    trayIcon.Text = "Lightroom Sync - Kein Katalog gefunden!";
                    break;
            }

            if (trayIcon.ContextMenuStrip != null)
                ((ToolStripMenuItem)trayIcon.ContextMenuStrip.Items["statusItem"]).Text = "Status: " + status;
        }

        private Icon CreateColoredIcon(Color color)
        {
            Bitmap bitmap = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                using (Brush brush = new SolidBrush(color))
                    g.FillEllipse(brush, 2, 2, 28, 28);
                using (Pen pen = new Pen(Color.White, 2))
                    g.DrawEllipse(pen, 2, 2, 28, 28);
            }
            return Icon.FromHandle(bitmap.GetHicon());
        }
    }
}