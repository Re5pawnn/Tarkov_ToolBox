import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";

const OUTPUT_PATH = resolve(process.argv[2] ?? "map-season-documents.json");
const manifest = JSON.parse(await readFile(OUTPUT_PATH, "utf8"));
let imageCount = 0;

for (const map of manifest.maps ?? []) {
  const documents = Array.isArray(map.documents) ? map.documents : [];
  if (documents.length === 0) continue;

  const response = await fetch(
    `https://member.kaedeori.com/api/tarkov/season-document/location/list?mapId=${encodeURIComponent(map.sourceId)}`,
  );
  if (!response.ok) throw new Error(`${map.key}: HTTP ${response.status}`);

  const payload = await response.json();
  if (payload?.code !== 200 || !Array.isArray(payload?.data?.list))
    throw new Error(`${map.key}: invalid season-document response`);

  const remoteById = new Map(payload.data.list.map((entry) => [String(entry.uuid), entry]));
  for (const document of documents) {
    const remote = remoteById.get(String(document.id));
    const imageUrl = remote?.image?.url;
    if (!remote || typeof imageUrl !== "string" || !imageUrl.startsWith("https://cdn.kaedeori.com/"))
      throw new Error(`${map.key}/${document.id}: location screenshot is missing`);
    document.imageUrl = imageUrl;
    imageCount += 1;
  }
}

manifest.schemaVersion = Math.max(2, Number(manifest.schemaVersion) || 0);
manifest.generatedAt = new Date().toISOString();
await writeFile(OUTPUT_PATH, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
console.log(`Attached ${imageCount} season-document screenshots to ${OUTPUT_PATH}`);
