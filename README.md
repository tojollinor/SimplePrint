# SimplePrint 0.1.5

Kleiner Windows-Druckserver für genau einen Zweck: **Drucken ohne Rendering-Veränderung**, plus Diagnose.

SimplePrint ersetzt weder den nativen Druckertreiber noch verarbeitet es PDF/PostScript. Der Client rendert mit dem normalen Hersteller-Treiber. SimplePrint tunnelt danach nur den fertigen RAW-Datenstrom zum Server und gibt ihn dort als `RAW` an den Windows-Spooler weiter.

## Enthalten

- **Serverdienst** mit automatischer LAN-Erkennung (UDP 45880) und Print-Gateway (TCP 45881)
- **Server-GUI** mit Druckerfreigaben, Client-Online/Offline-Anzeige, Status, Testseite, Firewall-Verwaltung und Diagnose-ZIP
- **Client-Agent als Windows-Dienst**, der Server automatisch findet und DHCP-Adresswechsel transparent abfängt
- **Client-GUI** zum Suchen und festen Auswählen eines Servers, Installieren/Entfernen eigener `(SimplePrint)`-Druckerqueues sowie Testseite und Diagnose-ZIP
- **Installer**, der Dienste automatisch erstellt/startet und auf dem Server die benötigten Firewallregeln anlegt

## Warum DHCP kein Problem ist

Der Windows-Drucker auf dem Client wird nicht direkt auf die Server-IP gelegt. Stattdessen zeigt sein Standard-TCP/IP-Port auf `127.0.0.1` und einen lokalen SimplePrint-Port. Der Client-Agent sucht den Server im LAN selbstständig und tunnelt den Auftrag an dessen aktuelle Adresse.

```text
Windows-App
  ↓
Hersteller-Treiber (z. B. Brother DCP-L2510D)
  ↓
127.0.0.1:19100
  ↓
SimplePrint Client Agent
  ↓ automatisch gefundener Server
SimplePrint Server
  ↓ RAW
Windows-Spooler
  ↓
USB-/lokaler Drucker
```

## Installation aus fertigem Setup

1. Auf dem Druckserver `SimplePrint-Setup-0.1.5.exe` starten und **PrintServer** auswählen.
2. `SimplePrint Server` öffnen und den lokal installierten Drucker über **Drucker hinzufügen** freigeben.
3. Auf einem Windows-10/11-Client dasselbe Setup starten und **PrintClient** auswählen.
4. `SimplePrint Client` öffnen. Der Server sollte automatisch erscheinen.
5. Drucker markieren → **Drucker installieren**.
6. Wenn der identische Treibername lokal vorhanden ist, wird er automatisch benutzt. Andernfalls fragt die GUI nach dem passenden installierten Treiber.
7. Mit **Testseite** den vollständigen Weg prüfen.

## Build

Voraussetzungen auf einem Windows-x64-Rechner:

- .NET 8 SDK
- Inno Setup 6 (nur für den Setup-Build)

Dann PowerShell als normaler Benutzer öffnen:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
```

Die veröffentlichten Programme landen in `dist\`. Wenn Inno Setup vorhanden ist, entsteht zusätzlich:

```text
dist\Installer\SimplePrint-Setup-0.1.5.exe
```

## Diagnose

Beide GUIs können ein ZIP-Diagnosepaket erzeugen. Es enthält nur Konfiguration, Dienst-/Drucker-/Netzwerkstatus und Logs. Dokumentinhalte werden nicht gespeichert.

Serverdaten:

```text
C:\ProgramData\SimplePrint\Server\
```

Clientdaten:

```text
C:\ProgramData\SimplePrint\Client\
```

## Wichtiger Test für deinen Fall

Beim Brother DCP-L2510D sollte auf dem **Client der native Brother-Treiber** ausgewählt werden. Dadurch wird das Dokument dort gerendert. SimplePrint überträgt anschließend nur den fertigen Druckdatenstrom und führt keine PostScript→PDF- oder PDF→GDI-Konvertierung wie Mobility Print durch.

## Stand

Die aktuelle Fassung ist `0.1.5`. Sie ist als praxisnaher MVP gebaut und wurde um robuste LAN-Erkennung, Client-Präsenz, sichere eigene `(SimplePrint)`-Druckerqueues, Netzwerkprofil-Prüfung sowie verbesserte Diagnose- und Statusanzeigen erweitert. Besonders GDI-Treiber können herstellerspezifisches Verhalten haben. Die Diagnosefunktionen sind genau dafür eingebaut.

## Logo und Branding

SimplePrint trennt das feste Programm-Icon vom austauschbaren Logo in der Oberfläche:

- `assets\app.ico` wird beim Build fest in Client-/Server-GUI und Installer eingebettet. Es wird für EXE, Taskleiste, Fenstersymbol und den **Über**-Dialog verwendet und ändert sich nach der Installation nicht.
- Das sichtbare Logo im Kopfbereich von Client- und Server-GUI wird aus `{Installationsordner}\assets\logo.png` geladen.
- `logo.png` kann nach der Installation durch eine eigene PNG-Datei ersetzt werden. Beim nächsten Start der GUI wird das neue Bild angezeigt.
- Fehlt `logo.png` oder ist die Datei beschädigt, verwendet die GUI automatisch das intern eingebettete Standardlogo.
- Ein vorhandenes, nachträglich angepasstes `logo.png` wird bei einer erneuten Installation/Update nicht überschrieben.
