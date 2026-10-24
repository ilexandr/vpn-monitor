package main

import "C"
import (
    "golang.org/x/sys/windows"
    "github.com/amnezia-vpn/amneziawg-windows/v3/conf"
)

//export VpnMonitorValidateConfig
func VpnMonitorValidateConfig(text *uint16, name *uint16) bool {
    _, err := conf.FromWgQuickWithUnknownEncoding(windows.UTF16PtrToString(text), windows.UTF16PtrToString(name))
    return err == nil
}
//export VpnMonitorTunnelService
func VpnMonitorTunnelService(text *uint16, name *uint16, dataDirectory *uint16) bool {
    conf.PresetRootDirectory(windows.UTF16PtrToString(dataDirectory))
    UseFixedGUIDInsteadOfDeterministic = false
    return Run(windows.UTF16PtrToString(text), windows.UTF16PtrToString(name)) == nil
}