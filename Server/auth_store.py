from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import tempfile
import unicodedata
from dataclasses import dataclass
from pathlib import Path
from typing import Any

DEFAULT_USERS_FILE = "/etc/vpn-monitor-users.json"
SCRYPT_N = 2**14
SCRYPT_R = 8
SCRYPT_P = 1
SCRYPT_DKLEN = 32


def normalize_identity(value: str) -> str:
    return unicodedata.normalize("NFKC", value.strip()).casefold()


def _b64e(value: bytes) -> str:
    return base64.b64encode(value).decode("ascii")


def _b64d(value: str) -> bytes:
    return base64.b64decode(value.encode("ascii"), validate=True)


def hash_password(password: str) -> dict[str, Any]:
    if not password:
        raise ValueError("Пароль не может быть пустым")

    salt = os.urandom(16)
    digest = hashlib.scrypt(
        password.encode("utf-8"),
        salt=salt,
        n=SCRYPT_N,
        r=SCRYPT_R,
        p=SCRYPT_P,
        dklen=SCRYPT_DKLEN,
    )
    return {
        "algorithm": "scrypt",
        "n": SCRYPT_N,
        "r": SCRYPT_R,
        "p": SCRYPT_P,
        "dklen": SCRYPT_DKLEN,
        "salt": _b64e(salt),
        "hash": _b64e(digest),
    }


def verify_password(password: str, password_data: dict[str, Any]) -> bool:
    try:
        if password_data.get("algorithm") != "scrypt":
            return False
        salt = _b64d(str(password_data["salt"]))
        expected = _b64d(str(password_data["hash"]))
        actual = hashlib.scrypt(
            password.encode("utf-8"),
            salt=salt,
            n=int(password_data.get("n", SCRYPT_N)),
            r=int(password_data.get("r", SCRYPT_R)),
            p=int(password_data.get("p", SCRYPT_P)),
            dklen=int(password_data.get("dklen", len(expected))),
        )
        return hmac.compare_digest(actual, expected)
    except (ValueError, TypeError, KeyError):
        return False


@dataclass(frozen=True)
class StoredUser:
    username: str
    enabled: bool
    password: dict[str, Any]


class UserStore:
    def __init__(self, path: str | os.PathLike[str] | None = None):
        self.path = Path(path or os.getenv("USERS_FILE", DEFAULT_USERS_FILE))
        self._cached_mtime_ns: int | None = None
        self._cached_users: dict[str, StoredUser] = {}

    def ensure_exists(self) -> None:
        if self.path.exists():
            return
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._write_document({"version": 1, "users": []})

    def _load_document(self) -> dict[str, Any]:
        try:
            raw = self.path.read_text(encoding="utf-8")
        except FileNotFoundError:
            return {"version": 1, "users": []}
        data = json.loads(raw)
        if not isinstance(data, dict):
            raise ValueError("Файл пользователей должен содержать JSON-объект")
        if not isinstance(data.get("users", []), list):
            raise ValueError("Поле users должно быть списком")
        return data

    def _write_document(self, data: dict[str, Any]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd, temp_name = tempfile.mkstemp(
            prefix=f".{self.path.name}.",
            dir=str(self.path.parent),
            text=True,
        )
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                json.dump(data, handle, ensure_ascii=False, indent=2)
                handle.write("\n")
                handle.flush()
                os.fsync(handle.fileno())
            os.chmod(temp_name, 0o640)
            os.replace(temp_name, self.path)
            os.chmod(self.path, 0o640)
        finally:
            if os.path.exists(temp_name):
                os.unlink(temp_name)
        self._cached_mtime_ns = None

    def _users(self) -> dict[str, StoredUser]:
        try:
            mtime_ns = self.path.stat().st_mtime_ns
        except FileNotFoundError:
            self._cached_mtime_ns = None
            self._cached_users = {}
            return {}

        if self._cached_mtime_ns == mtime_ns:
            return self._cached_users

        data = self._load_document()
        users: dict[str, StoredUser] = {}
        for item in data.get("users", []):
            if not isinstance(item, dict):
                continue
            username = str(item.get("username", "")).strip()
            password = item.get("password")
            if not username or not isinstance(password, dict):
                continue
            users[normalize_identity(username)] = StoredUser(
                username=username,
                enabled=bool(item.get("enabled", True)),
                password=password,
            )

        self._cached_mtime_ns = mtime_ns
        self._cached_users = users
        return users

    def authenticate(self, username: str, password: str) -> StoredUser | None:
        user = self._users().get(normalize_identity(username))
        if user is None or not user.enabled:
            return None
        return user if verify_password(password, user.password) else None

    def list_users(self) -> list[StoredUser]:
        return sorted(self._users().values(), key=lambda x: normalize_identity(x.username))

    def upsert(self, username: str, password: str, *, enabled: bool = True) -> None:
        username = unicodedata.normalize("NFKC", username.strip())
        if not username:
            raise ValueError("Имя пользователя не может быть пустым")
        if ":" in username:
            raise ValueError("Имя пользователя не может содержать ':'")

        data = self._load_document()
        target = normalize_identity(username)
        users = [x for x in data.get("users", []) if isinstance(x, dict)]
        updated = False
        new_password = hash_password(password)
        for item in users:
            if normalize_identity(str(item.get("username", ""))) == target:
                item.clear()
                item.update({
                    "username": username,
                    "enabled": bool(enabled),
                    "password": new_password,
                })
                updated = True
                break
        if not updated:
            users.append({
                "username": username,
                "enabled": bool(enabled),
                "password": new_password,
            })
        data["version"] = 1
        data["users"] = users
        self._write_document(data)

    def set_enabled(self, username: str, enabled: bool) -> bool:
        data = self._load_document()
        target = normalize_identity(username)
        changed = False
        for item in data.get("users", []):
            if isinstance(item, dict) and normalize_identity(str(item.get("username", ""))) == target:
                item["enabled"] = bool(enabled)
                changed = True
                break
        if changed:
            self._write_document(data)
        return changed

    def delete(self, username: str) -> bool:
        data = self._load_document()
        target = normalize_identity(username)
        old = data.get("users", [])
        users = [
            item for item in old
            if not (
                isinstance(item, dict)
                and normalize_identity(str(item.get("username", ""))) == target
            )
        ]
        if len(users) == len(old):
            return False
        data["users"] = users
        self._write_document(data)
        return True
