// BridgeMap's half in the page: a MapLibre map drawing the maps bridge's tiles with the Protomaps basemap style, and the
// overlays the component adds — pins as DOM markers, lines and areas as GeoJSON layers above the roads and below the
// labels. Everything is loaded from this package, so the map works with no connection.
//
// Objects cross to and from .NET as JSON text, serialized there by source-generated metadata, so nothing on the .NET
// side needs reflection.
import * as maplibregl from "./maplibre/maplibre-gl.mjs";
import { layers, namedFlavor } from "./basemaps.mjs";

const maps = new Map();

const empty = () => ({ type: "FeatureCollection", features: [] });
const toLngLat = p => [p.longitude, p.latitude];
const fromLngLat = ll => ({ latitude: ll.lat, longitude: ll.lng });

// MapLibre's stylesheet, loaded once per page. Without it markers are not positioned absolutely: each one sits in the
// page's flow under the map instead of at its point, and popups and controls fall out of place the same way.
let stylesheet;
function loadStylesheet() {
    const href = new URL("./maplibre/maplibre-gl.css", import.meta.url).href;
    stylesheet ??= new Promise(resolve => {
        if ([...document.styleSheets].some(s => s.href === href))
            return resolve();

        const link = Object.assign(document.createElement("link"), { rel: "stylesheet", href });
        link.onload = link.onerror = () => resolve();
        document.head.appendChild(link);
    });
    return stylesheet;
}

export async function create(element, dotnet, optionsJson) {
    // Before the map exists, so it measures its container and places its controls with the rules applied.
    await loadStylesheet();

    const options = JSON.parse(optionsJson);
    const origin = document.baseURI;
    const absolute = url => new URL(url, origin).href.replace(/%7B/g, "{").replace(/%7D/g, "}");

    const map = new maplibregl.Map({
        container: element,
        center: [options.longitude, options.latitude],
        zoom: options.zoom,
        attributionControl: { compact: true },
        style: {
            version: 8,
            glyphs: absolute(options.glyphsUrl),
            sprite: absolute(options.spritesUrl + "/" + options.flavor),
            sources: {
                protomaps: {
                    type: "vector",
                    tiles: [absolute(options.tilesUrl)],
                    maxzoom: options.maxZoom,
                    attribution: options.attribution
                }
            },
            layers: layers("protomaps", namedFlavor(options.flavor), { lang: options.language })
        }
    });

    if (options.navigation)
        map.addControl(new maplibregl.NavigationControl(), "top-right");

    const state = { map, dotnet, pins: new Map(), shapes: new Map(), draft: [], mode: "None", ready: false, queue: [], traffic: options.traffic, trafficTimer: null, incidents: options.incidents, incidentsTimer: null };
    maps.set(options.id, state);

    map.on("load", () => {
        addOverlayLayers(map);
        if (options.showTraffic)
            showTraffic(state, true);
        if (options.showIncidents)
            showIncidents(state, true);
        state.ready = true;
        state.queue.splice(0).forEach(run => run());
        dotnet.invokeMethodAsync("JsReady");
    });

    map.on("click", e => onClick(state, e));
    map.on("click", "bridge-incident-points", e => onIncidentClick(state, e));
    map.on("mouseenter", "bridge-incident-points", () => map.getCanvas().style.cursor = "pointer");
    map.on("mouseleave", "bridge-incident-points", () => map.getCanvas().style.cursor = "");
    map.on("dblclick", e => onDoubleClick(state, e));
    map.on("moveend", () => dotnet.invokeMethodAsync("JsMoved", JSON.stringify({ center: fromLngLat(map.getCenter()), zoom: map.getZoom() })));
}

// Calls made before the style loads wait for it: a source cannot be touched earlier.
function whenReady(id, run) {
    const state = maps.get(id);
    if (!state)
        return;

    if (state.ready)
        run(state);
    else
        state.queue.push(() => run(state));
}

// The first symbol layer is where labels start; overlays go under it, so street names stay readable over a route.
function firstLabelLayer(map) {
    return map.getStyle().layers.find(l => l.type === "symbol")?.id;
}

function addOverlayLayers(map) {
    const before = firstLabelLayer(map);
    map.addSource("bridge-shapes", { type: "geojson", data: empty() });
    map.addSource("bridge-draft", { type: "geojson", data: empty() });

    const polygons = ["==", ["geometry-type"], "Polygon"];
    const lines = ["==", ["geometry-type"], "LineString"];

    map.addLayer({
        id: "bridge-fill", type: "fill", source: "bridge-shapes", filter: polygons,
        paint: { "fill-color": ["get", "color"], "fill-opacity": ["get", "fillOpacity"] }
    }, before);
    map.addLayer({
        id: "bridge-outline", type: "line", source: "bridge-shapes", filter: polygons,
        paint: { "line-color": ["get", "color"], "line-width": ["get", "width"] }
    }, before);
    map.addLayer({
        id: "bridge-casing", type: "line", source: "bridge-shapes", filter: ["all", lines, ["get", "casing"]],
        layout: { "line-cap": "round", "line-join": "round" },
        paint: { "line-color": ["get", "casingColor"], "line-width": ["+", ["get", "width"], 4] }
    }, before);
    map.addLayer({
        id: "bridge-line", type: "line", source: "bridge-shapes", filter: lines,
        layout: { "line-cap": "round", "line-join": "round" },
        paint: { "line-color": ["get", "color"], "line-width": ["get", "width"] }
    }, before);
    map.addLayer({
        id: "bridge-draft", type: "line", source: "bridge-draft",
        paint: { "line-color": "#ef4444", "line-width": 2, "line-dasharray": [2, 2] }
    });
}

// Live traffic: its own source, drawn above the roads and below everything the app adds. It is fetched again every
// refreshSeconds, with a new query string so neither MapLibre nor the WebView answers from what it already has.
function showTraffic(state, show) {
    const { map, traffic } = state;
    clearInterval(state.trafficTimer);
    state.trafficTimer = null;

    if (map.getLayer("bridge-traffic"))
        map.removeLayer("bridge-traffic");
    if (map.getSource("bridge-traffic"))
        map.removeSource("bridge-traffic");

    if (!show || !traffic)
        return;

    const url = () => liveUrl(traffic.tilesUrl);
    const vector = traffic.format === "Vector";

    map.addSource("bridge-traffic", {
        type: vector ? "vector" : "raster",
        tiles: [url()],
        minzoom: traffic.minZoom,
        maxzoom: traffic.maxZoom,
        attribution: traffic.attribution,
        ...(vector ? {} : { tileSize: traffic.tileSize })
    });

    const ratio = ["to-number", ["get", traffic.speedRatioProperty], 1];
    const colour = ["step", ratio, "#dc2626", 0.4, "#f97316", 0.75, "#16a34a"];

    map.addLayer(vector
        ? {
            id: "bridge-traffic", type: "line", source: "bridge-traffic", "source-layer": traffic.sourceLayer,
            minzoom: traffic.minZoom,
            layout: { "line-cap": "round", "line-join": "round" },
            paint: {
                "line-color": traffic.closedProperty
                    ? ["case", ["==", ["to-boolean", ["get", traffic.closedProperty]], true], "#7f1d1d", colour]
                    : colour,
                "line-width": ["interpolate", ["linear"], ["zoom"], 6, 1, 12, 2.5, 16, 5]
            }
        }
        : { id: "bridge-traffic", type: "raster", source: "bridge-traffic", minzoom: traffic.minZoom, paint: { "raster-opacity": 0.85 } },
        map.getLayer("bridge-incident-lines") ? "bridge-incident-lines" : "bridge-fill");

    state.trafficTimer = setInterval(() => map.getSource("bridge-traffic")?.setTiles([url()]), traffic.refreshSeconds * 1000);
}

// Incident colours by kind, the same whatever the provider calls them.
const incidentColours = {
    Accident: "#dc2626", Congestion: "#ea580c", RoadWorks: "#d97706", RoadClosed: "#7f1d1d", LaneClosed: "#b45309",
    Weather: "#2563eb", Hazard: "#9333ea", BrokenDownVehicle: "#db2777", Other: "#6b7280"
};
const incidentLayers = ["bridge-incident-lines", "bridge-incident-points", "bridge-incident-labels"];

// A tile URL that neither MapLibre nor the WebView answers from what it already has.
const liveUrl = template => `${new URL(template, document.baseURI).href.replace(/%7B/g, "{").replace(/%7D/g, "}")}?t=${Date.now()}`;

// Incidents: the affected stretch of road as a dashed line, and a marker with "!" — or, for a cluster, how many — that shows
// the description when tapped. Above traffic flow and below everything the app adds.
function showIncidents(state, show) {
    const { map, incidents } = state;
    clearInterval(state.incidentsTimer);
    state.incidentsTimer = null;

    for (const id of incidentLayers)
        if (map.getLayer(id))
            map.removeLayer(id);
    if (map.getSource("bridge-incidents"))
        map.removeSource("bridge-incidents");

    if (!show || !incidents)
        return;

    map.addSource("bridge-incidents", {
        type: "vector", tiles: [liveUrl(incidents.tilesUrl)], minzoom: incidents.minZoom, maxzoom: incidents.maxZoom, attribution: incidents.attribution
    });

    // The provider's value, as text, to a kind; then the kind to a colour.
    const kinds = Object.entries(incidents.kinds ?? {});
    const kind = kinds.length ? ["match", ["to-string", ["get", incidents.kindProperty]], ...kinds.flat(), "Other"] : "Other";
    const colour = ["match", kind, ...Object.entries(incidentColours).filter(([k]) => k !== "Other").flat(), incidentColours.Other];

    if (incidents.lineSourceLayer)
        map.addLayer({
            id: "bridge-incident-lines", type: "line", source: "bridge-incidents", "source-layer": incidents.lineSourceLayer,
            minzoom: incidents.minZoom,
            layout: { "line-cap": "round" },
            paint: { "line-color": colour, "line-width": ["interpolate", ["linear"], ["zoom"], 8, 2, 16, 6], "line-dasharray": [1, 1.5] }
        }, "bridge-fill");

    if (incidents.pointSourceLayer) {
        const cluster = incidents.clusterSizeProperty;
        const isCluster = cluster ? ["has", cluster] : false;

        map.addLayer({
            id: "bridge-incident-points", type: "circle", source: "bridge-incidents", "source-layer": incidents.pointSourceLayer,
            minzoom: incidents.minZoom,
            paint: {
                "circle-color": colour, "circle-radius": ["case", isCluster, 11, 8],
                "circle-stroke-color": "#ffffff", "circle-stroke-width": 2
            }
        });
        map.addLayer({
            id: "bridge-incident-labels", type: "symbol", source: "bridge-incidents", "source-layer": incidents.pointSourceLayer,
            minzoom: incidents.minZoom,
            layout: {
                "text-field": cluster ? ["case", isCluster, ["to-string", ["get", cluster]], "!"] : "!",
                "text-font": ["Noto Sans Medium"], "text-size": 11, "text-allow-overlap": true, "text-ignore-placement": true
            },
            paint: { "text-color": "#ffffff" }
        });
    }

    state.incidentsTimer = setInterval(() => map.getSource("bridge-incidents")?.setTiles([liveUrl(incidents.tilesUrl)]), incidents.refreshSeconds * 1000);
}

function onIncidentClick(state, e) {
    const { incidents } = state;
    const feature = e.features?.[0];
    if (!feature || state.mode !== "None")
        return;

    const properties = feature.properties ?? {};
    const lines = [];
    if (incidents.descriptionProperty && properties[incidents.descriptionProperty])
        lines.push(properties[incidents.descriptionProperty]);
    if (incidents.clusterSizeProperty && properties[incidents.clusterSizeProperty])
        lines.push(`${properties[incidents.clusterSizeProperty]} incidents — zoom in`);
    if (incidents.delayProperty && Number(properties[incidents.delayProperty]) > 0)
        lines.push(`Delay: ${Math.round(Number(properties[incidents.delayProperty]) / 60)} min`);
    if (!lines.length)
        return;

    const content = document.createElement("div");
    for (const text of lines)
        content.appendChild(Object.assign(document.createElement("div"), { textContent: text }));

    new maplibregl.Popup({ closeButton: false, maxWidth: "260px" }).setLngLat(e.lngLat).setDOMContent(content).addTo(state.map);
}

export function setIncidents(id, show) {
    whenReady(id, state => showIncidents(state, show));
}

export function setTraffic(id, show) {
    whenReady(id, state => showTraffic(state, show));
}

function refresh(state) {
    state.map.getSource("bridge-shapes")?.setData({ type: "FeatureCollection", features: [...state.shapes.values()] });
    const draft = state.draft.length > 1 ? [{ type: "Feature", properties: {}, geometry: { type: "LineString", coordinates: state.draft } }] : [];
    state.map.getSource("bridge-draft")?.setData({ type: "FeatureCollection", features: draft });
}

export function addPin(id, pinJson) {
    const pin = JSON.parse(pinJson);
    whenReady(id, state => {
        removePin(id, pin.id);

        const marker = new maplibregl.Marker({ color: pin.color, draggable: pin.draggable })
            .setLngLat(toLngLat(pin.position))
            .addTo(state.map);

        if (pin.label)
            marker.setPopup(new maplibregl.Popup({ offset: 24, closeButton: false }).setText(pin.label));

        marker.getElement().addEventListener("click", e => {
            e.stopPropagation();
            state.dotnet.invokeMethodAsync("JsPinClicked", pin.id);
        });
        marker.on("dragend", () => state.dotnet.invokeMethodAsync("JsPinMoved", pin.id, JSON.stringify(fromLngLat(marker.getLngLat()))));
        state.pins.set(pin.id, marker);
    });
}

export function removePin(id, pinId) {
    const state = maps.get(id);
    state?.pins.get(pinId)?.remove();
    state?.pins.delete(pinId);
}

export function addShape(id, shapeJson) {
    const shape = JSON.parse(shapeJson);
    whenReady(id, state => {
        const coordinates = shape.points.map(toLngLat);
        const polygon = shape.kind === "Polygon";

        state.shapes.set(shape.id, {
            type: "Feature",
            id: shape.id,
            properties: {
                id: shape.id,
                label: shape.label ?? "",
                color: shape.color,
                width: shape.width,
                fillOpacity: shape.fillOpacity,
                casing: !!shape.casingColor,
                casingColor: shape.casingColor ?? shape.color
            },
            geometry: polygon
                ? { type: "Polygon", coordinates: [[...coordinates, coordinates[0]]] }
                : { type: "LineString", coordinates }
        });
        refresh(state);
    });
}

export function removeShape(id, shapeId) {
    whenReady(id, state => {
        state.shapes.delete(shapeId);
        refresh(state);
    });
}

export function clear(id) {
    whenReady(id, state => {
        state.pins.forEach(m => m.remove());
        state.pins.clear();
        state.shapes.clear();
        state.draft = [];
        refresh(state);
    });
}

export function setDrawMode(id, mode) {
    whenReady(id, state => {
        state.mode = mode;
        state.draft = [];

        // A double-click finishes a line or an area, so it cannot zoom while drawing one.
        const drawing = mode === "Line" || mode === "Polygon";
        state.map.doubleClickZoom[drawing ? "disable" : "enable"]();
        state.map.getCanvas().style.cursor = mode === "None" ? "" : "crosshair";
        refresh(state);
    });
}

function onClick(state, e) {
    const point = fromLngLat(e.lngLat);

    if (state.mode === "Line" || state.mode === "Polygon") {
        state.draft.push([e.lngLat.lng, e.lngLat.lat]);
        refresh(state);
        return;
    }

    state.dotnet.invokeMethodAsync("JsClicked", JSON.stringify(point), state.mode === "Pin");
}

function onDoubleClick(state, e) {
    const needed = state.mode === "Line" ? 2 : state.mode === "Polygon" ? 3 : 0;
    if (!needed)
        return;

    e.preventDefault();

    // The double-click's own two clicks each added the same point; one is enough.
    const points = state.draft.filter((p, i, all) => i === 0 || p[0] !== all[i - 1][0] || p[1] !== all[i - 1][1]);
    state.draft = [];
    refresh(state);

    if (points.length >= needed)
        state.dotnet.invokeMethodAsync("JsDrawn", state.mode, JSON.stringify(points.map(p => ({ latitude: p[1], longitude: p[0] }))));
}

export function flyTo(id, latitude, longitude, zoom) {
    whenReady(id, state => state.map.flyTo({ center: [longitude, latitude], zoom: zoom ?? state.map.getZoom() }));
}

export function fitBounds(id, west, south, east, north, padding) {
    const bounds = [west, south, east, north];
    whenReady(id, state => state.map.fitBounds([[bounds[0], bounds[1]], [bounds[2], bounds[3]]], { padding, duration: 0 }));
}

export function fitAll(id, padding) {
    whenReady(id, state => {
        const coords = [
            ...[...state.pins.values()].map(m => m.getLngLat().toArray()),
            ...[...state.shapes.values()].flatMap(f => f.geometry.type === "Polygon" ? f.geometry.coordinates[0] : f.geometry.coordinates)
        ];
        if (!coords.length)
            return;

        const bounds = coords.reduce((b, c) => b.extend(c), new maplibregl.LngLatBounds(coords[0], coords[0]));
        state.map.fitBounds(bounds, { padding, duration: 0 });
    });
}

// The map as a PNG data URL, pins included. For sharing, printing, or looking at a map whose window is off screen.
export function snapshot(id) {
    const state = maps.get(id);
    if (!state)
        return Promise.resolve(null);

    return new Promise(resolve => {
        state.map.once("render", () => {
            const source = state.map.getCanvas();
            const canvas = Object.assign(document.createElement("canvas"), { width: source.width, height: source.height });
            const g = canvas.getContext("2d");
            g.drawImage(source, 0, 0);

            const scale = source.width / source.clientWidth;
            for (const marker of state.pins.values()) {
                const p = state.map.project(marker.getLngLat());
                g.fillStyle = marker.getElement().querySelector("path")?.getAttribute("fill") ?? "#e11d48";
                g.beginPath();
                g.arc(p.x * scale, (p.y - 14) * scale, 8 * scale, 0, 2 * Math.PI);
                g.fill();
            }

            resolve(canvas.toDataURL("image/png"));
        });
        state.map.triggerRepaint();
    });
}

export function dispose(id) {
    const state = maps.get(id);
    if (!state)
        return;

    clearInterval(state.trafficTimer);
    clearInterval(state.incidentsTimer);
    state.pins.forEach(m => m.remove());
    state.map.remove();
    maps.delete(id);
}
