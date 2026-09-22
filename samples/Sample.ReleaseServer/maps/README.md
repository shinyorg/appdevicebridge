# Sample map regions

The sample release server serves this directory at `/maps` — the signed catalog at `/maps/catalog`, the files at
`/maps/files/{name}` — and the sample app's maps bridge reads both. Nothing here is committed except this file.

Build it with `shiny-map-packs` (`src/Shiny.AppDeviceBridge.MapPacks`), which needs the
[pmtiles CLI](https://github.com/protomaps/go-pmtiles/releases) and, for directions, Valhalla's tools or Docker:

```bash
# The online tiles the app streams a range at a time: any PMTiles archive, named planet.pmtiles.
# A daily Protomaps build is the whole planet (about 140 GB); a region cut from it works as well.
pmtiles extract https://build.protomaps.com/20260922.pmtiles planet.pmtiles --bbox=-109.06,36.99,-102.04,41.0 --maxzoom=14

# A downloadable region: its map, and its road network for on-device directions.
shiny-map-packs region colorado --name Colorado --bbox=-109.06,36.99,-102.04,41.0 \
  --planet planet.pmtiles \
  --osm https://download.geofabrik.de/north-america/us/colorado-latest.osm.pbf

# The label fonts and icons, installed with the first region so the map draws offline.
shiny-map-packs assets

shiny-map-packs list
```

Run them from this directory, or pass `--out samples/Sample.ReleaseServer/maps`.
