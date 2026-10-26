from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import tempfile
import unicodedata
import threading
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path
from typing import Any

DEFAULT_USERS_FILE = "/var/lib/vpn-monitor/users/users.json"
_STORE_LOCK = threading.RLock()
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


class ProtectedAccountError(PermissionError):
    pass


class UserStore:
    def __init__(self, path: str | os.PathLike[str] | None = None):
        self.path = Path(path or os.getenv("USERS_FILE", DEFAULT_USERS_FILE)).resolve()
        self._cached_mtime_ns: int | None = None
        self._cached_users: dict[str, StoredUser] = {}

    @contextmanager
    def _locked(self):
        # Serialize CLI and web writers across processes, then atomically replace the document.
        with _STORE_LOCK:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            with open(self.path.with_suffix(self.path.suffix + ".lock"), "a+b") as handle:
                if hasattr(os, "geteuid") and os.geteuid() == 0:
                    owner = self.path.stat() if self.path.exists() else self.path.parent.stat()
                    os.fchown(handle.fileno(), owner.st_uid, owner.st_gid)
                    os.fchmod(handle.fileno(), 0o660)
                try:
                    import fcntl
                except ImportError:  # Windows is used only for isolated developer tests.
                    fcntl = None
                if fcntl:
                    fcntl.flock(handle, fcntl.LOCK_EX)
                try:
                    yield
                finally:
                    if fcntl:
                        fcntl.flock(handle, fcntl.LOCK_UN)

    def ensure_exists(self) -> None:
        with self._locked():
            if self.path.exists():
                return
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
            if hasattr(os, "geteuid") and os.geteuid() == 0 and self.path.exists():
                previous = self.path.stat()
                os.chown(temp_name, previous.st_uid, previous.st_gid)
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

    def manage(self, username: str, *, admin: str, create: bool = False,
               password: str | None = None, enabled: bool | None = None, delete: bool = False) -> None:
        username = unicodedata.normalize("NFKC", username.strip())
        if not username or len(username) > 80 or ":" in username or any(unicodedata.category(c).startswith("C") for c in username):
            raise ValueError("Имя: от 1 до 80 символов, без двоеточия и управляющих символов")
        if normalize_identity(username) == normalize_identity(admin):
            raise ProtectedAccountError("Администратор управляется конфигурацией сервера")
        if password is not None and (not password or len(password) > 256):
            raise ValueError("Пароль: от 1 до 256 символов")
        with self._locked():
            data = self._load_document()
            users = data.get("users", [])
            existing = next((u for u in users if normalize_identity(str(u.get("username", ""))) == normalize_identity(username)), None)
            if create and existing is not None:
                raise FileExistsError("Пользователь с таким именем уже существует")
            if not create and existing is None:
                raise LookupError("Пользователь не найден")
            if create:
                if password is None:
                    raise ValueError("Задайте пароль")
                existing = {"username": username, "enabled": True}
                users.append(existing)
            if delete:
                users.remove(existing)
            else:
                if password is not None:
                    existing["password"] = hash_password(password)
                if enabled is not None:
                    existing["enabled"] = enabled
            data["version"] = 1
            data["users"] = users
            self._write_document(data)

    def upsert(self, username: str, password: str, *, enabled: bool = True) -> None:
        with self._locked():
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
        with self._locked():
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
        with self._locked():
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
