# SimplePrint 0.2.0

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

1. Auf dem Druckserver `SimplePrint-Setup-0.2.0.exe` starten und **PrintServer** auswählen.
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
dist\Installer\SimplePrint-Setup-0.2.0.exe
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

Die aktuelle Fassung ist `0.2.0`. Sie erweitert SimplePrint um eine echte End-to-End-Druckbereitschaftsprüfung mit Client-, Server-, Windows-Spooler- und Gerätestatus. Netzwerkdrucker können zusätzlich über SNMP v1 und die standardisierte Printer-MIB abgefragt werden. Soweit vom Gerät unterstützt, zeigt SimplePrint unter anderem Leerlauf/Druckt, Papier- und Tonerwarnungen sowie Verbrauchsmaterialstände an.

Zusätzlich enthalten sind Versions-/Protokollkompatibilität, verifizierte Firewallregeln, Serverdienst-Neustart, Warteschlangenaufruf, Offline-Clientverwaltung, Druckauftrags-Historienverwaltung, erweiterte Diagnosepakete, generische Class-Driver-Warnungen und Ampelstatus im Tray.

## Logo und Branding

SimplePrint trennt das feste Programm-Icon vom austauschbaren Logo in der Oberfläche:

- `assets\app.ico` wird beim Build fest in Client-/Server-GUI und Installer eingebettet. Es wird für EXE, Taskleiste, Fenstersymbol und den **Über**-Dialog verwendet und ändert sich nach der Installation nicht.
- Das sichtbare Logo im Kopfbereich von Client- und Server-GUI wird aus `{Installationsordner}\assets\logo.png` geladen.
- `logo.png` kann nach der Installation durch eine eigene PNG-Datei ersetzt werden. Beim nächsten Start der GUI wird das neue Bild angezeigt.
- Fehlt `logo.png` oder ist die Datei beschädigt, verwendet die GUI automatisch das intern eingebettete Standardlogo.
- Ein vorhandenes, nachträglich angepasstes `logo.png` wird bei einer erneuten Installation/Update nicht überschrieben.


## Druckauftragsstatus ab 0.1.6

Client und Server speichern die letzten Druckaufträge mit derselben Job-ID. Dadurch lässt sich der Weg eines Auftrags nachvollziehen:

```text
Windows-Warteschlange
  → lokaler SimplePrint-Proxy
  → an Server übertragen
  → vom Server empfangen
  → an Windows-Spooler des Servers übergeben
  → druckt / gedruckt / abgeschlossen / Fehler
```

Hinweis: **„Gedruckt“** wird nur angezeigt, wenn der Windows-Spooler diesen Status tatsächlich meldet. Verschwindet ein Auftrag nach erfolgreicher Übergabe aus der Warteschlange, ohne dass der Drucker einen separaten Printed-Status liefert, zeigt SimplePrint **„Abgeschlossen“**. Das bestätigt den Abschluss im Windows-Drucksystem, nicht mechanisch das Vorhandensein eines Blattes im Ausgabefach.

Für den RAW-Tunnel werden Hersteller-PCL6- oder PostScript-Treiber empfohlen. Der Microsoft IPP Class Driver kann eine echte IPP-Gegenstelle erwarten und wird deshalb in der Schnelldiagnose entsprechend gekennzeichnet.


## Druckbereitschaft in 0.2.0

Die Prüfung unterscheidet zwischen:

- **Grün / Bereit:** keine bekannten Hindernisse
- **Gelb / Warnung:** Drucken wahrscheinlich möglich, aber z. B. generischer Treiber, niedriger Toner oder unvollständige Gerätestatusdaten
- **Rot / Nicht druckbereit:** Queue fehlt/pausiert/offline, Gerät nicht erreichbar oder kritischer Gerätestatus

Bei Netzwerkdruckern versucht SimplePrint zusätzlich:

- Ping
- TCP-Verbindung zum Druckerport
- SNMP v1 über UDP 161
- Host-Resources Printer Status
- Printer-MIB Fehlerstatus
- Verbrauchsmaterialbeschreibung, Maximalstand und aktuellen Stand

Bei USB-, WSD- oder sonstigen lokalen Druckern stehen nur die Informationen zur Verfügung, die Windows bzw. der installierte Treiber an den Spooler zurückmeldet.

Eine grüne Prüfung bedeutet: **Nach allem technisch Abfragbaren sollte der Druckpfad funktionieren.** Sie kann ohne tatsächlichen Ausdruck nicht mechanisch bestätigen, dass ein Blatt Papier aus dem Gerät gekommen ist.
