# PDF-Druckweg

Zusätzlich zum RAW-Weg kann ein Client-Drucker PDF-Dokumente an den Server übertragen. Der alte Weg bleibt unverändert und ist der Standard.

## Ablauf

1. Auf dem Client wird die Warteschlange eines Tunnel-Druckers auf den Windows-Treiber „Microsoft Print to PDF“ umgestellt (Oberfläche: Reiter *Drucker*, Rechtsklick auf den Drucker, *Als PDF-Drucker nutzen*). Der Tunnel (127.0.0.1:19xxx) bleibt, nur der Inhalt ist eine PDF.
2. Der Server prüft die ersten Bytes. Beginnen sie mit `%PDF-`, wird der PDF-Weg genutzt, sonst läuft alles wie bisher als RAW (`RawPrinter.SendStreamAsync`).
3. Die PDF wird unter `C:\ProgramData\SimplePrint\Device\Pdfs\<Job-ID>.pdf` gespeichert. Den Inhalt können nur SYSTEM und Administratoren lesen, normale Benutzer sehen nur die Dateinamen.
4. `PdfPrinter` rendert die Seiten mit `Windows.Data.Pdf` (300 dpi) und druckt sie über die Windows-Warteschlange des Servers. Der Treiber des Servers und die dortigen Druckeinstellungen (Duplex, Farbe, Qualität) bestimmen das Ergebnis. Der Client nimmt keine Druckeinstellungen vor.
5. Die Job-ID des Spoolers wird über den Dokumentnamen ermittelt, damit die vorhandene Statusanzeige weiter funktioniert. Wird der Auftrag so schnell gedruckt, dass er nicht mehr gefunden wird, steht er als abgeschlossen in der Liste.

## Oberfläche

- **Reiter Drucker:** Rechtsklick auf einen Netzwerkdrucker, *Als PDF-Drucker nutzen* (Haken). Das Umstellen braucht eine Administratorfreigabe. Umgestellte Drucker tragen den Hinweis „[PDF-Modus]“. Nur Tunnel-Drucker, die SimplePrint selbst angelegt hat, lassen sich umstellen. Ein erneutes Einrichten des Druckers setzt den normalen Modus wieder.
- **Reiter Druckaufträge:** Spalte *PDF* und Rechtsklick *PDF öffnen* bei eingehenden Aufträgen, deren PDF noch gespeichert ist. Zum Öffnen wird nach einer Administratorfreigabe eine Kopie im Temp-Ordner des Benutzers angelegt.
- **Reiter Einstellungen:** *PDFs aufbewahren für … Tage*.

## Aufbewahrung

`PdfRetentionDays` in der Gerätekonfiguration (Standard 30 Tage). Abgelaufene PDFs werden höchstens einmal pro Stunde beim nächsten PDF-Auftrag gelöscht, bei 0 Tagen direkt nach dem Druck. Defekte oder nicht druckbare Dateien werden nicht aufbewahrt.

## Grenzen

- Höchstens 100 MB und 500 Seiten pro Auftrag, keine passwortgeschützten PDFs.
- Die Seiten werden als Bilder gedruckt, es gibt keine Vektorausgabe.
- Ungetestet auf echter Hardware: Ob „Microsoft Print to PDF“ an einem TCP-Port die PDF-Bytes unverändert sendet, muss der erste Test zeigen.

## Manuell umstellen (ohne Oberfläche)

```powershell
Set-Printer -Name "<Druckername>" -DriverName "Microsoft Print To PDF"
```

Rückgängig machen mit dem ursprünglichen Treibernamen aus `Get-PrinterDriver`.
