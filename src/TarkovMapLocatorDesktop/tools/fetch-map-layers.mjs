import { createHash } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";

const VERSION = "4.6.0";
const OUTPUT_PATH = resolve(process.argv[2] ?? "map-layers.json");
const SOCKET_URL =
  `wss://member.kaedeori.com/socket.io/?channel=tarkov&version=${VERSION}` +
  `&token=&hash=${createHash("md5").update(`tarkov${VERSION}`).digest("hex")}` +
  "&EIO=4&transport=websocket";

const localMapByName = new Map([
  ["海关", "customs"],
  ["塔科夫街区", "streets-of-tarkov"],
  ["立交桥", "interchange"],
  ["海岸线", "shoreline"],
  ["储备站", "reserve"],
  ["中心区", "ground-zero"],
  ["工厂", "factory"],
  ["实验室", "the-lab"],
]);

const debugMapPrefix = new Map([
  ["customs", "Customs"],
  ["streets-of-tarkov", "StreetsOfTarkov"],
  ["interchange", "Interchange"],
  ["shoreline", "Shoreline"],
  ["reserve", "Reserve"],
  ["ground-zero", "GroundZero"],
  ["factory", "Factory"],
  ["the-lab", "Labs"],
]);

const translatedLayerNames = new Map([
  ["Underground", "地下层"],
  ["Underground Level", "地下层"],
  ["Basement", "地下层"],
  ["Tunnels", "地下隧道"],
  ["Garage", "地下车库"],
  ["Bunkers", "地下掩体"],
  ["First Floor", "一层"],
  ["Second Level", "二层"],
  ["2nd Floor", "二层"],
  ["Second Floor", "二层"],
  ["3rd Floor", "三层"],
  ["Third Floor", "三层"],
  ["4th Floor", "四层"],
  ["Fourth Floor", "四层"],
  ["5th Floor", "五层"],
  ["Fifth Floor", "五层"],
  ["Technical", "技术层"],
]);

let nextAckId = 1;
const pendingAcks = new Map();

function unwrap(value) {
  let current = value;
  for (let depth = 0; depth < 6; depth += 1) {
    if (Array.isArray(current) && current.length === 1) {
      current = current[0];
      continue;
    }
    if (current && typeof current === "object") {
      if ("data" in current && current.data !== undefined) {
        current = current.data;
        continue;
      }
      if ("result" in current && current.result !== undefined) {
        current = current.result;
        continue;
      }
    }
    break;
  }
  return current;
}

function emitWithAck(socket, event, payload) {
  const id = nextAckId++;
  return new Promise((resolveAck, rejectAck) => {
    const timer = setTimeout(() => {
      pendingAcks.delete(id);
      rejectAck(new Error(`API timeout: ${event}`));
    }, 30_000);
    pendingAcks.set(id, {
      resolve(value) {
        clearTimeout(timer);
        resolveAck(unwrap(value));
      },
    });
    socket.send(`42${id}${JSON.stringify([event, payload])}`);
  });
}

function connect() {
  return new Promise((resolveSocket, rejectSocket) => {
    const socket = new WebSocket(SOCKET_URL);
    const timer = setTimeout(() => rejectSocket(new Error("Socket connection timed out.")), 20_000);
    let namespaceReady = false;

    socket.addEventListener("message", (event) => {
      const message = String(event.data);
      if (message.startsWith("0")) {
        socket.send("40");
        return;
      }
      if (message === "2") {
        socket.send("3");
        return;
      }
      if (message.startsWith("40") && !namespaceReady) {
        namespaceReady = true;
        clearTimeout(timer);
        resolveSocket(socket);
        return;
      }
      if (!message.startsWith("43")) return;

      const match = /^43(\d+)(.*)$/s.exec(message);
      if (!match) return;
      const id = Number(match[1]);
      const pending = pendingAcks.get(id);
      if (!pending) return;
      pendingAcks.delete(id);
      pending.resolve(JSON.parse(match[2]));
    });

    socket.addEventListener("error", () => {
      clearTimeout(timer);
      rejectSocket(new Error("Socket connection failed."));
    });
  });
}

function slug(value) {
  return value
    .replace(/([a-z])([A-Z])/g, "$1-$2")
    .replace(/[^a-zA-Z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .toLowerCase();
}

function pair(value, fallback) {
  return Array.isArray(value) && value.length >= 2
    ? [Number(value[0]), Number(value[1])]
    : fallback;
}

function normalizeBounds(rawBounds) {
  if (!Array.isArray(rawBounds)) return [];
  return rawBounds
    .map((entry) => {
      if (!Array.isArray(entry) || entry.length < 2) return null;
      const first = pair(entry[0], null);
      const second = pair(entry[1], null);
      if (!first || !second) return null;
      return [first, second];
    })
    .filter(Boolean);
}

function normalizeExtents(rawExtents) {
  if (!Array.isArray(rawExtents)) return [];
  return rawExtents.map((extent) => ({
    height: pair(extent?.height, [-10000, 10000]),
    bounds: normalizeBounds(extent?.bounds),
  }));
}

function normalizeLayer(mapId, layer, index) {
  const sourceName = String(layer?.name ?? `Layer ${index + 1}`);
  const id = slug(sourceName) || `layer-${index + 1}`;
  const svgLayer = typeof layer?.svgLayer === "string" ? layer.svgLayer : null;
  const debugPrefix = debugMapPrefix.get(mapId);
  return {
    id,
    name: translatedLayerNames.get(sourceName) ?? sourceName,
    sourceName,
    image: `${mapId}--${id}.png`,
    tilePath: typeof layer?.tilePath === "string" ? layer.tilePath : null,
    svgLayer,
    debugAsset: svgLayer && debugPrefix ? `${debugPrefix}-${svgLayer}.png` : null,
    extents: normalizeExtents(layer?.extents),
  };
}

function normalizeMap(detail) {
  const mapId = localMapByName.get(detail.name);
  if (!mapId || !Array.isArray(detail.layers) || detail.layers.length === 0) return null;
  const layers = detail.layers
    .map((layer, index) => normalizeLayer(mapId, layer, index))
    .filter((layer) => mapId !== "reserve" || layer.id === "bunkers");
  return {
    id: mapId,
    sourceId: detail.id,
    sourceName: detail.name,
    surfaceHeight: pair(detail.heightRange ?? detail._heightRange, [-10000, 10000]),
    surface: {
      tilePath: typeof detail.tilePath === "string" ? detail.tilePath : null,
      svgPath: typeof detail.svgPath === "string" ? detail.svgPath : null,
      svgLayer: typeof detail.svgLayer === "string" ? detail.svgLayer : null,
    },
    projection: {
      minZoom: Number(detail.minZoom ?? 1),
      maxZoom: Number(detail.maxZoom ?? 5),
      transform: Array.isArray(detail.transform) ? detail.transform.map(Number) : [1, 0, 1, 0],
      coordinateRotation: Number(detail.coordinateRotation ?? 0),
      bounds: Array.isArray(detail.bounds) ? detail.bounds.map((value) => pair(value, [0, 0])) : [],
    },
    layers,
  };
}

const socket = await connect();
try {
  const mapList = await emitWithAck(socket, "/v2/tarkov/iMGetMapList", {
    lang: "zh-CN",
    gameMode: "pvp",
  });
  if (!Array.isArray(mapList)) throw new Error("Map list response is not an array.");

  const maps = [];
  for (const summary of mapList) {
    if (!localMapByName.has(summary.name)) continue;
    const detail = await emitWithAck(socket, "/v2/tarkov/iMGetMapDetail", {
      id: summary.id,
      lang: "zh-CN",
      gameMode: "pvp",
    });
    const normalized = normalizeMap(detail);
    if (normalized) maps.push(normalized);
  }

  maps.sort((left, right) => left.id.localeCompare(right.id));
  const manifest = {
    schemaVersion: 1,
    source: "https://member.kaedeori.com/app/interactive",
    sourceVersion: VERSION,
    generatedAt: new Date().toISOString(),
    maps,
  };
  await mkdir(dirname(OUTPUT_PATH), { recursive: true });
  await writeFile(OUTPUT_PATH, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
  console.log(`Wrote ${maps.length} layered maps to ${OUTPUT_PATH}`);
  for (const map of maps) console.log(`${map.id}: ${map.layers.length} layers`);
} finally {
  socket.close();
}
