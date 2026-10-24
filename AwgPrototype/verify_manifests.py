import ctypes, pathlib, re
k=ctypes.WinDLL("kernel32",use_last_error=True)
k.LoadLibraryExW.argtypes=[ctypes.c_wchar_p,ctypes.c_void_p,ctypes.c_uint32];k.LoadLibraryExW.restype=ctypes.c_void_p
k.FindResourceW.argtypes=[ctypes.c_void_p,ctypes.c_void_p,ctypes.c_void_p];k.FindResourceW.restype=ctypes.c_void_p
k.LoadResource.argtypes=[ctypes.c_void_p,ctypes.c_void_p];k.LoadResource.restype=ctypes.c_void_p
k.LockResource.argtypes=[ctypes.c_void_p];k.LockResource.restype=ctypes.c_void_p
k.SizeofResource.argtypes=[ctypes.c_void_p,ctypes.c_void_p];k.SizeofResource.restype=ctypes.c_uint32
k.FreeLibrary.argtypes=[ctypes.c_void_p]
for path, expected in [
 (pathlib.Path("AwgPrototype/manager-bin/AwgPrototype.Manager.exe"),"requireAdministrator"),
 (pathlib.Path("artifacts/v0.1.58/bin/VpnMonitor.Client/release/VpnMonitor.Client.exe"),"asInvoker"),
 (pathlib.Path("C:/Program Files/VpnMonitor/AwgPrototype/AwgPrototype.Manager.exe"),"requireAdministrator")
]:
 module=k.LoadLibraryExW(str(path.resolve()),None,2)
 assert module, "PE load failed"
 try:
  resource=k.FindResourceW(module,ctypes.c_void_p(1),ctypes.c_void_p(24))
  assert resource, "Manifest missing"
  size=k.SizeofResource(module,resource)
  xml=ctypes.string_at(k.LockResource(k.LoadResource(module,resource)),size).decode("utf-8")
  assert re.search(r'level\s*=\s*["\']'+expected+r'["\']',xml), "Unexpected privilege level"
  print("PASS:",path.name,expected)
 finally:k.FreeLibrary(module)