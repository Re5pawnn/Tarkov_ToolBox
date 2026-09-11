import { createHash } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";

const VERSION = "4.6.0";
const OUTPUT_PATH = resolve(process.argv[2] ?? "map-switches.json");
const SOCKET_URL =
  `wss://member.kaedeori.com/socket.io/?channel=tarkov&version=${VERSION}` +
  `&token=&hash=${createHash("md5").update(`tarkov${VERSION}`).digest("hex")}` +
  "&EIO=4&transport=websocket";

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

const socket = await connect();
try {
  const mapList = await emitWithAck(socket, "/v2/tarkov/iMGetMapList", {
    lang: "zh-CN",
    gameMode: "pvp",
  });
  if (!Array.isArray(mapList)) throw new Error("Map list response is not an array.");

  const maps = [];
  for (const summary of mapList) {
    const detail = await emitWithAck(socket, "/v2/tarkov/iMGetMapDetail", {
      id: summary.id,
      lang: "zh-CN",
      gameMode: "pvp",
    });
    const switches = Array.isArray(detail?.switches) ? detail.switches : [];
    if (switches.length === 0) continue;
    maps.push({
      sourceId: String(summary.id),
      name: String(summary.name ?? detail?.name ?? ""),
      key: String(detail?.key ?? ""),
      bounds: Array.isArray(detail?.bounds) ? detail.bounds : [],
      coordinateRotation: Number(detail?.coordinateRotation ?? 0),
      reverseCoordinate: Boolean(detail?.reverseCoordinate ?? false),
      switches,
    });
  }

  const manifest = {
    schemaVersion: 1,
    source: "https://kaedeori.com/app/interactive",
    sourceVersion: VERSION,
    generatedAt: new Date().toISOString(),
    maps,
  };
  await mkdir(dirname(OUTPUT_PATH), { recursive: true });
  await writeFile(OUTPUT_PATH, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
  console.log(`Wrote ${maps.reduce((sum, map) => sum + map.switches.length, 0)} switches across ${maps.length} maps to ${OUTPUT_PATH}`);
  for (const map of maps) console.log(`${map.name} (${map.key}): ${map.switches.length}`);
} finally {
  socket.close();
}
