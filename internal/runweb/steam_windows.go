//go:build windows

package runweb

import "golang.org/x/sys/windows/registry"

// steamPathFromRegistry reads HKCU\Software\Valve\Steam\SteamPath, as
// Sts2PathDiscovery.props does.
func steamPathFromRegistry() string {
	k, err := registry.OpenKey(registry.CURRENT_USER, `Software\Valve\Steam`, registry.QUERY_VALUE)
	if err != nil {
		return ""
	}
	defer k.Close()
	v, _, err := k.GetStringValue("SteamPath")
	if err != nil {
		return ""
	}
	return v
}
