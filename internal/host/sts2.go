package host

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"time"
)

func basicAuth(user, token string) string {
	return base64.StdEncoding.EncodeToString([]byte(user + ":" + token))
}

// bridgeHTTPGet is overridable in tests.
var bridgeHTTPGet = func(url string) (int, []byte, error) {
	client := &http.Client{Timeout: 2500 * time.Millisecond}
	resp, err := client.Get(url)
	if err != nil {
		return 0, nil, err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	return resp.StatusCode, body, nil
}

// BridgeURL is the SpireLensMcp bridge health URL (restart-sts2.ps1 pinged "/").
func BridgeURL(host string, port int) string {
	return fmt.Sprintf("http://%s:%d/", host, port)
}

// BridgePingOK reports whether the bridge responds with {"status":"ok"}.
func BridgePingOK(host string, port int) bool {
	code, body, err := bridgeHTTPGet(BridgeURL(host, port))
	if err != nil || code < 200 || code >= 300 {
		return false
	}
	var doc struct {
		Status string `json:"status"`
	}
	if json.Unmarshal(body, &doc) != nil {
		return false
	}
	return doc.Status == "ok"
}

// sts2ProcessImages are the image names restart-sts2.ps1 stopped.
var sts2ProcessImages = []string{"SlayTheSpire2.exe", "crashpad_handler.exe"}

// StopSts2 force-stops the STS2 processes and blocks on their process handles
// until they have exited, so the mods/ DLLs are unlocked when it returns.
// Missing processes are not an error (idempotent teardown). Process selection
// is by image name; the laptop runs only the user's STS2 (AGENTS.md mod
// policy), so an image-name match is the host's single live instance.
func StopSts2(ctx context.Context, w io.Writer, shutdownTimeoutSeconds int) error {
	// Open the handles before killing, so a process that exits in between is
	// still waited on correctly (its handle is already signaled).
	pids, err := sts2Pids()
	if err != nil {
		fmt.Fprintf(w, "list STS2 processes: %v\n", err)
	}
	handles := openForExitWait(pids)
	for _, image := range sts2ProcessImages {
		// taskkill returns non-zero when the image is not running; that is fine.
		_, _ = runCmd(ctx, w, "", nil, "taskkill", "/F", "/T", "/IM", image)
	}
	if !waitForExit(handles, time.Duration(shutdownTimeoutSeconds)*time.Second) {
		// Best-effort: teardown is advisory, as before.
		fmt.Fprintf(w, "STS2 processes still running after %ds\n", shutdownTimeoutSeconds)
	}
	return nil
}

// errReadyTimeout reports that the bridge's ready file did not appear in time.
var errReadyTimeout = errors.New("bridge ready file did not appear")

// readyDoc is bridge-ready.json, written by SpireLensMcpBridge once
// NGame.GameStartupComplete finishes (logo, main menu, startup error checks).
type readyDoc struct {
	Pid     int    `json:"pid"`
	Port    int    `json:"port"`
	Version string `json:"version"`
}

// BridgeReadyFile is where the bridge writes its ready file: the game's user
// data directory, %APPDATA%\SlayTheSpire2\SpireLensMcpBridge\bridge-ready.json.
// SPIRELENS_BRIDGE_READY_FILE overrides it.
func BridgeReadyFile() string {
	if p := os.Getenv("SPIRELENS_BRIDGE_READY_FILE"); p != "" {
		return p
	}
	appData := os.Getenv("APPDATA")
	if appData == "" {
		appData = filepath.Join(os.Getenv("USERPROFILE"), "AppData", "Roaming")
	}
	return filepath.Join(appData, "SlayTheSpire2", "SpireLensMcpBridge", "bridge-ready.json")
}

// readValidReadyFile returns the ready file when it names a running STS2
// process. A file left by a killed or crashed game names a dead pid.
func readValidReadyFile(path string) (readyDoc, bool) {
	b, err := os.ReadFile(path)
	if err != nil {
		return readyDoc{}, false
	}
	var doc readyDoc
	if json.Unmarshal(b, &doc) != nil || doc.Pid == 0 {
		return readyDoc{}, false
	}
	return doc, pidIsSts2(doc.Pid)
}

// StartSts2 launches STS2, mirroring restart-sts2.ps1's Start-Sts2 priority:
// a configured scheduled task, then SlayTheSpire2.exe, then launch_opengl.bat.
func StartSts2(ctx context.Context, w io.Writer, gameDir, launchArgs string) error {
	if task := os.Getenv("SPIRELENS_HOST_STS2_LAUNCH_TASK"); task != "" {
		return mustRun(ctx, w, "", nil, "start STS2 via scheduled task", "schtasks", "/Run", "/TN", task)
	}
	exe := gameDir + `\SlayTheSpire2.exe`
	if fileExists(exe) {
		cmd := exec.Command(exe, splitArgs(launchArgs)...)
		cmd.Dir = gameDir
		return cmd.Start()
	}
	bat := gameDir + `\launch_opengl.bat`
	if fileExists(bat) {
		cmd := exec.Command(bat)
		cmd.Dir = gameDir
		return cmd.Start()
	}
	return fmt.Errorf("no STS2 launcher found under %s (SlayTheSpire2.exe / launch_opengl.bat) and SPIRELENS_HOST_STS2_LAUNCH_TASK is unset", gameDir)
}

// WaitSts2Ready blocks until the bridge reports game startup complete: it
// waits on a directory change notification for the bridge's ready file, then
// confirms once over HTTP. "Ready" here means the main menu has settled, not
// merely that the listener is up — loading a run before that can cancel the
// game's own startup.
func WaitSts2Ready(host string, port, startupTimeoutSeconds int) error {
	path := BridgeReadyFile()
	if _, err := waitForReadyFile(path, time.Duration(startupTimeoutSeconds)*time.Second); err != nil {
		return fmt.Errorf("STS2 bridge did not report ready (%s) within %ds: %w", path, startupTimeoutSeconds, err)
	}
	if !BridgePingOK(host, port) {
		return fmt.Errorf("STS2 bridge wrote %s but does not answer on %s", path, BridgeURL(host, port))
	}
	return nil
}

func splitArgs(s string) []string {
	out := []string{}
	for _, f := range fieldsQuoteAware(s) {
		if f != "" {
			out = append(out, f)
		}
	}
	return out
}

// fieldsQuoteAware splits on spaces (the launch args here are simple flags like
// "--rendering-driver opengl3").
func fieldsQuoteAware(s string) []string {
	cur := ""
	out := []string{}
	for _, r := range s {
		if r == ' ' {
			if cur != "" {
				out = append(out, cur)
				cur = ""
			}
			continue
		}
		cur += string(r)
	}
	if cur != "" {
		out = append(out, cur)
	}
	return out
}
