# SimplePrint 0.2.11

Kleiner Windows-Druckserver mit zwei Druckpfaden: **RAW-Tunnel für klassische/lokale Treiber** und **direkte IPP-/WSD-Anbindung für Microsoft IPP Class Driver**, plus Diagnose.

Bei Tunnel-Druckern rendert der Client mit exakt dem passenden Treiber und SimplePrint überträgt den fertigen Datenstrom zum Server. Bei Microsoft-IPP/WSD-Druckern versucht SimplePrint zunächst eine echte direkte Windows-Geräteverbindung. Kann der Client das WSD-Gerät zunächst nicht entdecken, aktiviert SimplePrint auf privaten bzw. Domänennetzwerken zuerst gezielt die benötigten WSD-Voraussetzungen (Function Discovery sowie 3702/UDP und 5357-5358/TCP für LocalSubnet) und wiederholt die Erkennung. Erst wenn das weiterhin scheitert, wird die vom SimplePrint-Server bereitgestellte Windows-Druckerfreigabe als letzter Fallback versucht. Dadurch bleibt die physische WSD-/IPP-Verbindung ausschließlich auf dem Server.

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

1. Auf dem Druckserver `SimplePrint-Server-Setup-0.2.11.exe` installieren.
2. `SimplePrint Server` öffnen und die gewünschten lokalen Drucker freigeben.
3. Auf jedem Windows-10/11-Client `SimplePrint-Client-Setup-0.2.11.exe` installieren.
4. `SimplePrint Client` öffnen. Der Server sollte automatisch erscheinen.
5. Drucker über die Checkbox auswählen und die Druckerauswahl speichern.
6. Bei Class-Driver-Tunnelqueues installiert/verwendet SimplePrint ausschließlich den **exakt gleichen Treiber wie auf dem Server**. Ein beliebiger Ersatztreiber wird nicht mehr akzeptiert.
7. Microsoft-IPP/WSD-Freigaben werden bevorzugt als **direkte IPP-/WSD-Queue** verwendet. Existiert lokal bereits eine Queue für exakt dieselbe WSD-DeviceUUID bzw. IPP-Geräteadresse, übernimmt SimplePrint diese Queue schreibgeschützt statt eine zweite anzulegen. Kann Windows den WSD-Drucker auf dem Client trotz UUID und Gerätescan nicht finden, verwendet 0.2.11 automatisch die lokale Windows-Druckerfreigabe des SimplePrint-Servers (`SimplePrint-<Drucker-ID>`). Der Server richtet diese Freigabe sowie die dafür auf Privat/Domäne und LocalSubnet beschränkten Firewallregeln selbst ein.
8. Mit **Testseite** den vollständigen Weg prüfen.

## Automatische Updates ab 0.2.3

Server- und Client-GUI prüfen beim Start sowie anschließend alle sechs Stunden den neuesten öffentlichen GitHub-Release unter `tojollinor/SimplePrint`. Ist eine neuere Version verfügbar, erscheint ein eigener SimplePrint-Dialog mit Release-Hinweisen.

Über **Jetzt aktualisieren** wird zuerst die Windows-Administratorfreigabe angefordert. Danach übernimmt ein separater, erhöhter SimplePrint-Updater den Ablauf: Download mit Fortschrittsleiste, Prüfung von Dateigröße und optionalem SHA-256-Digest, anschließend eine vollständig stille Installation mit eigener Installationsanzeige. Nach erfolgreichem Abschluss startet SimplePrint automatisch neu und bestätigt das Update. Server und Client laden dabei ausschließlich den jeweils passenden Installer aus dem GitHub-Release. Alternativ lässt sich der Release im Browser öffnen oder die Aktualisierung auf später verschieben.

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
dist\Installer\SimplePrint-Server-Setup-0.2.11.exe
dist\Installer\SimplePrint-Client-Setup-0.2.11.exe
```

## Diagnose

Die Diagnosefunktionen befinden sich unter **Allgemein**. Client und Server können dort weiterhin ein lokales ZIP-Diagnosepaket erzeugen. Zusätzlich kann der Server ab 0.2.8 ein **vollständiges Diagnosepaket** erstellen: Dabei fordert er von allen aktuell erreichbaren 0.2.8+-Client-Agenten deren Diagnose-ZIP an und bettet diese unter `clients/` in das Serverpaket ein. Offline-Clients und ältere Clients werden in `clients/collection.txt` dokumentiert, ohne die Gesamtdiagnose abzubrechen.

Der Remote-Abruf läuft über TCP 45882 und ist durch die Installer-Firewallregel auf **Privat/Domäne + LocalSubnet** beschränkt. Der Client akzeptiert den Abruf nur von einem aktuell erkannten bzw. fest zugeordneten SimplePrint-Server. Dokumentinhalte werden nicht gespeichert.

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

Die aktuelle Releasefassung ist `0.2.11`. Für WSD-Drucker bereitet der Client die Windows-Netzwerkerkennung nun selbst vor: `fdPHost` wird bei Bedarf gestartet, für Privat/Domäne werden nur LocalSubnet-Regeln für WSD-Discovery (UDP 3702) und WSD-Events (TCP 5357/5358) angelegt und die Geräteerkennung wird mit mehreren Wiederholungen neu angestoßen. Erst danach greift der bestehende Serverfreigabe-Fallback. Der Server richtet dafür eine kontrollierte Freigabe pro direkter Druckerqueue und LocalSubnet-beschränkte SMB/RPC-Firewallregeln ein; bei Abwahl oder Deinstallation werden die SimplePrint-Freigaben wieder bereinigt. Der WSD-Clientpfad stößt weiterhin vor dem Anlegen einer Queue die Windows-Geräteerkennung erneut an, prüft bereits bekannte WSD-Ports anhand der DeviceUUID und versucht die Installation anschließend erneut. Dadurch werden insbesondere Fälle abgefangen, in denen der Server eine gültige WSD-UUID kennt, der Client den Drucker aber noch nicht lokal aufgelöst hat. Die Clientdiagnose enthält dafür jetzt alle Drucker, Ports und den WSD-Port-Registryzweig. Passende bereits installierte WSD-/IPP-Queues werden weiterhin anhand der Geräteidentität übernommen, statt eine zweite Windows-Queue anzulegen. Übernommene Queues werden weder verändert noch bei Abwahl, Migration oder Deinstallation gelöscht. Sie behebt die Migration alter Tunnel-Zuordnungen auf direkte WSD-/IPP-Routen, ohne bereits vorhandene physische Windows-Druckerqueues zu löschen. Außerdem zeigt die Client-Bereitschaft die Queue-Zuordnung jetzt ausdrücklich als korrekt/falsch an. Sie ergänzt den Registry-Fallback für WSD-Geräte-UUIDs, den vollständigen Server+Client-Diagnoseabruf, den Umzug der Diagnosefunktionen nach **Allgemein** sowie **Nach Updates suchen** im Tray-Kontextmenü von Server und Client. Sie ergänzt direkten IPP-/WSD-Druck für Microsoft IPP Class Driver, exakte Class-Driver-Zuordnung und einen harten Schutz gegen rekursive SimplePrint-Druckschleifen. Enthalten ist außerdem die End-to-End-Druckbereitschaftsprüfung mit Client-, Server-, Windows-Spooler- und Gerätestatus. Netzwerkdrucker können zusätzlich über SNMP v1 und die standardisierte Printer-MIB abgefragt werden. Soweit vom Gerät unterstützt, zeigt SimplePrint unter anderem Leerlauf/Druckt, Papier- und Tonerwarnungen sowie Verbrauchsmaterialstände an.

Zusätzlich enthalten sind ein vollständig stiller Updateablauf nach einmaliger UAC-Freigabe mit Download- und Installationsanzeige, automatischem Neustart samt Erfolgsmeldung, ein fensterloser GUI-Autostart direkt in den Infobereich, Versions-/Protokollkompatibilität, verifizierte Firewallregeln, Serverdienst-Neustart, Warteschlangenaufruf, Offline-Clientverwaltung, Druckauftrags-Historienverwaltung, erweiterte Diagnosepakete, generische Class-Driver-Warnungen und Ampelstatus im Tray. Ab 0.2.3 wird die Checkbox per exakter Trefferprüfung behandelt: Ein Klick auf Druckername oder übrige Zeile markiert nur den Drucker; ausschließlich ein Klick direkt auf das Checkbox-Symbol setzt oder entfernt den Haken. Ab 0.2.8 zeigt die Druckbereitschaft konkrete Fehlerursachen statt Sammelmeldungen. Erfolgreiche Prüfungen erhalten ein grünes ✓, echte Fehler ein rotes ✗; bei nicht verfügbaren oder nicht geprüften Werten wird bewusst kein Symbol angezeigt. Windows-Queue-Status wie `NoToner` werden ebenfalls ausgewertet, sodass z. B. ausdrücklich **„Verbindung zum Drucker vorhanden, aber Toner leer.“** gemeldet wird. Herausgeber-Metadaten verweisen auf `tojollinor` bzw. das GitHub-Repository.

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

### 0.2.11

- WSD-Erkennung auf Clients wird vor `Add-Printer -DeviceUUID` gezielt vorbereitet und mehrfach wiederholt.
- Client-Diagnosen enthalten jetzt Function-Discovery-Dienste und die SimplePrint-WSD-Firewallregeln.
- Die fehlerhafte RPC-Firewallanlage des Windows-Freigabe-Fallbacks wurde entfernt; SMB bleibt auf `LocalSubnet` sowie Privat/Domäne beschränkt.
- Administrative PowerShell-Skripte werden mit UTF-8-BOM geschrieben, damit Umlaute in Fehlermeldungen korrekt dargestellt werden.
