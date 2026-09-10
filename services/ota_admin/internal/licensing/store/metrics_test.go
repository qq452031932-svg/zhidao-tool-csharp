package store

import (
	"context"
	"database/sql"
	"errors"
	"path/filepath"
	"testing"
)

func TestMetricsCountRealSQLiteContentionButNotDomainErrors(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "metrics.db")
	store, err := Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()

	store.db.SetMaxOpenConns(1)
	if _, err := store.db.ExecContext(ctx, `PRAGMA busy_timeout=1`); err != nil {
		t.Fatal(err)
	}
	locker, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	defer locker.Close()
	conn, err := locker.Conn(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	if _, err := conn.ExecContext(ctx, `PRAGMA busy_timeout=1`); err != nil {
		t.Fatal(err)
	}
	if _, err := conn.ExecContext(ctx, `BEGIN IMMEDIATE`); err != nil {
		t.Fatal(err)
	}
	if _, err := store.CreateSoftware(ctx, "Contended Product"); err == nil {
		t.Fatal("write unexpectedly succeeded while SQLite was locked")
	}
	if _, err := conn.ExecContext(ctx, `ROLLBACK`); err != nil {
		t.Fatal(err)
	}
	contention := store.Metrics()
	if contention.Busy+contention.Locked == 0 {
		t.Fatalf("contention metrics = %+v, want at least one busy/locked event", contention)
	}

	created, err := store.CreateSoftware(ctx, "Unique Product")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.CreateSoftware(ctx, created.Name); !errors.Is(err, ErrConflict) {
		t.Fatalf("duplicate error = %v, want ErrConflict", err)
	}
	if err := store.RenameSoftware(ctx, "missing", "Still Missing"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("missing error = %v, want ErrNotFound", err)
	}
	afterDomainErrors := store.Metrics()
	if afterDomainErrors != contention {
		t.Fatalf("domain errors changed metrics: before=%+v after=%+v", contention, afterDomainErrors)
	}
}

func TestSQLiteClassifierDistinguishesBusyLockedAndIgnoresOtherErrors(t *testing.T) {
	var counters metricCounters
	busy := errors.New("database is locked (5) (SQLITE_BUSY)")
	locked := errors.New("database table is locked (6) (SQLITE_LOCKED)")
	other := errors.New("constraint failed")
	if classifySQLiteError(&counters, busy) != busy || classifySQLiteError(&counters, locked) != locked || classifySQLiteError(&counters, other) != other {
		t.Fatal("classifier changed returned error identity")
	}
	if got := counters.snapshot(); got != (Metrics{Busy: 1, Locked: 1}) {
		t.Fatalf("metrics = %+v", got)
	}
}
