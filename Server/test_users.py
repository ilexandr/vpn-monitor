from concurrent.futures import ThreadPoolExecutor
from test_access import AccessTests
from auth_store import UserStore, normalize_identity

class UserManagementTests(AccessTests):
    admin = ("test-admin", "local-test-only")

    def token(self):
        response = self.client.get("/api/v1/admin/users", auth=self.admin)
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.headers["cache-control"], "no-store")
        for item in response.json()["items"]:
            self.assertNotIn("password", item)
            self.assertNotIn("hash", item)
        return {"X-VpnMonitor-CSRF": response.json()["csrf_token"]}

    def test_create_change_disable_delete(self):
        headers = self.token()
        before = self.store._load_document()["users"]
        response = self.client.post("/api/v1/admin/users", auth=self.admin, headers=headers,
                                    json={"username": "Test group", "password": "first-password"})
        self.assertEqual(response.status_code, 201, response.text)
        self.assertNotIn("first-password", self.store.path.read_text(encoding="utf-8"))
        self.assertEqual(self.client.get("/api/v1/me", auth=("Test group", "first-password")).status_code, 200)
        self.assertEqual(self.client.get("/api/v1/peers", auth=("Test group", "first-password")).json()["items"], [])
        for data in ({"enabled": False}, {"password": "second-password", "enabled": True}):
            response = self.client.patch("/api/v1/admin/users/Test%20group", auth=self.admin, headers=headers, json=data)
            self.assertEqual(response.status_code, 200, response.text)
            self.assertEqual(self.client.get("/api/v1/me", auth=("Test group", "first-password")).status_code, 401)
        self.assertEqual(self.client.get("/api/v1/me", auth=("Test group", "second-password")).status_code, 200)
        response = self.client.delete("/api/v1/admin/users/Test%20group", auth=self.admin, headers=headers)
        self.assertEqual(response.status_code, 200)
        self.assertEqual(self.store._load_document()["users"], before)
        self.assertEqual(self.client.get("/api/v1/me", auth=("Test group", "second-password")).status_code, 401)

    def test_admin_only_and_csrf(self):
        headers = self.token()
        for method, path, data in [("GET", "", None), ("POST", "", {"username":"x","password":"secret"}),
                                   ("PATCH", "/Empty", {"enabled":False}), ("DELETE", "/Empty", None)]:
            for auth, code in [(None,401), (("Empty","test-user-password"),403)]:
                response = self.client.request(method,"/api/v1/admin/users"+path,auth=auth,headers=headers,json=data)
                self.assertEqual(response.status_code,code,response.text)
        response = self.client.post("/api/v1/admin/users",auth=self.admin,json={"username":"x","password":"secret"})
        self.assertEqual(response.status_code,403)
        self.assertEqual(self.client.delete("/api/v1/admin/users/Empty",auth=self.admin).status_code,403)

    def test_protected_admin_duplicates_validation(self):
        headers=self.token()
        for path in ["test-admin","TEST-ADMIN"]:
            self.assertEqual(self.client.delete("/api/v1/admin/users/"+path,auth=self.admin,headers=headers).status_code,403)
        self.assertEqual(self.client.post("/api/v1/admin/users",auth=self.admin,headers=headers,json={"username":"TEST-admin","password":"secret"}).status_code,403)
        self.assertEqual(self.client.post("/api/v1/admin/users",auth=self.admin,headers=headers,json={"username":"empty","password":"secret"}).status_code,409)
        for body in [{"username":"bad:name","password":"secret"},{"username":"x","password":""},
                     {"username":"x","password":12345},{"username":"x","password":"secret","enabled":"false"},
                     {"username":"x","password":"secret","is_admin":True}]:
            response=self.client.post("/api/v1/admin/users",auth=self.admin,headers=headers,json=body)
            self.assertEqual(response.status_code,422,response.text)
            self.assertNotIn("secret",response.text)
        self.assertEqual(self.client.patch("/api/v1/admin/users/missing",auth=self.admin,headers=headers,json={"enabled":False}).status_code,404)

    def test_io_failure_is_sanitized(self):
        from unittest.mock import patch
        headers=self.token()
        with patch.object(self.store,"manage",side_effect=PermissionError("private-path-detail")):
            response=self.client.patch("/api/v1/admin/users/Empty",auth=self.admin,headers=headers,json={"enabled":False})
        self.assertEqual(response.status_code,503)
        self.assertNotIn("private-path-detail",response.text)

    def test_concurrent_stores_no_lost_users(self):
        def create(index):
            UserStore(self.store.path).manage("account"+str(index),admin="test-admin",create=True,password="fixture-password")
        with ThreadPoolExecutor(max_workers=4) as pool:
            list(pool.map(create,range(12)))
        self.assertEqual(len(self.store.list_users()),14)

if __name__ == "__main__":
    import unittest
    unittest.main()
