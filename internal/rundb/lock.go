package rundb

import (
	"fmt"
	"os"
)

// AcquireIngestLock takes an exclusive, non-blocking OS lock on
// <dbPath>.ingest.lock so two ingests never run at once (SpireLens launches
// one at every run end and Core load). ok is false when another process
// holds the lock. The OS drops the lock when the process exits, so a crash
// never leaves a stale lock behind. Call release when done.
func AcquireIngestLock(dbPath string) (release func(), ok bool, err error) {
	path := dbPath + ".ingest.lock"
	f, err := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0o644)
	if err != nil {
		return nil, false, fmt.Errorf("open ingest lock %s: %w", path, err)
	}
	locked, err := tryLockFile(f)
	if err != nil {
		f.Close()
		return nil, false, fmt.Errorf("lock %s: %w", path, err)
	}
	if !locked {
		f.Close()
		return nil, false, nil
	}
	return func() {
		unlockFile(f)
		f.Close()
	}, true, nil
}
