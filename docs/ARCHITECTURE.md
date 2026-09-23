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

## Firewall

Der Installer erstellt auf dem Server nur:
- UDP 45880 eingehend, Profile Private/Domain
- TCP 45881 eingehend, Profile Private/Domain

Der Client benötigt keine eingehende Firewallregel. Seine lokalen Proxy-Ports binden ausschließlich an `127.0.0.1`.

## Einschränkungen der ersten Version

- Discovery ist für dasselbe IPv4-LAN/Broadcast-Domain gedacht. VLAN-/Subnetz-Routing ist noch nicht vorgesehen.
- Der passende native Druckertreiber muss auf dem Client installiert sein.
- Keine Benutzerverwaltung, Quoten, Wasserzeichen, Dokumentkonvertierung oder Cloud-Funktion.
- Es gibt bewusst kein automatisches Herunterladen von Druckertreibern.
