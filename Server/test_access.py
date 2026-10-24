import os
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / ".testdeps"))
os.environ["AUTH_USER"] = "test-admin"
os.environ["AUTH_PASSWORD"] = "local-test-only"
from fastapi.testclient import TestClient
import app as server
from auth_store import hash_password

class AccessTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.old_store = server.user_store
        from auth_store import UserStore
        self.store = UserStore(Path(self.tmp.name) / "users.json")
        self.store._write_document({"version": 1, "users": [
            {"username": "Офис", "enabled": True, "password": hash_password("test-user-password")},
            {"username": "Empty", "enabled": True, "password": hash_password("test-user-password")},
        ]})
        server.user_store = self.store
        self.old_state = server.state
        server.state = {
            "updated": "2026-10-23", "configuration": "test", "error": None,
            "groups": [
                {"name": "Офис", "peer_ids": ["own", "shared"]},
                {"name": "Private", "peer_ids": ["other", "shared"]},
            ],
            "peers": [
                {"id": "own", "groups": ["Офис"], "status": "running"},
                {"id": "shared", "groups": ["Офис", "Private"], "status": "stopped"},
                {"id": "other", "groups": ["Private"], "status": "running"},
            ],
        }
        # No lifespan context: never start the WGDashboard refresh loop.
        self.client = TestClient(server.app)

    def tearDown(self):
        self.client.close()
        server.state = self.old_state
        server.user_store = self.old_store
        self.tmp.cleanup()

    def test_admin(self):
        response = self.client.get("/api/v1/peers", auth=("test-admin", "local-test-only"))
        self.assertEqual(response.status_code, 200)
        self.assertEqual(len(response.json()["items"]), 3)

    def test_restricted_routes(self):
        auth = ("Офис", "test-user-password")
        # httpx BasicAuth uses UTF-8, matching the desktop client.
        for path, field in [("/api/status", "peers"), ("/api/v1/peers", "items")]:
            response = self.client.get(path, auth=auth)
            self.assertEqual(response.status_code, 200, response.text)
            self.assertEqual({p["id"] for p in response.json()[field]}, {"own", "shared"})
            self.assertNotIn("Private", response.text)
        self.assertEqual(self.client.get("/api/v1/summary", auth=auth).json()["total"], 2)
        self.assertEqual(len(self.client.get("/api/v1/groups", auth=auth).json()["items"]), 1)
        self.assertEqual(self.client.get("/api/v1/peers/other", auth=auth).status_code, 404)
        self.assertEqual(self.client.get("/api/v1/peers/shared", auth=auth).status_code, 200)

    def test_empty_group_and_bad_auth(self):
        self.assertEqual(self.client.get("/api/v1/peers", auth=("Empty", "test-user-password")).json()["items"], [])
        for headers in [{}, {"Authorization": "Basic broken!"}]:
            self.assertEqual(self.client.get("/api/status", headers=headers).status_code, 401)
        self.assertEqual(self.client.get("/api/v1/peers", auth=("Офис", "wrong")).status_code, 401)

if __name__ == "__main__":
    unittest.main()