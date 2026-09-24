# SimplePrint 0.2.4

Kleiner Windows-Druckserver mit zwei Druckpfaden: **RAW-Tunnel für klassische/lokale Treiber** und **direkte IPP-/WSD-Anbindung für Microsoft IPP Class Driver**, plus Diagnose.

Bei Tunnel-Druckern rendert der Client mit exakt dem passenden Treiber und SimplePrint überträgt den fertigen Datenstrom zum Server. Bei Microsoft-IPP/WSD-Druckern installiert SimplePrint dagegen eine echte direkte Windows-Geräteverbindung, damit IPP-Fähigkeiten und Statusabfragen nicht durch einen RAW-Proxy verloren gehen.

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

1. Auf dem Druckserver `SimplePrint-Server-Setup-0.2.4.exe` installieren.
2. `SimplePrint Server` öffnen und die gewünschten lokalen Drucker freigeben.
3. Auf jedem Windows-10/11-Client `SimplePrint-Client-Setup-0.2.4.exe` installieren.
4. `SimplePrint Client` öffnen. Der Server sollte automatisch erscheinen.
5. Drucker über die Checkbox auswählen und die Druckerauswahl speichern.
6. Bei Class-Driver-Tunnelqueues installiert/verwendet SimplePrint ausschließlich den **exakt gleichen Treiber wie auf dem Server**. Ein beliebiger Ersatztreiber wird nicht mehr akzeptiert.
7. Microsoft-IPP/WSD-Freigaben werden, sofern die Geräteadresse ermittelbar ist, als **direkte IPP-/WSD-Queue** installiert.
8. Mit **Testseite** den vollständigen Weg prüfen.

## Automatische Updates ab 0.2.3

Server- und Client-GUI prüfen beim Start sowie anschließend alle sechs Stunden den neuesten öffentlichen GitHub-Release unter `tojollinor/SimplePrint`. Ist eine neuere Version verfügbar, erscheint ein eigener SimplePrint-Dialog mit Release-Hinweisen.

Über **Jetzt aktualisieren** lädt der Server ausschließlich `SimplePrint-Server-Setup-<Version>.exe` und der Client ausschließlich `SimplePrint-Client-Setup-<Version>.exe` aus dem GitHub-Release. Der Installer wird in ein temporäres Update-Verzeichnis geladen und anschließend gestartet. Dateigröße und, sofern GitHub für das Asset einen SHA-256-Digest bereitstellt, auch die Prüfsumme werden vor dem Start kontrolliert. Alternativ lässt sich der Release im Browser öffnen oder die Aktualisierung auf später verschieben.

Unter **Allgemein → Nach Updates suchen** kann die Prüfung auf Server und Client jederzeit manuell ausgelöst werden. Solange das Repository privat ist, kann die öffentliche Release-API ohne Authentifizierung nicht verwendet werden; für die automatische Updatefunktion muss das Repository öffentlich erreichbar sein.

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
dist\Installer\SimplePrint-Server-Setup-0.2.4.exe
dist\Installer\SimplePrint-Client-Setup-0.2.4.exe
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

Beim Brother DCP-L2510D muss der Client bei einer Class-Driver-Freigabe **denselben Brother-Treiber wie der Server** verwenden. SimplePrint versucht diesen Treiber aus dem lokalen Windows-Treiberspeicher zu installieren. Der bisher mögliche Ersatz durch z. B. einen abweichenden Universal-PCL-Treiber wird blockiert.

## Stand

Die aktuelle Entwicklungsfassung ist `0.2.4`. Sie ergänzt direkten IPP-/WSD-Druck für Microsoft IPP Class Driver, exakte Class-Driver-Zuordnung und einen harten Schutz gegen rekursive SimplePrint-Druckschleifen. Enthalten ist außerdem die End-to-End-Druckbereitschaftsprüfung mit Client-, Server-, Windows-Spooler- und Gerätestatus. Netzwerkdrucker können zusätzlich über SNMP v1 und die standardisierte Printer-MIB abgefragt werden. Soweit vom Gerät unterstützt, zeigt SimplePrint unter anderem Leerlauf/Druckt, Papier- und Tonerwarnungen sowie Verbrauchsmaterialstände an.

Zusätzlich enthalten sind Versions-/Protokollkompatibilität, verifizierte Firewallregeln, Serverdienst-Neustart, Warteschlangenaufruf, Offline-Clientverwaltung, Druckauftrags-Historienverwaltung, erweiterte Diagnosepakete, generische Class-Driver-Warnungen und Ampelstatus im Tray. Ab 0.2.3 wird die Checkbox per exakter Trefferprüfung behandelt: Ein Klick auf Druckername oder übrige Zeile markiert nur den Drucker; ausschließlich ein Klick direkt auf das Checkbox-Symbol setzt oder entfernt den Haken. Ab 0.2.4 zeigt die Druckbereitschaft konkrete Fehlerursachen statt Sammelmeldungen. Erfolgreiche Prüfungen erhalten ein grünes ✓, echte Fehler ein rotes ✗; bei nicht verfügbaren oder nicht geprüften Werten wird bewusst kein Symbol angezeigt. Windows-Queue-Status wie `NoToner` werden ebenfalls ausgewertet, sodass z. B. ausdrücklich **„Verbindung zum Drucker vorhanden, aber Toner leer.“** gemeldet wird. Herausgeber-Metadaten verweisen auf `tojollinor` bzw. das GitHub-Repository.

## Logo und Branding

SimplePrint trennt das feste Programm-Icon vom austauschbaren Logo in der Oberfläche:

- `assets\app.ico` bzw. das versionierte ICO wird für EXE, Taskleiste, Fenstersymbol, Tray und Verknüpfungen verwendet. Der **Über**-Dialog verwendet dagegen das hochauflösend eingebettete Standard-PNG, damit das Logo auch groß scharf bleibt.
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

Der Microsoft IPP Class Driver wird nicht mehr über den RAW-Tunnel betrieben, sobald der Server eine direkte IPP-/WSD-Geräteadresse liefern kann. Diese direkten Druckaufträge laufen nicht durch das SimplePrint-Gateway und erscheinen deshalb nicht in der Tunnel-Jobhistorie. Class-Driver, die weiterhin den Tunnel nutzen, müssen auf Client und Server exakt übereinstimmen.


## Druckbereitschaft ab 0.2.0

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

Bei USB-, WSD- oder sonstigen lokalen Druckern stehen nur die Informationen zur Verfügung, die Windows bzw. der installierte Treiber bereitstellt. SNMP-Werte werden nur bei erreichbaren Netzwerkdruckern angezeigt; fehlende Werte werden nicht geschätzt oder erfunden.

Eine grüne Prüfung bedeutet: **Nach allem technisch Abfragbaren sollte der Druckpfad funktionieren.** Sie kann ohne tatsächlichen Ausdruck nicht mechanisch bestätigen, dass ein Blatt Papier aus dem Gerät gekommen ist. Kritische Gerätewarnungen wie **„Toner leer“** färben die Druckbereitschaft rot, blockieren einen Druckauftrag derzeit aber nicht vorab; der Auftrag wird weiterhin an Windows bzw. den Drucker übergeben und dessen tatsächlicher Spooler-/Gerätestatus ausgewertet.