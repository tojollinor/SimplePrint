# PDF-Druckweg

Zusätzlich zum RAW-Weg kann ein Client-Drucker PDF-Dokumente an den Server übertragen. Der alte Weg bleibt unverändert und ist der Standard.

## Ablauf

1. Die Warteschlange auf dem Client verwendet den Windows-Treiber „Microsoft Print to PDF“ auf dem vorhandenen SimplePrint-Port (127.0.0.1:19xxx). Der Tunnel bleibt wie er ist, nur der Inhalt ist eine PDF.
2. Der Server prüft die ersten Bytes. Beginnen sie mit `%PDF-`, wird der PDF-Weg genutzt, sonst läuft alles wie bisher als RAW (`RawPrinter.SendStreamAsync`).
3. Die PDF wird unter `C:\ProgramData\SimplePrint\Device\Pdfs\<Job-ID>.pdf` gespeichert. Der Ordner ist nur für SYSTEM und Administratoren zugänglich.
4. `PdfPrinter` rendert die Seiten mit `Windows.Data.Pdf` (300 dpi) und druckt sie über die Windows-Warteschlange des Servers. Der Treiber des Servers und die dortigen Druckeinstellungen (Duplex, Farbe, Qualität) bestimmen das Ergebnis. Der Client nimmt keine Druckeinstellungen vor.
5. Die Job-ID des Spoolers wird über den Dokumentnamen ermittelt, damit die vorhandene Statusanzeige weiter funktioniert. Wird der Auftrag so schnell gedruckt, dass er nicht mehr gefunden wird, steht er als abgeschlossen in der Liste.

## Aufbewahrung

`PdfRetentionDays` in der Gerätekonfiguration (Standard 30 Tage). Abgelaufene PDFs werden höchstens einmal pro Stunde beim nächsten PDF-Auftrag gelöscht, bei 0 Tagen direkt nach dem Druck. Defekte oder nicht druckbare Dateien werden nicht aufbewahrt.

## Grenzen

- Höchstens 100 MB und 500 Seiten pro Auftrag, keine passwortgeschützten PDFs.
- Die Seiten werden als Bilder gedruckt, es gibt keine Vektorausgabe.
- Die Oberfläche (Haken pro Drucker, „PDF öffnen“ in der Auftragsliste, Einstellung der Aufbewahrungsdauer) folgt nach dem Merge der GUI-Überarbeitung (PR #10).

## Manueller Test, solange die Oberfläche fehlt

Auf dem Client in PowerShell (Administrator) die Warteschlange des Brother auf den PDF-Treiber umstellen. Der Port bleibt erhalten:

```powershell
Set-Printer -Name "<Druckername>" -DriverName "Microsoft Print To PDF"
```

Ruückgängig machen mit dem ursprünglichen Treibernamen aus `Get-PrinterDriver`.
