package rundb

import (
	"context"
	"path/filepath"
	"testing"
)

// A migrated database has the taken-over tag columns on both tables a
// taken-over run lands in (docs/replay-player.md, "Taking over").
func TestOpen_HasTakenOverColumns(t *testing.T) {
	ctx := context.Background()
	db, err := Open(ctx, filepath.Join(t.TempDir(), "runs.db"))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	defer db.Close()

	for _, table := range []string{"recordings", "spirelens_runs"} {
		for _, column := range []string{"taken_over_from_game_start_time", "taken_over_at_floor"} {
			var n int
			err := db.QueryRowContext(ctx,
				`SELECT COUNT(*) FROM pragma_table_info(?) WHERE name = ?`, table, column).Scan(&n)
			if err != nil {
				t.Fatalf("pragma_table_info(%s): %v", table, err)
			}
			if n != 1 {
				t.Errorf("%s.%s missing", table, column)
			}
		}
	}
}

// The tag reads as SQL NULLs for an untagged run and as its values for a
// taken-over one.
func TestTakenOver_SQLValues(t *testing.T) {
	var none *takenOver
	if none.fromGameStartTime() != nil || none.atFloor() != nil {
		t.Errorf("untagged run should give NULLs")
	}
	start, floor := int64(1794200070), int64(12)
	tagged := &takenOver{FromGameStartTime: &start, FromFloor: &floor}
	if got := tagged.fromGameStartTime(); got != nullInt64Ptr(&start) {
		t.Errorf("from_game_start_time = %v", got)
	}
	if got := tagged.atFloor(); got != nullInt64Ptr(&floor) {
		t.Errorf("from_floor = %v", got)
	}
}
