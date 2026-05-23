from __future__ import annotations

import argparse
import json
import math
import os
import re
import socket
import socketserver
import sys
import threading
import time
import uuid
from concurrent.futures import ThreadPoolExecutor
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, urlparse
from urllib.request import Request, urlopen


APP_TITLE = "TarkovMapLocator API"
HTTP_HOST = "127.0.0.1"
DEFAULT_HTTP_PORT = 5173
APP_STATE_DIR_NAME = "TarkovMapLocator"
LOCAL_PATHS_MARKER_NAME = "local-paths.json"
UI_PREFERENCES_NAME = "ui-preferences.json"
STARTUP_LOG_NAME = "startup.log"
SYNC_CLIENT_ID_MARKER_NAME = "sync-client-id.json"
SYNC_DEFAULT_PORT = 39247
SYNC_SOCKET_TIMEOUT_SEC = 4.0
PORT_UNAVAILABLE_EXIT_CODE = 98
PEER_TTL_SEC = 8.0
MAX_FRAME_BYTES = 256 * 1024
MAX_HTTP_BODY_BYTES = 256 * 1024
MARKET_API_URL = "https://api.tarkov.dev/graphql"
MARKET_CACHE_NAME = "market-cache.json"
MARKET_ICON_DIR_NAME = "market-icons"
MARKET_CACHE_TTL_SEC = 12 * 60 * 60
MARKET_QUERY_LIMIT = 6000
MARKET_SEARCH_LIMIT = 80
MARKET_ICON_MAX_BYTES = 2 * 1024 * 1024
TRACKER_CACHE_NAME = "item-tracker-cache.json"
TRACKER_STATE_NAME = "item-tracker-state.json"
TRACKER_REQUIREMENT_PATCH_SOURCE = "embedded-miaomiao-toolbox"
TRACKER_REQUIREMENT_PATCHES_JSON = r'''{"requirements":{"pvp":[{"itemId":"5937ee6486f77408994ba448","item":{"id":"5937ee6486f77408994ba448","name":"\u673a\u68b0\u94a5\u5319","shortName":"\u94a5\u5319","iconLink":"https://assets.tarkov.dev/5937ee6486f77408994ba448-icon.webp","gridImageLink":"https://assets.tarkov.dev/5937ee6486f77408994ba448-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"5936da9e86f7742d65037edf","sourceName":"\u80cc\u666f\u8c03\u67e5","sourceDetail":"\u53d6\u5f97\u6cb9\u7f50\u8f66\u94a5\u5319 [x1]","trader":"Prapor","level":2,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"5938144586f77473c2087145","item":{"id":"5938144586f77473c2087145","name":"\u7b80\u6613\u5de5\u68da\u94a5\u5319","shortName":"\u7b80\u6613\u5de5\u68da","iconLink":"https://assets.tarkov.dev/5938144586f77473c2087145-icon.webp","gridImageLink":"https://assets.tarkov.dev/5938144586f77473c2087145-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"5967530a86f77462ba22226b","sourceName":"\u7f6a\u8bc1","sourceDetail":"\u53d6\u5f97\u5de5\u68da\u7684\u94a5\u5319 [x1]","trader":"Prapor","level":6,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"591afe0186f77431bd616a11","item":{"id":"591afe0186f77431bd616a11","name":"ZB-014\u94a5\u5319","shortName":"ZB-014","iconLink":"https://assets.tarkov.dev/591afe0186f77431bd616a11-icon.webp","gridImageLink":"https://assets.tarkov.dev/591afe0186f77431bd616a11-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"59675d6c86f7740a842fc482","sourceName":"\u86cb\u5377\u51b0\u6dc7\u6dcb","sourceDetail":"\u5728TerraGroup\u5458\u5de5\u7684\u5bbf\u820d\u623f\u95f4\u627e\u5230\u5730\u5821\u7684\u94a5\u5319 [x1]","trader":"Prapor","level":9,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"5448bd6b4bdc2dfc2f8b4569","item":{"id":"5448bd6b4bdc2dfc2f8b4569","name":"\u9a6c\u5361\u6d1b\u592b9x18PM \u624b\u67aa","shortName":"PM","iconLink":"https://assets.tarkov.dev/5448bd6b4bdc2dfc2f8b4569-icon.webp","gridImageLink":"https://assets.tarkov.dev/5448bd6b4bdc2dfc2f8b4569-grid-image.webp"},"count":2,"foundInRaid":true,"sourceType":"task","sourceId":"59ca29fb86f77445ab465c87","sourceName":"\u60e9\u7f5a\u8005 - 5","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u5230\u4efb\u610f\u7248\u672c\u7684 PM \u624b\u67aa [x2] / \u4e0a\u4ea4\u624b\u67aa [x2]","trader":"Prapor","level":20,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"5644bd2b4bdc2d3b4c8b4572","item":{"id":"5644bd2b4bdc2d3b4c8b4572","name":"AK-74N 5.45x39 \u7a81\u51fb\u6b65\u67aa","shortName":"AK-74N","iconLink":"https://assets.tarkov.dev/5644bd2b4bdc2d3b4c8b4572-icon.webp","gridImageLink":"https://assets.tarkov.dev/5644bd2b4bdc2d3b4c8b4572-grid-image.webp"},"count":1,"foundInRaid":true,"sourceType":"task","sourceId":"59ca29fb86f77445ab465c87","sourceName":"\u60e9\u7f5a\u8005 - 5","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u5230\u4efb\u610f\u7248\u672c\u7684 AK-74 \u7cfb\u5217\u7a81\u51fb\u6b65\u67aa [x1] / \u4e0a\u4ea4 AK-74 \u7cfb\u5217\u7a81\u51fb\u6b65\u67aa [x1]","trader":"Prapor","level":20,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32bb586f774757e1e8442","item":{"id":"59f32bb586f774757e1e8442","name":"BEAR \u72d7\u724c","shortName":"BEAR","iconLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-grid-image.webp"},"count":7,"foundInRaid":false,"sourceType":"task","sourceId":"59ca2eb686f77445a80ed049","sourceName":"\u60e9\u7f5a\u8005 - 6","sourceDetail":"\u4e0a\u4ea4BEAR PMC\u72d7\u724c [x7]","trader":"Prapor","level":21,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32c3b86f77472a31742f0","item":{"id":"59f32c3b86f77472a31742f0","name":"USEC \u72d7\u724c","shortName":"USEC","iconLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-grid-image.webp"},"count":7,"foundInRaid":false,"sourceType":"task","sourceId":"59ca2eb686f77445a80ed049","sourceName":"\u60e9\u7f5a\u8005 - 6","sourceDetail":"\u4e0a\u4ea4USEC PMC\u72d7\u724c [x7]","trader":"Prapor","level":21,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"590c5bbd86f774785762df04","item":{"id":"590c5bbd86f774785762df04","name":"100\u6beb\u5347WD-40","shortName":"WD-40","iconLink":"https://assets.tarkov.dev/590c5bbd86f774785762df04-icon.webp","gridImageLink":"https://assets.tarkov.dev/590c5bbd86f774785762df04-grid-image.webp"},"count":1,"foundInRaid":true,"sourceType":"task","sourceId":"5a0327ba86f77456b9154236","sourceName":"\u7597\u517b\u4e4b\u65c5 - 3","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u5230\u5e76\u4e0a\u4ea4\uff1aWD-40 [x1]","trader":"Peacekeeper","level":12,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32c3b86f77472a31742f0","item":{"id":"59f32c3b86f77472a31742f0","name":"USEC \u72d7\u724c","shortName":"USEC","iconLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-grid-image.webp"},"count":7,"foundInRaid":false,"sourceType":"task","sourceId":"5a27c99a86f7747d2c6bdd8e","sourceName":"\u897f\u65b9\u6765\u5ba2 - 1","sourceDetail":"\u4e0a\u4ea4USEC PMC\u72d7\u724c [x7]","trader":"Skier","level":9,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"5c0e874186f7745dc7616606","item":{"id":"5c0e874186f7745dc7616606","name":"Maska-1SCh \u9632\u5f39\u5934\u76d4\uff08Killa \u7248\uff09","shortName":"Maska-1SCh KE","iconLink":"https://assets.tarkov.dev/5c0e874186f7745dc7616606-icon.webp","gridImageLink":"https://assets.tarkov.dev/5c0e874186f7745dc7616606-grid-image.webp"},"count":1,"foundInRaid":true,"sourceType":"task","sourceId":"5d25e2e286f77444001e2e48","sourceName":"\u730e\u4eba\u4e4b\u8def - \u8131\u9500","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u5230Killa\u7684\u5934\u76d4 [x1] / \u4e0a\u4ea4Killa\u7684\u5934\u76d4  [x1]","trader":"Jaeger","level":30,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"60a7acf20c5cb24b01346648","item":{"id":"60a7acf20c5cb24b01346648","name":"BOSS\u9e2d\u820c\u5e3d","shortName":"\u5e3d\u5b50","iconLink":"https://assets.tarkov.dev/60a7acf20c5cb24b01346648-icon.webp","gridImageLink":"https://assets.tarkov.dev/60a7acf20c5cb24b01346648-grid-image.webp"},"count":1,"foundInRaid":true,"sourceType":"task","sourceId":"60c0c018f7afb4354815096a","sourceName":"\u730e\u4eba\u4e4b\u8def - \u5de5\u5382\u5934\u76ee","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u5230BOSS\u9e2d\u820c\u5e3d [x1] / \u4e0a\u4ea4BOSS\u9e2d\u820c\u5e3d [x1]","trader":"Jaeger","level":12,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32bb586f774757e1e8442","item":{"id":"59f32bb586f774757e1e8442","name":"BEAR \u72d7\u724c","shortName":"BEAR","iconLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-grid-image.webp"},"count":20,"foundInRaid":true,"sourceType":"task","sourceId":"60e71ccb5688f6424c7bfec4","sourceName":"\u6218\u5229\u54c1","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u523050\u7ea7\u4ee5\u4e0aBEAR PMC\u72d7\u724c\u5e76\u4e0a\u4ea4 [x20]","trader":"Peacekeeper","level":55,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32c3b86f77472a31742f0","item":{"id":"59f32c3b86f77472a31742f0","name":"USEC \u72d7\u724c","shortName":"USEC","iconLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-grid-image.webp"},"count":20,"foundInRaid":true,"sourceType":"task","sourceId":"60e71ccb5688f6424c7bfec4","sourceName":"\u6218\u5229\u54c1","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u523050\u7ea7\u4ee5\u4e0aUSEC PMC\u72d7\u724c\u5e76\u4e0a\u4ea4 [x20]","trader":"Peacekeeper","level":55,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32c3b86f77472a31742f0","item":{"id":"59f32c3b86f77472a31742f0","name":"USEC \u72d7\u724c","shortName":"USEC","iconLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-grid-image.webp"},"count":20,"foundInRaid":false,"sourceType":"task","sourceId":"6179b5b06e9dd54ac275e409","sourceName":"\u5360\u5c71\u4e3a\u738b","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684USEC\u72d7\u724c [x20]","trader":"Prapor","level":30,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32bb586f774757e1e8442","item":{"id":"59f32bb586f774757e1e8442","name":"BEAR \u72d7\u724c","shortName":"BEAR","iconLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-grid-image.webp"},"count":20,"foundInRaid":true,"sourceType":"task","sourceId":"6179b5eabca27a099552e052","sourceName":"\u53cd\u6297","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684BEAR\u72d7\u724c [x20]","trader":"Peacekeeper","level":30,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"590c695186f7741e566b64a2","item":{"id":"590c695186f7741e566b64a2","name":"\u529b\u767e\u6c40\u6297\u751f\u7d20\u836f\u7247","shortName":"\u529b\u767e\u6c40","iconLink":"https://assets.tarkov.dev/590c695186f7741e566b64a2-icon.webp","gridImageLink":"https://assets.tarkov.dev/590c695186f7741e566b64a2-grid-image.webp"},"count":3,"foundInRaid":true,"sourceType":"task","sourceId":"657315ddab5a49b71f098853","sourceName":"\u65b0\u624b\u4e0a\u8def","sourceDetail":"\u4e0a\u4ea4\u4efb\u610f\u533b\u7597\u7269\u54c1 [x3]","trader":"Therapist","level":1,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"57505f6224597709a92585a9","item":{"id":"57505f6224597709a92585a9","name":"Alyonka \u5de7\u514b\u529b","shortName":"Alyonka","iconLink":"https://assets.tarkov.dev/57505f6224597709a92585a9-icon.webp","gridImageLink":"https://assets.tarkov.dev/57505f6224597709a92585a9-grid-image.webp"},"count":5,"foundInRaid":true,"sourceType":"task","sourceId":"66058cb5ae4719735349b9e8","sourceName":"\u8d5a\u70b9\u5feb\u94b1 - 2 [PVP ZONE]","sourceDetail":"\u4e0a\u4ea4\u6218\u5c40\u4e2d\u627e\u5230\u7684\u7269\u54c1\uff1a\u98df\u7269 [x5]","trader":"Ref","level":20,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"5c0fa877d174af02a012e1cf","item":{"id":"5c0fa877d174af02a012e1cf","name":"Aquamari\u5e26\u6ee4\u5634\u6c34\u74f6","shortName":"Aquamari","iconLink":"https://assets.tarkov.dev/5c0fa877d174af02a012e1cf-icon.webp","gridImageLink":"https://assets.tarkov.dev/5c0fa877d174af02a012e1cf-grid-image.webp"},"count":5,"foundInRaid":true,"sourceType":"task","sourceId":"66058cb5ae4719735349b9e8","sourceName":"\u8d5a\u70b9\u5feb\u94b1 - 2 [PVP ZONE]","sourceDetail":"\u4e0a\u4ea4\u6218\u5c40\u4e2d\u627e\u5230\u7684\u7269\u54c1\uff1a\u996e\u6599 [x5]","trader":"Ref","level":20,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"656f664200d62bcd2e024077","item":{"id":"656f664200d62bcd2e024077","name":"\u521a\u7389-VM \u9632\u5f39\u63d2\u677f\uff08\u524d\u90e8\uff09","shortName":"Korund-VM \u524d\u677f","iconLink":"https://assets.tarkov.dev/656f664200d62bcd2e024077-icon.webp","gridImageLink":"https://assets.tarkov.dev/656f664200d62bcd2e024077-grid-image.webp"},"count":3,"foundInRaid":true,"sourceType":"task","sourceId":"66058cbb06ef1d50a60c1f46","sourceName":"\u60ca\u559c [PVP ZONE]","sourceDetail":"\u4e0a\u4ea4\u6218\u5c40\u4e2d\u627e\u5230\u7684\u7269\u54c1\uff1a\u56db\u7ea7\u53ca\u4ee5\u4e0a\u88c5\u7532\u677f [x3]","trader":"Ref","level":20,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"664d4b0103ef2c61246afb56","item":{"id":"664d4b0103ef2c61246afb56","name":"\u516c\u5bd3\u7ba1\u7406\u5458\u94a5\u5319","shortName":"\u7ba1\u7406\u5458\u94a5\u5319","iconLink":"https://assets.tarkov.dev/664d4b0103ef2c61246afb56-icon.webp","gridImageLink":"https://assets.tarkov.dev/664d4b0103ef2c61246afb56-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"66058ccbc7f3584787181478","sourceName":"\u826f\u5fc3\u4f5c\u795f - 1 [PVP ZONE]","sourceDetail":"\u5728\u6d77\u5cb8\u7ebf\u7684\u8d70\u79c1\u8005\u8425\u5730\u627e\u5230\u5e76\u83b7\u53d6\u94a5\u5319 [x1]","trader":"Ref","level":20,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"64a536392d2c4e6e970f4121","item":{"id":"64a536392d2c4e6e970f4121","name":"WARTECH TV-115 \u63d2\u677f\u80f8\u6302\uff08\u6a44\u6984\u7eff\uff09","shortName":"TV-115","iconLink":"https://assets.tarkov.dev/64a536392d2c4e6e970f4121-icon.webp","gridImageLink":"https://assets.tarkov.dev/64a536392d2c4e6e970f4121-grid-image.webp"},"count":50,"foundInRaid":true,"sourceType":"task","sourceId":"6613f3007f6666d56807c929","sourceName":"\u4eba\u9760\u8863\u88c5 - 1(\u65e0\u6cd5\u81ea\u52a8\u6807\u8bb0)","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684 WARTECH \u54c1\u724c\u88c5\u5907 [x50]","trader":"Ragman","level":24,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"628baf0b967de16aab5a4f36","item":{"id":"628baf0b967de16aab5a4f36","name":"LBT-1961A Load Bearing\u80f8\u6302 (Goons\u7279\u522b\u7248)","shortName":"LBCR GE","iconLink":"https://assets.tarkov.dev/628baf0b967de16aab5a4f36-icon.webp","gridImageLink":"https://assets.tarkov.dev/628baf0b967de16aab5a4f36-grid-image.webp"},"count":50,"foundInRaid":true,"sourceType":"task","sourceId":"6613f307fca4f2f386029409","sourceName":"\u4eba\u9760\u8863\u88c5 - 2(\u65e0\u6cd5\u81ea\u52a8\u6807\u8bb0\uff09","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684 LBT \u54c1\u724c\u88c5\u5907 [x50]","trader":"Ragman","level":33,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"64a536392d2c4e6e970f4121","item":{"id":"64a536392d2c4e6e970f4121","name":"WARTECH TV-115 \u63d2\u677f\u80f8\u6302\uff08\u6a44\u6984\u7eff\uff09","shortName":"TV-115","iconLink":"https://assets.tarkov.dev/64a536392d2c4e6e970f4121-icon.webp","gridImageLink":"https://assets.tarkov.dev/64a536392d2c4e6e970f4121-grid-image.webp"},"count":50,"foundInRaid":true,"sourceType":"task","sourceId":"66151401efb0539ae10875ae","sourceName":"\u6d93\u6ef4\u6548\u5e94 - 1","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684 WARTECH \u54c1\u724c\u88c5\u5907 [x50]","trader":"Ragman","level":24,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"5ab8dab586f77441cd04f2a2","item":{"id":"5ab8dab586f77441cd04f2a2","name":"WARTECH MK3 TV-104 \u80f8\u6302 (\u590d\u5408\u8ff7\u5f69)","shortName":"MK3 TV-104","iconLink":"https://assets.tarkov.dev/5ab8dab586f77441cd04f2a2-icon.webp","gridImageLink":"https://assets.tarkov.dev/5ab8dab586f77441cd04f2a2-grid-image.webp"},"count":50,"foundInRaid":true,"sourceType":"task","sourceId":"6615141bfda04449120269a7","sourceName":"\u4eba\u9760\u8863\u88c5 - 1(\u65e0\u6cd5\u81ea\u52a8\u6807\u8bb0)","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684 Wartech \u54c1\u724c\u88c5\u5907 [x50]","trader":"Ragman","level":24,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"5734781f24597737e04bf32a","item":{"id":"5734781f24597737e04bf32a","name":"DVD\u5149\u9a71","shortName":"DVD","iconLink":"https://assets.tarkov.dev/5734781f24597737e04bf32a-icon.webp","gridImageLink":"https://assets.tarkov.dev/5734781f24597737e04bf32a-grid-image.webp"},"count":10,"foundInRaid":true,"sourceType":"task","sourceId":"6740a2c17e3818d5bb0648b6","sourceName":"\u534a\u6ee1\u534a\u7a7a","sourceDetail":"\u4e0a\u4ea4\u6218\u5c40\u4e2d\u627e\u5230\u7684\u7269\u54c1\uff1a\u7535\u8111\u914d\u4ef6 [x10]","trader":"Prapor","level":20,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"5d0378d486f77420421a5ff4","item":{"id":"5d0378d486f77420421a5ff4","name":"\u519b\u7528\u7535\u6e90\u6ee4\u6ce2\u5668","shortName":"\u6ee4\u6ce2\u5668","iconLink":"https://assets.tarkov.dev/5d0378d486f77420421a5ff4-icon.webp","gridImageLink":"https://assets.tarkov.dev/5d0378d486f77420421a5ff4-grid-image.webp"},"count":5,"foundInRaid":true,"sourceType":"task","sourceId":"6740a2c17e3818d5bb0648b6","sourceName":"\u534a\u6ee1\u534a\u7a7a","sourceDetail":"\u4e0a\u4ea4\u6218\u5c40\u4e2d\u627e\u5230\u7684\u7269\u54c1\uff1a\u519b\u89c4\u7535\u5b50\u5143\u4ef6 [x5]","trader":"Prapor","level":20,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"675aaab74bca0b001d02f356","item":{"id":"675aaab74bca0b001d02f356","name":"\u5199\u6709\u6697\u53f7\u201cVoron\u201d\u7684\u7eb8\u6761","shortName":"\u6697\u53f7","iconLink":"https://assets.tarkov.dev/675aaab74bca0b001d02f356-icon.webp","gridImageLink":"https://assets.tarkov.dev/675aaab74bca0b001d02f356-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"675c1cf4a757ddd00404f0a3","sourceName":"\u4e8b\u500d\u529f\u534a","sourceDetail":"\u83b7\u5f97\u6307\u5b9a\u9053\u5177\u5e76\u4ece\u79d8\u5bc6\u64a4\u79bb\u70b9\u64a4\u79bb [x1]","trader":"Jaeger","level":2,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"679b9716597ba2ed120c3d3f","item":{"id":"679b9716597ba2ed120c3d3f","name":"Knossos LLC \u8bbe\u65bd\u94a5\u5319","shortName":"Knossos","iconLink":"https://assets.tarkov.dev/679b9716597ba2ed120c3d3f-icon.webp","gridImageLink":"https://assets.tarkov.dev/679b9716597ba2ed120c3d3f-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"67a096577e86e067eb045733","sourceName":"\u6697\u85cf\u7384\u673a","sourceDetail":"\u627e\u5230\u5e76\u83b7\u53d6 Knossos LLC \u8bbe\u65bd\u94a5\u5319 [x1]","trader":"Mechanic","level":15,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"679b9819a2f2dd4da9023512","item":{"id":"679b9819a2f2dd4da9023512","name":"Labrys \u8bbf\u95ee\u94a5\u5319\u5361","shortName":"Labrys","iconLink":"https://assets.tarkov.dev/679b9819a2f2dd4da9023512-icon.webp","gridImageLink":"https://assets.tarkov.dev/679b9819a2f2dd4da9023512-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"67a0966817e34930e500754c","sourceName":"\u5f3a\u5236\u7ed3\u76df","sourceDetail":"\u627e\u5230\u8fdb\u5165\u6d77\u5cb8\u7ebf\u7597\u517b\u9662\u5730\u5821\u5bc6\u95ed\u95e8\u7684\u529e\u6cd5 [x1]","trader":"Mechanic","level":15,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"679b9819a2f2dd4da9023512","item":{"id":"679b9819a2f2dd4da9023512","name":"Labrys \u8bbf\u95ee\u94a5\u5319\u5361","shortName":"Labrys","iconLink":"https://assets.tarkov.dev/679b9819a2f2dd4da9023512-icon.webp","gridImageLink":"https://assets.tarkov.dev/679b9819a2f2dd4da9023512-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"67a0970744893b9f3f0d9b68","sourceName":"\u6b66\u88c5\u4fa6\u5bdf","sourceDetail":"\u83b7\u5f97 Labrys \u8bbf\u95ee\u94a5\u5319\u5361 [x1]","trader":"Mechanic","level":15,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"}],"pve":[{"itemId":"5937ee6486f77408994ba448","item":{"id":"5937ee6486f77408994ba448","name":"\u673a\u68b0\u94a5\u5319","shortName":"\u94a5\u5319","iconLink":"https://assets.tarkov.dev/5937ee6486f77408994ba448-icon.webp","gridImageLink":"https://assets.tarkov.dev/5937ee6486f77408994ba448-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"5936da9e86f7742d65037edf","sourceName":"\u80cc\u666f\u8c03\u67e5","sourceDetail":"\u53d6\u5f97\u6cb9\u7f50\u8f66\u94a5\u5319 [x1]","trader":"Prapor","level":2,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"5938144586f77473c2087145","item":{"id":"5938144586f77473c2087145","name":"\u7b80\u6613\u5de5\u68da\u94a5\u5319","shortName":"\u7b80\u6613\u5de5\u68da","iconLink":"https://assets.tarkov.dev/5938144586f77473c2087145-icon.webp","gridImageLink":"https://assets.tarkov.dev/5938144586f77473c2087145-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"5967530a86f77462ba22226b","sourceName":"\u7f6a\u8bc1","sourceDetail":"\u53d6\u5f97\u5de5\u68da\u7684\u94a5\u5319 [x1]","trader":"Prapor","level":6,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"591afe0186f77431bd616a11","item":{"id":"591afe0186f77431bd616a11","name":"ZB-014\u94a5\u5319","shortName":"ZB-014","iconLink":"https://assets.tarkov.dev/591afe0186f77431bd616a11-icon.webp","gridImageLink":"https://assets.tarkov.dev/591afe0186f77431bd616a11-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"59675d6c86f7740a842fc482","sourceName":"\u86cb\u5377\u51b0\u6dc7\u6dcb","sourceDetail":"\u5728TerraGroup\u5458\u5de5\u7684\u5bbf\u820d\u623f\u95f4\u627e\u5230\u5730\u5821\u7684\u94a5\u5319 [x1]","trader":"Prapor","level":9,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"5448bd6b4bdc2dfc2f8b4569","item":{"id":"5448bd6b4bdc2dfc2f8b4569","name":"\u9a6c\u5361\u6d1b\u592b9x18PM \u624b\u67aa","shortName":"PM","iconLink":"https://assets.tarkov.dev/5448bd6b4bdc2dfc2f8b4569-icon.webp","gridImageLink":"https://assets.tarkov.dev/5448bd6b4bdc2dfc2f8b4569-grid-image.webp"},"count":2,"foundInRaid":true,"sourceType":"task","sourceId":"59ca29fb86f77445ab465c87","sourceName":"\u60e9\u7f5a\u8005 - 5","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u5230\u4efb\u610f\u7248\u672c\u7684 PM \u624b\u67aa [x2] / \u4e0a\u4ea4\u624b\u67aa [x2]","trader":"Prapor","level":20,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"5644bd2b4bdc2d3b4c8b4572","item":{"id":"5644bd2b4bdc2d3b4c8b4572","name":"AK-74N 5.45x39 \u7a81\u51fb\u6b65\u67aa","shortName":"AK-74N","iconLink":"https://assets.tarkov.dev/5644bd2b4bdc2d3b4c8b4572-icon.webp","gridImageLink":"https://assets.tarkov.dev/5644bd2b4bdc2d3b4c8b4572-grid-image.webp"},"count":1,"foundInRaid":true,"sourceType":"task","sourceId":"59ca29fb86f77445ab465c87","sourceName":"\u60e9\u7f5a\u8005 - 5","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u5230\u4efb\u610f\u7248\u672c\u7684 AK-74 \u7cfb\u5217\u7a81\u51fb\u6b65\u67aa [x1] / \u4e0a\u4ea4 AK-74 \u7cfb\u5217\u7a81\u51fb\u6b65\u67aa [x1]","trader":"Prapor","level":20,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32bb586f774757e1e8442","item":{"id":"59f32bb586f774757e1e8442","name":"BEAR \u72d7\u724c","shortName":"BEAR","iconLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-grid-image.webp"},"count":7,"foundInRaid":false,"sourceType":"task","sourceId":"59ca2eb686f77445a80ed049","sourceName":"\u60e9\u7f5a\u8005 - 6","sourceDetail":"\u4e0a\u4ea4BEAR PMC\u72d7\u724c [x7]","trader":"Prapor","level":21,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32c3b86f77472a31742f0","item":{"id":"59f32c3b86f77472a31742f0","name":"USEC \u72d7\u724c","shortName":"USEC","iconLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-grid-image.webp"},"count":7,"foundInRaid":false,"sourceType":"task","sourceId":"59ca2eb686f77445a80ed049","sourceName":"\u60e9\u7f5a\u8005 - 6","sourceDetail":"\u4e0a\u4ea4USEC PMC\u72d7\u724c [x7]","trader":"Prapor","level":21,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"590c5bbd86f774785762df04","item":{"id":"590c5bbd86f774785762df04","name":"100\u6beb\u5347WD-40","shortName":"WD-40","iconLink":"https://assets.tarkov.dev/590c5bbd86f774785762df04-icon.webp","gridImageLink":"https://assets.tarkov.dev/590c5bbd86f774785762df04-grid-image.webp"},"count":1,"foundInRaid":true,"sourceType":"task","sourceId":"5a0327ba86f77456b9154236","sourceName":"\u7597\u517b\u4e4b\u65c5 - 3","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u5230\u5e76\u4e0a\u4ea4\uff1aWD-40 [x1]","trader":"Peacekeeper","level":12,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32c3b86f77472a31742f0","item":{"id":"59f32c3b86f77472a31742f0","name":"USEC \u72d7\u724c","shortName":"USEC","iconLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-grid-image.webp"},"count":7,"foundInRaid":false,"sourceType":"task","sourceId":"5a27c99a86f7747d2c6bdd8e","sourceName":"\u897f\u65b9\u6765\u5ba2 - 1","sourceDetail":"\u4e0a\u4ea4USEC PMC\u72d7\u724c [x7]","trader":"Skier","level":9,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"5c0e874186f7745dc7616606","item":{"id":"5c0e874186f7745dc7616606","name":"Maska-1SCh \u9632\u5f39\u5934\u76d4\uff08Killa \u7248\uff09","shortName":"Maska-1SCh KE","iconLink":"https://assets.tarkov.dev/5c0e874186f7745dc7616606-icon.webp","gridImageLink":"https://assets.tarkov.dev/5c0e874186f7745dc7616606-grid-image.webp"},"count":1,"foundInRaid":true,"sourceType":"task","sourceId":"5d25e2e286f77444001e2e48","sourceName":"\u730e\u4eba\u4e4b\u8def - \u8131\u9500","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u5230Killa\u7684\u5934\u76d4 [x1] / \u4e0a\u4ea4Killa\u7684\u5934\u76d4  [x1]","trader":"Jaeger","level":30,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"60a7acf20c5cb24b01346648","item":{"id":"60a7acf20c5cb24b01346648","name":"BOSS\u9e2d\u820c\u5e3d","shortName":"\u5e3d\u5b50","iconLink":"https://assets.tarkov.dev/60a7acf20c5cb24b01346648-icon.webp","gridImageLink":"https://assets.tarkov.dev/60a7acf20c5cb24b01346648-grid-image.webp"},"count":1,"foundInRaid":true,"sourceType":"task","sourceId":"60c0c018f7afb4354815096a","sourceName":"\u730e\u4eba\u4e4b\u8def - \u5de5\u5382\u5934\u76ee","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u5230BOSS\u9e2d\u820c\u5e3d [x1] / \u4e0a\u4ea4BOSS\u9e2d\u820c\u5e3d [x1]","trader":"Jaeger","level":12,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32bb586f774757e1e8442","item":{"id":"59f32bb586f774757e1e8442","name":"BEAR \u72d7\u724c","shortName":"BEAR","iconLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-grid-image.webp"},"count":20,"foundInRaid":true,"sourceType":"task","sourceId":"60e71ccb5688f6424c7bfec4","sourceName":"\u6218\u5229\u54c1","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u523050\u7ea7\u4ee5\u4e0aBEAR PMC\u72d7\u724c\u5e76\u4e0a\u4ea4 [x20]","trader":"Peacekeeper","level":55,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32c3b86f77472a31742f0","item":{"id":"59f32c3b86f77472a31742f0","name":"USEC \u72d7\u724c","shortName":"USEC","iconLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-grid-image.webp"},"count":20,"foundInRaid":true,"sourceType":"task","sourceId":"60e71ccb5688f6424c7bfec4","sourceName":"\u6218\u5229\u54c1","sourceDetail":"\u5728\u6218\u5c40\u4e2d\u627e\u523050\u7ea7\u4ee5\u4e0aUSEC PMC\u72d7\u724c\u5e76\u4e0a\u4ea4 [x20]","trader":"Peacekeeper","level":55,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32c3b86f77472a31742f0","item":{"id":"59f32c3b86f77472a31742f0","name":"USEC \u72d7\u724c","shortName":"USEC","iconLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32c3b86f77472a31742f0-grid-image.webp"},"count":20,"foundInRaid":false,"sourceType":"task","sourceId":"6179b5b06e9dd54ac275e409","sourceName":"\u5360\u5c71\u4e3a\u738b","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684USEC\u72d7\u724c [x20]","trader":"Prapor","level":30,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"59f32bb586f774757e1e8442","item":{"id":"59f32bb586f774757e1e8442","name":"BEAR \u72d7\u724c","shortName":"BEAR","iconLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-icon.webp","gridImageLink":"https://assets.tarkov.dev/59f32bb586f774757e1e8442-grid-image.webp"},"count":20,"foundInRaid":true,"sourceType":"task","sourceId":"6179b5eabca27a099552e052","sourceName":"\u53cd\u6297","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684BEAR\u72d7\u724c [x20]","trader":"Peacekeeper","level":30,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"590c695186f7741e566b64a2","item":{"id":"590c695186f7741e566b64a2","name":"\u529b\u767e\u6c40\u6297\u751f\u7d20\u836f\u7247","shortName":"\u529b\u767e\u6c40","iconLink":"https://assets.tarkov.dev/590c695186f7741e566b64a2-icon.webp","gridImageLink":"https://assets.tarkov.dev/590c695186f7741e566b64a2-grid-image.webp"},"count":3,"foundInRaid":true,"sourceType":"task","sourceId":"657315ddab5a49b71f098853","sourceName":"\u65b0\u624b\u4e0a\u8def","sourceDetail":"\u4e0a\u4ea4\u4efb\u610f\u533b\u7597\u7269\u54c1 [x3]","trader":"Therapist","level":1,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"64a536392d2c4e6e970f4121","item":{"id":"64a536392d2c4e6e970f4121","name":"WARTECH TV-115 \u63d2\u677f\u80f8\u6302\uff08\u6a44\u6984\u7eff\uff09","shortName":"TV-115","iconLink":"https://assets.tarkov.dev/64a536392d2c4e6e970f4121-icon.webp","gridImageLink":"https://assets.tarkov.dev/64a536392d2c4e6e970f4121-grid-image.webp"},"count":50,"foundInRaid":true,"sourceType":"task","sourceId":"6613f3007f6666d56807c929","sourceName":"\u4eba\u9760\u8863\u88c5 - 1(\u65e0\u6cd5\u81ea\u52a8\u6807\u8bb0)","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684 WARTECH \u54c1\u724c\u88c5\u5907 [x50]","trader":"Ragman","level":24,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"628baf0b967de16aab5a4f36","item":{"id":"628baf0b967de16aab5a4f36","name":"LBT-1961A Load Bearing\u80f8\u6302 (Goons\u7279\u522b\u7248)","shortName":"LBCR GE","iconLink":"https://assets.tarkov.dev/628baf0b967de16aab5a4f36-icon.webp","gridImageLink":"https://assets.tarkov.dev/628baf0b967de16aab5a4f36-grid-image.webp"},"count":50,"foundInRaid":true,"sourceType":"task","sourceId":"6613f307fca4f2f386029409","sourceName":"\u4eba\u9760\u8863\u88c5 - 2(\u65e0\u6cd5\u81ea\u52a8\u6807\u8bb0\uff09","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684 LBT \u54c1\u724c\u88c5\u5907 [x50]","trader":"Ragman","level":33,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"64a536392d2c4e6e970f4121","item":{"id":"64a536392d2c4e6e970f4121","name":"WARTECH TV-115 \u63d2\u677f\u80f8\u6302\uff08\u6a44\u6984\u7eff\uff09","shortName":"TV-115","iconLink":"https://assets.tarkov.dev/64a536392d2c4e6e970f4121-icon.webp","gridImageLink":"https://assets.tarkov.dev/64a536392d2c4e6e970f4121-grid-image.webp"},"count":50,"foundInRaid":true,"sourceType":"task","sourceId":"66151401efb0539ae10875ae","sourceName":"\u6d93\u6ef4\u6548\u5e94 - 1","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684 WARTECH \u54c1\u724c\u88c5\u5907 [x50]","trader":"Ragman","level":24,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"5ab8dab586f77441cd04f2a2","item":{"id":"5ab8dab586f77441cd04f2a2","name":"WARTECH MK3 TV-104 \u80f8\u6302 (\u590d\u5408\u8ff7\u5f69)","shortName":"MK3 TV-104","iconLink":"https://assets.tarkov.dev/5ab8dab586f77441cd04f2a2-icon.webp","gridImageLink":"https://assets.tarkov.dev/5ab8dab586f77441cd04f2a2-grid-image.webp"},"count":50,"foundInRaid":true,"sourceType":"task","sourceId":"6615141bfda04449120269a7","sourceName":"\u4eba\u9760\u8863\u88c5 - 1(\u65e0\u6cd5\u81ea\u52a8\u6807\u8bb0)","sourceDetail":"\u4e0a\u4ea4\u5728\u6218\u5c40\u4e2d\u627e\u5230\u7684 Wartech \u54c1\u724c\u88c5\u5907 [x50]","trader":"Ragman","level":24,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"5734781f24597737e04bf32a","item":{"id":"5734781f24597737e04bf32a","name":"DVD\u5149\u9a71","shortName":"DVD","iconLink":"https://assets.tarkov.dev/5734781f24597737e04bf32a-icon.webp","gridImageLink":"https://assets.tarkov.dev/5734781f24597737e04bf32a-grid-image.webp"},"count":10,"foundInRaid":true,"sourceType":"task","sourceId":"6740a2c17e3818d5bb0648b6","sourceName":"\u534a\u6ee1\u534a\u7a7a","sourceDetail":"\u4e0a\u4ea4\u6218\u5c40\u4e2d\u627e\u5230\u7684\u7269\u54c1\uff1a\u7535\u8111\u914d\u4ef6 [x10]","trader":"Prapor","level":20,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"5d0378d486f77420421a5ff4","item":{"id":"5d0378d486f77420421a5ff4","name":"\u519b\u7528\u7535\u6e90\u6ee4\u6ce2\u5668","shortName":"\u6ee4\u6ce2\u5668","iconLink":"https://assets.tarkov.dev/5d0378d486f77420421a5ff4-icon.webp","gridImageLink":"https://assets.tarkov.dev/5d0378d486f77420421a5ff4-grid-image.webp"},"count":5,"foundInRaid":true,"sourceType":"task","sourceId":"6740a2c17e3818d5bb0648b6","sourceName":"\u534a\u6ee1\u534a\u7a7a","sourceDetail":"\u4e0a\u4ea4\u6218\u5c40\u4e2d\u627e\u5230\u7684\u7269\u54c1\uff1a\u519b\u89c4\u7535\u5b50\u5143\u4ef6 [x5]","trader":"Prapor","level":20,"objectiveType":"giveItem","patchSource":"miaomiao-toolbox"},{"itemId":"675aaab74bca0b001d02f356","item":{"id":"675aaab74bca0b001d02f356","name":"\u5199\u6709\u6697\u53f7\u201cVoron\u201d\u7684\u7eb8\u6761","shortName":"\u6697\u53f7","iconLink":"https://assets.tarkov.dev/675aaab74bca0b001d02f356-icon.webp","gridImageLink":"https://assets.tarkov.dev/675aaab74bca0b001d02f356-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"675c1cf4a757ddd00404f0a3","sourceName":"\u4e8b\u500d\u529f\u534a","sourceDetail":"\u83b7\u5f97\u6307\u5b9a\u9053\u5177\u5e76\u4ece\u79d8\u5bc6\u64a4\u79bb\u70b9\u64a4\u79bb [x1]","trader":"Jaeger","level":2,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"679b9716597ba2ed120c3d3f","item":{"id":"679b9716597ba2ed120c3d3f","name":"Knossos LLC \u8bbe\u65bd\u94a5\u5319","shortName":"Knossos","iconLink":"https://assets.tarkov.dev/679b9716597ba2ed120c3d3f-icon.webp","gridImageLink":"https://assets.tarkov.dev/679b9716597ba2ed120c3d3f-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"67a096577e86e067eb045733","sourceName":"\u6697\u85cf\u7384\u673a","sourceDetail":"\u627e\u5230\u5e76\u83b7\u53d6 Knossos LLC \u8bbe\u65bd\u94a5\u5319 [x1]","trader":"Mechanic","level":15,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"679b9819a2f2dd4da9023512","item":{"id":"679b9819a2f2dd4da9023512","name":"Labrys \u8bbf\u95ee\u94a5\u5319\u5361","shortName":"Labrys","iconLink":"https://assets.tarkov.dev/679b9819a2f2dd4da9023512-icon.webp","gridImageLink":"https://assets.tarkov.dev/679b9819a2f2dd4da9023512-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"67a0966817e34930e500754c","sourceName":"\u5f3a\u5236\u7ed3\u76df","sourceDetail":"\u627e\u5230\u8fdb\u5165\u6d77\u5cb8\u7ebf\u7597\u517b\u9662\u5730\u5821\u5bc6\u95ed\u95e8\u7684\u529e\u6cd5 [x1]","trader":"Mechanic","level":15,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"},{"itemId":"679b9819a2f2dd4da9023512","item":{"id":"679b9819a2f2dd4da9023512","name":"Labrys \u8bbf\u95ee\u94a5\u5319\u5361","shortName":"Labrys","iconLink":"https://assets.tarkov.dev/679b9819a2f2dd4da9023512-icon.webp","gridImageLink":"https://assets.tarkov.dev/679b9819a2f2dd4da9023512-grid-image.webp"},"count":1,"foundInRaid":false,"sourceType":"task","sourceId":"67a0970744893b9f3f0d9b68","sourceName":"\u6b66\u88c5\u4fa6\u5bdf","sourceDetail":"\u83b7\u5f97 Labrys \u8bbf\u95ee\u94a5\u5319\u5361 [x1]","trader":"Mechanic","level":15,"objectiveType":"findItem","patchSource":"miaomiao-toolbox"}]}}'''
TRACKER_CACHE_TTL_SEC = 24 * 60 * 60
TRACKER_QUERY_LIMIT = 2000
TRACKER_SEARCH_LIMIT = 1000
TRACKER_SORT_MODES = {"default", "quantity-desc", "quantity-asc", "name-asc", "name-desc"}
PACKET_VERSION = 1
SYNC_UPDATE_TYPE = "sync_update"
SYNC_SNAPSHOT_TYPE = "sync_snapshot"
_RAID_LOG_TAIL_LOCK = threading.RLock()
_RAID_LOG_TAIL_STATE: dict[str, dict[str, Any]] = {}
_MARKET_CACHE_LOCK = threading.RLock()
_TRACKER_CACHE_LOCK = threading.RLock()
_TRACKER_STATE_LOCK = threading.RLock()
NAME_MAX_LENGTH = 24
DEFAULT_COLOR = "#4fd1ff"
HEX_COLOR_RE = re.compile(r"^#[0-9a-fA-F]{6}$")
SYNC_CLIENT_ID_RE = re.compile(r"^[0-9a-fA-F]{32}$")
TRACKER_ITEM_ID_RE = re.compile(r"^(?:[0-9a-f]{24}|choice:[A-Za-z0-9_.:-]{1,120})$")
SCREENSHOT_RE = re.compile(
    r"^(?P<date>\d{4}-\d{2}-\d{2})\[(?P<hour>\d{2})-(?P<minute>\d{2})\]_"
    r"(?P<x>-?\d+(?:\.\d+)?),\s*(?P<y>-?\d+(?:\.\d+)?),\s*(?P<z>-?\d+(?:\.\d+)?)_"
    r"(?P<qx>-?\d+(?:\.\d+)?),\s*(?P<qy>-?\d+(?:\.\d+)?),\s*"
    r"(?P<qz>-?\d+(?:\.\d+)?),\s*(?P<qw>-?\d+(?:\.\d+)?)_"
    r"(?P<scale>-?\d+(?:\.\d+)?)(?:\s*\((?P<index>\d+)\))?\.png$",
    re.IGNORECASE,
)
LOG_DIRECTORY_RE = re.compile(r"^log_(?P<stamp>\d{4}\.\d{2}\.\d{2}_\d{1,2}-\d{2}-\d{2})_(?P<suffix>[0-9.]+)$", re.IGNORECASE)


def get_resource_root() -> Path:
    if getattr(sys, "frozen", False) and hasattr(sys, "_MEIPASS"):
        return Path(getattr(sys, "_MEIPASS"))
    return Path(__file__).resolve().parent


def build_api_health_payload() -> dict[str, Any]:
    root = get_resource_root()
    return {
        "available": True,
        "app": APP_TITLE,
        "pid": os.getpid(),
        "resourceRoot": str(root),
        "launcher": str(root / "launcher.py"),
    }


def can_bind(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        try:
            sock.bind((HTTP_HOST, port))
            return True
        except OSError:
            return False


def is_valid_tcp_port(port: int) -> bool:
    return 1 <= port <= 65535


def get_app_state_dir() -> Path:
    local_app_data = os.environ.get("LOCALAPPDATA")
    base = Path(local_app_data) if local_app_data else Path.home() / "AppData" / "Local"
    return base / APP_STATE_DIR_NAME


def get_local_paths_marker_path() -> Path:
    return get_app_state_dir() / LOCAL_PATHS_MARKER_NAME


def get_ui_preferences_path() -> Path:
    return get_app_state_dir() / UI_PREFERENCES_NAME


def get_sync_client_id_marker_path() -> Path:
    return get_app_state_dir() / SYNC_CLIENT_ID_MARKER_NAME


def get_startup_log_path() -> Path:
    return get_app_state_dir() / STARTUP_LOG_NAME


def get_market_cache_path() -> Path:
    return get_app_state_dir() / MARKET_CACHE_NAME


def get_market_icon_dir() -> Path:
    return get_app_state_dir() / MARKET_ICON_DIR_NAME


def get_tracker_cache_path() -> Path:
    return get_app_state_dir() / TRACKER_CACHE_NAME


def get_tracker_state_path() -> Path:
    return get_app_state_dir() / TRACKER_STATE_NAME



def append_startup_log(message: str) -> None:
    try:
        log_path = get_startup_log_path()
        log_path.parent.mkdir(parents=True, exist_ok=True)
        timestamp = time.strftime("%Y-%m-%d %H:%M:%S")
        with log_path.open("a", encoding="utf-8") as fh:
            fh.write(f"[{timestamp}] {message}\n")
    except OSError:
        return


def report_startup_error(message: str) -> None:
    append_startup_log(message)
    if os.environ.get("TARKOV_MAP_HIDDEN_LAUNCH") != "1":
        return
    try:
        import ctypes

        ctypes.windll.user32.MessageBoxW(
            None,
            f"{message}\n\n日志: {get_startup_log_path()}",
            APP_TITLE,
            0x10,
        )
    except Exception:
        return


def write_json_atomic(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    tmp_path.write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    os.replace(tmp_path, path)


def guess_image_content_type(data: bytes, fallback: str = "") -> str:
    content_type = fallback.split(";", 1)[0].strip().lower()
    if content_type.startswith("image/"):
        return content_type
    if data.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image/png"
    if data.startswith(b"\xff\xd8\xff"):
        return "image/jpeg"
    if data.startswith(b"GIF87a") or data.startswith(b"GIF89a"):
        return "image/gif"
    if len(data) >= 12 and data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "image/webp"
    return "application/octet-stream"


def read_saved_sync_client_id() -> str | None:
    try:
        payload = json.loads(get_sync_client_id_marker_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    client_id = str(payload.get("id") if isinstance(payload, dict) else "").strip().lower()
    return client_id if SYNC_CLIENT_ID_RE.fullmatch(client_id) else None


def write_sync_client_id(client_id: str) -> None:
    if not SYNC_CLIENT_ID_RE.fullmatch(client_id):
        return
    try:
        marker_path = get_sync_client_id_marker_path()
        marker_path.parent.mkdir(parents=True, exist_ok=True)
        marker_path.write_text(
            json.dumps({"id": client_id, "updatedAt": time.time()}, separators=(",", ":")),
            encoding="utf-8",
        )
    except OSError:
        return


def get_or_create_sync_client_id() -> str:
    saved = read_saved_sync_client_id()
    if saved:
        return saved
    client_id = uuid.uuid4().hex
    write_sync_client_id(client_id)
    return client_id


def read_local_paths() -> dict[str, str]:
    try:
        payload = json.loads(get_local_paths_marker_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    if not isinstance(payload, dict):
        return {}
    paths: dict[str, str] = {}
    for key in ("screenshot", "game"):
        value = payload.get(key)
        if isinstance(value, str) and value.strip():
            paths[key] = value.strip()
    return paths


def write_local_paths(paths: dict[str, str]) -> None:
    try:
        marker_path = get_local_paths_marker_path()
        marker_path.parent.mkdir(parents=True, exist_ok=True)
        marker_path.write_text(
            json.dumps({**paths, "updatedAt": time.time()}, ensure_ascii=False, separators=(",", ":")),
            encoding="utf-8",
        )
    except OSError:
        return


def save_local_path(kind: str, path: Path) -> None:
    if kind not in {"screenshot", "game"}:
        return
    paths = read_local_paths()
    paths[kind] = str(path)
    write_local_paths(paths)


def normalize_price_mode(value: Any) -> str:
    return "pve" if str(value or "").strip().lower() == "pve" else "pvp"


def normalize_tracker_sort_mode(value: Any) -> str:
    mode = str(value or "").strip()
    return mode if mode in TRACKER_SORT_MODES else "default"


def sanitize_ui_preferences(payload: dict[str, Any] | None) -> dict[str, Any]:
    payload = payload if isinstance(payload, dict) else {}
    return {
        "schemaVersion": 1,
        "marketMode": normalize_price_mode(payload.get("marketMode")),
        "trackerMode": normalize_price_mode(payload.get("trackerMode")),
        "trackerIncludeTasks": payload.get("trackerIncludeTasks") if isinstance(payload.get("trackerIncludeTasks"), bool) else True,
        "trackerIncludeHideout": payload.get("trackerIncludeHideout") if isinstance(payload.get("trackerIncludeHideout"), bool) else True,
        "trackerSortMode": normalize_tracker_sort_mode(payload.get("trackerSortMode")),
        "updatedAt": payload.get("updatedAt") if isinstance(payload.get("updatedAt"), (int, float)) else None,
    }


def read_ui_preferences() -> dict[str, Any]:
    try:
        payload = json.loads(get_ui_preferences_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        payload = {}
    return sanitize_ui_preferences(payload if isinstance(payload, dict) else {})


def write_ui_preferences(payload: dict[str, Any]) -> dict[str, Any]:
    preferences = sanitize_ui_preferences(payload)
    preferences["updatedAt"] = time.time()
    write_json_atomic(get_ui_preferences_path(), preferences)
    return preferences


def parse_log_directory_stamp(name: str) -> float | None:
    match = LOG_DIRECTORY_RE.match(name)
    if not match:
        return None
    date_part, time_part = match.group("stamp").split("_", 1)
    parts = time_part.split("-")
    if len(parts) != 3:
        return None
    hour, minute, second = parts
    normalized = f"{date_part.replace('.', '-')}T{hour.zfill(2)}:{minute}:{second}"
    try:
        return time.mktime(time.strptime(normalized, "%Y-%m-%dT%H:%M:%S"))
    except ValueError:
        return None


def resolve_logs_root(path: Path) -> Path | None:
    if path.name == "Logs" and path.is_dir():
        return path
    logs = path / "Logs"
    return logs if logs.is_dir() else None


def is_log_leaf_dir(path: Path) -> bool:
    if not path.is_dir() or parse_log_directory_stamp(path.name) is None:
        return False
    try:
        return any(
            entry.is_file() and ("application" in entry.name or "notifications" in entry.name)
            for entry in path.iterdir()
        )
    except OSError:
        return False


def resolve_latest_log_dir(path: Path) -> Path | None:
    if is_log_leaf_dir(path):
        return path
    logs_root = resolve_logs_root(path)
    if not logs_root:
        return None
    latest: Path | None = None
    latest_stamp = -1.0
    try:
        entries = list(logs_root.iterdir())
    except OSError:
        return None
    for entry in entries:
        if not entry.is_dir():
            continue
        stamp = parse_log_directory_stamp(entry.name)
        if stamp is not None and stamp > latest_stamp:
            latest = entry
            latest_stamp = stamp
    return latest


def describe_local_path(kind: str, raw_path: str | None) -> dict[str, Any]:
    path = Path(raw_path) if raw_path else None
    exists = bool(path and path.is_dir())
    valid = exists
    latest_log_dir = ""
    if kind == "game" and path:
        latest = resolve_latest_log_dir(path)
        valid = latest is not None
        latest_log_dir = latest.name if latest else ""
    return {
        "path": str(path) if path else "",
        "name": path.name if path else "",
        "exists": exists,
        "valid": valid,
        "latestLogDir": latest_log_dir,
    }


def build_local_paths_state() -> dict[str, Any]:
    paths = read_local_paths()
    return {
        "available": True,
        "screenshot": describe_local_path("screenshot", paths.get("screenshot")),
        "game": describe_local_path("game", paths.get("game")),
    }


def quaternion_to_yaw_deg(qx: float, qy: float, qz: float, qw: float) -> float:
    siny_cosp = 2 * (qw * qy + qx * qz)
    cosy_cosp = 1 - 2 * (qy * qy + qz * qz)
    return (math.degrees(math.atan2(siny_cosp, cosy_cosp)) + 360) % 360


def parse_screenshot_name(name: str) -> dict[str, Any] | None:
    match = SCREENSHOT_RE.match(name)
    if not match:
        return None
    groups = match.groupdict()
    try:
        x = float(groups["x"])
        y = float(groups["y"])
        z = float(groups["z"])
        qx = float(groups["qx"])
        qy = float(groups["qy"])
        qz = float(groups["qz"])
        qw = float(groups["qw"])
        index = int(groups.get("index") or "0")
        timestamp = time.mktime(time.strptime(f"{groups['date']}T{groups['hour']}:{groups['minute']}:00", "%Y-%m-%dT%H:%M:%S"))
    except (TypeError, ValueError):
        return None
    return {
        "name": name,
        "x": x,
        "y": y,
        "z": z,
        "qx": qx,
        "qy": qy,
        "qz": qz,
        "qw": qw,
        "yawDeg": quaternion_to_yaw_deg(qx, qy, qz, qw),
        "order": int(timestamp * 1000) * 1000 + index,
    }


def build_screenshot_scan_payload() -> dict[str, Any]:
    state = build_local_paths_state()
    path_text = state["screenshot"].get("path")
    path = Path(path_text) if path_text else None
    if not path or not path.is_dir():
        return {**state, "records": [], "skippedNoCoordinate": 0, "error": "Screenshot path is not available."}
    records: list[dict[str, Any]] = []
    skipped = 0
    try:
        entries = list(path.iterdir())
    except OSError as exc:
        return {**state, "records": [], "skippedNoCoordinate": 0, "error": str(exc)}
    for entry in entries:
        if not entry.is_file() or entry.suffix.lower() != ".png":
            continue
        parsed = parse_screenshot_name(entry.name)
        if not parsed:
            skipped += 1
            continue
        try:
            parsed["fileModifiedAt"] = int(entry.stat().st_mtime * 1000)
        except OSError:
            parsed["fileModifiedAt"] = parsed["order"]
        records.append(parsed)
    records.sort(key=lambda item: (item.get("order") or 0, item.get("fileModifiedAt") or 0, item.get("name") or ""))
    return {**state, "records": records, "skippedNoCoordinate": skipped}


def build_raid_log_files_payload(full_rescan: bool = False) -> dict[str, Any]:
    state = build_local_paths_state()
    path_text = state["game"].get("path")
    path = Path(path_text) if path_text else None
    latest_log_dir = resolve_latest_log_dir(path) if path else None
    if not latest_log_dir:
        return {**state, "logDirName": "", "files": [], "error": "Log path is not available."}
    files: list[dict[str, Any]] = []
    try:
        entries = list(latest_log_dir.iterdir())
    except OSError as exc:
        return {**state, "logDirName": latest_log_dir.name, "files": [], "error": str(exc)}
    for entry in entries:
        if not entry.is_file() or ("application" not in entry.name and "notifications" not in entry.name):
            continue
        kind = "application" if "application" in entry.name else "notifications"
        try:
            stat = entry.stat()
        except OSError:
            continue

        cache_key = str(entry.resolve())
        with _RAID_LOG_TAIL_LOCK:
            previous = _RAID_LOG_TAIL_STATE.get(cache_key, {})
            previous_offset = int(previous.get("offset") or 0)
            start_offset = 0 if full_rescan or stat.st_size < previous_offset else previous_offset
            pending = "" if start_offset == 0 else str(previous.get("pending") or "")

        try:
            with entry.open("rb") as fh:
                fh.seek(start_offset)
                raw = fh.read()
        except OSError:
            continue

        next_offset = start_offset + len(raw)
        if not raw and not full_rescan:
            continue

        text = pending + raw.decode("utf-8", errors="replace")
        next_pending = ""
        if not full_rescan and text and not text.endswith(("\n", "\r")):
            newline_index = max(text.rfind("\n"), text.rfind("\r"))
            if newline_index >= 0:
                next_pending = text[newline_index + 1 :]
                text = text[: newline_index + 1]
            else:
                next_pending = text
                text = ""

        with _RAID_LOG_TAIL_LOCK:
            _RAID_LOG_TAIL_STATE[cache_key] = {
                "offset": next_offset,
                "pending": next_pending,
                "mtime": stat.st_mtime,
                "size": stat.st_size,
            }

        if not text and not full_rescan:
            continue

        files.append(
            {
                "kind": kind,
                "name": entry.name,
                "lastModified": int(stat.st_mtime * 1000),
                "text": text,
                "incremental": not full_rescan,
                "fromOffset": start_offset,
            }
        )
    files.sort(key=lambda item: item["name"])
    return {**state, "logDirName": latest_log_dir.name, "files": files}


def pick_local_directory(kind: str) -> dict[str, Any]:
    if kind not in {"screenshot", "game"}:
        return {"error": "Invalid path kind."}
    try:
        import tkinter as tk
        from tkinter import filedialog
    except Exception as exc:  # noqa: BLE001
        return {"error": f"Directory picker is unavailable: {exc}"}

    saved = read_local_paths().get(kind)
    initial = saved if saved and Path(saved).is_dir() else str(Path.home())
    title = "选择截图目录" if kind == "screenshot" else "选择游戏目录、Logs 目录或 log_* 目录"
    root = tk.Tk()
    root.withdraw()
    root.attributes("-topmost", True)
    root.update()
    root.deiconify()
    root.lift()
    root.focus_force()
    root.withdraw()
    try:
        selected = filedialog.askdirectory(parent=root, title=title, initialdir=initial, mustexist=True)
    finally:
        root.destroy()
    if not selected:
        return {**build_local_paths_state(), "cancelled": True}
    path = Path(selected)
    if kind == "game" and resolve_latest_log_dir(path) is None:
        return {**build_local_paths_state(), "error": "未找到 Logs 或最新 log_* 目录"}
    save_local_path(kind, path)
    return build_local_paths_state()


def _market_graphql(query: str, variables: dict[str, Any], timeout: float = 35.0) -> dict[str, Any]:
    body = json.dumps({"query": query, "variables": variables}, separators=(",", ":")).encode("utf-8")
    request = Request(
        MARKET_API_URL,
        data=body,
        headers={
            "Accept": "application/json",
            "Content-Type": "application/json",
            "User-Agent": "TarkovMapLocator/market",
        },
        method="POST",
    )
    with urlopen(request, timeout=timeout) as response:
        payload = json.loads(response.read().decode("utf-8"))
    if not isinstance(payload, dict):
        raise RuntimeError("Invalid market API response.")
    if payload.get("errors"):
        raise RuntimeError("Market API returned errors.")
    data = payload.get("data")
    if not isinstance(data, dict):
        raise RuntimeError("Market API response missing data.")
    return data


def _fetch_market_items(lang: str, game_mode: str) -> list[dict[str, Any]]:
    query = """
    query MarketItems($lang: LanguageCode, $gameMode: GameMode, $limit: Int) {
      items(lang: $lang, gameMode: $gameMode, limit: $limit) {
        id
        name
        shortName
        avg24hPrice
        low24hPrice
        high24hPrice
        lastLowPrice
        iconLink
        gridImageLink
        width
        height
        types
        sellFor {
          price
          source
          vendor {
            name
          }
        }
      }
    }
    """
    data = _market_graphql(query, {"lang": lang, "gameMode": game_mode, "limit": MARKET_QUERY_LIMIT})
    items = data.get("items")
    return items if isinstance(items, list) else []


def _compact_market_sell_for(values: Any) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    if not isinstance(values, list):
        return rows
    for value in values:
        if not isinstance(value, dict):
            continue
        source = str(value.get("source") or "").strip()
        price = value.get("price")
        if not source or not isinstance(price, int):
            continue
        vendor = value.get("vendor") if isinstance(value.get("vendor"), dict) else {}
        rows.append({"price": price, "source": source, "vendorName": str(vendor.get("name") or "").strip()})
    return rows


def _compact_market_item(zh_item: dict[str, Any], en_item: dict[str, Any] | None = None) -> dict[str, Any]:
    en_item = en_item or {}
    item_id = str(zh_item.get("id") or en_item.get("id") or "").strip()
    sell_for = _compact_market_sell_for(zh_item.get("sellFor"))
    flea_price = next((row["price"] for row in sell_for if row.get("source") == "fleaMarket"), None)
    trader_prices = [row for row in sell_for if row.get("source") != "fleaMarket"]
    best_trader = max(trader_prices, key=lambda row: row.get("price", 0), default=None)
    return {
        "id": item_id,
        "nameZh": str(zh_item.get("name") or "").strip(),
        "shortNameZh": str(zh_item.get("shortName") or "").strip(),
        "name": str(en_item.get("name") or "").strip(),
        "shortName": str(en_item.get("shortName") or "").strip(),
        "avg24hPrice": zh_item.get("avg24hPrice") if isinstance(zh_item.get("avg24hPrice"), int) else None,
        "low24hPrice": zh_item.get("low24hPrice") if isinstance(zh_item.get("low24hPrice"), int) else None,
        "high24hPrice": zh_item.get("high24hPrice") if isinstance(zh_item.get("high24hPrice"), int) else None,
        "lastLowPrice": zh_item.get("lastLowPrice") if isinstance(zh_item.get("lastLowPrice"), int) else None,
        "fleaPrice": flea_price,
        "bestTrader": best_trader,
        "sellFor": sell_for,
        "iconLink": str(zh_item.get("iconLink") or en_item.get("iconLink") or "").strip(),
        "gridImageLink": str(zh_item.get("gridImageLink") or en_item.get("gridImageLink") or "").strip(),
        "types": zh_item.get("types") if isinstance(zh_item.get("types"), list) else [],
        "width": zh_item.get("width") if isinstance(zh_item.get("width"), int) else None,
        "height": zh_item.get("height") if isinstance(zh_item.get("height"), int) else None,
    }


def refresh_market_cache() -> dict[str, Any]:
    def merge_items(zh_items: list[dict[str, Any]], en_items: list[dict[str, Any]]) -> list[dict[str, Any]]:
        en_by_id = {str(item.get("id") or ""): item for item in en_items if isinstance(item, dict)}
        merged = []
        for item in zh_items:
            if not isinstance(item, dict):
                continue
            compact = _compact_market_item(item, en_by_id.get(str(item.get("id") or "")))
            if compact["id"]:
                merged.append(compact)
        return merged

    with _MARKET_CACHE_LOCK:
        started = time.time()
        with ThreadPoolExecutor(max_workers=4) as executor:
            future_pvp_zh = executor.submit(_fetch_market_items, "zh", "regular")
            future_pvp_en = executor.submit(_fetch_market_items, "en", "regular")
            future_pve_zh = executor.submit(_fetch_market_items, "zh", "pve")
            future_pve_en = executor.submit(_fetch_market_items, "en", "pve")
            pvp_zh = future_pvp_zh.result()
            pvp_en = future_pvp_en.result()
            pve_zh = future_pve_zh.result()
            pve_en = future_pve_en.result()

        cache = {
            "schemaVersion": 1,
            "marketTimestamp": time.time(),
            "source": MARKET_API_URL,
            "marketItemsPvp": merge_items(pvp_zh, pvp_en),
            "marketItemsPve": merge_items(pve_zh, pve_en),
        }
        write_json_atomic(get_market_cache_path(), cache)
        cache["refreshSeconds"] = round(time.time() - started, 2)
        return cache


def read_market_cache() -> dict[str, Any] | None:
    try:
        payload = json.loads(get_market_cache_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    return payload if isinstance(payload, dict) else None


def ensure_market_cache(force: bool = False) -> tuple[dict[str, Any] | None, str | None]:
    with _MARKET_CACHE_LOCK:
        cache = read_market_cache()
        timestamp = cache.get("marketTimestamp") if isinstance(cache, dict) else None
        stale = not isinstance(timestamp, (int, float)) or time.time() - float(timestamp) > MARKET_CACHE_TTL_SEC
        if cache and not force and not stale:
            return cache, None
        try:
            return refresh_market_cache(), None
        except Exception as exc:  # noqa: BLE001
            if cache:
                cache["lastError"] = str(exc)
                return cache, str(exc)
            return None, str(exc)


def market_items_for_mode(cache: dict[str, Any], mode: str) -> list[dict[str, Any]]:
    key = "marketItemsPve" if mode == "pve" else "marketItemsPvp"
    items = cache.get(key)
    return items if isinstance(items, list) else []


def normalize_market_query(value: Any) -> str:
    return str(value or "").strip().casefold()


def market_search_score(item: dict[str, Any], query: str) -> int:
    fields = [
        str(item.get("shortNameZh") or ""),
        str(item.get("nameZh") or ""),
        str(item.get("shortName") or ""),
        str(item.get("name") or ""),
        str(item.get("id") or ""),
    ]
    normalized = [field.casefold() for field in fields if field]
    if any(field == query for field in normalized):
        return 0
    if any(field.startswith(query) for field in normalized):
        return 1
    if any(query in field for field in normalized):
        return 2
    return 99


def market_search_payload(mode: str, query: str, limit: int = MARKET_SEARCH_LIMIT) -> dict[str, Any]:
    cache, error = ensure_market_cache(False)
    if not cache:
        return {"available": False, "error": error or "Market cache unavailable.", "items": []}
    items = market_items_for_mode(cache, mode)
    normalized_query = normalize_market_query(query)
    if normalized_query:
        scored = [(market_search_score(item, normalized_query), item) for item in items if isinstance(item, dict)]
        rows = [item for score, item in scored if score < 99]
        rows.sort(
            key=lambda item: (
                market_search_score(item, normalized_query),
                -(item.get("fleaPrice") or item.get("avg24hPrice") or 0),
                str(item.get("nameZh") or item.get("name") or ""),
            )
        )
    else:
        rows = [item for item in items if isinstance(item, dict) and item.get("fleaPrice")]
        rows.sort(key=lambda item: -(item.get("fleaPrice") or item.get("avg24hPrice") or 0))
    return {
        "available": True,
        "mode": "pve" if mode == "pve" else "pvp",
        "query": query,
        "updatedAt": cache.get("marketTimestamp"),
        "stale": bool(error),
        "lastError": error,
        "count": len(rows),
        "items": rows[: max(1, min(limit, 200))],
    }


def market_state_payload() -> dict[str, Any]:
    cache, error = ensure_market_cache(False)
    if not cache:
        return {"available": False, "error": error or "Market cache unavailable."}
    pvp_items = market_items_for_mode(cache, "pvp")
    pve_items = market_items_for_mode(cache, "pve")
    return {
        "available": True,
        "updatedAt": cache.get("marketTimestamp"),
        "source": cache.get("source") or MARKET_API_URL,
        "stale": bool(error),
        "lastError": error,
        "counts": {
            "pvp": len(pvp_items),
            "pve": len(pve_items),
            "pvpFlea": sum(1 for item in pvp_items if isinstance(item, dict) and item.get("fleaPrice")),
            "pveFlea": sum(1 for item in pve_items if isinstance(item, dict) and item.get("fleaPrice")),
        },
    }


def market_refresh_payload() -> dict[str, Any]:
    try:
        cache = refresh_market_cache()
    except Exception as exc:  # noqa: BLE001
        return {"available": False, "error": str(exc)}
    return {**market_state_payload(), "refreshSeconds": cache.get("refreshSeconds")}


def sanitize_market_icon_id(value: str) -> str | None:
    candidate = value.strip().lower()
    return candidate if re.fullmatch(r"[0-9a-f]{24}", candidate) else None


def sanitize_tracker_item_id(value: str) -> str | None:
    candidate = value.strip()
    if re.fullmatch(r"[0-9a-fA-F]{24}", candidate):
        return candidate.lower()
    return candidate if TRACKER_ITEM_ID_RE.fullmatch(candidate) else None


def _fetch_tracker_tasks(lang: str, game_mode: str) -> list[dict[str, Any]]:
    query = """
    query TrackerTasks($lang: LanguageCode, $gameMode: GameMode, $limit: Int) {
      tasks(lang: $lang, gameMode: $gameMode, limit: $limit) {
        id
        name
        minPlayerLevel
        trader { name }
        objectives {
          id
          type
          description
          optional
          ... on TaskObjectiveItem {
            count
            foundInRaid
            items {
              id
              name
              shortName
              iconLink
              gridImageLink
            }
          }
        }
      }
    }
    """
    data = _market_graphql(query, {"lang": lang, "gameMode": game_mode, "limit": TRACKER_QUERY_LIMIT})
    tasks = data.get("tasks")
    return tasks if isinstance(tasks, list) else []


def _fetch_tracker_hideout(lang: str, game_mode: str) -> list[dict[str, Any]]:
    query = """
    query TrackerHideout($lang: LanguageCode, $gameMode: GameMode, $limit: Int) {
      hideoutStations(lang: $lang, gameMode: $gameMode, limit: $limit) {
        id
        name
        levels {
          id
          level
          itemRequirements {
            count
            quantity
            item {
              id
              name
              shortName
              iconLink
              gridImageLink
            }
            attributes {
              name
              value
            }
          }
        }
      }
    }
    """
    data = _market_graphql(query, {"lang": lang, "gameMode": game_mode, "limit": TRACKER_QUERY_LIMIT})
    stations = data.get("hideoutStations")
    return stations if isinstance(stations, list) else []


def _is_fir_requirement(attributes: Any, default: bool = False) -> bool:
    if not isinstance(attributes, list):
        return default
    for attribute in attributes:
        if not isinstance(attribute, dict):
            continue
        if str(attribute.get("name") or "") == "foundInRaid":
            return str(attribute.get("value") or "").lower() == "true"
    return default


def _tracker_item_stub(item: Any) -> dict[str, Any] | None:
    if not isinstance(item, dict):
        return None
    item_id = str(item.get("id") or "").strip()
    if not item_id:
        return None
    return {
        "id": item_id,
        "name": str(item.get("name") or "").strip(),
        "shortName": str(item.get("shortName") or "").strip(),
        "iconLink": str(item.get("iconLink") or "").strip(),
        "gridImageLink": str(item.get("gridImageLink") or "").strip(),
    }


def _choice_requirement_item(objective: dict[str, Any], items: list[Any]) -> dict[str, Any] | None:
    objective_id = re.sub(r"[^A-Za-z0-9_.:-]+", "-", str(objective.get("id") or "").strip()).strip("-")
    if not objective_id:
        return None
    stubs = [_tracker_item_stub(item) for item in items]
    choices = [stub for stub in stubs if stub]
    if not choices:
        return None
    names = [choice.get("shortName") or choice.get("name") or choice["id"] for choice in choices]
    return {
        "id": f"choice:{objective_id[:120]}",
        "name": str(objective.get("description") or "任选物品").strip() or "任选物品",
        "shortName": " / ".join(names[:6]) + (" ..." if len(names) > 6 else ""),
        "iconLink": "",
        "gridImageLink": "",
        "choices": choices,
    }


def _collect_task_requirements(tasks: list[dict[str, Any]]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for task in tasks:
        if not isinstance(task, dict):
            continue
        trader = task.get("trader") if isinstance(task.get("trader"), dict) else {}
        task_rows: dict[str, dict[str, Any]] = {}
        for objective in task.get("objectives") or []:
            if not isinstance(objective, dict) or objective.get("optional"):
                continue
            count = objective.get("count")
            if not isinstance(count, int) or count <= 0:
                continue
            items = objective.get("items") if isinstance(objective.get("items"), list) else []
            if len(items) > 1:
                item = _choice_requirement_item(objective, items)
                if not item:
                    continue
                rows.append(
                    {
                        "itemId": item["id"],
                        "item": item,
                        "count": count,
                        "foundInRaid": bool(objective.get("foundInRaid")),
                        "sourceType": "task",
                        "sourceId": str(task.get("id") or ""),
                        "sourceName": str(task.get("name") or ""),
                        "sourceDetail": str(objective.get("description") or ""),
                        "trader": str(trader.get("name") or ""),
                        "level": task.get("minPlayerLevel") if isinstance(task.get("minPlayerLevel"), int) else None,
                        "choice": True,
                    }
                )
                continue
            if len(items) != 1:
                continue
            item = _tracker_item_stub(items[0])
            if not item:
                continue
            item_id = item["id"]
            existing = task_rows.get(item_id)
            found_in_raid = bool(objective.get("foundInRaid"))
            if existing is None:
                task_rows[item_id] = {
                    "itemId": item_id,
                    "item": item,
                    "count": count,
                    "foundInRaid": found_in_raid,
                    "sourceType": "task",
                    "sourceId": str(task.get("id") or ""),
                    "sourceName": str(task.get("name") or ""),
                    "sourceDetail": str(objective.get("description") or ""),
                    "trader": str(trader.get("name") or ""),
                    "level": task.get("minPlayerLevel") if isinstance(task.get("minPlayerLevel"), int) else None,
                }
                continue

            # Some quests expose both "find" and "hand over" objectives for the
            # same item. They describe one practical requirement, so keep the
            # largest count instead of summing the pair.
            if count > existing["count"]:
                existing["count"] = count
                existing["sourceDetail"] = str(objective.get("description") or existing.get("sourceDetail") or "")
            existing["foundInRaid"] = bool(existing.get("foundInRaid")) or found_in_raid
        rows.extend(task_rows.values())
    return rows


def _collect_hideout_requirements(stations: list[dict[str, Any]]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for station in stations:
        if not isinstance(station, dict):
            continue
        station_name = str(station.get("name") or "")
        for level in station.get("levels") or []:
            if not isinstance(level, dict):
                continue
            level_value = level.get("level")
            for requirement in level.get("itemRequirements") or []:
                if not isinstance(requirement, dict):
                    continue
                count = requirement.get("count")
                if not isinstance(count, int) or count <= 0:
                    continue
                item = _tracker_item_stub(requirement.get("item"))
                if not item:
                    continue
                rows.append(
                    {
                        "itemId": item["id"],
                        "item": item,
                        "count": count,
                        "foundInRaid": _is_fir_requirement(requirement.get("attributes")),
                        "sourceType": "hideout",
                        "sourceId": str(station.get("id") or ""),
                        "sourceName": station_name,
                        "sourceDetail": f"{station_name} {level_value}级" if isinstance(level_value, int) else station_name,
                        "station": station_name,
                        "level": level_value if isinstance(level_value, int) else None,
                    }
                )
    return rows


def read_tracker_requirement_patches() -> dict[str, list[dict[str, Any]]]:
    try:
        payload = json.loads(TRACKER_REQUIREMENT_PATCHES_JSON)
    except json.JSONDecodeError:
        return {"pvp": [], "pve": []}
    requirements = payload.get("requirements") if isinstance(payload, dict) else {}
    if not isinstance(requirements, dict):
        return {"pvp": [], "pve": []}
    return {
        "pvp": requirements.get("pvp") if isinstance(requirements.get("pvp"), list) else [],
        "pve": requirements.get("pve") if isinstance(requirements.get("pve"), list) else [],
    }


def merge_tracker_requirements(base_rows: list[dict[str, Any]], patch_rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    merged = list(base_rows)
    seen = {
        (str(row.get("sourceId") or ""), str(row.get("itemId") or ""))
        for row in merged
        if isinstance(row, dict) and str(row.get("sourceType") or "") == "task"
    }
    for row in patch_rows:
        if not isinstance(row, dict):
            continue
        key = (str(row.get("sourceId") or ""), str(row.get("itemId") or ""))
        if not key[0] or not key[1] or key in seen:
            continue
        next_row = dict(row)
        next_row["sourceType"] = "task"
        merged.append(next_row)
        seen.add(key)
    return merged


def refresh_tracker_cache() -> dict[str, Any]:
    with _TRACKER_CACHE_LOCK:
        started = time.time()
        with ThreadPoolExecutor(max_workers=4) as executor:
            future_pvp_tasks = executor.submit(_fetch_tracker_tasks, "zh", "regular")
            future_pve_tasks = executor.submit(_fetch_tracker_tasks, "zh", "pve")
            future_pvp_hideout = executor.submit(_fetch_tracker_hideout, "zh", "regular")
            future_pve_hideout = executor.submit(_fetch_tracker_hideout, "zh", "pve")
            pvp_tasks = future_pvp_tasks.result()
            pve_tasks = future_pve_tasks.result()
            pvp_hideout = future_pvp_hideout.result()
            pve_hideout = future_pve_hideout.result()
        patch_rows = read_tracker_requirement_patches()
        pvp_requirements = merge_tracker_requirements(
            _collect_task_requirements(pvp_tasks) + _collect_hideout_requirements(pvp_hideout),
            patch_rows["pvp"],
        )
        pve_requirements = merge_tracker_requirements(
            _collect_task_requirements(pve_tasks) + _collect_hideout_requirements(pve_hideout),
            patch_rows["pve"],
        )
        cache = {
            "schemaVersion": 1,
            "updatedAt": time.time(),
            "source": MARKET_API_URL,
            "patchSource": TRACKER_REQUIREMENT_PATCH_SOURCE,
            "patchCounts": {"pvp": len(patch_rows["pvp"]), "pve": len(patch_rows["pve"])},
            "requirements": {
                "pvp": pvp_requirements,
                "pve": pve_requirements,
            },
        }
        write_json_atomic(get_tracker_cache_path(), cache)
        cache["refreshSeconds"] = round(time.time() - started, 2)
        return cache


def read_tracker_cache() -> dict[str, Any] | None:
    try:
        payload = json.loads(get_tracker_cache_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    return payload if isinstance(payload, dict) else None


def ensure_tracker_cache(force: bool = False) -> tuple[dict[str, Any] | None, str | None]:
    with _TRACKER_CACHE_LOCK:
        cache = read_tracker_cache()
        timestamp = cache.get("updatedAt") if isinstance(cache, dict) else None
        stale = not isinstance(timestamp, (int, float)) or time.time() - float(timestamp) > TRACKER_CACHE_TTL_SEC
        if cache and not force and not stale:
            return cache, None
        try:
            return refresh_tracker_cache(), None
        except Exception as exc:  # noqa: BLE001
            if cache:
                cache["lastError"] = str(exc)
                return cache, str(exc)
            return None, str(exc)


def read_tracker_state() -> dict[str, Any]:
    try:
        payload = json.loads(get_tracker_state_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        payload = {}
    if not isinstance(payload, dict):
        payload = {}
    return {
        "schemaVersion": 2,
        "items": payload.get("items") if isinstance(payload.get("items"), dict) else {},
        "completedSources": payload.get("completedSources") if isinstance(payload.get("completedSources"), dict) else {},
        "updatedAt": payload.get("updatedAt") if isinstance(payload.get("updatedAt"), (int, float)) else None,
    }


def write_tracker_state(state_payload: dict[str, Any]) -> None:
    write_json_atomic(get_tracker_state_path(), state_payload)


def normalize_tracker_mode(value: Any) -> str:
    return "pve" if str(value or "").strip().lower() == "pve" else "pvp"


def tracker_source_key(requirement: dict[str, Any]) -> str:
    parts = [
        str(requirement.get("sourceType") or ""),
        str(requirement.get("sourceId") or ""),
        str(requirement.get("itemId") or ""),
        str(requirement.get("level") or ""),
        str(requirement.get("sourceName") or ""),
    ]
    return "|".join(parts)


def tracker_requirements_for_mode(cache: dict[str, Any], mode: str) -> list[dict[str, Any]]:
    requirements = cache.get("requirements") if isinstance(cache.get("requirements"), dict) else {}
    rows = requirements.get(normalize_tracker_mode(mode))
    return rows if isinstance(rows, list) else []


def build_market_item_lookup(mode: str) -> dict[str, dict[str, Any]]:
    cache, _error = ensure_market_cache(False)
    if not cache:
        return {}
    return {str(item.get("id") or ""): item for item in market_items_for_mode(cache, mode) if isinstance(item, dict)}


def aggregate_tracker_items(mode: str, include_tasks: bool, include_hideout: bool) -> tuple[list[dict[str, Any]], str | None, dict[str, Any] | None]:
    cache, error = ensure_tracker_cache(False)
    if not cache:
        return [], error or "Tracker cache unavailable.", None
    state_payload = read_tracker_state()
    collected = state_payload["items"].get(mode) if isinstance(state_payload["items"].get(mode), dict) else {}
    completed_sources = (
        state_payload["completedSources"].get(mode)
        if isinstance(state_payload["completedSources"].get(mode), dict)
        else {}
    )
    market_by_id = build_market_item_lookup(mode)
    grouped: dict[str, dict[str, Any]] = {}
    for requirement in tracker_requirements_for_mode(cache, mode):
        if not isinstance(requirement, dict):
            continue
        source_type = str(requirement.get("sourceType") or "")
        if source_type == "task" and not include_tasks:
            continue
        if source_type == "hideout" and not include_hideout:
            continue
        item_id = str(requirement.get("itemId") or "").strip()
        if not item_id:
            continue
        source_key = tracker_source_key(requirement)
        source_completed = bool(completed_sources.get(source_key))
        market_item = market_by_id.get(item_id, {})
        item_stub = requirement.get("item") if isinstance(requirement.get("item"), dict) else {}
        record = grouped.setdefault(
            item_id,
            {
                "id": item_id,
                "name": market_item.get("nameZh") or item_stub.get("name") or market_item.get("name") or "",
                "shortName": market_item.get("shortNameZh") or item_stub.get("shortName") or market_item.get("shortName") or "",
                "iconLink": market_item.get("gridImageLink") or market_item.get("iconLink") or item_stub.get("gridImageLink") or item_stub.get("iconLink") or "",
                "required": 0,
                "completedRequired": 0,
                "completedTaskRequired": 0,
                "completedHideoutRequired": 0,
                "foundInRaidRequired": 0,
                "taskRequired": 0,
                "hideoutRequired": 0,
                "have": int(collected.get(item_id, 0)) if isinstance(collected.get(item_id), int) else 0,
                "fleaPrice": market_item.get("fleaPrice"),
                "avg24hPrice": market_item.get("avg24hPrice"),
                "sources": [],
                "choice": bool(requirement.get("choice")),
                "choices": item_stub.get("choices") if isinstance(item_stub.get("choices"), list) else [],
            },
        )
        count = requirement.get("count") if isinstance(requirement.get("count"), int) else 0
        if requirement.get("foundInRaid"):
            record["foundInRaidRequired"] += count
        if source_completed:
            record["completedRequired"] += count
            if source_type == "task":
                record["completedTaskRequired"] += count
            elif source_type == "hideout":
                record["completedHideoutRequired"] += count
        else:
            record["required"] += count
            if source_type == "task":
                record["taskRequired"] += count
            elif source_type == "hideout":
                record["hideoutRequired"] += count
        if len(record["sources"]) < 80:
            record["sources"].append(
                {
                    "key": source_key,
                    "type": source_type,
                    "name": requirement.get("sourceName") or "",
                    "detail": requirement.get("sourceDetail") or "",
                    "count": count,
                    "foundInRaid": bool(requirement.get("foundInRaid")),
                    "trader": requirement.get("trader") or "",
                    "completed": source_completed,
                    "choice": bool(requirement.get("choice")),
                }
            )
    rows = list(grouped.values())
    for row in rows:
        row["remaining"] = max(0, int(row["required"]) - int(row["have"]))
        row["completed"] = int(row["required"]) <= 0 and int(row.get("completedRequired", 0)) > 0
        price = row.get("fleaPrice") if isinstance(row.get("fleaPrice"), int) else row.get("avg24hPrice")
        row["estimatedCost"] = (row["remaining"] * price) if isinstance(price, int) else None
    return rows, error, cache


def tracker_search_payload(
    mode: str,
    query: str,
    include_tasks: bool = True,
    include_hideout: bool = True,
    show_completed: bool = False,
    limit: int = TRACKER_SEARCH_LIMIT,
) -> dict[str, Any]:
    mode = normalize_tracker_mode(mode)
    rows, error, cache = aggregate_tracker_items(mode, include_tasks, include_hideout)
    normalized_query = normalize_market_query(query)
    if normalized_query:
        rows = [
            row
            for row in rows
            if any(
                normalized_query in str(row.get(field) or "").casefold()
                for field in ("name", "shortName", "id")
            )
        ]
    if not show_completed:
        rows = [row for row in rows if row.get("remaining", 0) > 0]
    rows.sort(key=lambda row: str(row.get("name") or row.get("shortName") or ""))
    return {
        "available": cache is not None,
        "mode": mode,
        "query": query,
        "updatedAt": cache.get("updatedAt") if cache else None,
        "stale": bool(error),
        "lastError": error,
        "count": len(rows),
        "items": rows[: max(1, min(limit, 1000))],
        "totals": {
            "required": sum(int(row.get("required", 0)) for row in rows),
            "remaining": sum(int(row.get("remaining", 0)) for row in rows),
            "estimatedCost": sum(int(row.get("estimatedCost") or 0) for row in rows),
        },
    }


def tracker_state_payload() -> dict[str, Any]:
    cache, error = ensure_tracker_cache(False)
    if not cache:
        return {"available": False, "error": error or "Tracker cache unavailable."}
    req = cache.get("requirements") if isinstance(cache.get("requirements"), dict) else {}
    return {
        "available": True,
        "updatedAt": cache.get("updatedAt"),
        "stale": bool(error),
        "lastError": error,
        "counts": {
            "pvp": len(req.get("pvp") if isinstance(req.get("pvp"), list) else []),
            "pve": len(req.get("pve") if isinstance(req.get("pve"), list) else []),
        },
    }


def tracker_refresh_payload() -> dict[str, Any]:
    try:
        cache = refresh_tracker_cache()
    except Exception as exc:  # noqa: BLE001
        return {"available": False, "error": str(exc)}
    return {**tracker_state_payload(), "refreshSeconds": cache.get("refreshSeconds")}


def update_tracker_item_payload(payload: dict[str, Any]) -> dict[str, Any]:
    mode = normalize_tracker_mode(payload.get("mode"))
    item_id = sanitize_tracker_item_id(str(payload.get("itemId") or ""))
    if not item_id:
        return {"available": False, "error": "Invalid item id."}
    try:
        have = max(0, int(payload.get("have")))
    except (TypeError, ValueError):
        return {"available": False, "error": "Invalid item count."}
    with _TRACKER_STATE_LOCK:
        state_payload = read_tracker_state()
        items = state_payload["items"]
        mode_items = items.get(mode) if isinstance(items.get(mode), dict) else {}
        if have > 0:
            mode_items[item_id] = have
        else:
            mode_items.pop(item_id, None)
        items[mode] = mode_items
        next_state = {
            "schemaVersion": 2,
            "items": items,
            "completedSources": state_payload["completedSources"],
            "updatedAt": time.time(),
        }
        write_tracker_state(next_state)
    return {"available": True, "mode": mode, "itemId": item_id, "have": have}


def update_tracker_source_payload(payload: dict[str, Any]) -> dict[str, Any]:
    mode = normalize_tracker_mode(payload.get("mode"))
    source_key = str(payload.get("sourceKey") or "").strip()
    if not source_key or len(source_key) > 500:
        return {"available": False, "error": "Invalid source key."}
    completed = bool(payload.get("completed"))
    with _TRACKER_STATE_LOCK:
        state_payload = read_tracker_state()
        completed_sources = state_payload["completedSources"]
        mode_sources = completed_sources.get(mode) if isinstance(completed_sources.get(mode), dict) else {}
        if completed:
            mode_sources[source_key] = True
        else:
            mode_sources.pop(source_key, None)
        completed_sources[mode] = mode_sources
        next_state = {
            "schemaVersion": 2,
            "items": state_payload["items"],
            "completedSources": completed_sources,
            "updatedAt": time.time(),
        }
        write_tracker_state(next_state)
    return {"available": True, "mode": mode, "sourceKey": source_key, "completed": completed}


def startup_port_candidates(preferred: int) -> list[int]:
    candidates = []
    if is_valid_tcp_port(preferred):
        candidates.append(preferred)
    return list(dict.fromkeys(candidates))


def pick_startup_port(preferred: int, allow_fallback: bool) -> int | None:
    candidates = startup_port_candidates(preferred)
    if not candidates:
        return None
    for port in candidates:
        if can_bind(port):
            return port
    if not allow_fallback:
        return None
    fallback_start = candidates[-1]
    for port in range(fallback_start + 1, min(65535, fallback_start + 30) + 1):
        if can_bind(port):
            return port
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind((HTTP_HOST, 0))
        return int(sock.getsockname()[1])


def normalize_name(value: Any, fallback: str = "Player") -> str:
    text = str(value or "").strip()
    if not text:
        text = fallback
    return text[:NAME_MAX_LENGTH]


def normalize_color(value: Any) -> str:
    text = str(value or "").strip()
    if HEX_COLOR_RE.fullmatch(text):
        return text.lower()
    return DEFAULT_COLOR


def normalize_host(value: Any) -> str:
    text = str(value or "").strip()
    return text[:128]


def is_finite_number(value: Any) -> bool:
    return isinstance(value, (int, float)) and math.isfinite(value)


def normalize_port(value: Any, default: int = SYNC_DEFAULT_PORT) -> int:
    try:
        numeric = int(value)
    except (TypeError, ValueError):
        return default
    if 1 <= numeric <= 65535:
        return numeric
    return default


def normalize_optional_port(value: Any) -> int | None:
    text = str(value or "").strip()
    if not text or not text.isdecimal():
        return None
    numeric = int(text)
    return numeric if 1 <= numeric <= 65535 else None


def normalize_optional_text(value: Any, max_length: int = 80) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    if not text:
        return None
    return text[:max_length]


def normalize_yaw(value: Any) -> float | None:
    if not is_finite_number(value):
        return None
    return float(value) % 360.0


def normalize_timestamp_ms(value: Any) -> int:
    if is_finite_number(value):
        numeric = int(value)
        if numeric > 0:
            return numeric
    return int(time.time() * 1000)


def sanitize_local_state(payload: dict[str, Any] | None) -> dict[str, Any]:
    payload = payload or {}
    x = float(payload["x"]) if is_finite_number(payload.get("x")) else None
    y = float(payload["y"]) if is_finite_number(payload.get("y")) else None
    z = float(payload["z"]) if is_finite_number(payload.get("z")) else None
    return {
        "mapId": normalize_optional_text(payload.get("mapId"), 64),
        "mapName": normalize_optional_text(payload.get("mapName"), 80),
        "x": x,
        "y": y,
        "z": z,
        "yawDeg": normalize_yaw(payload.get("yawDeg")),
        "raidStatus": normalize_optional_text(payload.get("raidStatus"), 40),
        "updatedAt": normalize_timestamp_ms(payload.get("updatedAt")),
    }


class SyncState:
    def __init__(self) -> None:
        self._lock = threading.RLock()
        self.instance_id = get_or_create_sync_client_id()
        self.enabled = False
        self.display_name = normalize_name("Player")
        self.color = DEFAULT_COLOR
        self.remote_host = ""
        self.remote_port: int | None = None
        self.sync_port = SYNC_DEFAULT_PORT
        self.last_error = ""
        self.local_state = sanitize_local_state({})
        self.peers: dict[str, dict[str, Any]] = {}

    def mode(self) -> str:
        with self._lock:
            return self._mode_locked()

    def configure(self, payload: dict[str, Any] | None) -> dict[str, Any]:
        payload = payload or {}
        with self._lock:
            if "enabled" in payload:
                self.enabled = bool(payload["enabled"])
            if "displayName" in payload:
                self.display_name = normalize_name(payload.get("displayName"), self.display_name)
            if "color" in payload:
                self.color = normalize_color(payload.get("color"))
            if "remoteHost" in payload:
                self.remote_host = normalize_host(payload.get("remoteHost"))
            if "remotePort" in payload:
                self.remote_port = normalize_optional_port(payload.get("remotePort"))
            if "syncPort" in payload:
                self.sync_port = normalize_port(payload.get("syncPort"), self.sync_port)

            if not self.enabled:
                self.peers.clear()
                self.last_error = ""
            elif self._mode_locked() == "host":
                self.last_error = ""
            return self._snapshot_locked()

    def update_local_state(self, payload: dict[str, Any] | None) -> None:
        with self._lock:
            self.local_state = sanitize_local_state(payload)

    def set_last_error(self, message: str) -> None:
        with self._lock:
            self.last_error = message[:180]

    def clear_last_error(self) -> None:
        with self._lock:
            self.last_error = ""

    def prune_peers(self) -> None:
        now = time.time()
        with self._lock:
            stale_ids = [
                peer_id
                for peer_id, peer in self.peers.items()
                if now - float(peer.get("lastSeenAt", 0.0)) > PEER_TTL_SEC
            ]
            for peer_id in stale_ids:
                self.peers.pop(peer_id, None)

    def snapshot(self) -> dict[str, Any]:
        self.prune_peers()
        with self._lock:
            return self._snapshot_locked()

    def get_join_target(self) -> tuple[str, int] | None:
        with self._lock:
            if self._mode_locked() != "join":
                return None
            host = self.remote_host
            port = self.remote_port
            if not host or not port:
                return None
            return (host, port)

    def get_host_port(self) -> int:
        with self._lock:
            return self.sync_port

    def build_join_update_packet(self) -> dict[str, Any]:
        with self._lock:
            return {
                "type": SYNC_UPDATE_TYPE,
                "version": PACKET_VERSION,
                "senderId": self.instance_id,
                "displayName": self.display_name,
                "color": self.color,
                "state": dict(self.local_state),
                "sentAt": int(time.time() * 1000),
            }

    def consume_remote_update(self, payload: dict[str, Any], address: tuple[str, int]) -> None:
        sender_id = str(payload.get("senderId") or "").strip()
        if (
            payload.get("type") != SYNC_UPDATE_TYPE
            or payload.get("version") != PACKET_VERSION
            or not sender_id
            or sender_id == self.instance_id
        ):
            return
        with self._lock:
            if self._mode_locked() != "host":
                return
            peer_state = sanitize_local_state(payload.get("state"))
            now = time.time()
            display_name = normalize_name(payload.get("displayName"))
            color = normalize_color(payload.get("color"))
            self.peers[sender_id] = {
                "senderId": sender_id,
                "displayName": display_name,
                "color": color,
                "host": address[0],
                "port": int(address[1]),
                "lastSeenAt": now,
                "state": peer_state,
            }

    def replace_peers_from_remote_snapshot(self, peers: list[dict[str, Any]]) -> None:
        now = time.time()
        with self._lock:
            self.peers.clear()
            for peer in peers:
                sender_id = str(peer.get("senderId") or "").strip()
                if not sender_id or sender_id == self.instance_id:
                    continue
                state_payload = peer.get("state") if isinstance(peer.get("state"), dict) else {}
                timestamp_ms = normalize_timestamp_ms(peer.get("lastSeenAt"))
                self.peers[sender_id] = {
                    "senderId": sender_id,
                    "displayName": normalize_name(peer.get("displayName")),
                    "color": normalize_color(peer.get("color")),
                    "host": normalize_host(peer.get("host")),
                    "port": normalize_port(peer.get("port"), SYNC_DEFAULT_PORT),
                    "lastSeenAt": min(now, timestamp_ms / 1000.0),
                    "state": sanitize_local_state(state_payload),
                }

    def clear_remote_snapshot(self) -> None:
        with self._lock:
            if self._mode_locked() == "join":
                self.peers.clear()

    def build_host_response(self) -> dict[str, Any]:
        self.prune_peers()
        with self._lock:
            if self._mode_locked() != "host":
                return {
                    "type": "sync_error",
                    "version": PACKET_VERSION,
                    "message": "host_disabled",
                    "serverTime": int(time.time() * 1000),
                    "peers": [],
                }
            peers = [
                {
                    "senderId": self.instance_id,
                    "displayName": self.display_name,
                    "color": self.color,
                    "host": "host",
                    "port": self.sync_port,
                    "lastSeenAt": int(time.time() * 1000),
                    "state": dict(self.local_state),
                }
            ]
            peers.extend(
                {
                    "senderId": peer["senderId"],
                    "displayName": peer["displayName"],
                    "color": peer["color"],
                    "host": peer["host"],
                    "port": peer["port"],
                    "lastSeenAt": int(float(peer["lastSeenAt"]) * 1000),
                    "state": dict(peer["state"]),
                }
                for peer in self.peers.values()
            )
        return {
            "type": SYNC_SNAPSHOT_TYPE,
            "version": PACKET_VERSION,
            "serverTime": int(time.time() * 1000),
            "peers": peers,
        }

    def _mode_locked(self) -> str:
        if not self.enabled:
            return "off"
        if self.remote_host:
            return "join"
        return "host"

    def _snapshot_locked(self) -> dict[str, Any]:
        peers = [
            {
                "senderId": peer["senderId"],
                "displayName": peer["displayName"],
                "color": peer["color"],
                "host": peer["host"],
                "port": peer["port"],
                "lastSeenAt": int(float(peer["lastSeenAt"]) * 1000),
                "state": dict(peer["state"]),
            }
            for peer in self.peers.values()
        ]
        peers.sort(key=lambda item: item["displayName"].lower())
        return {
            "enabled": self.enabled,
            "mode": self._mode_locked(),
            "instanceId": self.instance_id,
            "displayName": self.display_name,
            "color": self.color,
            "remoteHost": self.remote_host,
            "remotePort": self.remote_port,
            "syncPort": self.sync_port,
            "transport": "frp-tcp",
            "lastError": self.last_error,
            "localState": dict(self.local_state),
            "peers": peers,
            "now": int(time.time() * 1000),
        }


class FrpSyncTCPHandler(socketserver.StreamRequestHandler):
    def handle(self) -> None:
        sync_state: SyncState = self.server.sync_state  # type: ignore[attr-defined]
        try:
            self.request.settimeout(SYNC_SOCKET_TIMEOUT_SEC)
            raw = self.rfile.readline(MAX_FRAME_BYTES + 1)
            if not raw or len(raw) > MAX_FRAME_BYTES:
                return
            payload = json.loads(raw.decode("utf-8"))
        except (socket.timeout, UnicodeDecodeError, json.JSONDecodeError):
            return
        if not isinstance(payload, dict):
            return
        sync_state.consume_remote_update(payload, self.client_address)
        response = sync_state.build_host_response()
        data = json.dumps(response, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
        try:
            self.wfile.write(data)
            self.wfile.flush()
        except (OSError, socket.timeout):
            return


class FrpSyncTCPServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, server_address: tuple[str, int], handler_class: type[FrpSyncTCPHandler], sync_state: SyncState) -> None:
        super().__init__(server_address, handler_class)
        self.sync_state = sync_state


class FrpSyncService:
    def __init__(self, state: SyncState) -> None:
        self.state = state
        self._lock = threading.RLock()
        self._host_server: FrpSyncTCPServer | None = None
        self._host_thread: threading.Thread | None = None
        self._active_port: int | None = None

    def apply_config(self) -> None:
        mode = self.state.mode()
        if mode == "host":
            port = self.state.get_host_port()
            try:
                self._ensure_host_server(port)
            except OSError as exc:
                self.state.set_last_error(f"房主同步端口 {port} 启动失败: {exc}")
                return
            self.state.clear_last_error()
            return
        self._stop_host_server()
        if mode == "off":
            self.state.clear_last_error()

    def stop(self) -> None:
        self._stop_host_server()

    def sync_once_after_update(self) -> dict[str, Any]:
        self.state.prune_peers()
        mode = self.state.mode()
        if mode == "join":
            self._sync_join_once()
        return self.state.snapshot()

    def _sync_join_once(self) -> None:
        target = self.state.get_join_target()
        if not target:
            self.state.set_last_error("Missing FRP target address.")
            return
        host, port = target
        packet = self.state.build_join_update_packet()
        data = json.dumps(packet, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
        try:
            with socket.create_connection((host, port), timeout=SYNC_SOCKET_TIMEOUT_SEC) as sock:
                sock.sendall(data)
                sock.settimeout(SYNC_SOCKET_TIMEOUT_SEC)
                response_raw = b""
                response: dict[str, Any] | None = None
                while True:
                    try:
                        chunk = sock.recv(4096)
                    except socket.timeout:
                        if response_raw:
                            break
                        raise TimeoutError("Timed out waiting for host response.")
                    if not chunk:
                        break
                    response_raw += chunk
                    if len(response_raw) > MAX_FRAME_BYTES:
                        raise ValueError("Remote snapshot is too large.")
                    candidate = response_raw.strip()
                    if not candidate:
                        continue
                    try:
                        parsed = json.loads(candidate.decode("utf-8"))
                    except (UnicodeDecodeError, json.JSONDecodeError):
                        continue
                    if isinstance(parsed, dict):
                        response = parsed
                        break
            if response is None and response_raw:
                parsed = json.loads(response_raw.decode("utf-8").strip())
                response = parsed if isinstance(parsed, dict) else None
            if response is None:
                raise ValueError("No response from host.")
            if response.get("type") == "sync_error":
                message = str(response.get("message") or "Remote host rejected update.")
                raise ValueError(message)
            peers = response.get("peers")
            if not isinstance(peers, list):
                raise ValueError("Snapshot peers missing.")
            parsed_peers = [peer for peer in peers if isinstance(peer, dict)]
            self.state.replace_peers_from_remote_snapshot(parsed_peers)
            self.state.clear_last_error()
        except Exception as exc:  # noqa: BLE001
            self.state.clear_remote_snapshot()
            self.state.set_last_error(f"Join sync failed: {exc}")

    def _ensure_host_server(self, port: int) -> None:
        with self._lock:
            if self._host_server and self._active_port == port:
                return
            self._stop_host_server_locked()
            server = FrpSyncTCPServer(("0.0.0.0", port), FrpSyncTCPHandler, self.state)
            thread = threading.Thread(target=server.serve_forever, name="frp-sync-host", daemon=True)
            thread.start()
            self._host_server = server
            self._host_thread = thread
            self._active_port = port

    def _stop_host_server(self) -> None:
        with self._lock:
            self._stop_host_server_locked()

    def _stop_host_server_locked(self) -> None:
        if self._host_server:
            self._host_server.shutdown()
            self._host_server.server_close()
            self._host_server = None
        if self._host_thread:
            self._host_thread.join(timeout=1.5)
            self._host_thread = None
        self._active_port = None


class RequestBodyTooLarge(Exception):
    pass


class TarkovMapRequestHandler(BaseHTTPRequestHandler):
    server_version = "TarkovMapLocator/1.2"

    def log_message(self, fmt: str, *args: object) -> None:
        return

    @property
    def sync_state(self) -> SyncState:
        return self.server.sync_state  # type: ignore[attr-defined]

    @property
    def sync_service(self) -> FrpSyncService:
        return self.server.sync_service  # type: ignore[attr-defined]

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        if parsed.path == "/api/health":
            self._send_json(HTTPStatus.OK, build_api_health_payload())
            return
        if parsed.path == "/api/market/state":
            self._send_json(HTTPStatus.OK, market_state_payload())
            return
        if parsed.path == "/api/market/search":
            params = parse_qs(parsed.query)
            mode = str(params.get("mode", ["pvp"])[0] or "pvp").strip().lower()
            query = str(params.get("q", [""])[0] or "").strip()
            limit_text = str(params.get("limit", [str(MARKET_SEARCH_LIMIT)])[0] or MARKET_SEARCH_LIMIT)
            try:
                limit = int(limit_text)
            except ValueError:
                limit = MARKET_SEARCH_LIMIT
            self._send_json(HTTPStatus.OK, market_search_payload(mode, query, limit))
            return
        if parsed.path.startswith("/api/market/icon/"):
            self._send_market_icon(parsed.path.rsplit("/", 1)[-1])
            return
        if parsed.path == "/api/tracker/state":
            self._send_json(HTTPStatus.OK, tracker_state_payload())
            return
        if parsed.path == "/api/tracker/search":
            params = parse_qs(parsed.query)
            mode = str(params.get("mode", ["pvp"])[0] or "pvp").strip().lower()
            query = str(params.get("q", [""])[0] or "").strip()
            include_tasks = str(params.get("tasks", ["1"])[0]).lower() not in {"0", "false", "no"}
            include_hideout = str(params.get("hideout", ["1"])[0]).lower() not in {"0", "false", "no"}
            show_completed = str(params.get("completed", ["0"])[0]).lower() in {"1", "true", "yes"}
            limit_text = str(params.get("limit", [str(TRACKER_SEARCH_LIMIT)])[0] or TRACKER_SEARCH_LIMIT)
            try:
                limit = int(limit_text)
            except ValueError:
                limit = TRACKER_SEARCH_LIMIT
            self._send_json(
                HTTPStatus.OK,
                tracker_search_payload(mode, query, include_tasks, include_hideout, show_completed, limit),
            )
            return
        if parsed.path == "/api/local-paths/state":
            self._send_json(HTTPStatus.OK, build_local_paths_state())
            return
        if parsed.path == "/api/ui-preferences":
            self._send_json(HTTPStatus.OK, read_ui_preferences())
            return
        if parsed.path == "/api/local-paths/screenshots":
            self._send_json(HTTPStatus.OK, build_screenshot_scan_payload())
            return
        if parsed.path == "/api/local-paths/raid-log-files":
            params = parse_qs(parsed.query)
            full_rescan = str(params.get("full", ["0"])[0]).lower() in {"1", "true", "yes"}
            self._send_json(HTTPStatus.OK, build_raid_log_files_payload(full_rescan))
            return
        if parsed.path == "/api/lan-sync/state":
            self._send_json(HTTPStatus.OK, self.sync_state.snapshot())
            return
        self._send_json(HTTPStatus.NOT_FOUND, {"error": "API not found."})

    def do_POST(self) -> None:
        parsed = urlparse(self.path)
        if parsed.path == "/api/market/refresh":
            payload = market_refresh_payload()
            status = HTTPStatus.BAD_GATEWAY if payload.get("error") and not payload.get("available") else HTTPStatus.OK
            self._send_json(status, payload)
            return
        if parsed.path == "/api/tracker/refresh":
            payload = tracker_refresh_payload()
            status = HTTPStatus.BAD_GATEWAY if payload.get("error") and not payload.get("available") else HTTPStatus.OK
            self._send_json(status, payload)
            return
        if parsed.path == "/api/tracker/item":
            try:
                payload = self._read_json_body()
            except RequestBodyTooLarge:
                self._send_json(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, {"error": "Request body too large."})
                return
            if payload is None:
                self._send_json(HTTPStatus.BAD_REQUEST, {"error": "Invalid JSON payload."})
                return
            result = update_tracker_item_payload(payload)
            status = HTTPStatus.BAD_REQUEST if result.get("error") else HTTPStatus.OK
            self._send_json(status, result)
            return
        if parsed.path == "/api/tracker/source":
            try:
                payload = self._read_json_body()
            except RequestBodyTooLarge:
                self._send_json(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, {"error": "Request body too large."})
                return
            if payload is None:
                self._send_json(HTTPStatus.BAD_REQUEST, {"error": "Invalid JSON payload."})
                return
            result = update_tracker_source_payload(payload)
            status = HTTPStatus.BAD_REQUEST if result.get("error") else HTTPStatus.OK
            self._send_json(status, result)
            return
        if parsed.path == "/api/local-paths/pick":
            try:
                payload = self._read_json_body()
            except RequestBodyTooLarge:
                self._send_json(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, {"error": "Request body too large."})
                return
            if payload is None:
                self._send_json(HTTPStatus.BAD_REQUEST, {"error": "Invalid JSON payload."})
                return
            result = pick_local_directory(str(payload.get("kind") or ""))
            status = HTTPStatus.BAD_REQUEST if result.get("error") else HTTPStatus.OK
            self._send_json(status, result)
            return
        if parsed.path == "/api/ui-preferences":
            try:
                payload = self._read_json_body()
            except RequestBodyTooLarge:
                self._send_json(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, {"error": "Request body too large."})
                return
            if payload is None:
                self._send_json(HTTPStatus.BAD_REQUEST, {"error": "Invalid JSON payload."})
                return
            self._send_json(HTTPStatus.OK, write_ui_preferences(payload))
            return
        if parsed.path == "/api/lan-sync/config":
            try:
                payload = self._read_json_body()
            except RequestBodyTooLarge:
                self._send_json(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, {"error": "Request body too large."})
                return
            if payload is None:
                self._send_json(HTTPStatus.BAD_REQUEST, {"error": "Invalid JSON payload."})
                return
            self.sync_state.configure(payload)
            self.sync_service.apply_config()
            self._send_json(HTTPStatus.OK, self.sync_state.snapshot())
            return
        if parsed.path == "/api/lan-sync/update":
            try:
                payload = self._read_json_body()
            except RequestBodyTooLarge:
                self._send_json(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, {"error": "Request body too large."})
                return
            if payload is None:
                self._send_json(HTTPStatus.BAD_REQUEST, {"error": "Invalid JSON payload."})
                return
            self.sync_state.update_local_state(payload)
            snapshot = self.sync_service.sync_once_after_update()
            self._send_json(HTTPStatus.OK, snapshot)
            return
        self._send_json(HTTPStatus.NOT_FOUND, {"error": "API not found."})

    def _read_json_body(self) -> dict[str, Any] | None:
        length_text = self.headers.get("Content-Length", "0")
        try:
            length = max(0, int(length_text))
        except ValueError:
            length = 0
        if length > MAX_HTTP_BODY_BYTES:
            raise RequestBodyTooLarge
        raw = self.rfile.read(length) if length > 0 else b"{}"
        try:
            payload = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return None
        return payload if isinstance(payload, dict) else None

    def _send_json(self, status: HTTPStatus, payload: dict[str, Any], cache_control: str = "no-store") -> None:
        data = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", cache_control)
        self.end_headers()
        self.wfile.write(data)

    def _send_market_icon(self, raw_name: str) -> None:
        item_id = sanitize_market_icon_id(raw_name.rsplit(".", 1)[0])
        if not item_id:
            self.send_error(HTTPStatus.NOT_FOUND, "Icon not found.")
            return
        icon_path = get_market_icon_dir() / f"{item_id}.webp"
        if not icon_path.exists():
            cache = read_market_cache()
            icon_link = ""
            if cache:
                for mode in ("pvp", "pve"):
                    for item in market_items_for_mode(cache, mode):
                        if isinstance(item, dict) and item.get("id") == item_id:
                            icon_link = str(item.get("gridImageLink") or item.get("iconLink") or "")
                            break
                    if icon_link:
                        break
            if icon_link:
                try:
                    request = Request(icon_link, headers={"User-Agent": "TarkovMapLocator/market-icon"})
                    with urlopen(request, timeout=12.0) as response:
                        content_type = response.headers.get("Content-Type", "")
                        data = response.read(MARKET_ICON_MAX_BYTES + 1)
                    if len(data) <= MARKET_ICON_MAX_BYTES and data and guess_image_content_type(data, content_type).startswith("image/"):
                        icon_path.parent.mkdir(parents=True, exist_ok=True)
                        icon_path.write_bytes(data)
                except Exception:  # noqa: BLE001
                    pass
        if not icon_path.exists():
            self.send_error(HTTPStatus.NOT_FOUND, "Icon not found.")
            return
        try:
            data = icon_path.read_bytes()
        except OSError:
            self.send_error(HTTPStatus.NOT_FOUND, "Icon not found.")
            return
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", guess_image_content_type(data))
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "public, max-age=604800")
        self.end_headers()
        self.wfile.write(data)


class TarkovMapServer(ThreadingHTTPServer):
    allow_reuse_address = True

    def __init__(
        self,
        server_address: tuple[str, int],
        handler_class: type[TarkovMapRequestHandler],
        sync_state: SyncState,
        sync_service: FrpSyncService,
    ) -> None:
        super().__init__(server_address, handler_class)
        self.sync_state = sync_state
        self.sync_service = sync_service


def run() -> int:
    parser = argparse.ArgumentParser(description=APP_TITLE)
    parser.add_argument("--port", type=int, default=DEFAULT_HTTP_PORT, help="Preferred HTTP port.")
    parser.add_argument("--auto-port", action="store_true", help="Use a nearby available HTTP port if --port is busy.")
    args = parser.parse_args()
    root = get_resource_root()

    if not args.auto_port:
        if not can_bind(args.port):
            message = (
                f"无法启动本地 API 服务：端口 {args.port} 不可用。"
                "请关闭占用端口的程序后重试。"
            )
            print(f"[ERROR] {message}")
            report_startup_error(message)
            return PORT_UNAVAILABLE_EXIT_CODE

        port = args.port
    else:
        port = pick_startup_port(args.port, True)

    if port is None:
        message = (
            f"无法启动本地 API 服务：端口 {args.port} 不可用。"
            "请关闭占用端口的程序后重试。"
        )
        print(f"[ERROR] {message}")
        report_startup_error(message)
        return PORT_UNAVAILABLE_EXIT_CODE
    if port != args.port:
        append_startup_log(f"端口 {args.port} 被占用，已临时改用 {port}。")
    sync_state = SyncState()
    sync_service = FrpSyncService(sync_state)
    sync_service.apply_config()

    try:
        server = TarkovMapServer((HTTP_HOST, port), TarkovMapRequestHandler, sync_state, sync_service)
    except OSError as exc:
        message = f"HTTP port {port} failed to bind: {exc}"
        print(f"[ERROR] {message}")
        report_startup_error(message)
        return PORT_UNAVAILABLE_EXIT_CODE
    thread = threading.Thread(target=server.serve_forever, name="http-server", daemon=True)
    thread.start()

    url = f"http://{HTTP_HOST}:{port}/"
    print(f"{APP_TITLE} started")
    print(f"Root: {root}")
    print(f"URL: {url}")
    print(f"FRP sync default port: {SYNC_DEFAULT_PORT}")
    print("Press Ctrl+C to stop")

    try:
        while thread.is_alive():
            time.sleep(0.5)
    except KeyboardInterrupt:
        print("\nStopping...")
    finally:
        server.shutdown()
        server.server_close()
        sync_service.stop()
    return 0


if __name__ == "__main__":
    raise SystemExit(run())
