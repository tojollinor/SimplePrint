# SimplePrint – Architektur

## Ziel

SimplePrint transportiert bereits vom **nativen Windows-Druckertreiber auf dem Client erzeugte RAW-Druckdaten** unverändert zu einem Windows-Drucker am Server. Es rendert, konvertiert und verändert keine Dokumente.

```text
Anwendung
  ↓
nativer Druckertreiber auf dem Client
  ↓
Windows Standard TCP/IP Port → 127.0.0.1:19xxx
  ↓
SimplePrint Client Agent
  ↓  (automatische UDP-Erkennung des Servers)
SimplePrint Gateway TCP 45881
  ↓
Windows-Spooler, Datentyp RAW
  ↓
lokaler/USB-Drucker
```

## DHCP ohne feste Server-IP

Der Windows-Drucker auf dem Client zeigt **immer auf localhost**. Der Client-Agent sucht den Server regelmäßig per UDP-Broadcast auf Port 45880 und merkt sich dessen aktuelle IP-Adresse. Dadurch muss der Windows-Druckerport bei einem DHCP-Wechsel nicht geändert werden.

## Protokoll

Discovery:
- Client sendet `SPRDISC1` per UDP-Broadcast an Port 45880.
- Server antwortet mit JSON, Server-ID, Name, Gateway-Port und freigegebenen Druckern.

Druckgateway:
- TCP 45881.
- 24-Byte-Header: `SPR1`, Version 1, Drucker-GUID.
- Danach folgen die RAW-Druckdaten bytegenau.

## Einmalige Treiberübertragung

Fehlt der Treiber eines Server-Druckers auf dem Client, kann er beim Einrichten **einmalig** vom Server bezogen werden. Es gibt keine regelmäßige Synchronisation und keinen automatischen Download.

Ablauf:
1. Der Client fragt den Server an (TCP 45883): Treibername und Architektur des Clients.
2. Der Server prüft, dass der Anfrager ein aktuell bekanntes SimplePrint-Gerät ist, dass ein freigegebener Drucker den Treiber verwendet und dass die **Architektur** passt (x64 zu x64, ARM64 zu ARM64, x86 zu x86). Der Treiberpfad kommt nie vom Client.
3. Der Client zeigt „Bitte am Server fortfahren“, der Server fragt seinen Benutzer (Oberfläche, auch im Infobereich). Erst nach dem Ja wird das Paket erstellt.
4. Der Server exportiert das Treiberpaket aus `DriverStore\FileRepository` und sendet es mit SHA-256-Prüfsumme.
5. Der Client prüft Prüfsumme, Architektur und die digitale Signatur der Katalogdatei, fragt den Benutzer und installiert mit `pnputil /add-driver` und `Add-PrinterDriver` über eine Administratorabfrage.

Der Port ist nur offen, solange auf dem Server mindestens ein Drucker freigegeben ist.

## Firewall

Der Installer erstellt auf dem Server nur:
- UDP 45880 eingehend, Profile Private/Domain
- TCP 45881 eingehend, Profile Private/Domain

Der Dienst legt zusätzlich `SimplePrint-DriverTransfer` (TCP 45883, Private/Domain, LocalSubnet) an, solange ein Drucker freigegeben ist, und entfernt die Regel danach wieder.

Der Client benötigt keine eingehende Firewallregel. Seine lokalen Proxy-Ports binden ausschließlich an `127.0.0.1`.

## Einschränkungen der ersten Version

- Discovery ist für dasselbe IPv4-LAN/Broadcast-Domain gedacht. VLAN-/Subnetz-Routing ist noch nicht vorgesehen.
- Der passende native Druckertreiber muss auf dem Client installiert sein oder über die einmalige Treiberübertragung vom Server bezogen werden.
- Keine Benutzerverwaltung, Quoten, Wasserzeichen, Dokumentkonvertierung oder Cloud-Funktion.
