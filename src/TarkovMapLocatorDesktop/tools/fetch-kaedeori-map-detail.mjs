import { createHash } from "node:crypto";
import { writeFile } from "node:fs/promises";
import { resolve } from "node:path";

const VERSION = "4.6.0";
const MAP_ID = process.argv[2];
const OUTPUT_PATH = resolve(process.argv[3] ?? `${MAP_ID}.online.json`);
if (!MAP_ID) throw new Error("Usage: node fetch-kaedeori-map-detail.mjs <map-id> [output-path]");

const socketUrl =
  `wss://member.kaedeori.com/socket.io/?channel=tarkov&version=${VERSION}` +
  `&token=&hash=${createHash("md5").update(`tarkov${VERSION}`).digest("hex")}` +
  "&EIO=4&transport=websocket";
let nextAckId = 1;
const pending = new Map();

function unwrap(value) {
  let current = value;
  for (let depth = 0; depth < 6; depth += 1) {
    if (Array.isArray(current) && current.length === 1) {
      current = current[0];
      continue;
    }
    if (current && typeof current === "object" && current.data !== undefined) {
      current = current.data;
      continue;
    }
    if (current && typeof current === "object" && current.result !== undefined) {
      current = current.result;
      continue;
    }
    break;
  }
  return current;
}

function emitWithAck(socket, event, payload) {
  const id = nextAckId++;
  return new Promise((resolveAck, rejectAck) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      rejectAck(new Error(`API timeout: ${event}`));
    }, 30_000);
    pending.set(id, value => {
      clearTimeout(timer);
      resolveAck(unwrap(value));
    });
    socket.send(`42${id}${JSON.stringify([event, payload])}`);
  });
}

const socket = new WebSocket(socketUrl);
const connected = new Promise((resolveConnected, rejectConnected) => {
  const timer = setTimeout(() => rejectConnected(new Error("Socket connection timed out.")), 20_000);
  socket.addEventListener("message", event => {
    const message = String(event.data);
    if (message.startsWith("0")) {
      socket.send("40");
      return;
    }
    if (message === "2") {
      socket.send("3");
      return;
    }
    if (message.startsWith("40")) {
      clearTimeout(timer);
      resolveConnected();
      return;
    }
    const match = /^43(\d+)(.*)$/s.exec(message);
    if (!match) return;
    const handler = pending.get(Number(match[1]));
    if (!handler) return;
    pending.delete(Number(match[1]));
    handler(JSON.parse(match[2]));
  });
  socket.addEventListener("error", () => rejectConnected(new Error("Socket connection failed.")));
});

await connected;
try {
  const detail = await emitWithAck(socket, "/v2/tarkov/iMGetMapDetail", {
    id: MAP_ID,
    lang: "zh-CN",
    gameMode: "pvp",
  });
  await writeFile(OUTPUT_PATH, `${JSON.stringify({ sourceVersion: VERSION, detail }, null, 2)}\n`, "utf8");
  console.log(`Wrote ${detail?.key ?? MAP_ID} to ${OUTPUT_PATH}`);
} finally {
  socket.close();
}
