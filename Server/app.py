from __future__ import annotations

import asyncio
import base64
import os
import re
import secrets
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import httpx
from fastapi import Depends, FastAPI, HTTPException, Request, status
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles

from auth_store import UserStore, normalize_identity

BASE_DIR = Path(__file__).resolve().parent
WG_URL = os.getenv("WG_URL", "http://127.0.0.1:10086").rstrip("/")
WG_API_KEY = os.getenv("WG_API_KEY", "")
WG_CONFIG = os.getenv("WG_CONFIG", "awg0")
REFRESH_SECONDS = int(os.getenv("REFRESH_SECONDS", "5"))
PORT = int(os.getenv("PORT", "80"))

AUTH_USER = os.getenv("AUTH_USER", "")
AUTH_PASSWORD = os.getenv("AUTH_PASSWORD", "")
USERS_FILE = os.getenv("USERS_FILE", "/etc/vpn-monitor-users.json")

if not AUTH_USER or not AUTH_PASSWORD:
    raise RuntimeError(
        "AUTH_USER и AUTH_PASSWORD должны быть заданы в /etc/vpn-monitor.env"
    )

user_store = UserStore(USERS_FILE)
app = FastAPI(title="VPN Monitor")
app.mount("/static", StaticFiles(directory=BASE_DIR / "static"), name="static")

state: dict[str, Any] = {
    "updated": None,
    "configuration": WG_CONFIG,
    "peers": [],
    "groups": [],
    "error": None,
}


@dataclass(frozen=True)
class Principal:
    username: str
    is_admin: bool


def _unauthorized() -> None:
    raise HTTPException(
        status_code=status.HTTP_401_UNAUTHORIZED,
        detail="Неверный логин или пароль",
        headers={
            "WWW-Authenticate": 'Basic realm="VPN Monitor", charset="UTF-8"'
        },
    )


def _decode_basic(request: Request) -> tuple[str, str]:
    header = request.headers.get("Authorization", "")
    if not header:
        _unauthorized()

    scheme, separator, encoded = header.partition(" ")
    if separator != " " or scheme.lower() != "basic" or not encoded:
        _unauthorized()

    try:
        raw = base64.b64decode(encoded, validate=True)
        try:
            value = raw.decode("utf-8")
        except UnicodeDecodeError:
            # Старые Basic-клиенты иногда отправляют ISO-8859-1.
            value = raw.decode("latin-1")
        username, password = value.split(":", 1)
    except (ValueError, UnicodeDecodeError):
        _unauthorized()
        raise AssertionError("unreachable")

    return username, password


def _compare_text(left: str, right: str) -> bool:
    return secrets.compare_digest(
        left.encode("utf-8"),
        right.encode("utf-8"),
    )


def require_auth(request: Request) -> Principal:
    username, password = _decode_basic(request)

    admin_user_ok = _compare_text(
        normalize_identity(username), normalize_identity(AUTH_USER)
    )
    admin_password_ok = _compare_text(password, AUTH_PASSWORD)
    if admin_user_ok and admin_password_ok:
        return Principal(username=AUTH_USER, is_admin=True)

    stored = user_store.authenticate(username, password)
    if stored is None:
        _unauthorized()

    return Principal(username=stored.username, is_admin=False)


def recursive_find(obj: Any, key: str):
    if isinstance(obj, dict):
        if key in obj:
            return obj[key]
        for value in obj.values():
            result = recursive_find(value, key)
            if result is not None:
                return result
    elif isinstance(obj, list):
        for value in obj:
            result = recursive_find(value, key)
            if result is not None:
                return result
    return None


def first_ipv4(value: Any) -> str | None:
    if not value:
        return None
    match = re.search(r"\b(?:\d{1,3}\.){3}\d{1,3}\b", str(value))
    return match.group(0) if match else None


def extract_groups(config: dict[str, Any]) -> list[dict[str, Any]]:
    info = config.get("Info") or {}
    raw_groups = info.get("PeerGroups") or {}
    if not isinstance(raw_groups, dict):
        return []

    result: list[dict[str, Any]] = []
    for group_id, group in raw_groups.items():
        if not isinstance(group, dict):
            continue
        peer_ids = group.get("Peers") or []
        if not isinstance(peer_ids, list):
            peer_ids = []
        result.append({
            "id": str(group_id),
            "name": group.get("GroupName") or "Без названия",
            "description": group.get("Description") or "",
            "icon": group.get("Icon") or "",
            "background_color": group.get("BackgroundColor") or "",
            "peer_ids": [str(x) for x in peer_ids],
        })
    return result


async def fetch_data() -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    headers = {"wg-dashboard-apikey": WG_API_KEY}

    async with httpx.AsyncClient(timeout=10) as client:
        peers_response = await client.get(
            f"{WG_URL}/api/getWireguardConfigurationInfo",
            params={"configurationName": WG_CONFIG},
            headers=headers,
        )
        peers_response.raise_for_status()
        peers_data = peers_response.json()

        groups_response = await client.get(
            f"{WG_URL}/api/getWireguardConfigurations",
            headers=headers,
        )
        groups_response.raise_for_status()
        groups_data = groups_response.json()

    raw_peers = recursive_find(peers_data, "configurationPeers")
    if not isinstance(raw_peers, list):
        raise RuntimeError("WGDashboard не вернул configurationPeers")

    configurations = groups_data.get("data") or []
    selected_config = next(
        (
            config
            for config in configurations
            if isinstance(config, dict) and config.get("Name") == WG_CONFIG
        ),
        None,
    )
    if selected_config is None:
        raise RuntimeError(f"Конфигурация {WG_CONFIG} не найдена в WGDashboard")

    groups = extract_groups(selected_config)
    peer_groups: dict[str, list[str]] = {}
    for group in groups:
        for peer_id in group["peer_ids"]:
            peer_groups.setdefault(peer_id, []).append(group["name"])

    peers: list[dict[str, Any]] = []
    for peer in raw_peers:
        if not isinstance(peer, dict):
            continue
        peer_id = str(peer.get("id", ""))
        allowed_ip = peer.get("allowed_ip", "")
        peers.append({
            "id": peer_id,
            "name": peer.get("name") or peer_id,
            "vpn_ip": first_ipv4(allowed_ip),
            "allowed_ip": allowed_ip,
            "endpoint": peer.get("endpoint"),
            "status": peer.get("status"),
            "latest_handshake": peer.get("latest_handshake"),
            "receive": peer.get("total_receive"),
            "sent": peer.get("total_sent"),
            "notes": peer.get("notes") or "",
            "groups": peer_groups.get(peer_id, []),
        })

    return peers, groups


async def refresh_loop() -> None:
    while True:
        try:
            peers, groups = await fetch_data()
            state["peers"] = peers
            state["groups"] = groups
            state["updated"] = __import__("datetime").datetime.now().isoformat(
                timespec="seconds"
            )
            state["error"] = None
        except Exception as exc:
            state["error"] = str(exc)
        await asyncio.sleep(REFRESH_SECONDS)


@app.on_event("startup")
async def startup() -> None:
    asyncio.create_task(refresh_loop())


def _visible_state(principal: Principal) -> dict[str, Any]:
    if principal.is_admin:
        return {
            "updated": state["updated"],
            "configuration": state["configuration"],
            "peers": list(state["peers"]),
            "groups": list(state["groups"]),
            "error": state["error"],
        }

    wanted = normalize_identity(principal.username)
    visible_groups = [
        dict(group)
        for group in state["groups"]
        if normalize_identity(str(group.get("name", ""))) == wanted
    ]
    allowed_peer_ids = {
        str(peer_id)
        for group in visible_groups
        for peer_id in group.get("peer_ids", [])
    }
    allowed_group_names = {
        normalize_identity(str(group.get("name", "")))
        for group in visible_groups
    }

    visible_peers: list[dict[str, Any]] = []
    for peer in state["peers"]:
        if str(peer.get("id", "")) not in allowed_peer_ids:
            continue
        item = dict(peer)
        # Не раскрываем названия других групп, если peer входит сразу в несколько.
        item["groups"] = [
            name
            for name in peer.get("groups", [])
            if normalize_identity(str(name)) in allowed_group_names
        ]
        visible_peers.append(item)

    return {
        "updated": state["updated"],
        "configuration": state["configuration"],
        "peers": visible_peers,
        "groups": visible_groups,
        "error": state["error"],
    }


def _summary(data: dict[str, Any]) -> dict[str, Any]:
    peers = data["peers"]
    online = sum(1 for peer in peers if peer.get("status") == "running")
    return {
        "configuration": data["configuration"],
        "updated": data["updated"],
        "total": len(peers),
        "online": online,
        "offline": len(peers) - online,
        "groups": len(data["groups"]),
        "error": data["error"],
    }


@app.get("/", response_class=FileResponse)
async def index(principal: Principal = Depends(require_auth)):
    return FileResponse(BASE_DIR / "templates" / "index.html")


@app.get("/api/status")
async def api_status(principal: Principal = Depends(require_auth)):
    return _visible_state(principal)


@app.get("/api/v1/health")
async def api_v1_health(principal: Principal = Depends(require_auth)):
    return {
        "status": "ok" if state["error"] is None else "degraded",
        "configuration": state["configuration"],
        "updated": state["updated"],
        "error": state["error"],
    }


@app.get("/api/v1/me")
async def api_v1_me(principal: Principal = Depends(require_auth)):
    return {
        "username": principal.username,
        "is_admin": principal.is_admin,
        "group": None if principal.is_admin else principal.username,
    }


@app.get("/api/v1/summary")
async def api_v1_summary(principal: Principal = Depends(require_auth)):
    return _summary(_visible_state(principal))


@app.get("/api/v1/peers")
async def api_v1_peers(principal: Principal = Depends(require_auth)):
    data = _visible_state(principal)
    return {
        "updated": data["updated"],
        "configuration": data["configuration"],
        "items": data["peers"],
    }


@app.get("/api/v1/groups")
async def api_v1_groups(principal: Principal = Depends(require_auth)):
    data = _visible_state(principal)
    return {
        "updated": data["updated"],
        "configuration": data["configuration"],
        "items": data["groups"],
    }


@app.get("/api/v1/peers/{peer_id:path}")
async def api_v1_peer(peer_id: str, principal: Principal = Depends(require_auth)):
    data = _visible_state(principal)
    peer = next((item for item in data["peers"] if item.get("id") == peer_id), None)
    if peer is None:
        # 404, а не 403: не раскрываем наличие чужого peer.
        raise HTTPException(status_code=404, detail="Peer not found")
    return peer


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="0.0.0.0", port=PORT)
