//go:build !windows && !unix

package rundb

import "os"

// No OS file locking here; SQLite's own locking still serializes writers.
func tryLockFile(*os.File) (bool, error) { return true, nil }

func unlockFile(*os.File) {}
