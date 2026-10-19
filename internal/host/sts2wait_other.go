//go:build !windows

package host

import (
	"errors"
	"time"
)

// The host face only runs on the Windows laptop; these exist so the pod face,
// which shares the package, builds elsewhere.

func waitForReadyFile(path string, timeout time.Duration) (readyDoc, error) {
	return readyDoc{}, errors.New("waiting for the bridge ready file is only implemented on Windows")
}

type exitHandle struct{}

func sts2Pids() ([]uint32, error)                                  { return nil, nil }
func openForExitWait(pids []uint32) []exitHandle                   { return nil }
func waitForExit(handles []exitHandle, timeout time.Duration) bool { return true }
func pidIsSts2(pid int) bool                                       { return false }
