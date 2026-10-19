//go:build !windows

package runweb

import (
	"context"
	"errors"
)

// watchDir has no implementation off Windows (the game, and so the play-now
// consumer, only runs there). The play-status stream still sends the state it
// opens with.
func watchDir(ctx context.Context, dir string, changed func()) error {
	return errors.New("directory watching is only implemented on Windows")
}
