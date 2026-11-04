package main

import "C"
import (
	"github.com/amnezia-vpn/amneziawg-windows/v3/conf"
	"golang.org/x/sys/windows"
)

//export VpnMonitorValidateConfig
func VpnMonitorValidateConfig(text *uint16, name *uint16) bool {
	_, err := conf.FromWgQuickWithUnknownEncoding(windows.UTF16PtrToString(text), windows.UTF16PtrToString(name))
	return err == nil
}

//export VpnMonitorTunnelService
func VpnMonitorTunnelService(text *uint16, name *uint16, dataDirectory *uint16) bool {
	directory := windows.UTF16PtrToString(dataDirectory)
	config := windows.UTF16PtrToString(text)
	conf.PresetRootDirectory(directory)
	activeGuardProfile = loadGuardProfile(directory, config)
	UseFixedGUIDInsteadOfDeterministic = false
	return Run(config, windows.UTF16PtrToString(name)) == nil
}
