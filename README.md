# SimplePrint 0.3.0

SimplePrint ist eine selbst gehostete Windows-Drucklösung für Windows 10/11. Seit **0.3.0** gibt es keine getrennten Server- und Client-Programme mehr: Jede Installation kann gleichzeitig eigene Drucker bereitstellen und Drucker beliebig vieler anderer SimplePrint-Geräte verwenden.

## Architektur

Jeder Rechner installiert genau zwei Komponenten:

- **SimplePrint** als Windows-Dienst
- **SimplePrint** als gemeinsame GUI

Der Dienst übernimmt Geräteerkennung, Druckweiterleitung, Druckerfreigaben, Diagnose und die Kommunikation mit anderen SimplePrint-Geräten.

Ein Gerät kann gleichzeitig:

- eigene Windows-Drucker über SimplePrint bereitstellen
- Drucker von mehreren anderen SimplePrint-Geräten verwenden
- für ein anderes Gerät als Server wirken
- für dasselbe oder ein anderes Gerät als Client wirken

Die Begriffe **Server** und **Clients** beschreiben in der GUI deshalb nur noch die jeweilige Beziehung zwischen Geräten.

## Oberfläche

Die gemeinsame GUI enthält:

- **Übersicht**
- **Drucker**
  - **Eigene Drucker**
  - **Netzwerkdrucker**
- **Server**
- **Clients**
- **Druckaufträge**
- **Diagnose**
- **Einstellungen**

Unter **Eigene Drucker** werden lokale Windows-Drucker angezeigt und per Checkbox für andere SimplePrint-Geräte freigegeben.

Unter **Netzwerkdrucker** werden die Drucker aller aktuell erkannten SimplePrint-Geräte nach Quellgerät gruppiert. Drucker von mehreren Geräten können gleichzeitig installiert werden.

Die Netzwerkdruckerliste wird beim Öffnen geladen und anschließend nur neu aufgebaut, wenn sich der erkannte Gerätekatalog tatsächlich ändert oder eine manuelle Aktualisierung ausgelöst wird. Ein bloßer Reiterwechsel löst keine erneute Druckersuche aus. Bereits gesetzte, noch nicht gespeicherte Checkboxen bleiben bei einer Katalogaktualisierung erhalten.

## Druckpfade

SimplePrint unterstützt weiterhin die bisherigen Druckpfade:

1. **RAW-Tunnel** für klassische bzw. herstellerspezifische Windows-Treiber
2. **Direktes IPP/WSD** für geeignete Drucker, insbesondere Microsoft IPP Class Driver
3. **Windows-Druckerfreigabe** als Fallback, wenn der direkte Gerätepfad nicht nutzbar ist

Für WSD-Drucker versucht SimplePrint eine gerichtete Geräteadresse aus den Windows-Geräteinformationen zu ermitteln. Wenn möglich, wird daraus eine direkte IPP-Verbindung. Dadurch ist auf dem verwendenden Gerät keine WSD-Multicast-Erkennung erforderlich.

Von SimplePrint selbst erzeugte Druckerqueues werden nicht erneut als eigene freigebbare Drucker angeboten. Dadurch werden rekursive Druckschleifen verhindert.

## Diagnose

Es gibt nur noch **Diagnosepaket erstellen**.

Die Auswahl zeigt:

- **Dieses Gerät**
- **Server**
- **Clients**

Dieses Gerät ist standardmäßig ausgewählt, kann aber abgewählt werden. Es werden nur aktuell erreichbare entfernte Geräte angeboten.

Jedes Gerät kann einzeln per Checkbox gewählt werden. Ist dasselbe Gerät gleichzeitig Server und Client, wird dessen Diagnose intern nur einmal abgerufen.

Das erzeugte ZIP enthält:

- ein Manifest
- ein Diagnose-Unterpaket pro erfolgreich abgefragtem Gerät
- separate Fehlerdateien für einzelne Geräte, deren Abruf fehlschlägt

Der Fehler eines einzelnen entfernten Geräts bricht das restliche Diagnosepaket nicht ab.

## Netzwerk

Standardports:

| Funktion | Protokoll | Port |
| --- | --- | ---: |
| Geräteerkennung | UDP | 45880 |
| Print-Gateway / Druckerstatus | TCP | 45881 |
| Diagnose | TCP | 45882 |

Die Installer-Firewallregeln gelten nur für **Privat/Domäne** und **LocalSubnet**. Auf einem öffentlichen Windows-Netzwerkprofil wird der automatische SimplePrint-Netzwerkzugriff bewusst nicht freigegeben.

## Installation

### Online-Installer

`SimplePrint-Setup.exe`

Der Online-Installer trägt keine Versionsnummer. Er fragt GitHub nach dem neuesten veröffentlichten SimplePrint-Release, lädt automatisch den aktuellen Offline-Installer und verwendet die von GitHub bereitgestellte SHA-256-Prüfsumme, sofern vorhanden.

### Offline-Installer

`SimplePrint-Setup-0.3.0.exe`

Enthält GUI und Dienst vollständig und kann ohne Internetverbindung installiert werden.

## Upgrade von 0.2.x

Der 0.3.0-Installer erkennt vorhandene getrennte SimplePrint-Server-/Client-Installationen.

Beim Upgrade werden:

1. alte Dienste zunächst nur gestoppt
2. Server- und Client-Konfigurationen in die neue gemeinsame Gerätekonfiguration übernommen
3. der neue Dienst `SimplePrint` installiert und gestartet
4. Firewallregeln für Discovery, Gateway und Diagnose eingerichtet
5. erst nach erfolgreicher Migration die alten Dienste, Autostarts, Verknüpfungen und Installationsreste entfernt

Die alte Konfiguration unter `C:\ProgramData\SimplePrint\Server\` und `...\Client\` bleibt als Rückfallebene erhalten. Die neue aktive Konfiguration liegt unter:

`C:\ProgramData\SimplePrint\Device\`

## Administratorrechte

Die GUI läuft normal ohne Administratorrechte.

Ein UAC-Prompt erscheint nur für Aktionen, die Windows tatsächlich erhöht ausführen muss, beispielsweise:

- Dienststeuerung
- Firewalländerungen
- bestimmte Drucker-/Portinstallationen
- Updates

Netzwerk-Anmeldefehler werden nicht als Administratorproblem behandelt.

## Updates

Die gemeinsame GUI verwendet für Updates ausschließlich den versionierten Offline-Installer:

`SimplePrint-Setup-<Version>.exe`

Der Ablauf besteht aus Updateprüfung, Download, optionaler SHA-256-Prüfung, UAC-Freigabe, stiller Installation, Neustart und Erfolgsmeldung.

Im Tray und unter **Einstellungen** steht zusätzlich **Nach Updates suchen** zur Verfügung.

## Build

Voraussetzungen:

- .NET 8 SDK
- Inno Setup 6

Build:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
```

Erzeugt werden:

```text
dist\Unified\Service\
dist\Unified\Gui\
dist\Installer\SimplePrint-Setup-0.3.0.exe
dist\Installer\SimplePrint-Setup.exe
```

Das Release-Workflow veröffentlicht bei einem Commit mit `[release]` beide Installer als GitHub-Release-Assets.

## Branding

Das feste Programm-Icon wird für EXE, Fenster, Taskleiste, Tray und Verknüpfungen verwendet.

Das Logo im Kopfbereich der GUI bleibt austauschbar. Ein vorhandenes benutzerdefiniertes Logo aus einer älteren Server-/Client-Installation wird beim Upgrade nach Möglichkeit übernommen.

## Version 0.3.0

0.3.0 ist die Architektur-Umstellung von getrenntem Server und Client auf ein einziges SimplePrint-Gerät.

Wesentliche Änderungen:

- eine gemeinsame GUI
- ein gemeinsamer Windows-Dienst
- mehrere gleichzeitig verwendbare SimplePrint-Quellgeräte
- eigene und entfernte Drucker in einer gemeinsamen Druckerverwaltung
- Server-/Client-Ansichten als Beziehungsansichten
- ein einziges auswählbares Mehrgeräte-Diagnosepaket
- gemeinsame Druckauftragshistorie
- gemeinsame Firewall-, Dienst-, Autostart- und Updateverwaltung
- Online- und Offline-Installer
- automatische Migration von 0.2.x
