package main

import (
	"bytes"
	"os"
	"regexp"
	"strings"
	"testing"
)

// Each variable .goreleaser.yaml stamps exists here, since the linker drops an -X
// naming one that doesn't.
func TestReleaseStampsExistingVariables(t *testing.T) {
	stamped := map[string]*string{"version": &version, "buildRepo": &buildRepo, "buildServer": &buildServer, "buildTime": &buildTime}
	config, err := os.ReadFile("../../.goreleaser.yaml")
	if err != nil {
		t.Fatal(err)
	}
	names := regexp.MustCompile(`-X main\.(\w+)=`).FindAllStringSubmatch(string(config), -1)
	if len(names) == 0 {
		t.Fatal(".goreleaser.yaml stamps nothing into main")
	}
	for _, name := range names {
		if stamped[name[1]] == nil {
			t.Errorf(".goreleaser.yaml stamps main.%s, which spirelens-db doesn't declare", name[1])
		}
	}
}

func TestPrintVersion(t *testing.T) {
	saved := [...]string{version, buildRepo, buildServer, buildTime}
	t.Cleanup(func() { version, buildRepo, buildServer, buildTime = saved[0], saved[1], saved[2], saved[3] })

	version, buildRepo, buildServer, buildTime = "", "", "", ""
	var out bytes.Buffer
	printVersion(&out)
	if !strings.Contains(out.String(), "development build") {
		t.Errorf("unstamped build printed %q", out.String())
	}

	version, buildRepo, buildServer, buildTime = "v0.5.9", "romaine-life/spirelens", "https://forgejo.romaine.life", "2026-11-19T22:00:00.000Z"
	out.Reset()
	printVersion(&out)
	for _, want := range []string{"spirelens-db v0.5.9", "https://forgejo.romaine.life/romaine-life/spirelens", "2026-11-19T22:00:00.000Z"} {
		if !strings.Contains(out.String(), want) {
			t.Errorf("stamped build printed %q, missing %q", out.String(), want)
		}
	}
}
