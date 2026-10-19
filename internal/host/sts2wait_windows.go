//go:build windows

package host

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// waitForReadyFile blocks until the bridge's ready file names a live STS2
// process, or the timeout passes. It waits on a directory change
// notification, so nothing polls.
func waitForReadyFile(path string, timeout time.Duration) (readyDoc, error) {
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return readyDoc{}, err
	}
	note, err := windows.FindFirstChangeNotification(dir, false,
		windows.FILE_NOTIFY_CHANGE_FILE_NAME|windows.FILE_NOTIFY_CHANGE_LAST_WRITE)
	if err != nil {
		return readyDoc{}, fmt.Errorf("watch %s: %w", dir, err)
	}
	defer windows.FindCloseChangeNotification(note)

	deadline := time.Now().Add(timeout)
	for {
		// Checked after the watch is armed, so a write in between still wakes us.
		if doc, ok := readValidReadyFile(path); ok {
			return doc, nil
		}
		remaining := time.Until(deadline)
		if remaining <= 0 {
			return readyDoc{}, errReadyTimeout
		}
		ev, err := windows.WaitForSingleObject(note, uint32(remaining.Milliseconds()))
		if err != nil {
			return readyDoc{}, fmt.Errorf("watch %s: %w", dir, err)
		}
		if ev == uint32(windows.WAIT_TIMEOUT) {
			return readyDoc{}, errReadyTimeout
		}
		if err := windows.FindNextChangeNotification(note); err != nil {
			return readyDoc{}, fmt.Errorf("watch %s: %w", dir, err)
		}
	}
}

// sts2Pids lists the running processes whose image is one of sts2ProcessImages.
func sts2Pids() ([]uint32, error) {
	snap, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return nil, err
	}
	defer windows.CloseHandle(snap)

	var entry windows.ProcessEntry32
	entry.Size = uint32(unsafe.Sizeof(entry))
	var pids []uint32
	for err = windows.Process32First(snap, &entry); err == nil; err = windows.Process32Next(snap, &entry) {
		name := windows.UTF16ToString(entry.ExeFile[:])
		for _, image := range sts2ProcessImages {
			if strings.EqualFold(name, image) {
				pids = append(pids, entry.ProcessID)
			}
		}
	}
	if !errors.Is(err, windows.ERROR_NO_MORE_FILES) {
		return pids, err
	}
	return pids, nil
}

// openForExitWait opens SYNCHRONIZE handles to the processes. A pid that can
// no longer be opened has already exited and is skipped.
func openForExitWait(pids []uint32) []windows.Handle {
	var handles []windows.Handle
	for _, pid := range pids {
		if h, err := windows.OpenProcess(windows.SYNCHRONIZE, false, pid); err == nil {
			handles = append(handles, h)
		}
	}
	return handles
}

// maximumWaitObjects is WaitForMultipleObjects' handle limit (winnt.h).
const maximumWaitObjects = 64

// waitForExit blocks on the process handles until every one has exited, or
// the timeout passes, and closes them.
func waitForExit(handles []windows.Handle, timeout time.Duration) bool {
	defer func() {
		for _, h := range handles {
			windows.CloseHandle(h)
		}
	}()
	if len(handles) == 0 {
		return true
	}
	wait := handles
	if len(wait) > maximumWaitObjects {
		wait = wait[:maximumWaitObjects]
	}
	ev, err := windows.WaitForMultipleObjects(wait, true, uint32(timeout.Milliseconds()))
	return err == nil && ev != uint32(windows.WAIT_TIMEOUT)
}

func pidIsSts2(pid int) bool {
	pids, err := sts2Pids()
	if err != nil {
		return false
	}
	for _, p := range pids {
		if int(p) == pid {
			return true
		}
	}
	return false
}
