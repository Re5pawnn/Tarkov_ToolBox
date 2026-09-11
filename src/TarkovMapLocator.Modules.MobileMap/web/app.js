(() => {
  "use strict";

  const viewport = document.getElementById("viewport");
  const scene = document.getElementById("scene");
  const baseMap = document.getElementById("base-map");
  const layerMap = document.getElementById("layer-map");
  const markers = document.getElementById("markers");
  const emptyState = document.getElementById("empty-state");
  const connection = document.getElementById("connection");
  const mapLoad = document.getElementById("map-load");
  const mapLoadBar = document.getElementById("map-load-bar");
  const mapLoadPercent = document.getElementById("map-load-percent");
  const mapName = document.getElementById("map-name");
  const layerName = document.getElementById("layer-name");
  const updateText = document.getElementById("update-text");
  const updateState = document.querySelector(".update-state");
  const bottomRail = document.getElementById("bottom-rail");
  const filterInputs = [...document.querySelectorAll("[data-filter]")];

  const view = { x: 0, y: 0, scale: 1, fitScale: 1, width: 1, height: 1 };
  const pointers = new Map();
  let gesture = null;
  let snapshot = null;
  let currentBaseKey = "";
  let currentLayerKey = "";
  let eventSource = null;
  let markerElements = [];
  let resetAfterImageLoad = true;
  let baseLoadVersion = 0;
  let layerLoadVersion = 0;
  let resizeTimer = 0;
  let mapLoadHideTimer = 0;
  let baseLoadController = null;
  let layerLoadController = null;
  let baseObjectUrl = "";
  let layerObjectUrl = "";

  const assetUrl = (key) => `/asset/${encodeURIComponent(key)}`;

  function selectAssetKeys(value) {
    const physicalWidth = Math.max(viewport.clientWidth, window.innerWidth || 0) * Math.min(Math.max(window.devicePixelRatio || 1, 1), 3);
    const wantsSharp = physicalWidth >= 1500;
    const baseKey = wantsSharp
      ? value.sharpBaseAssetKey || value.compactBaseAssetKey || value.baseAssetKey
      : value.compactBaseAssetKey || value.sharpBaseAssetKey || value.baseAssetKey;
    const layerKey = wantsSharp
      ? value.sharpLayerAssetKey || value.compactLayerAssetKey || value.layerAssetKey
      : value.compactLayerAssetKey || value.sharpLayerAssetKey || value.layerAssetKey;
    const tier = baseKey === value.sharpBaseAssetKey
      ? "sharp"
      : baseKey === value.compactBaseAssetKey ? "compact" : "original";
    return { baseKey: baseKey || "", layerKey: layerKey || "", tier };
  }

  function setConnection(state, text) {
    connection.className = `connection ${state}`;
    connection.lastElementChild.textContent = text;
  }

  function setMapLoadProgress(percent, state = "loading") {
    window.clearTimeout(mapLoadHideTimer);
    const safePercent = Math.min(100, Math.max(0, Math.round(percent)));
    mapLoad.hidden = false;
    mapLoad.dataset.state = state;
    mapLoadBar.style.width = `${safePercent}%`;
    mapLoadPercent.textContent = state === "error" ? "失败" : `${safePercent}%`;
  }

  function finishMapLoad(version) {
    if (version !== baseLoadVersion) return;
    setMapLoadProgress(100, "complete");
    mapLoadHideTimer = window.setTimeout(() => {
      if (version !== baseLoadVersion) return;
      mapLoad.hidden = true;
      mapLoad.dataset.state = "idle";
    }, 420);
  }

  function failMapLoad(version) {
    if (version !== baseLoadVersion) return;
    setMapLoadProgress(0, "error");
  }

  async function fetchAssetObjectUrl(key, signal, onProgress) {
    const response = await fetch(assetUrl(key), { cache: "default", signal });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const total = Number(response.headers.get("content-length")) || 0;
    if (!response.body || total <= 0) {
      onProgress?.(0);
      const blob = await response.blob();
      onProgress?.(100);
      return URL.createObjectURL(blob);
    }

    const reader = response.body.getReader();
    const chunks = [];
    let received = 0;
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      chunks.push(value);
      received += value.byteLength;
      onProgress?.(received / total * 100);
    }
    const contentType = response.headers.get("content-type") || "application/octet-stream";
    return URL.createObjectURL(new Blob(chunks, { type: contentType }));
  }

  function setTransform() {
    scene.style.transform = `translate(${view.x}px, ${view.y}px) scale(${view.scale})`;
    const inverse = 1 / Math.max(view.scale, .001);
    markerElements.forEach((element) => {
      element.style.transform = `translate(-50%, -50%) scale(${inverse})`;
    });
  }

  function fitMap(centerPlayer = false) {
    if (!view.width || !view.height) return;
    const bounds = viewport.getBoundingClientRect();
    view.fitScale = Math.min(bounds.width / view.width, bounds.height / view.height) * .96;
    view.scale = centerPlayer ? Math.min(Math.max(view.fitScale * 2.1, view.fitScale), view.fitScale * 6) : view.fitScale;
    let focusX = view.width / 2;
    let focusY = view.height / 2;
    if (centerPlayer && snapshot) {
      const player = snapshot.markers.find((marker) => marker.type === "player" || marker.type === "player-stale");
      if (player) {
        focusX = player.x * view.width;
        focusY = player.y * view.height;
      }
    }
    view.x = bounds.width / 2 - focusX * view.scale;
    view.y = bounds.height / 2 - focusY * view.scale;
    setTransform();
  }

  function zoomAt(nextScale, screenX, screenY) {
    const minimum = view.fitScale * .72;
    const maximum = Math.max(view.fitScale * 9, 1.5);
    nextScale = Math.min(maximum, Math.max(minimum, nextScale));
    const worldX = (screenX - view.x) / view.scale;
    const worldY = (screenY - view.y) / view.scale;
    view.x = screenX - worldX * nextScale;
    view.y = screenY - worldY * nextScale;
    view.scale = nextScale;
    setTransform();
  }

  function markerGroup(type) {
    if (type === "extract" || type === "transit") return "extract";
    if (type === "task") return "task";
    if (type === "player" || type === "player-stale" || type === "player-preview") return "player";
    if (type === "peer") return "peer";
    return null;
  }

  function isGroupVisible(group) {
    return filterInputs.find((input) => input.dataset.filter === group)?.checked !== false;
  }

  function createDirectionArrow(heading) {
    const safeHeading = Number.isFinite(Number(heading)) ? Number(heading) : 0;
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", "direction-arrow");
    svg.setAttribute("viewBox", "0 0 16 18");
    svg.setAttribute("aria-hidden", "true");
    svg.style.transform = `rotate(${safeHeading}deg)`;
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", "M8 0L16 8H11.5V18H4.5V8H0Z");
    svg.appendChild(path);
    return svg;
  }

  function renderMarkers() {
    markers.replaceChildren();
    markerElements = [];
    if (!snapshot) return;
    const fragment = document.createDocumentFragment();
    let extractLabelIndex = 0;
    snapshot.markers.forEach((marker) => {
      const group = markerGroup(marker.type);
      if (!group) return;
      if (!isGroupVisible(group)) return;
      const element = document.createElement("button");
      element.type = "button";
      element.className = `map-marker ${marker.type}`;
      element.dataset.group = group;
      element.style.left = `${marker.x * 100}%`;
      element.style.top = `${marker.y * 100}%`;
      if (marker.colorHex && /^#[0-9a-f]{6}$/i.test(marker.colorHex)) element.style.setProperty("--marker-color", marker.colorHex);
      element.setAttribute("aria-label", marker.label || "地图点位");
      if (marker.headingDegrees !== null && marker.headingDegrees !== undefined) element.appendChild(createDirectionArrow(marker.headingDegrees));
      const label = document.createElement("span");
      label.className = "marker-label";
      label.textContent = marker.label || "地图点位";
      if (group === "extract") {
        const labelIndex = extractLabelIndex++;
        const placement = marker.x < .12 ? "right" : marker.x > .88 ? "left" : labelIndex % 2 === 0 ? "above" : "below";
        label.classList.add(`marker-label-${placement}`);
      }
      element.appendChild(label);
      markerElements.push(element);
      fragment.appendChild(element);
    });
    markers.appendChild(fragment);
    setTransform();
  }

  function applyImage(key, image, isLayer) {
    const version = isLayer ? ++layerLoadVersion : ++baseLoadVersion;
    const previousController = isLayer ? layerLoadController : baseLoadController;
    previousController?.abort();
    const controller = new AbortController();
    if (isLayer) layerLoadController = controller;
    else {
      baseLoadController = controller;
      setMapLoadProgress(0);
    }
    if (!key) {
      image.removeAttribute("src");
      if (isLayer) image.style.display = "none";
      else mapLoad.hidden = true;
      return;
    }
    void (async () => {
      let objectUrl = "";
      try {
        objectUrl = await fetchAssetObjectUrl(
          key,
          controller.signal,
          isLayer ? null : percent => setMapLoadProgress(percent));
        if (version !== (isLayer ? layerLoadVersion : baseLoadVersion)) {
          URL.revokeObjectURL(objectUrl);
          return;
        }
        const preload = new Image();
        preload.decoding = "async";
        await new Promise((resolve, reject) => {
          preload.addEventListener("load", resolve, { once: true });
          preload.addEventListener("error", () => reject(new Error("地图图片解码失败")), { once: true });
          preload.src = objectUrl;
        });
        if (version !== (isLayer ? layerLoadVersion : baseLoadVersion)) {
          URL.revokeObjectURL(objectUrl);
          return;
        }
        const previousObjectUrl = isLayer ? layerObjectUrl : baseObjectUrl;
        if (isLayer) layerObjectUrl = objectUrl;
        else baseObjectUrl = objectUrl;
        image.src = objectUrl;
        if (previousObjectUrl) URL.revokeObjectURL(previousObjectUrl);
        if (isLayer) image.style.display = "block";
      } catch (error) {
        if (error?.name === "AbortError" || version !== (isLayer ? layerLoadVersion : baseLoadVersion)) return;
        if (objectUrl) URL.revokeObjectURL(objectUrl);
        if (isLayer) {
          image.removeAttribute("src");
          image.style.display = "none";
        } else {
          failMapLoad(version);
          currentBaseKey = "";
          markers.style.visibility = "visible";
          emptyState.style.display = "block";
          emptyState.textContent = "地图加载失败，请稍后重试";
        }
      }
    })();
  }

  function applySnapshot(envelope) {
    if (!envelope || !envelope.snapshot) return;
    const next = envelope.snapshot;
    const selectedAssets = selectAssetKeys(next);
    const mapChanged = selectedAssets.baseKey !== currentBaseKey;
    snapshot = next;
    scene.dataset.assetTier = selectedAssets.tier;
    mapName.textContent = next.mapName || "等待地图";
    layerName.textContent = next.layerName || "地面";
    updateText.textContent = next.hasLivePosition
      ? next.isPositionStale ? "坐标已过期" : "坐标已更新"
      : next.isListening ? "等待坐标" : "电脑端未监听";
    updateState.classList.toggle("live", next.hasLivePosition && !next.isPositionStale);
    baseMap.style.opacity = selectedAssets.layerKey && next.layerDimsBaseMap ? ".14" : ".94";

    if (mapChanged) {
      currentBaseKey = selectedAssets.baseKey;
      resetAfterImageLoad = true;
      markers.style.visibility = "hidden";
      applyImage(currentBaseKey, baseMap, false);
    }
    if (selectedAssets.layerKey !== currentLayerKey) {
      currentLayerKey = selectedAssets.layerKey;
      applyImage(currentLayerKey, layerMap, true);
    }
    emptyState.style.display = currentBaseKey ? "none" : "block";
    scene.style.visibility = currentBaseKey ? "visible" : "hidden";
    if (!mapChanged) renderMarkers();
  }

  async function fetchSnapshot() {
    const response = await fetch("/api/snapshot", { cache: "no-store" });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    applySnapshot(await response.json());
  }

  function connect() {
    setConnection("", "正在连接");
    fetchSnapshot().catch(() => {});
    eventSource?.close();
    eventSource = new EventSource("/api/events");
    eventSource.addEventListener("open", () => setConnection("live", "实时连接"));
    eventSource.addEventListener("snapshot", (event) => {
      try { applySnapshot(JSON.parse(event.data)); } catch { setConnection("offline", "数据异常"); }
    });
    eventSource.addEventListener("error", () => setConnection("offline", "连接中断，正在重连"));
  }

  baseMap.addEventListener("load", () => {
    view.width = Math.max(1, baseMap.naturalWidth);
    view.height = Math.max(1, baseMap.naturalHeight);
    scene.style.width = `${view.width}px`;
    scene.style.height = `${view.height}px`;
    if (resetAfterImageLoad) {
      resetAfterImageLoad = false;
      fitMap(false);
    }
    renderMarkers();
    markers.style.visibility = "visible";
    finishMapLoad(baseLoadVersion);
  });

  viewport.addEventListener("pointerdown", (event) => {
    if (event.target.closest(".zoom-controls")) return;
    viewport.setPointerCapture(event.pointerId);
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    viewport.classList.add("dragging");
    if (pointers.size === 1) gesture = { type: "pan", x: event.clientX, y: event.clientY, viewX: view.x, viewY: view.y };
    if (pointers.size === 2) {
      const [a, b] = [...pointers.values()];
      const cx = (a.x + b.x) / 2;
      const cy = (a.y + b.y) / 2;
      gesture = {
        type: "pinch",
        distance: Math.hypot(a.x - b.x, a.y - b.y),
        scale: view.scale,
        worldX: (cx - view.x) / view.scale,
        worldY: (cy - view.y) / view.scale
      };
    }
  });

  viewport.addEventListener("pointermove", (event) => {
    if (!pointers.has(event.pointerId)) return;
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (pointers.size === 1 && gesture?.type === "pan") {
      view.x = gesture.viewX + event.clientX - gesture.x;
      view.y = gesture.viewY + event.clientY - gesture.y;
      setTransform();
    } else if (pointers.size === 2 && gesture?.type === "pinch") {
      const [a, b] = [...pointers.values()];
      const cx = (a.x + b.x) / 2;
      const cy = (a.y + b.y) / 2;
      const distance = Math.max(1, Math.hypot(a.x - b.x, a.y - b.y));
      const nextScale = Math.min(Math.max(gesture.scale * distance / Math.max(1, gesture.distance), view.fitScale * .72), Math.max(view.fitScale * 9, 1.5));
      view.scale = nextScale;
      view.x = cx - gesture.worldX * nextScale;
      view.y = cy - gesture.worldY * nextScale;
      setTransform();
    }
  });

  function releasePointer(event) {
    pointers.delete(event.pointerId);
    if (pointers.size === 0) {
      gesture = null;
      viewport.classList.remove("dragging");
    } else if (pointers.size === 1) {
      const point = [...pointers.values()][0];
      gesture = { type: "pan", x: point.x, y: point.y, viewX: view.x, viewY: view.y };
    }
  }

  viewport.addEventListener("pointerup", releasePointer);
  viewport.addEventListener("pointercancel", releasePointer);
  viewport.addEventListener("wheel", (event) => {
    event.preventDefault();
    const bounds = viewport.getBoundingClientRect();
    zoomAt(view.scale * (event.deltaY < 0 ? 1.16 : 1 / 1.16), event.clientX - bounds.left, event.clientY - bounds.top);
  }, { passive: false });
  viewport.addEventListener("dblclick", (event) => {
    const bounds = viewport.getBoundingClientRect();
    zoomAt(view.scale * 1.7, event.clientX - bounds.left, event.clientY - bounds.top);
  });

  document.getElementById("zoom-in").addEventListener("click", () => zoomAt(view.scale * 1.22, viewport.clientWidth / 2, viewport.clientHeight / 2));
  document.getElementById("zoom-out").addEventListener("click", () => zoomAt(view.scale / 1.22, viewport.clientWidth / 2, viewport.clientHeight / 2));
  document.getElementById("recenter").addEventListener("click", () => fitMap(true));
  document.getElementById("filter-toggle").addEventListener("click", () => bottomRail.classList.toggle("collapsed"));
  filterInputs.forEach((input) => input.addEventListener("change", renderMarkers));
  window.addEventListener("resize", () => {
    window.clearTimeout(resizeTimer);
    resizeTimer = window.setTimeout(() => {
      if (!snapshot) return;
      const selectedAssets = selectAssetKeys(snapshot);
      if (selectedAssets.baseKey !== currentBaseKey || selectedAssets.layerKey !== currentLayerKey)
        applySnapshot({ snapshot });
      else
        fitMap(false);
    }, 140);
  });
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible" && !eventSource) connect();
  });

  if ("wakeLock" in navigator) navigator.wakeLock.request("screen").catch(() => {});
  connect();
})();
