//go:build windows

package runweb

import (
	"context"
	"fmt"
	"os"

	"golang.org/x/sys/windows"
)

// watchDir calls changed each time a file in dir is created, deleted, renamed
// or written, until ctx ends. It blocks on a Windows change notification
// together with a cancel event, so nothing polls.
func watchDir(ctx context.Context, dir string, changed func()) error {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	note, err := windows.FindFirstChangeNotification(dir, false,
		windows.FILE_NOTIFY_CHANGE_FILE_NAME|windows.FILE_NOTIFY_CHANGE_LAST_WRITE)
	if err != nil {
		return fmt.Errorf("watch %s: %w", dir, err)
	}
	defer windows.FindCloseChangeNotification(note)

	cancel, err := windows.CreateEvent(nil, 1, 0, nil)
	if err != nil {
		return fmt.Errorf("watch %s: %w", dir, err)
	}
	defer windows.CloseHandle(cancel)
	stop := context.AfterFunc(ctx, func() { windows.SetEvent(cancel) })
	defer stop()

	handles := []windows.Handle{note, cancel}
	for {
		which, err := windows.WaitForMultipleObjects(handles, false, windows.INFINITE)
		if err != nil {
			return fmt.Errorf("watch %s: %w", dir, err)
		}
		if which != windows.WAIT_OBJECT_0 {
			return ctx.Err()
		}
		changed()
		if err := windows.FindNextChangeNotification(note); err != nil {
			return fmt.Errorf("watch %s: %w", dir, err)
		}
	}
}
