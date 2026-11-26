# ThixGate – Logo

Ein Tor – ein Bogen auf zwei Pfeilern – und ein Pfeil, der hindurchgeht: die Verbindung durch das Gateway zum entfernten
Rechner. Auf einer Indigo-Kachel. Akzentfarbe **#4B3FCF** (weißer Text 7,2 : 1), Verlauf der Kachel **#8F86FF → #4B3FCF**,
dunkles Indigo **#2E2690** (Wolke). Alle Detailstufen zeigen dasselbe Tor auf einem 256er-Raster.

| Datei | Detailstufe | Einsatz |
|---|---|---|
| `logo-icon.svg` | voll (ab 48 px): Tor und Pfeil auf der Verlaufskachel | App-Icon (Windows .ico ab 48 px, macOS .icns), Fenster, Über-Seite |
| `logo-small.svg` | reduziert (16–32 px): dickeres, größeres Tor, Kachel randlos, einfarbig | Favicon, kleine .ico-Größen, Taskleiste |
| `logo-tray.svg` | einfarbig, ohne Kachel: Tor und Pfeil | macOS-Menüleiste (Template-Bild, das System färbt es) |
| `logo-cloud.svg` | voll, randlose Kachel mit Wolke | Kachel für Startbildschirme und die ThixCloud (auch „maskable“): `png/thixgate-cloud-*.png` |
| `logo-full.svg` | Icon + Wortmarke „Thix**Gate**“ | README, Kopfzeilen, Über-Seiten |

`png/` enthält alle gerenderten Größen. Neu erzeugen (nach jeder Änderung an einer SVG):

```
cd tools && npm install && node render-icons.js
```

Das Skript rendert mit dem installierten Microsoft Edge (headless) und schreibt zusätzlich
`src/ThixGate/Assets/*` (thixgate.ico, Tray-Icons, Logos).
Die macOS-Varianten `png/thixgate-mac-*.png` halten Apples Rand um die Kachel ein; `scripts/package-macos.sh` baut daraus das `.icns`.
Das Logo liegt außerdem in der ThixCloud (MatCloud: `src/MatCloud/wwwroot/img/apps/thixgate*`).
